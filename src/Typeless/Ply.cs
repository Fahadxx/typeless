using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Typeless
{
    // Stanford PLY: ascii and binary (both endians) in, binary_little_endian out.
    // Use through ModelBackend.Read / Write.
    internal static class Ply
    {
        private sealed class Prop
        {
            public string Name = "";
            public string Type = "";       // scalar type, or the item type of a list
            public string? CountType;      // set for a list property
            public bool IsList => CountType != null;
        }

        private sealed class Element
        {
            public string Name = "";
            public long Count;
            public readonly List<Prop> Props = new List<Prop>();
        }

        public static Scene Read(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var scene = new Scene();
            var elements = new List<Element>();
            string format = "";
            int pos = 0;
            bool first = true;

            // Header: ASCII lines up to end_header; the body starts right after its '\n' (CRLF tolerated).
            while (true)
            {
                if (pos >= data.Length) throw new InvalidDataException("PLY: no end_header.");
                int nl = Array.IndexOf(data, (byte)'\n', pos);
                if (nl < 0) throw new InvalidDataException("PLY: no end_header.");
                string line = Encoding.ASCII.GetString(data, pos, nl - pos).Trim();
                pos = nl + 1;
                if (first)
                {
                    if (line != "ply") throw new InvalidDataException("Not a PLY file.");
                    first = false;
                    continue;
                }
                string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                if (t[0] == "end_header") break;
                switch (t[0])
                {
                    case "format":
                        if (t.Length < 2) throw new InvalidDataException("PLY: bad format line.");
                        format = t[1];
                        break;
                    case "element":
                        if (t.Length < 3 || !long.TryParse(t[2], NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                            throw new InvalidDataException("PLY: bad element line.");
                        elements.Add(new Element { Name = t[1], Count = count });
                        break;
                    case "property":
                        if (elements.Count == 0) throw new InvalidDataException("PLY: property before any element.");
                        if (t.Length >= 5 && t[1] == "list")
                            elements[^1].Props.Add(new Prop { CountType = t[2], Type = t[3], Name = t[4] });
                        else if (t.Length >= 3)
                            elements[^1].Props.Add(new Prop { Type = t[1], Name = t[2] });
                        else throw new InvalidDataException("PLY: bad property line.");
                        break;
                }
            }

            Body body = format switch
            {
                "ascii" => new AsciiBody(Encoding.ASCII.GetString(data, pos, data.Length - pos)),
                "binary_little_endian" => new BinBody(data, pos, bigEndian: false),
                "binary_big_endian" => new BinBody(data, pos, bigEndian: true),
                _ => throw new NotSupportedException($"PLY format '{format}' is not supported."),
            };

            var positions = new List<float>();
            var tris = new List<int>();
            var poly = new List<int>();
            bool seenVertex = false;

            foreach (Element e in elements)
            {
                if (e.Name == "vertex" && !seenVertex)
                {
                    seenVertex = true;
                    int ix = e.Props.FindIndex(p => p.Name == "x");
                    int iy = e.Props.FindIndex(p => p.Name == "y");
                    int iz = e.Props.FindIndex(p => p.Name == "z");
                    if (ix < 0 || iy < 0 || iz < 0) throw new InvalidDataException("PLY: vertex has no x, y, z.");
                    if (e.Props.Any(p => p.IsList)) throw new NotSupportedException("PLY vertex list properties are not supported.");
                    foreach (Prop p in e.Props)
                    {
                        switch (p.Name)
                        {
                            case "red": case "green": case "blue": case "alpha":
                            case "diffuse_red": case "diffuse_green": case "diffuse_blue":
                                scene.Drop("ply vertex colors"); break;
                            case "nx": case "ny": case "nz":
                                scene.Drop("normals"); break;
                            case "s": case "t": case "u": case "v": case "texture_u": case "texture_v":
                                scene.Drop("uvs"); break;
                        }
                    }
                    Scene.CheckCount(e.Count, Scene.MaxVertices);
                    var xyz = new float[3];
                    for (long i = 0; i < e.Count; i++)
                    {
                        for (int k = 0; k < e.Props.Count; k++)
                        {
                            Prop p = e.Props[k];
                            double v = body.Read(p.Type);
                            if (k == ix) xyz[0] = (float)v;
                            else if (k == iy) xyz[1] = (float)v;
                            else if (k == iz) xyz[2] = (float)v;
                        }
                        positions.Add(xyz[0]);
                        positions.Add(xyz[1]);
                        positions.Add(xyz[2]);
                    }
                }
                else if (e.Name == "face")
                {
                    int il = e.Props.FindIndex(p => p.IsList && (p.Name == "vertex_indices" || p.Name == "vertex_index"));
                    if (il < 0) scene.Drop("ply faces without vertex_indices");
                    foreach (Prop p in e.Props)
                    {
                        if (p.Name is "red" or "green" or "blue" or "alpha") scene.Drop("ply face colors");
                        else if (p.Name == "texcoord") scene.Drop("uvs");
                    }
                    if (e.Props.Count > 0) Scene.CheckCount(e.Count, Scene.MaxTriangles);
                    for (long i = 0; i < e.Count && e.Props.Count > 0; i++)
                    {
                        for (int k = 0; k < e.Props.Count; k++)
                        {
                            Prop p = e.Props[k];
                            if (!p.IsList) { body.Read(p.Type); continue; }
                            long n = (long)body.Read(p.CountType!);
                            if (n < 0) throw new InvalidDataException("PLY: negative list count.");
                            if (k != il)
                            {
                                for (long j = 0; j < n; j++) body.Read(p.Type);
                                continue;
                            }
                            poly.Clear();
                            for (long j = 0; j < n; j++)
                            {
                                double vi = body.Read(p.Type);
                                if (vi < 0 || vi > int.MaxValue) throw new InvalidDataException("PLY: vertex index out of range.");
                                poly.Add((int)vi);
                            }
                            for (int j = 1; j + 1 < poly.Count; j++)
                            {
                                tris.Add(poly[0]);
                                tris.Add(poly[j]);
                                tris.Add(poly[j + 1]);
                            }
                        }
                    }
                }
                else
                {
                    if (e.Count > 0) scene.Drop($"ply {e.Name} elements");
                    // An element without properties takes no bytes, so there is nothing to skip.
                    for (long i = 0; i < e.Count && e.Props.Count > 0; i++)
                        foreach (Prop p in e.Props)
                        {
                            if (!p.IsList) { body.Read(p.Type); continue; }
                            long n = (long)body.Read(p.CountType!);
                            for (long j = 0; j < n; j++) body.Read(p.Type);
                        }
                }
            }

            scene.AddPart(Path.GetFileNameWithoutExtension(path), positions, tris, null);
            return scene;
        }

        private abstract class Body
        {
            public abstract double Read(string type);
        }

        private sealed class BinBody : Body
        {
            private readonly byte[] d;
            private readonly bool be;
            private int pos;

            public BinBody(byte[] data, int start, bool bigEndian)
            {
                d = data;
                pos = start;
                be = bigEndian;
            }

            private ReadOnlySpan<byte> Take(int n)
            {
                if (pos + n > d.Length) throw new InvalidDataException("PLY: file is truncated.");
                var s = new ReadOnlySpan<byte>(d, pos, n);
                pos += n;
                return s;
            }

            public override double Read(string type)
            {
                switch (type)
                {
                    case "char": case "int8": return (sbyte)Take(1)[0];
                    case "uchar": case "uint8": return Take(1)[0];
                    case "short": case "int16":
                        return be ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2));
                    case "ushort": case "uint16":
                        return be ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                    case "int": case "int32":
                        return be ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));
                    case "uint": case "uint32":
                        return be ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
                    case "float": case "float32":
                        return be ? BinaryPrimitives.ReadSingleBigEndian(Take(4)) : BinaryPrimitives.ReadSingleLittleEndian(Take(4));
                    case "double": case "float64":
                        return be ? BinaryPrimitives.ReadDoubleBigEndian(Take(8)) : BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
                    default:
                        throw new InvalidDataException($"PLY: unknown property type '{type}'.");
                }
            }
        }

        private sealed class AsciiBody : Body
        {
            private readonly string s;
            private int pos;

            public AsciiBody(string text) { s = text; }

            public override double Read(string type)
            {
                while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
                if (pos >= s.Length) throw new InvalidDataException("PLY: file is truncated.");
                int start = pos;
                while (pos < s.Length && !char.IsWhiteSpace(s[pos])) pos++;
                string tok = s.Substring(start, pos - start);
                // float32 values parse straight to float, so the stored bits are exactly the text's float.
                if (type is "float" or "float32")
                    return float.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
                return double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        // binary_little_endian: float x y z, faces as uchar count + int indices, all parts merged.
        public static void Write(Scene scene, string path)
        {
            if (scene.Parts.Count > 1)
            {
                scene.Drop("part separation");
                scene.Drop("part names");
            }
            if (scene.Parts.Any(p => p.Color != null)) scene.Drop("colors");

            long vertices = scene.Parts.Sum(p => (long)p.VertexCount);
            long triangles = scene.Parts.Sum(p => (long)p.TriangleCount);
            string header =
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                "comment Typeless\n" +
                $"element vertex {vertices.ToString(CultureInfo.InvariantCulture)}\n" +
                "property float x\n" +
                "property float y\n" +
                "property float z\n" +
                $"element face {triangles.ToString(CultureInfo.InvariantCulture)}\n" +
                "property list uchar int vertex_indices\n" +
                "end_header\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            long size = head.Length + 12 * vertices + 13 * triangles;
            if (size > int.MaxValue) throw new InvalidOperationException("Model too large for PLY.");

            var buf = new byte[size];
            Array.Copy(head, buf, head.Length);
            int o = head.Length;
            foreach (Part p in scene.Parts)
                foreach (float f in p.Positions)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o, 4), f);
                    o += 4;
                }
            int offset = 0;
            foreach (Part p in scene.Parts)
            {
                int[] tri = p.Triangles;
                for (int t = 0; t < tri.Length; t += 3)
                {
                    buf[o++] = 3;
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o, 4), tri[t] + offset); o += 4;
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o, 4), tri[t + 1] + offset); o += 4;
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o, 4), tri[t + 2] + offset); o += 4;
                }
                offset += p.VertexCount;
            }
            File.WriteAllBytes(path, buf);
        }
    }
}
