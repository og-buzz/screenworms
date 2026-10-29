// Entry point for the Linux/KDE build: one GLFW window per monitor, one renderer per context.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenWorm
{
    enum RunMode { Saver, Demo, Window, Screenshot }

    class Args
    {
        public readonly string Mode = "";
        readonly Dictionary<string, string> opts = new Dictionary<string, string>(); public readonly List<string> Rest = new List<string>();
        static readonly HashSet<string> Flags = new HashSet<string> { "demo", "live", "crt", "help", "stats", "screens", "run", "window", "screenshot", "install", "uninstall" };
        public Args(string[] a)
        {
            int start = 0;
            if (a.Length > 0 && (a[0].StartsWith("/") || a[0].StartsWith("--") || a[0].StartsWith("-")))
            {
                string m = a[0].ToLowerInvariant();
                if (m.StartsWith("/")) { Mode = m.Substring(0, Math.Min(2, m.Length)); if (m.Length > 3 && m[2] == ':') Rest.Add(m.Substring(3)); }
                else Mode = m;
                start = 1;
            }
            for (int i = start; i < a.Length; i++)
            {
                if (a[i].StartsWith("--"))
                {
                    string k = a[i].Substring(2).ToLowerInvariant(); string v = "1";
                    if (!Flags.Contains(k) && i + 1 < a.Length && !a[i + 1].StartsWith("--")) v = a[++i];
                    opts[k] = v;
                }
                else Rest.Add(a[i]);
            }
        }
        public bool Has(string k) { return opts.ContainsKey(k); }
        public string Get(string k, string d) { string v; return opts.TryGetValue(k, out v) ? v : d; }
        public int Int(string k, int d) { int v; return int.TryParse(Get(k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d; }
        public double Dbl(string k, double d) { double v; return double.TryParse(Get(k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d; }
    }

    static class Program
    {
        public const string Usage = @"ScreenWorm - one glowing worm per CPU core, fed by live system stats.
  screenworm --run [--demo]     full screen on every monitor; any input quits
  screenworm --window           one resizable window (keys: h legend, c crt, b bloom, q quit)
  screenworm --screenshot out.png [--size 1920x1080] [--seconds 12] [--demo|--live] [--cores N] [--hud 0|1|2]
  screenworm --stats            stream JSON samples to stdout (--seconds N to stop)
  screenworm --screens          print monitor layout
  screenworm --install          install from the source tree (runs linux/install.sh)
  screenworm --uninstall        remove the launcher and autostart entry (from the source tree)
  options: --q 0.3..1 (render scale)  --particles N  --hud 0|1|2  --crt  --seconds N (auto quit)";

        [STAThread]
        static int Main(string[] args)
        {
            var a = new Args(args);
            try
            {
                string mode = a.Mode;
                if (mode == "/s") mode = "--run";
                switch (mode)
                {
                    case "/s": case "--run": case "run": return new App(RunMode.Saver, a).Run();
                    case "--window": case "window": return new App(RunMode.Window, a).Run();
                    case "--screenshot": case "screenshot": return new App(RunMode.Screenshot, a).Run();
                    case "--stats": case "stats": return StatsCli.Run(a);
                    case "--screens": case "screens": Console.WriteLine(Monitors.ToJson(Monitors.Detect())); return 0;
                    case "--install": case "install": return Installer.Run(true);
                    case "--uninstall": case "uninstall": return Installer.Run(false);
                    case "--help": case "-h": case "/?": case "help": Console.WriteLine(Usage); return 0;
                }
            }
            catch (Exception ex)
            {
                Log.Write("fatal: " + ex);
                Console.Error.WriteLine(ex);
                return 2;
            }
            Console.WriteLine(Usage);
            return 1;
        }
    }

    class Win
    {
        public IntPtr H; public Renderer R; public ViewRes V; public HudTexture Hud;
        public RectD View; public int CW, CH, FbW, FbH; public double Scale = 1;
    }

    class App
    {
        readonly RunMode mode; readonly Args args; readonly Config cfg;
        readonly List<Win> wins = new List<Win>();
        readonly List<PosixSignalRegistration> signals = new List<PosixSignalRegistration>();
        volatile bool quitting; Exception error;
        Sampler sampler; volatile StatSample latest; readonly bool demo; Sim sim; readonly Settings settings = new Settings();
        volatile int hudMode; double quality; readonly bool pinnedQ; readonly double autoQuit;
        readonly uint seed = (uint)(Environment.TickCount * 2654435761u) | 1u; RectD world; bool multi;
        double fpsEma = 60, dtEma = 16.7; FileStream lockFile; readonly long startMs = Json.NowMs();

        static readonly Glfw.ErrorCallback ErrorCallback = delegate (int code, IntPtr desc) { Log.Write("glfw " + code + ": " + Glfw.Describe(desc)); };
        readonly Glfw.KeyCallback keyCallback;
        readonly Glfw.MouseButtonCallback mouseCallback;
        readonly Glfw.CursorPosCallback cursorCallback;

        public App(RunMode mode, Args a)
        {
            this.mode = mode; args = a; cfg = Config.Load();
            demo = a.Has("demo") || (mode == RunMode.Screenshot && !a.Has("live"));
            hudMode = Math.Max(0, Math.Min(2, a.Int("hud", cfg.Hud)));
            settings.Crt = a.Has("crt") || cfg.Crt ? 0.7 : 0;
            double q = a.Dbl("q", cfg.QualityValue); pinnedQ = a.Has("q") || cfg.QualityValue > 0; quality = q > 0 ? Math.Max(0.3, Math.Min(1, q)) : 1;
            autoQuit = a.Dbl("seconds", 0);
            keyCallback = delegate (IntPtr w, int key, int sc, int action, int mods) { if (action != Glfw.RELEASE) OnKey(key); };
            mouseCallback = delegate (IntPtr w, int button, int action, int mods) { if (action == Glfw.PRESS) OnClick(); };
            cursorCallback = delegate (IntPtr w, double x, double y) { OnMove(w, x, y); };
        }

        public int Run()
        {
            Log.Write("start " + mode + " args=" + string.Join(" ", Environment.GetCommandLineArgs()));
            if (mode == RunMode.Saver || mode == RunMode.Demo) AcquireLock();
            if (quitting) return 0;
            RegisterSignals();
            sampler = new Sampler();
            if (mode == RunMode.Screenshot) return RunScreenshot();
            try
            {
                Glfw.SetErrorCallback(ErrorCallback);
                Glfw.Init();
                CreateWindows();
                Glfw.MakeContextCurrent(wins[0].H);
                GL.Load(); CheckVersion();
                int particles = args.Int("particles", cfg.Particles);
                BuildSim();
                for (int i = 0; i < wins.Count; i++)
                {
                    var w = wins[i];
                    Glfw.MakeContextCurrent(w.H);
                    Glfw.SwapInterval(0);
                    w.R = new Renderer(particles, 6000, sim.Cores);
                    w.R.SetQuality(quality);
                    w.R.SetWorld(world, seed);
                    w.V = new ViewRes();
                    w.V.Build(Math.Max(8, w.FbW), Math.Max(8, w.FbH), quality, false);
                }
                for (int i = 0; i < wins.Count; i++)
                {
                    var w = wins[i];
                    Glfw.MakeContextCurrent(w.H);
                    bool on = hudMode == 2 || (hudMode == 1 && i == 0);
                    w.Hud = new HudTexture(Math.Min(8192, Math.Max(64, w.FbW)), w.Scale, demo ? "DEMO" : Environment.MachineName, sampler.CpuModel, sampler.OsName, sim, on ? hudMode : 0);
                    w.Hud.Mode = on ? hudMode : 0;
                }
                Glfw.MakeContextCurrent(wins[0].H);
                Log.Write("gl: " + wins[0].R.Info);
                Thread samplerThread = new Thread(SampleLoop) { IsBackground = true, Name = "sampler" };
                samplerThread.Start();
                RenderLoop();
                Log.Write("render loop end: ~" + fpsEma.ToString("0", CultureInfo.InvariantCulture) + " fps, quality " + quality.ToString("0.00", CultureInfo.InvariantCulture) + ", " + wins.Count + " window(s)");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                quitting = true;
                foreach (var w in wins) { try { if (w.V != null) w.V.Dispose(); } catch (Exception) { } }
                try { Glfw.Terminate(); } catch (Exception) { }
                if (lockFile != null) { lockFile.Dispose(); lockFile = null; }
            }
            if (error != null)
            {
                Log.Write("render error: " + error);
                Console.Error.WriteLine(error);
                return 3;
            }
            Log.Write("exit " + mode);
            return 0;
        }

        void AcquireLock()
        {
            try
            {
                Directory.CreateDirectory(Paths.StateDir);
                lockFile = new FileStream(Path.Combine(Paths.StateDir, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception) { quitting = true; }
        }

        void RegisterSignals()
        {
            try
            {
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, delegate (PosixSignalContext ctx) { ctx.Cancel = true; quitting = true; }));
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, delegate (PosixSignalContext ctx) { ctx.Cancel = true; quitting = true; }));
            }
            catch (Exception) { }
        }

        static void CheckVersion()
        {
            var v = new int[1]; GL.glGetIntegerv(GL.MAJOR_VERSION, v); int major = v[0]; GL.glGetIntegerv(GL.MINOR_VERSION, v); int minor = v[0];
            GL.glGetError();
            if (major < 3 || (major == 3 && minor < 3)) throw new Exception("OpenGL 3.3 is required; this driver reports " + GL.glGetString(GL.VERSION));
        }

        void CreateWindows()
        {
            Glfw.DefaultWindowHints();
            Glfw.WindowHint(Glfw.CONTEXT_VERSION_MAJOR, 3); Glfw.WindowHint(Glfw.CONTEXT_VERSION_MINOR, 3);
            Glfw.WindowHint(Glfw.OPENGL_PROFILE, Glfw.OPENGL_CORE_PROFILE);
            Glfw.WindowHint(Glfw.VISIBLE, Glfw.FALSE);
            multi = mode == RunMode.Saver || mode == RunMode.Demo;
            if (mode == RunMode.Window)
            {
                Glfw.WindowHint(Glfw.RESIZABLE, Glfw.TRUE);
                Glfw.WindowHint(Glfw.SCALE_TO_MONITOR, Glfw.TRUE);
                int w = args.Int("w", 1600), h = args.Int("h", 900);
                IntPtr win = Glfw.CreateWindow(w, h, "ScreenWorm", IntPtr.Zero, IntPtr.Zero);
                if (win == IntPtr.Zero) throw new Exception("could not create a GLFW window; no Wayland/X11 display?");
                Glfw.ShowWindow(win);
                Glfw.FocusWindow(win);
                int cw, ch, fw, fh; Glfw.GetWindowSize(win, out cw, out ch); Glfw.GetFramebufferSize(win, out fw, out fh);
                var v = new Win { H = win, CW = cw, CH = ch, FbW = fw, FbH = fh, Scale = cw > 0 ? fw / (double)cw : 1, View = new RectD(0, 0, cw, ch) };
                wins.Add(v);
                SetCallbacks(win);
                world = v.View;
                return;
            }
            Glfw.WindowHint(Glfw.DECORATED, Glfw.FALSE);
            Glfw.WindowHint(Glfw.AUTO_ICONIFY, Glfw.FALSE);
            Glfw.WindowHint(Glfw.FLOATING, Glfw.TRUE);
            int count; IntPtr monitors = Glfw.GetMonitors(out count);
            if (count <= 0) throw new Exception("no monitors reported by GLFW");
            IntPtr share = IntPtr.Zero;
            var screens = new List<RectD>();
            for (int i = 0; i < count; i++)
            {
                IntPtr mon = Marshal.ReadIntPtr(monitors, i * IntPtr.Size);
                var vm = Glfw.VideoMode(mon);
                int vw = vm.Width > 0 ? vm.Width : 1920, vh = vm.Height > 0 ? vm.Height : 1080;
                IntPtr win = Glfw.CreateWindow(vw, vh, "ScreenWorm", mon, share);
                if (win == IntPtr.Zero) { Log.Write("could not create window on monitor " + i); continue; }
                if (share == IntPtr.Zero) share = win;
                int mx, my; Glfw.GetMonitorPos(mon, out mx, out my);
                int cw, ch, fw, fh;
                Glfw.GetWindowSize(win, out cw, out ch); Glfw.GetFramebufferSize(win, out fw, out fh);
                if (cw <= 0 || ch <= 0) { cw = vw; ch = vh; }
                if (fw <= 0 || fh <= 0) { fw = cw; fh = ch; }
                var v = new Win { H = win, CW = cw, CH = ch, FbW = fw, FbH = fh, Scale = cw > 0 ? fw / (double)cw : 1, View = new RectD(mx, my, cw, ch) };
                wins.Add(v); screens.Add(v.View);
                SetCallbacks(win);
            }
            if (wins.Count == 0) throw new Exception("could not create any fullscreen windows");
            foreach (var w in wins) { Glfw.ShowWindow(w.H); Glfw.SetInputMode(w.H, Glfw.CURSOR, Glfw.CURSOR_HIDDEN); }
            Glfw.FocusWindow(wins[0].H);
            world = RectD.Union(screens);
        }

        void SetCallbacks(IntPtr win)
        {
            Glfw.SetKeyCallback(win, keyCallback);
            Glfw.SetMouseButtonCallback(win, mouseCallback);
            Glfw.SetCursorPosCallback(win, cursorCallback);
        }

        int lastMx = int.MinValue, lastMy = int.MinValue;
        void OnMove(IntPtr win, double x, double y)
        {
            int ix = (int)x, iy = (int)y;
            if (lastMx == int.MinValue) { lastMx = ix; lastMy = iy; return; }
            if (Math.Abs(ix - lastMx) + Math.Abs(iy - lastMy) > 12) { lastMx = ix; lastMy = iy; if (mode == RunMode.Saver || mode == RunMode.Demo) Quit(); }
        }

        void OnClick() { if (mode == RunMode.Saver || mode == RunMode.Demo) Quit(); }
        void OnKey(int key)
        {
            if (mode == RunMode.Saver || mode == RunMode.Demo) { Quit(); return; }
            switch (key)
            {
                case Glfw.KEY_ESCAPE: case Glfw.KEY_Q: Quit(); break;
                case Glfw.KEY_H: hudMode = (hudMode + 1) % 3; break;
                case Glfw.KEY_C: settings.Crt = settings.Crt > 0 ? 0 : 0.7; break;
                case Glfw.KEY_B: settings.Bloom = settings.Bloom > 0 ? 0 : 1; break;
            }
        }
        void Quit() { quitting = true; }

        static List<List<int>> Pairs(int cores) { var l = new List<List<int>>(); for (int i = 0; i < cores; i += 2) l.Add(i + 1 < cores ? new List<int> { i, i + 1 } : new List<int> { i }); return l; }

        void BuildSim()
        {
            int cores = demo && args.Has("cores") ? Math.Max(1, args.Int("cores", sampler.Logical)) : sampler.Logical;
            var siblings = cores == sampler.Logical ? sampler.Siblings : Pairs(cores);
            var screens = new List<RectD>(); foreach (var w in wins) screens.Add(w.View);
            sim = new Sim(seed, cores, siblings, cores == sampler.Logical ? sampler.Efficiency : null, world, screens);
        }

        void SampleLoop()
        {
            int k = 0;
            while (!quitting)
            {
                var sw = Stopwatch.StartNew();
                try { latest = demo ? Sim.DemoSample(k, sim != null ? sim.Cores : sampler.Logical) : sampler.Collect(); }
                catch (Exception ex) { Log.Write("sample: " + ex.Message); }
                k++;
                int wait = (int)Math.Max(50, 1000 - sw.ElapsedMilliseconds);
                for (int i = 0; i < wait / 50 && !quitting; i++) Thread.Sleep(50);
            }
        }

        void RenderLoop()
        {
            IntPtr current = wins[0].H;
            Glfw.MakeContextCurrent(current);
            double last = Glfw.glfwGetTime(), lastGovern = last, lastQChange = last, acc = 0, pAcc = 0;
            while (!quitting)
            {
                Glfw.PollEvents();
                for (int i = 0; i < wins.Count; i++) if (Glfw.WindowShouldClose(wins[i].H)) { quitting = true; break; }
                if (quitting) break;
                double frameStart = Glfw.glfwGetTime();
                double now = frameStart, dt = Math.Min(0.25, now - last); last = now;
                dtEma += (dt * 1000 - dtEma) * 0.05; fpsEma = 1000 / Math.Max(0.1, dtEma);
                var s = latest; if (s != null) { latest = null; sim.ApplySample(s); }
                acc += dt; int steps = 0;
                while (acc >= Sim.Dt && steps < 12) { sim.StepOnce(); acc -= Sim.Dt; steps++; }
                if (acc > Sim.Dt) acc = 0;
                pAcc += dt; int pn = 0;
                while (pAcc >= 1.0 / 60 && pn < 4) { pAcc -= 1.0 / 60; pn++; }
                if (pAcc > 1.0 / 60) pAcc = 0;

                for (int i = 0; i < wins.Count; i++)
                {
                    var w = wins[i];
                    if (w.FbW < 8 || w.FbH < 8) continue;
                    Glfw.MakeContextCurrent(w.H);
                    int fw, fh; Glfw.GetFramebufferSize(w.H, out fw, out fh);
                    if (fw != w.FbW || fh != w.FbH || w.V.Hdr == null || w.V.Quality != quality)
                    {
                        w.FbW = Math.Max(8, fw); w.FbH = Math.Max(8, fh);
                        w.V.Build(w.FbW, w.FbH, quality, false);
                        if (!multi)
                        {
                            int cw, ch; Glfw.GetWindowSize(w.H, out cw, out ch);
                            w.CW = Math.Max(1, cw); w.CH = Math.Max(1, ch); w.Scale = w.FbW / (double)w.CW;
                            w.View = new RectD(0, 0, w.CW, w.CH); world = w.View;
                            sim.SetWorld(world, null); w.R.SetWorld(world, seed);
                            if (w.Hud != null) w.Hud.SetMaxWidth(w.FbW);
                        }
                    }
                    for (int k = 0; k < pn; k++) w.R.StepParticles(sim, 1f / 60);
                    bool on = hudMode == 2 || (hudMode == 1 && i == 0);
                    if (w.Hud != null) { w.Hud.Mode = on ? hudMode : 0; w.Hud.Update(sim, fpsEma, w.R.ParticleCount, quality, Label(), w.R.Info); }
                    w.R.Render(sim, w.V, w.View, settings, w.Hud);
                    Glfw.SwapBuffers(w.H);
                }

                if (!pinnedQ && now - lastGovern > 2)
                {
                    lastGovern = now;
                    if (dtEma > 21 && quality > 0.5) { quality -= 0.125; foreach (var w in wins) w.R.SetQuality(quality); lastQChange = now; Log.Write("quality -> " + quality); }
                    else if (dtEma < 13 && quality < 1 && now - lastQChange > 12) { quality += 0.125; foreach (var w in wins) w.R.SetQuality(quality); lastQChange = now; Log.Write("quality -> " + quality); }
                }
                if (autoQuit > 0 && (Json.NowMs() - startMs) / 1000.0 > autoQuit) quitting = true;
                double spent = Glfw.glfwGetTime() - frameStart;
                int sleep = (int)((1.0 / 60 - spent) * 1000);
                if (sleep > 0) Thread.Sleep(sleep);
            }
            GL.glFinish();
        }

        string Label() { return multi ? wins.Count + " monitors, world " + (int)world.W + "x" + (int)world.H : wins[0].CW + "x" + wins[0].CH; }

        int RunScreenshot()
        {
            string outFile = args.Get("out", args.Rest.Count > 0 ? args.Rest[0] : "screenworm.png");
            int W = 1920, H = 1080; var parts = args.Get("size", "1920x1080").ToLowerInvariant().Split('x');
            if (parts.Length == 2) { int.TryParse(parts[0], out W); int.TryParse(parts[1], out H); }
            W = Math.Max(64, W); H = Math.Max(64, H);
            double seconds = Math.Max(1, args.Dbl("seconds", 12));
            try
            {
                Glfw.SetErrorCallback(ErrorCallback);
                Glfw.Init();
                Glfw.DefaultWindowHints();
                Glfw.WindowHint(Glfw.CONTEXT_VERSION_MAJOR, 3); Glfw.WindowHint(Glfw.CONTEXT_VERSION_MINOR, 3);
                Glfw.WindowHint(Glfw.OPENGL_PROFILE, Glfw.OPENGL_CORE_PROFILE); Glfw.WindowHint(Glfw.VISIBLE, Glfw.FALSE);
                IntPtr win = Glfw.CreateWindow(640, 360, "ScreenWorm", IntPtr.Zero, IntPtr.Zero);
                if (win == IntPtr.Zero) throw new Exception("could not create an offscreen GL context");
                Glfw.MakeContextCurrent(win); GL.Load(); CheckVersion(); Glfw.SwapInterval(0);
                int cores = demo ? Math.Max(1, args.Int("cores", sampler.Logical)) : sampler.Logical;
                Renderer renderer = new Renderer(args.Int("particles", cfg.Particles), 6000, cores);
                renderer.SetQuality(quality);
                var world = new RectD(0, 0, W, H);
                sim = new Sim(seed, cores, cores == sampler.Logical ? sampler.Siblings : Pairs(cores), cores == sampler.Logical ? sampler.Efficiency : null, world, new List<RectD> { world });
                renderer.SetWorld(world, seed);
                var vr = new ViewRes(); vr.Build(W, H, quality, true);
                HudTexture hud = hudMode > 0 ? new HudTexture(W, args.Dbl("dpr", 1), demo ? "DEMO" : Environment.MachineName, sampler.CpuModel, sampler.OsName, sim, hudMode) : null;
                if (hud != null) hud.Force = true;
                int frames = (int)(seconds * 60);
                for (int i = 0; i < frames && !quitting; i++)
                {
                    if (i % 60 == 0)
                    {
                        if (demo) sim.ApplySample(Sim.DemoSample(i / 60, cores));
                        else { if (i > 0) Thread.Sleep(1000); sim.ApplySample(sampler.Collect()); }
                    }
                    sim.StepOnce(); sim.StepOnce();
                    renderer.StepParticles(sim, 1f / 60);
                    if (i >= frames - 3)
                    {
                        if (hud != null) hud.Update(sim, 60, renderer.ParticleCount, quality, W + "x" + H, renderer.Info);
                        renderer.Render(sim, vr, world, settings, hud);
                    }
                }
                GL.glFinish();
                var rgba = new byte[W * H * 4];
                var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                try
                {
                    GL.glBindFramebuffer(GL.FRAMEBUFFER, vr.Final.Fbo); GL.glPixelStorei(GL.PACK_ALIGNMENT, 4);
                    GL.glReadPixels(0, 0, W, H, GL.RGBA, GL.UNSIGNED_BYTE, handle.AddrOfPinnedObject());
                }
                finally { handle.Free(); }
                Png.Save(outFile, rgba, W, H, true);
                Console.WriteLine("wrote " + outFile + " (" + W + "x" + H + ", " + cores + " cores, " + renderer.ParticleCount + " particles) " + renderer.Info);
                vr.Dispose();
                return 0;
            }
            finally { Glfw.Terminate(); }
        }
    }

    static class Installer
    {
        public static int Run(bool install)
        {
            string script = FindScript();
            if (script != null)
            {
                var psi = new ProcessStartInfo("bash", "\"" + script + "\" " + (install ? "--install" : "--uninstall")) { UseShellExecute = false };
                var p = Process.Start(psi);
                if (p == null) return 1;
                p.WaitForExit();
                return p.ExitCode;
            }
            Console.WriteLine(install
                ? "Run linux/install.sh --install from the source tree."
                : "Run linux/install.sh --uninstall from the source tree.");
            return 1;
        }

        static string FindScript()
        {
            string[] candidates = new string[] {
                Path.Combine(AppContext.BaseDirectory, "install.sh"),
                Path.Combine(Directory.GetCurrentDirectory(), "linux", "install.sh"),
                Path.Combine(Directory.GetCurrentDirectory(), "install.sh")
            };
            foreach (string p in candidates) if (File.Exists(p)) return p;
            return null;
        }
    }

    class Config
    {
        public string Quality = "auto"; public int Hud = 1; public bool Crt = false; public int Particles = 262144;
        public double QualityValue { get { double v; return Quality != "auto" && double.TryParse(Quality, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; } }
        static string FilePath { get { return Path.Combine(Paths.ConfigDir, "config.txt"); } }
        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int k = line.IndexOf('='); if (k <= 0) continue;
                        string key = line.Substring(0, k).Trim(), val = line.Substring(k + 1).Trim();
                        switch (key)
                        {
                            case "quality": c.Quality = val; break;
                            case "hud": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Hud); break;
                            case "crt": c.Crt = val == "1" || val == "true"; break;
                            case "particles": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Particles); break;
                        }
                    }
            }
            catch (Exception) { }
            return c;
        }
        public void Save()
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            File.WriteAllText(FilePath, "quality=" + Quality + "\nhud=" + Hud + "\ncrt=" + (Crt ? 1 : 0) + "\nparticles=" + Particles + "\n");
        }
    }
}
