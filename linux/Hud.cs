// Legend overlay rendered into a BGRA texture with FreeType. Same layout and data as the Windows HUD.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenWorm
{
    static class Ft
    {
        const string Lib = "libfreetype.so.6";
        public const int LOAD_RENDER = 4;

        [DllImport(Lib)] public static extern int FT_Init_FreeType(out IntPtr library);
        [DllImport(Lib)] public static extern int FT_Done_FreeType(IntPtr library);
        [DllImport(Lib)] public static extern int FT_New_Face(IntPtr library, string path, long index, out IntPtr face);
        [DllImport(Lib)] public static extern int FT_Done_Face(IntPtr face);
        [DllImport(Lib)] public static extern int FT_Set_Pixel_Sizes(IntPtr face, uint width, uint height);
        [DllImport(Lib)] public static extern int FT_Load_Char(IntPtr face, ulong charcode, int flags);

        public static IntPtr Glyph(IntPtr face) { return Marshal.ReadIntPtr(face, 152); }
        public static int Rows(IntPtr glyph) { return Marshal.ReadInt32(glyph, 152); }
        public static int Width(IntPtr glyph) { return Marshal.ReadInt32(glyph, 156); }
        public static int Pitch(IntPtr glyph) { return Marshal.ReadInt32(glyph, 160); }
        public static IntPtr Buffer(IntPtr glyph) { return Marshal.ReadIntPtr(glyph, 168); }
        public static int Left(IntPtr glyph) { return Marshal.ReadInt32(glyph, 192); }
        public static int Top(IntPtr glyph) { return Marshal.ReadInt32(glyph, 196); }
        public static int PixelMode(IntPtr glyph) { return Marshal.ReadByte(glyph, 178); }
        public static int Advance(IntPtr glyph) { return (int)(Marshal.ReadInt64(glyph, 128) >> 6); }
    }

    class Glyph
    {
        public int W, H, Left, Top, Advance; public byte[] Cov = new byte[0];
    }

    class FontFace
    {
        public IntPtr Face; public int Size; public int Ascent, LineH;
        readonly Dictionary<long, Glyph> cache = new Dictionary<long, Glyph>();
        public FontFace(IntPtr lib, string path, int px)
        {
            Size = px;
            if (Ft.FT_New_Face(lib, path, 0, out Face) != 0) Face = IntPtr.Zero;
            if (Face != IntPtr.Zero) Ft.FT_Set_Pixel_Sizes(Face, 0, (uint)px);
            Ascent = (int)Math.Round(px * 0.78); LineH = (int)Math.Round(px * 1.30);
        }
        public Glyph Get(long cp)
        {
            Glyph g;
            if (cache.TryGetValue(cp, out g)) return g;
            g = new Glyph();
            cache[cp] = g;
            if (Face == IntPtr.Zero || Ft.FT_Load_Char(Face, (ulong)cp, Ft.LOAD_RENDER) != 0) return g;
            IntPtr slot = Ft.Glyph(Face); int w = Ft.Width(slot), h = Ft.Rows(slot), pitch = Ft.Pitch(slot);
            g.W = w; g.H = h; g.Left = Ft.Left(slot); g.Top = Ft.Top(slot); g.Advance = Ft.Advance(slot);
            IntPtr buf = Ft.Buffer(slot);
            if (w <= 0 || h <= 0 || buf == IntPtr.Zero) return g;
            g.Cov = new byte[w * h];
            int mode = Ft.PixelMode(slot);
            if (mode == 1)
            {
                int stride = (w + 7) >> 3;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int bit = Marshal.ReadByte(buf, y * pitch + (x >> 3));
                        if ((bit & (0x80 >> (x & 7))) != 0) g.Cov[y * w + x] = 255;
                    }
            }
            else if (mode == 2 || mode == 3)
            {
                int copy = Math.Min(w, Math.Abs(pitch));
                for (int y = 0; y < h; y++)
                    Marshal.Copy(new IntPtr(buf.ToInt64() + (long)y * pitch), g.Cov, y * w, copy);
            }
            return g;
        }
    }

    class HudTexture
    {
        public uint Tex; public int W, H; public int Mode;
        public bool Visible { get { return Mode > 0 && Tex != 0 && builtMode == Mode; } }
        readonly double sc; readonly string host, cpuModel, os; readonly int cores; readonly uint[] colors; int maxW;
        readonly FontFace font, bold; bool ready; int builtMode = -1;
        byte[] pixels = new byte[0]; long lastMs = -10000; public bool Force;

        public HudTexture(int maxWidth, double scale, string host, string cpuModel, string os, Sim sim, int mode)
        {
            maxW = Math.Max(64, maxWidth); sc = Math.Max(0.5, scale); this.host = host; this.cpuModel = cpuModel; this.os = os; cores = sim.Cores; Mode = mode;
            colors = new uint[cores];
            for (int i = 0; i < cores; i++) { var w = sim.Worms[i]; colors[i] = Rgba((byte)C(w.R), (byte)C(w.G), (byte)C(w.B), 255); }
            try
            {
                IntPtr lib;
                if (Ft.FT_Init_FreeType(out lib) != 0) throw new Exception("FT_Init_FreeType failed");
                int px = Math.Max(8, (int)Math.Round(11.5 * this.sc));
                font = new FontFace(lib, FindFont(false), px);
                bold = new FontFace(lib, FindFont(true), px);
                ready = font.Face != IntPtr.Zero;
                GL.glGenTextures(1, out Tex);
            }
            catch (Exception ex) { Log.Write("HUD unavailable: " + ex.Message); ready = false; Tex = 0; }
        }

        static int C(float v) { return Math.Max(0, Math.Min(255, (int)Math.Round(v * 255))); }
        static uint Rgba(byte r, byte g, byte b, byte a) { return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b; }
        static byte A(uint c) { return (byte)(c >> 24); }
        static byte R(uint c) { return (byte)(c >> 16); }
        static byte G(uint c) { return (byte)(c >> 8); }
        static byte B(uint c) { return (byte)c; }

        static readonly uint Bright = Rgba(232, 240, 255, 235), Fg = Rgba(200, 216, 255, 185), Accent = Rgba(127, 215, 255, 235), Dim = Rgba(200, 216, 255, 120);

        static string FindFont(bool boldFace)
        {
            string[] names = boldFace
                ? new[] { "DejaVuSansMono-Bold.ttf", "DejaVuSans-Bold.ttf", "LiberationMono-Bold.ttf", "NotoSansMono-Bold.ttf", "NotoSans-Bold.ttf" }
                : new[] { "DejaVuSansMono.ttf", "DejaVuSans.ttf", "LiberationMono-Regular.ttf", "NotoSansMono-Regular.ttf", "NotoSans-Regular.ttf" };
            string[] dirs = { "/usr/share/fonts/TTF", "/usr/share/fonts/truetype", "/usr/share/fonts/dejavu", "/usr/share/fonts/liberation", "/usr/share/fonts/noto", "/usr/share/fonts" };
            foreach (string d in dirs)
                foreach (string n in names)
                {
                    string p = Path.Combine(d, n);
                    if (File.Exists(p)) return p;
                }
            foreach (string d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                try
                {
                    foreach (string p in Directory.EnumerateFiles(d, "*.ttf", SearchOption.AllDirectories))
                        if ((boldFace && p.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0) || (!boldFace && p.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) < 0)) return p;
                }
                catch (Exception) { }
            }
            return "/usr/share/fonts/TTF/DejaVuSans.ttf";
        }

        int StripH { get { return (int)Math.Round(38 * sc); } }
        int PanelH { get { int rows = (cores + 7) / 8; return (int)Math.Round((64 + rows * 18) * sc); } }

        void Rebuild()
        {
            builtMode = Mode;
            W = Math.Min(maxW, (int)Math.Round(1900 * sc)); H = StripH + (Mode == 2 ? PanelH + (int)Math.Round(12 * sc) : 0);
            if (W < 4) W = 4; if (H < 4) H = 4;
            pixels = new byte[W * H * 4];
            GL.glBindTexture(GL.TEXTURE_2D, Tex);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR); GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE); GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
            GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, W, H, 0, GL.BGRA, GL.UNSIGNED_BYTE, IntPtr.Zero);
            lastMs = -10000;
        }

        void Clear() { Array.Clear(pixels, 0, pixels.Length); }

        void Blend(int x, int y, uint color, double cover)
        {
            if (x < 0 || y < 0 || x >= W || y >= H || cover <= 0) return;
            if (cover > 1) cover = 1;
            int i = (y * W + x) * 4;
            double sa = (A(color) / 255.0) * cover;
            if (sa <= 0) return;
            double da = pixels[i + 3] / 255.0;
            double oa = sa + da * (1 - sa);
            if (oa <= 0) return;
            pixels[i] = (byte)Math.Round((B(color) * sa + pixels[i] * da * (1 - sa)) / oa);
            pixels[i + 1] = (byte)Math.Round((G(color) * sa + pixels[i + 1] * da * (1 - sa)) / oa);
            pixels[i + 2] = (byte)Math.Round((R(color) * sa + pixels[i + 2] * da * (1 - sa)) / oa);
            pixels[i + 3] = (byte)Math.Round(oa * 255);
        }

        void FillRect(int x, int y, int w, int h, uint color)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(W, x + w), y1 = Math.Min(H, y + h);
            for (int py = y0; py < y1; py++) for (int px = x0; px < x1; px++) Blend(px, py, color, 1);
        }

        void StrokeRect(int x, int y, int w, int h, uint color)
        {
            FillRect(x, y, w, 1, color); FillRect(x, y + h - 1, w, 1, color);
            FillRect(x, y, 1, h, color); FillRect(x + w - 1, y, 1, h, color);
        }

        void FillCircle(int cx, int cy, int r, uint color)
        {
            for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++)
            {
                double d = Math.Sqrt(x * x + y * y);
                double cov = Math.Max(0, Math.Min(1, r + 0.5 - d));
                Blend(cx + x, cy + y, color, cov);
            }
        }

        int Text(string text, int x, int yTop, uint color, bool boldFace)
        {
            var f = boldFace && bold != null && bold.Face != IntPtr.Zero ? bold : font;
            if (f == null || f.Face == IntPtr.Zero) return x;
            int pen = x, baseline = yTop + f.Ascent;
            for (int i = 0; i < text.Length; i++)
            {
                int cp;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
                else cp = text[i];
                Glyph g = f.Get(cp);
                if (g.Cov.Length > 0)
                {
                    int gx = pen + g.Left, gy = baseline - g.Top;
                    for (int yy = 0; yy < g.H; yy++) for (int xx = 0; xx < g.W; xx++)
                    {
                        byte c = g.Cov[yy * g.W + xx];
                        if (c != 0) Blend(gx + xx, gy + yy, color, c / 255.0);
                    }
                }
                pen += g.Advance;
            }
            return pen;
        }

        static string Rate(double b)
        {
            if (b < 1024) return b.ToString("0", CultureInfo.InvariantCulture) + " B/s";
            if (b < 1048576) return (b / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " kB/s";
            if (b < 1073741824) return (b / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            return (b / 1073741824).ToString("0.00", CultureInfo.InvariantCulture) + " GB/s";
        }
        static string Up(double s)
        {
            long t = (long)Math.Max(0, s); long d = t / 86400, h = t % 86400 / 3600, m = t % 3600 / 60;
            return d > 0 ? d + "d " + h + "h " + m + "m" : h > 0 ? h + "h " + m + "m" : m + "m";
        }
        static string Pct(double v) { return (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; }

        void Draw(Sim sim, double fps, int particles, double quality, string label, string renderer)
        {
            Clear();
            var st = sim.Stats; var smp = sim.LastSample; float s = (float)sc;
            int y0 = H - StripH;
            for (int y = 0; y < StripH; y++)
            {
                double a = 160.0 * y / Math.Max(1, StripH - 1);
                FillRect(0, y0 + y, W, 1, Rgba(2, 4, 12, (byte)a));
            }
            int lineTop = y0 + (StripH - font.LineH) / 2;
            int gap = (int)Math.Round(18 * s), x = (int)Math.Round(16 * s);
            int yTop = lineTop;
            x = Text("SCREEN", x, yTop, Bright, true); x = Text("WORM", x, yTop, Accent, true) + gap;
            x = Text(host, x, yTop, Bright, false) + gap;
            if (smp.Uptime > 0) x = Text("up " + Up(smp.Uptime), x, yTop, Dim, false) + gap;
            int barW = Math.Max(1, (int)Math.Round(3 * s)), bgap = Math.Max(1, (int)Math.Round(2 * s));
            int barH = (int)Math.Round(14 * s), by = y0 + (StripH + barH) / 2;
            for (int i = 0; i < cores; i++)
            {
                int bx = x + i * (barW + bgap);
                FillRect(bx, by - barH, barW, barH, Rgba(255, 255, 255, 16));
                double u = sim.Worms[i].Util;
                int h = Math.Max(1, (int)(u * barH));
                FillRect(bx, by - h, barW, h, Rgba(R(colors[i]), G(colors[i]), B(colors[i]), (byte)(110 + 145 * u)));
            }
            x += cores * (barW + bgap) + gap;
            x = Text("cpu " + Pct(st.Cpu), x, yTop, Fg, false) + gap;
            x = Text("mem " + Pct(st.Mem), x, yTop, Fg, false) + gap;
            x = Text("net \u2193" + Rate(st.Rx) + " \u2191" + Rate(st.Tx), x, yTop, Fg, false) + gap;
            x = Text("disk r " + Rate(st.Read) + " w " + Rate(st.Write), x, yTop, Fg, false) + gap;
            if (sim.HasGpu) x = Text("gpu " + Pct(st.Gpu), x, yTop, Fg, false) + gap;
            if (st.Procs > 0) Text(((int)st.Procs) + " procs \u00b7 " + ((int)st.Threads) + " threads", x, yTop, Dim, false);

            if (Mode == 2)
            {
                int px = (int)Math.Round(16 * s), py = (int)Math.Round(6 * s);
                int pw = Math.Min(W - (int)Math.Round(32 * s), (int)Math.Round((8 * 118 + 24) * s)), ph = PanelH;
                FillRect(px, py, pw, ph, Rgba(4, 8, 20, 165));
                StrokeRect(px, py, pw, ph, Rgba(120, 160, 255, 50));
                int cy = py + (int)Math.Round(10 * s);
                Text(cpuModel + " \u00b7 " + os, px + (int)Math.Round(12 * s), cy, Bright, false); cy += (int)Math.Round(22 * s);
                double colW = (pw - 24 * s) / 8.0;
                for (int i = 0; i < cores; i++)
                {
                    int cx = px + (int)Math.Round(12 * s + (i % 8) * colW), ry = cy + (i / 8) * (int)Math.Round(18 * s);
                    FillCircle(cx + (int)Math.Round(4 * s), ry + (int)Math.Round(7 * s), (int)Math.Round(4 * s), colors[i]);
                    Text("C" + i.ToString("00", CultureInfo.InvariantCulture) + " " + Pct(sim.Worms[i].Util).PadLeft(4), cx + (int)Math.Round(13 * s), ry, Fg, false);
                }
                cy += ((cores + 7) / 8) * (int)Math.Round(18 * s) + (int)Math.Round(8 * s);
                Text(fps.ToString("0", CultureInfo.InvariantCulture) + " fps \u00b7 quality " + quality.ToString("0.00", CultureInfo.InvariantCulture) + " \u00b7 " +
                     particles.ToString("N0", CultureInfo.InvariantCulture) + " particles \u00b7 " + label + " \u00b7 " + renderer,
                     px + (int)Math.Round(12 * s), cy, Dim, false);
            }
        }

        public void SetMaxWidth(int width)
        {
            int w = Math.Max(64, width);
            if (w == maxW) return;
            maxW = w;
            builtMode = -1;
        }

        public void Update(Sim sim, double fps, int particles, double quality, string label, string renderer)
        {
            if (Mode == 0 || !ready || Tex == 0) return;
            if (builtMode != Mode) Rebuild();
            long now = Json.NowMs();
            if (!Force && now - lastMs < 250) return;
            lastMs = now;
            Draw(sim, fps, particles, quality, label, renderer);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                GL.glBindTexture(GL.TEXTURE_2D, Tex); GL.glPixelStorei(GL.UNPACK_ALIGNMENT, 4);
                GL.glTexSubImage2D(GL.TEXTURE_2D, 0, 0, 0, W, H, GL.BGRA, GL.UNSIGNED_BYTE, handle.AddrOfPinnedObject());
            }
            finally { handle.Free(); }
        }
    }
}
