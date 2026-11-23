using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CsdBridge.Core.Rendering
{
    internal readonly struct Rgb
    {
        public Rgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public static Rgb Hex(int rgb) => new Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>
    /// A small software canvas: lines and filled polygons on a white background, drawn at
    /// twice the final size and averaged down for smooth edges, written out as a PNG.
    /// No dependencies, so it works the same inside BricsCAD, in the CLI and in tests.
    /// </summary>
    internal sealed class Canvas
    {
        private const int Oversample = 2;

        private readonly int _w, _h; // oversampled size
        private readonly byte[] _rgb;

        public Canvas(int width, int height)
        {
            Width = width;
            Height = height;
            _w = width * Oversample;
            _h = height * Oversample;
            _rgb = new byte[_w * _h * 3];
            for (int i = 0; i < _rgb.Length; i++)
            {
                _rgb[i] = 255;
            }
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>A line between two points in picture pixels; <paramref name="width"/> in pixels.</summary>
        public void Line(double x0, double y0, double x1, double y1, Rgb color, double width = 1)
        {
            double half = Math.Max(0.5, width * Oversample / 2);
            x0 *= Oversample;
            y0 *= Oversample;
            x1 *= Oversample;
            y1 *= Oversample;
            if (!Clip(ref x0, ref y0, ref x1, ref y1, -half - 1, -half - 1, _w + half + 1, _h + half + 1))
            {
                return;
            }

            double dx = x1 - x0, dy = y1 - y0;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) / 0.75));
            int reach = (int)Math.Ceiling(half);
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                double cx = x0 + dx * t, cy = y0 + dy * t;
                int px0 = (int)Math.Floor(cx - half + 0.5), py0 = (int)Math.Floor(cy - half + 0.5);
                for (int py = py0; py < py0 + 2 * reach && py < _h; py++)
                {
                    if (py < 0)
                    {
                        continue;
                    }

                    for (int px = px0; px < px0 + 2 * reach && px < _w; px++)
                    {
                        if (px >= 0)
                        {
                            int i = (py * _w + px) * 3;
                            _rgb[i] = color.R;
                            _rgb[i + 1] = color.G;
                            _rgb[i + 2] = color.B;
                        }
                    }
                }
            }
        }

        /// <summary>Fill a closed polygon (picture pixels).</summary>
        public void Fill(IReadOnlyList<(double X, double Y)> polygon, Rgb color)
        {
            if (polygon.Count < 3)
            {
                return;
            }

            double minY = double.MaxValue, maxY = double.MinValue;
            foreach ((double _, double y) in polygon)
            {
                minY = Math.Min(minY, y * Oversample);
                maxY = Math.Max(maxY, y * Oversample);
            }

            int rowFrom = Math.Max(0, (int)Math.Floor(minY)), rowTo = Math.Min(_h - 1, (int)Math.Ceiling(maxY));
            var crossings = new List<double>();
            for (int row = rowFrom; row <= rowTo; row++)
            {
                double y = row + 0.5;
                crossings.Clear();
                for (int i = 0; i < polygon.Count; i++)
                {
                    (double ax, double ay) = polygon[i];
                    (double bx, double by) = polygon[(i + 1) % polygon.Count];
                    ax *= Oversample;
                    ay *= Oversample;
                    bx *= Oversample;
                    by *= Oversample;
                    if ((ay <= y && by > y) || (by <= y && ay > y))
                    {
                        crossings.Add(ax + (y - ay) / (by - ay) * (bx - ax));
                    }
                }

                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int from = Math.Max(0, (int)Math.Ceiling(crossings[k] - 0.5));
                    int to = Math.Min(_w - 1, (int)Math.Floor(crossings[k + 1] - 0.5));
                    for (int px = from; px <= to; px++)
                    {
                        int i = (row * _w + px) * 3;
                        _rgb[i] = color.R;
                        _rgb[i + 1] = color.G;
                        _rgb[i + 2] = color.B;
                    }
                }
            }
        }

        /// <summary>The picture as an 8-bit RGB PNG.</summary>
        public byte[] ToPng()
        {
            // Average each 2 x 2 block down to one pixel; each row starts with filter type 0.
            int stride = Width * 3 + 1;
            byte[] raw = new byte[stride * Height];
            for (int y = 0; y < Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < Width; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = 0;
                        for (int oy = 0; oy < Oversample; oy++)
                        {
                            for (int ox = 0; ox < Oversample; ox++)
                            {
                                sum += _rgb[((y * Oversample + oy) * _w + x * Oversample + ox) * 3 + c];
                            }
                        }

                        raw[row + 1 + x * 3 + c] = (byte)(sum / (Oversample * Oversample));
                    }
                }
            }

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                byte[] header = new byte[13];
                WriteInt(header, 0, Width);
                WriteInt(header, 4, Height);
                header[8] = 8;  // bits per channel
                header[9] = 2;  // colour type: RGB
                Chunk(png, "IHDR", header);
                Chunk(png, "IDAT", Zlib(raw));
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static byte[] Zlib(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                uint a = 1, b = 0;
                foreach (byte value in data)
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }

                byte[] adler = new byte[4];
                WriteInt(adler, 0, (int)((b << 16) | a));
                output.Write(adler, 0, 4);
                return output.ToArray();
            }
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            byte[] length = new byte[4];
            WriteInt(length, 0, data.Length);
            png.Write(length, 0, 4);

            byte[] body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++)
            {
                body[i] = (byte)type[i];
            }

            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            png.Write(body, 0, body.Length);

            byte[] crc = new byte[4];
            WriteInt(crc, 0, (int)Crc32(body));
            png.Write(crc, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }

        private static void WriteInt(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        /// <summary>Liang-Barsky: clip a segment to a rectangle. False when none of it is inside.</summary>
        private static bool Clip(ref double x0, ref double y0, ref double x1, ref double y1, double minX, double minY, double maxX, double maxY)
        {
            double dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - minX, maxX - x0, y0 - minY, maxY - y0 };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0)
                    {
                        return false;
                    }
                }
                else
                {
                    double t = q[i] / p[i];
                    if (p[i] < 0)
                    {
                        t0 = Math.Max(t0, t);
                    }
                    else
                    {
                        t1 = Math.Min(t1, t);
                    }
                }
            }

            if (t0 > t1)
            {
                return false;
            }

            double nx0 = x0 + t0 * dx, ny0 = y0 + t0 * dy;
            x1 = x0 + t1 * dx;
            y1 = y0 + t1 * dy;
            x0 = nx0;
            y0 = ny0;
            return true;
        }
    }
}
