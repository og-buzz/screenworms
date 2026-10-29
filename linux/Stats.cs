// Linux system statistics: /proc, /sys, and DRM sysfs. Same Sampler surface as the Windows build.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ScreenWorm
{
    static class Paths
    {
        public static string Home
        {
            get
            {
                string h = Environment.GetEnvironmentVariable("HOME");
                return string.IsNullOrEmpty(h) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : h;
            }
        }
        public static string ConfigDir
        {
            get
            {
                string x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return Path.Combine(string.IsNullOrEmpty(x) ? Path.Combine(Home, ".config") : x, "screenworm");
            }
        }
        public static string StateDir
        {
            get
            {
                string x = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                return Path.Combine(string.IsNullOrEmpty(x) ? Path.Combine(Home, ".local", "state") : x, "screenworm");
            }
        }
    }

    class Sampler
    {
        public int Logical; public string CpuModel = "", OsName = ""; public ulong MemTotal; public volatile double[] LastCpu;
        public readonly List<List<int>> Siblings = new List<List<int>>(); public int[] Efficiency;
        readonly long[] prevIdle, prevBusy; long prevIoR, prevIoW, prevRx, prevTx, prevMs; bool haveBase;

        public Sampler()
        {
            Logical = Math.Max(1, CountCpus());
            prevIdle = new long[Logical]; prevBusy = new long[Logical]; LastCpu = new double[Logical]; Efficiency = new int[Logical];
            ReadTopology();
            CpuModel = ReadCpuModel();
            OsName = ReadOsName();
            ulong total; ReadMem(out total, out _); MemTotal = total;
            Collect();
        }

        static int CountCpus()
        {
            int n = 0;
            try
            {
                foreach (string line in File.ReadLines("/proc/stat"))
                    if (line.Length > 3 && line[0] == 'c' && line[1] == 'p' && line[2] == 'u' && char.IsDigit(line[3])) n++;
            }
            catch (Exception) { }
            return n > 0 ? n : Environment.ProcessorCount;
        }

        static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (Exception) { return ""; }
        }

        static long ReadLong(string path)
        {
            long v;
            return long.TryParse(ReadText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static long L(string[] fields, int index)
        {
            long v;
            return index < fields.Length && long.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static List<int> ParseCpuList(string s)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (string token in s.Trim().Split(','))
            {
                string t = token.Trim();
                if (t.Length == 0) continue;
                int dash = t.IndexOf('-');
                int a, b;
                if (dash > 0 && int.TryParse(t.Substring(0, dash), out a) && int.TryParse(t.Substring(dash + 1), out b))
                {
                    if (b < a) { int x = a; a = b; b = x; }
                    for (int i = a; i <= b; i++) list.Add(i);
                }
                else if (int.TryParse(t, out a)) list.Add(a);
            }
            return list;
        }

        void ReadTopology()
        {
            var groups = new Dictionary<string, List<int>>();
            var caps = new int[Logical];
            for (int i = 0; i < Logical; i++)
            {
                string basePath = "/sys/devices/system/cpu/cpu" + i.ToString(CultureInfo.InvariantCulture);
                var ids = ParseCpuList(ReadText(basePath + "/topology/thread_siblings_list"));
                ids.RemoveAll(delegate (int id) { return id < 0 || id >= Logical; });
                if (ids.Count == 0) ids.Add(i);
                ids.Sort();
                string key = string.Join(",", ids.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());
                if (!groups.ContainsKey(key)) groups[key] = ids;
                int c;
                if (int.TryParse(ReadText(basePath + "/cpu_capacity").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out c)) caps[i] = c;
            }
            Siblings.Clear();
            foreach (var kv in groups) Siblings.Add(kv.Value);
            Siblings.Sort(delegate (List<int> a, List<int> b) { return a[0].CompareTo(b[0]); });
            int max = 0, min = int.MaxValue;
            for (int i = 0; i < Logical; i++) if (caps[i] > 0) { if (caps[i] > max) max = caps[i]; if (caps[i] < min) min = caps[i]; }
            for (int i = 0; i < Logical; i++) Efficiency[i] = max > min && caps[i] > 0 && caps[i] < max ? 1 : 0;
        }

        static string ReadCpuModel()
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        int k = line.IndexOf(':');
                        if (k >= 0) return line.Substring(k + 1).Trim();
                    }
            }
            catch (Exception) { }
            return "CPU";
        }

        static string ReadOsName()
        {
            try
            {
                foreach (string line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                        return line.Substring(12).Trim().Trim('"');
            }
            catch (Exception) { }
            return Environment.OSVersion.ToString();
        }

        static void ReadMem(out ulong total, out ulong avail)
        {
            total = 0; avail = 0; ulong free = 0, buffers = 0, cached = 0;
            try
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    int k = line.IndexOf(':');
                    if (k < 0) continue;
                    string key = line.Substring(0, k);
                    string[] parts = line.Substring(k + 1).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    ulong v;
                    if (parts.Length == 0 || !ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) continue;
                    v *= 1024;
                    if (key == "MemTotal") total = v;
                    else if (key == "MemAvailable") avail = v;
                    else if (key == "MemFree") free = v;
                    else if (key == "Buffers") buffers = v;
                    else if (key == "Cached") cached = v;
                }
            }
            catch (Exception) { }
            if (avail == 0) avail = free + buffers + cached;
        }

        void ReadCpu(long[] idle, long[] busy)
        {
            for (int i = 0; i < Logical; i++) { idle[i] = 0; busy[i] = 0; }
            try
            {
                int idx = 0;
                foreach (string line in File.ReadLines("/proc/stat"))
                {
                    if (line.Length < 4 || line[0] != 'c' || line[1] != 'p' || line[2] != 'u' || !char.IsDigit(line[3])) continue;
                    if (idx >= Logical) break;
                    string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    long idleT = L(f, 4) + L(f, 5);
                    long total = L(f, 1) + L(f, 2) + L(f, 3) + L(f, 4) + L(f, 5) + L(f, 6) + L(f, 7) + L(f, 8);
                    idle[idx] = idleT; busy[idx] = total - idleT; idx++;
                }
            }
            catch (Exception) { }
        }

        static readonly Regex WholeDisk = new Regex("^(sd[a-z]+|hd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+)$", RegexOptions.Compiled);

        static void ReadIo(out long rd, out long wr)
        {
            rd = 0; wr = 0;
            try
            {
                foreach (string line in File.ReadLines("/proc/diskstats"))
                {
                    string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 10 || !WholeDisk.IsMatch(f[2])) continue;
                    rd += L(f, 5) * 512;
                    wr += L(f, 9) * 512;
                }
            }
            catch (Exception) { }
        }

        static void ReadNet(out long rx, out long tx)
        {
            rx = 0; tx = 0;
            try
            {
                foreach (string line in File.ReadLines("/proc/net/dev"))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    if (name == "lo" || name.StartsWith("veth") || name.StartsWith("docker") || name.StartsWith("br-") ||
                        name.StartsWith("virbr") || name.StartsWith("podman") || name.StartsWith("cni")) continue;
                    string[] f = line.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 9) continue;
                    rx += L(f, 0);
                    tx += L(f, 8);
                }
            }
            catch (Exception) { }
        }

        static void ReadProcs(out int procs, out int threads)
        {
            procs = 0; threads = 0;
            try
            {
                foreach (string dir in Directory.EnumerateDirectories("/proc"))
                {
                    int pid;
                    if (!int.TryParse(Path.GetFileName(dir), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) continue;
                    procs++;
                    try
                    {
                        foreach (string line in File.ReadLines(Path.Combine(dir, "status")))
                            if (line.StartsWith("Threads:", StringComparison.Ordinal))
                            {
                                int t;
                                if (int.TryParse(line.Substring(8).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) threads += t;
                                break;
                            }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        static double ReadGpu()
        {
            int best = -1;
            try
            {
                foreach (string dir in Directory.GetDirectories("/sys/class/drm"))
                {
                    string name = Path.GetFileName(dir);
                    if (!name.StartsWith("card", StringComparison.Ordinal)) continue;
                    string path = Path.Combine(dir, "device", "gpu_busy_percent");
                    if (!File.Exists(path)) continue;
                    int v;
                    if (int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > best) best = v;
                }
            }
            catch (Exception) { }
            return best < 0 ? -1 : Math.Max(0, Math.Min(1, best / 100.0));
        }

        void ReadPerf(StatSample c)
        {
            var perf = new double[Logical];
            bool any = false; double sum = 0; int n = 0;
            for (int i = 0; i < Logical; i++)
            {
                string dir = "/sys/devices/system/cpu/cpu" + i.ToString(CultureInfo.InvariantCulture) + "/cpufreq/";
                long cur = ReadLong(dir + "scaling_cur_freq");
                if (cur == 0) cur = ReadLong(dir + "cpuinfo_cur_freq");
                long bas = ReadLong(dir + "base_frequency");
                if (bas == 0) bas = ReadLong(dir + "cpuinfo_max_freq");
                if (cur > 0 && bas > 0)
                {
                    double p = cur / (double)bas;
                    perf[i] = Math.Max(0, Math.Min(2, p)); any = true; sum += perf[i]; n++;
                }
                else perf[i] = 1;
            }
            if (any) { c.Perf = perf; c.PerfTotal = n > 0 ? sum / n : 1; }
        }

        static double ReadUptime()
        {
            try
            {
                string s = ReadText("/proc/uptime");
                int k = s.IndexOf(' ');
                double v;
                if (double.TryParse(k > 0 ? s.Substring(0, k) : s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            }
            catch (Exception) { }
            return 0;
        }

        public string TopologyJson()
        {
            var sb = new StringBuilder("{\"physical\":").Append(Siblings.Count).Append(",\"siblings\":[");
            for (int i = 0; i < Siblings.Count; i++) { if (i > 0) sb.Append(','); sb.Append('[').Append(string.Join(",", Siblings[i].ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())).Append(']'); }
            sb.Append("],\"efficiency\":[");
            for (int i = 0; i < Logical; i++) { if (i > 0) sb.Append(','); sb.Append(Efficiency[i]); }
            return sb.Append("]}").ToString();
        }

        public string InfoJson()
        {
            return "{\"type\":\"info\",\"cores\":" + Logical + ",\"cpuModel\":" + Json.S(CpuModel) + ",\"host\":" + Json.S(Environment.MachineName) + ",\"os\":" + Json.S(OsName) + ",\"memTotal\":" + MemTotal + ",\"topology\":" + TopologyJson() + "}";
        }

        public StatSample Collect()
        {
            long now = Json.NowMs();
            var idle = new long[Logical]; var busy = new long[Logical];
            ReadCpu(idle, busy);
            var c = new StatSample { Cpu = new double[Logical], MemTotal = MemTotal };
            double dt = haveBase ? Math.Max(0.001, (now - prevMs) / 1000.0) : 1;
            double sum = 0;
            for (int i = 0; i < Logical; i++)
            {
                long di = idle[i] - prevIdle[i], db = busy[i] - prevBusy[i];
                double u = haveBase && (di + db) > 0 ? (double)db / (di + db) : 0;
                c.Cpu[i] = Math.Max(0, Math.Min(1, u)); sum += c.Cpu[i];
                prevIdle[i] = idle[i]; prevBusy[i] = busy[i];
            }
            c.CpuTotal = Logical > 0 ? sum / Logical : 0;
            LastCpu = c.Cpu;
            ulong total, avail; ReadMem(out total, out avail);
            c.MemAvail = avail; c.MemUsed = total > 0 ? 1.0 - (double)avail / total : 0;
            if (MemTotal == 0) MemTotal = total;
            long rd, wr; ReadIo(out rd, out wr);
            long rx, tx; ReadNet(out rx, out tx);
            if (haveBase) { c.Read = Math.Max(0, (rd - prevIoR) / dt); c.Write = Math.Max(0, (wr - prevIoW) / dt); c.Rx = Math.Max(0, (rx - prevRx) / dt); c.Tx = Math.Max(0, (tx - prevTx) / dt); }
            prevIoR = rd; prevIoW = wr; prevRx = rx; prevTx = tx;
            int p, t; ReadProcs(out p, out t); c.Procs = p; c.Threads = t;
            c.Gpu = ReadGpu();
            ReadPerf(c);
            c.Uptime = ReadUptime();
            prevMs = now; haveBase = true;
            return c;
        }

        public string SampleJson(int seq, long t, bool withType)
        {
            var c = Collect();
            var sb = new StringBuilder(withType ? "{\"type\":\"sample\"," : "{");
            sb.Append("\"seq\":").Append(seq).Append(",\"t\":").Append(t).Append(",\"cpu\":[");
            for (int i = 0; i < c.Cpu.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Json.N(c.Cpu[i])); }
            sb.Append("],\"cpuTotal\":").Append(Json.N(c.CpuTotal));
            if (c.Perf != null) { sb.Append(",\"perf\":["); for (int i = 0; i < c.Perf.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Json.N(c.Perf[i])); } sb.Append("],\"perfTotal\":").Append(Json.N(c.PerfTotal)); }
            if (c.Gpu >= 0) sb.Append(",\"gpu\":").Append(Json.N(c.Gpu));
            sb.Append(",\"mem\":{\"used\":").Append(Json.N(c.MemUsed)).Append(",\"total\":").Append(MemTotal).Append(",\"avail\":").Append(Json.N(c.MemAvail)).Append('}');
            sb.Append(",\"net\":{\"rx\":").Append(Json.N(c.Rx)).Append(",\"tx\":").Append(Json.N(c.Tx)).Append('}');
            sb.Append(",\"disk\":{\"read\":").Append(Json.N(c.Read)).Append(",\"write\":").Append(Json.N(c.Write)).Append('}');
            sb.Append(",\"sys\":{\"procs\":").Append(c.Procs).Append(",\"threads\":").Append(c.Threads).Append(",\"uptime\":").Append(Json.N(c.Uptime)).Append("}}");
            return sb.ToString();
        }
    }

    static class StatsCli
    {
        [DllImport("libc")] static extern IntPtr signal(int signum, IntPtr handler);

        public static int Run(Args args)
        {
            try { signal(13, IntPtr.Zero); } catch (Exception) { }
            int limit = Math.Max(0, args.Int("seconds", 0));
            var s = new Sampler();
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            try
            {
                stdout.WriteLine(s.InfoJson());
                long t0 = Json.NowMs(); int seq = 0;
                while (limit == 0 || seq < limit)
                {
                    long due = t0 + (long)(seq + 1) * 1000;
                    long wait = due - Json.NowMs(); if (wait > 0) Thread.Sleep((int)wait);
                    stdout.WriteLine(s.SampleJson(seq++, due, true));
                }
                return 0;
            }
            catch (IOException) { return 0; }
            catch (ObjectDisposedException) { return 0; }
        }
    }

    class MonitorInfo { public int X, Y, W, H, Dpi = 96; public bool Primary; public string Name; }

    static class Monitors
    {
        public static List<MonitorInfo> Detect()
        {
            var list = new List<MonitorInfo>();
            var wins = new List<IntPtr>();
            try
            {
                Glfw.Init();
                int count; IntPtr monitors = Glfw.GetMonitors(out count);
                IntPtr primary = Glfw.GetPrimaryMonitor();
                Glfw.WindowHint(Glfw.CLIENT_API, Glfw.NO_API);
                Glfw.WindowHint(Glfw.VISIBLE, Glfw.FALSE);
                for (int i = 0; i < count; i++)
                {
                    IntPtr m = Marshal.ReadIntPtr(monitors, i * IntPtr.Size);
                    int x, y; Glfw.GetMonitorPos(m, out x, out y);
                    float sx, sy; Glfw.GetMonitorContentScale(m, out sx, out sy);
                    int w = 0, h = 0;
                    IntPtr win = Glfw.CreateWindow(640, 480, "ScreenWorm", m, IntPtr.Zero);
                    if (win != IntPtr.Zero) { Glfw.GetWindowSize(win, out w, out h); wins.Add(win); }
                    if (w <= 0 || h <= 0) { int wx, wy; Glfw.GetMonitorWorkarea(m, out wx, out wy, out w, out h); x = wx; y = wy; }
                    list.Add(new MonitorInfo { X = x, Y = y, W = w, H = h, Primary = m == primary, Name = Glfw.MonitorName(m), Dpi = (int)Math.Round(96.0 * Math.Max(sx, sy)) });
                }
            }
            catch (Exception ex) { Log.Write("monitors: " + ex.Message); }
            finally
            {
                foreach (var w in wins) Glfw.DestroyWindow(w);
                if (wins.Count > 0) Glfw.Terminate();
            }
            list.Sort(delegate (MonitorInfo a, MonitorInfo b) { if (a.Primary != b.Primary) return a.Primary ? -1 : 1; int c = a.X.CompareTo(b.X); return c != 0 ? c : a.Y.CompareTo(b.Y); });
            return list;
        }

        public static string ToJson(List<MonitorInfo> screens)
        {
            var sb = new StringBuilder("{\"screens\":[");
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"x\":").Append(s.X).Append(",\"y\":").Append(s.Y).Append(",\"w\":").Append(s.W).Append(",\"h\":").Append(s.H).Append(",\"dpi\":").Append(s.Dpi)
                  .Append(",\"primary\":").Append(s.Primary ? "true" : "false").Append(",\"name\":").Append(Json.S(s.Name)).Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }

    static class Json
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        public static string N(double v) { if (double.IsNaN(v) || double.IsInfinity(v)) v = 0; return v.ToString("0.#####", CultureInfo.InvariantCulture); }
        public static string S(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in s) { if (c == '"' || c == '\\') sb.Append('\\').Append(c); else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); }
            return sb.Append('"').ToString();
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static string Dir { get { return Paths.StateDir; } }
        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Dir);
                    string f = Path.Combine(Dir, "screenworm.log");
                    if (File.Exists(f) && new FileInfo(f).Length > 1 << 20) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\n");
                }
            }
            catch (Exception) { }
        }
    }
}
