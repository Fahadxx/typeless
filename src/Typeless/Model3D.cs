using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Typeless
{
    // In-memory 3D model shared by every reader and writer: millimeters, Z-up, world space
    // (transforms already applied), float32 positions as the formats store them.

    public readonly record struct Rgba(byte R, byte G, byte B, byte A)
    {
        public string Hex => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

        public override string ToString() => Hex;

        // 0..1 floats (OBJ Kd/d, glTF baseColorFactor) to bytes, clamped and rounded.
        public static Rgba FromUnit(double r, double g, double b, double a) =>
            new Rgba(ToByte(r), ToByte(g), ToByte(b), ToByte(a));

        private static byte ToByte(double v)
        {
            if (double.IsNaN(v)) return 0;
            return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class Part
    {
        public string Name { get; set; } = "";
        public float[] Positions { get; set; } = Array.Empty<float>();   // x, y, z per vertex
        public int[] Triangles { get; set; } = Array.Empty<int>();       // 3 vertex indices per triangle
        public Rgba? Color { get; set; }

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Triangles.Length / 3;
    }

    public sealed class Scene
    {
        public List<Part> Parts { get; } = new List<Part>();

        // What the reader or writer could not keep (textures, uvs, ...), for the log.
        public List<string> Dropped { get; } = new List<string>();

        public int TriangleCount
        {
            get
            {
                long n = 0;
                foreach (Part p in Parts) n += p.TriangleCount;
                return (int)Math.Min(n, int.MaxValue);
            }
        }

        public int VertexCount
        {
            get
            {
                long n = 0;
                foreach (Part p in Parts) n += p.VertexCount;
                return (int)Math.Min(n, int.MaxValue);
            }
        }

        public void Drop(string what)
        {
            if (!Dropped.Contains(what)) Dropped.Add(what);
        }

        // Bounding box over the vertices that triangles reference (unreferenced vertices do not count,
        // so a reader that keeps them and a writer that drops them still agree). All zero when empty.
        public (double[] Min, double[] Max) Bounds()
        {
            var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            bool any = false;
            foreach (Part p in Parts)
            {
                float[] pos = p.Positions;
                foreach (int v in p.Triangles)
                {
                    int o = v * 3;
                    for (int k = 0; k < 3; k++)
                    {
                        double c = pos[o + k];
                        if (c < min[k]) min[k] = c;
                        if (c > max[k]) max[k] = c;
                    }
                    any = true;
                }
            }
            if (!any) return (new double[3], new double[3]);
            return (min, max);
        }

        // "minx,miny,minz..maxx,maxy,maxz", values printed as their exact float32 (shortest round-trip).
        public string BoundsText()
        {
            (double[] min, double[] max) = Bounds();
            static string F(double d) => ((float)d).ToString("R", CultureInfo.InvariantCulture);
            return $"{F(min[0])},{F(min[1])},{F(min[2])}..{F(max[0])},{F(max[1])},{F(max[2])}";
        }

        // Appends a part from world-space double positions (cast once to float32) and triangle indices.
        // Triangles that reference a vertex out of range throw; empty parts are skipped.
        public void AddPart(string name, IReadOnlyList<double> xyz, IReadOnlyList<int> triangles, Rgba? color)
        {
            var pos = new float[xyz.Count];
            for (int i = 0; i < pos.Length; i++) pos[i] = (float)xyz[i];
            AddPart(name, pos, triangles, color);
        }

        public void AddPart(string name, IReadOnlyList<float> xyz, IReadOnlyList<int> triangles, Rgba? color)
        {
            if (triangles.Count < 3) return;
            if (triangles.Count % 3 != 0) throw new InvalidDataException("Triangle index count is not a multiple of 3.");
            int vertices = xyz.Count / 3;
            var tris = new int[triangles.Count];
            for (int i = 0; i < tris.Length; i++)
            {
                int v = triangles[i];
                if (v < 0 || v >= vertices) throw new InvalidDataException($"Vertex index {v} out of range (0..{vertices - 1}).");
                tris[i] = v;
            }
            float[] pos = xyz is float[] arr && arr.Length == vertices * 3 ? arr : CopyFloats(xyz, vertices * 3);
            Parts.Add(new Part { Name = name, Positions = pos, Triangles = tris, Color = color });
        }

        private static float[] CopyFloats(IReadOnlyList<float> src, int count)
        {
            var dst = new float[count];
            for (int i = 0; i < count; i++) dst[i] = src[i];
            return dst;
        }
    }

    // Merges vertices whose three float32 coordinates are bit-identical (and nothing else: -0 and +0
    // stay apart, no tolerance). Used by STL, which stores every triangle corner on its own.
    public sealed class VertexDedup
    {
        private readonly Dictionary<(int, int, int), int> index = new Dictionary<(int, int, int), int>();

        public List<float> Positions { get; } = new List<float>();

        public int Add(float x, float y, float z)
        {
            var key = (BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y), BitConverter.SingleToInt32Bits(z));
            if (index.TryGetValue(key, out int i)) return i;
            i = Positions.Count / 3;
            Positions.Add(x);
            Positions.Add(y);
            Positions.Add(z);
            index[key] = i;
            return i;
        }
    }
}
