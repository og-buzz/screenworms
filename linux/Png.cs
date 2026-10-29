// Minimal PNG writer: 8-bit RGBA, one zlib-compressed IDAT, no external imaging library.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ScreenWorm
{
    static class Png
    {
        static readonly uint[] Crc = BuildCrc();

        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static uint Crc32(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++) c = Crc[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        static void Be(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            byte[] t = Encoding.ASCII.GetBytes(type);
            Be(s, (uint)data.Length); s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            uint crc = Crc32(t, 0, 4);
            byte[] cat = new byte[4 + data.Length];
            Buffer.BlockCopy(t, 0, cat, 0, 4); Buffer.BlockCopy(data, 0, cat, 4, data.Length);
            Be(s, Crc32(cat, 0, cat.Length));
        }

        // rgba is top-down RGBA; flip converts a bottom-up GL framebuffer readback.
        public static void Save(string path, byte[] rgba, int w, int h, bool flip)
        {
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                {
                    var row = new byte[w * 4 + 1];
                    for (int y = 0; y < h; y++)
                    {
                        int sy = flip ? h - 1 - y : y;
                        row[0] = 0;
                        Buffer.BlockCopy(rgba, sy * w * 4, row, 1, w * 4);
                        for (int x = 0; x < w; x++) row[1 + x * 4 + 3] = 255;
                        z.Write(row, 0, row.Length);
                    }
                }
                byte[] idat = raw.ToArray();
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var f = File.Create(path))
                {
                    f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                    var ihdr = new byte[13];
                    ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
                    ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
                    ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                    Chunk(f, "IHDR", ihdr);
                    Chunk(f, "IDAT", idat);
                    Chunk(f, "IEND", new byte[0]);
                }
            }
        }
    }
}
