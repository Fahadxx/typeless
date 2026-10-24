using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Typeless
{
    // STL: binary or ASCII in, binary out. Use through ModelBackend.Read / Write.
    internal static class Stl
    {
        public static Scene Read(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            var scene = new Scene();

            // Binary vs ASCII by the size check, not the "solid" prefix (binary headers often say "solid").
            long n = data.Length >= 84 ? (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80, 4)) : -1L;
            if (n >= 0 && 84 + 50 * n == data.Length)
            {
                ReadBinary(data, (int)n, stem, scene);
                return scene;
            }

            ReadAscii(data, stem, scene);
            if (scene.TriangleCount == 0 && n > 0 && 84 + 50 * n < data.Length)
            {
                // Not ASCII after all: a binary file with trailing bytes. Read the declared triangles.
                Log.Write($"STL: {Path.GetFileName(path)} is binary with {data.Length - (84 + 50 * n)} trailing bytes; ignored them");
                scene = new Scene();
                ReadBinary(data, (int)n, stem, scene);
            }
            return scene;
        }

        private static void ReadBinary(byte[] data, int n, string stem, Scene scene)
        {
            var dedup = new VertexDedup();
            var tris = new List<int>(n * 3);
            bool attributes = false;
            for (int t = 0; t < n; t++)
            {
                int o = 84 + 50 * t + 12; // skip the stored normal, it is recomputed on write
                for (int c = 0; c < 3; c++)
                {
                    int p = o + 12 * c;
                    float x = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p, 4));
                    float y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p + 4, 4));
                    float z = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p + 8, 4));
                    tris.Add(dedup.Add(x, y, z));
                }
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 36, 2)) != 0) attributes = true;
            }
            if (attributes) scene.Drop("stl facet attributes");
            scene.AddPart(stem, dedup.Positions, tris, null);
        }

        // Tolerant ASCII: any whitespace, keywords in any case, several "solid name" blocks become parts.
        private static void ReadAscii(byte[] data, string stem, Scene scene)
        {
            string text = Encoding.UTF8.GetString(data);
            string? name = null;
            VertexDedup dedup = new VertexDedup();
            var tris = new List<int>();
            var loop = new List<int>();
            int parts = 0;

            void Flush()
            {
                if (tris.Count > 0)
                {
                    parts++;
                    string partName = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : parts == 1 ? stem : $"{stem} {parts}";
                    scene.AddPart(partName, dedup.Positions, tris, null);
                }
                dedup = new VertexDedup();
                tris = new List<int>();
                loop.Clear();
            }

            foreach (string rawLine in text.Split('\n'))
            {
                string[] tok = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tok.Length; i++)
                {
                    string k = tok[i].ToLowerInvariant();
                    switch (k)
                    {
                        case "solid":
                        {
                            Flush();
                            // The name is the rest of the line, up to a "facet" keyword when the file is one line.
                            var sb = new StringBuilder();
                            int j = i + 1;
                            for (; j < tok.Length && !tok[j].Equals("facet", StringComparison.OrdinalIgnoreCase); j++)
                            {
                                if (sb.Length > 0) sb.Append(' ');
                                sb.Append(tok[j]);
                            }
                            name = sb.ToString();
                            i = j - 1;
                            break;
                        }
                        case "endsolid":
                            Flush();
                            name = null;
                            i = tok.Length; // the rest of the line repeats the name
                            break;
                        case "outer":
                        case "loop":
                            loop.Clear();
                            break;
                        case "vertex":
                            if (i + 3 >= tok.Length) throw new InvalidDataException("STL: incomplete vertex line.");
                            float x = ParseFloat(tok[i + 1]);
                            float y = ParseFloat(tok[i + 2]);
                            float z = ParseFloat(tok[i + 3]);
                            loop.Add(dedup.Add(x, y, z));
                            i += 3;
                            break;
                        case "endloop":
                            for (int v = 1; v + 1 < loop.Count; v++)
                            {
                                tris.Add(loop[0]);
                                tris.Add(loop[v]);
                                tris.Add(loop[v + 1]);
                            }
                            loop.Clear();
                            break;
                        case "facet":
                            // "facet normal nx ny nz": the stored normal is recomputed on write.
                            i += 4;
                            break;
                    }
                }
            }
            Flush();
        }

        private static float ParseFloat(string s) =>
            float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        // Binary STL: every part merged, facet normals from the winding, attribute 0.
        public static void Write(Scene scene, string path)
        {
            if (scene.Parts.Count > 1)
            {
                scene.Drop("part separation");
                scene.Drop("part names");
            }
            foreach (Part p in scene.Parts)
                if (p.Color != null) { scene.Drop("colors"); break; }

            long count = 0;
            foreach (Part p in scene.Parts) count += p.TriangleCount;
            if (84 + 50 * count > int.MaxValue) throw new InvalidOperationException("Model too large for binary STL.");

            var buf = new byte[84 + 50 * count];
            byte[] header = Encoding.ASCII.GetBytes("Typeless");
            Array.Copy(header, buf, header.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), (uint)count);

            int o = 84;
            foreach (Part p in scene.Parts)
            {
                float[] pos = p.Positions;
                int[] tri = p.Triangles;
                for (int t = 0; t < tri.Length; t += 3)
                {
                    int a = tri[t] * 3, b = tri[t + 1] * 3, c = tri[t + 2] * 3;
                    double ux = (double)pos[b] - pos[a], uy = (double)pos[b + 1] - pos[a + 1], uz = (double)pos[b + 2] - pos[a + 2];
                    double vx = (double)pos[c] - pos[a], vy = (double)pos[c + 1] - pos[a + 1], vz = (double)pos[c + 2] - pos[a + 2];
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 0 && double.IsFinite(len)) { nx /= len; ny /= len; nz /= len; }
                    else { nx = 0; ny = 0; nz = 0; }

                    Span<byte> rec = buf.AsSpan(o, 50);
                    BinaryPrimitives.WriteSingleLittleEndian(rec.Slice(0, 4), (float)nx);
                    BinaryPrimitives.WriteSingleLittleEndian(rec.Slice(4, 4), (float)ny);
                    BinaryPrimitives.WriteSingleLittleEndian(rec.Slice(8, 4), (float)nz);
                    WriteVertex(rec.Slice(12, 12), pos, a);
                    WriteVertex(rec.Slice(24, 12), pos, b);
                    WriteVertex(rec.Slice(36, 12), pos, c);
                    // attribute bytes stay 0
                    o += 50;
                }
            }
            File.WriteAllBytes(path, buf);
        }

        private static void WriteVertex(Span<byte> dst, float[] pos, int i)
        {
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(0, 4), pos[i]);
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(4, 4), pos[i + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(8, 4), pos[i + 2]);
        }
    }
}
