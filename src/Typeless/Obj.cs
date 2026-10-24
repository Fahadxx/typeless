using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Typeless
{
    // Wavefront OBJ (+ MTL base color). Use through ModelBackend.Read / Write.
    internal static class Obj
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Builder
        {
            public string Name = "";
            public string? Material;
            public readonly Dictionary<int, int> Remap = new Dictionary<int, int>();
            public readonly List<float> Positions = new List<float>();
            public readonly List<int> Triangles = new List<int>();
        }

        public static Scene Read(string path)
        {
            var scene = new Scene();
            string stem = Path.GetFileNameWithoutExtension(path);
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var vertices = new List<float>();
            var materials = new Dictionary<string, Rgba?>(StringComparer.Ordinal);
            var built = new List<(Builder B, string? Material)>();

            string objectName = stem;
            var cur = new Builder { Name = stem };
            var face = new List<int>();

            void Finish()
            {
                if (cur.Triangles.Count > 0) built.Add((cur, cur.Material));
            }

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = IndexOfSpace(line);
                string key = sp < 0 ? line : line.Substring(0, sp);
                string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();

                switch (key)
                {
                    case "v":
                    {
                        string[] t = Split(rest);
                        if (t.Length < 3) throw new InvalidDataException("OBJ: vertex with fewer than 3 coordinates.");
                        vertices.Add(ParseFloat(t[0]));
                        vertices.Add(ParseFloat(t[1]));
                        vertices.Add(ParseFloat(t[2]));
                        if (t.Length >= 6) scene.Drop("obj vertex colors");
                        break;
                    }
                    case "vt":
                        scene.Drop("uvs");
                        break;
                    case "vn":
                        scene.Drop("normals");
                        break;
                    case "f":
                    {
                        face.Clear();
                        int count = vertices.Count / 3;
                        foreach (string ref_ in Split(rest))
                        {
                            int slash = ref_.IndexOf('/');
                            string num = slash < 0 ? ref_ : ref_.Substring(0, slash);
                            if (!int.TryParse(num, NumberStyles.AllowLeadingSign, Inv, out int idx) || idx == 0)
                                throw new InvalidDataException($"OBJ: bad face index '{ref_}'.");
                            int g = idx > 0 ? idx - 1 : count + idx;
                            if (g < 0 || g >= count) throw new InvalidDataException($"OBJ: face index {idx} out of range.");
                            if (!cur.Remap.TryGetValue(g, out int local))
                            {
                                local = cur.Positions.Count / 3;
                                cur.Positions.Add(vertices[g * 3]);
                                cur.Positions.Add(vertices[g * 3 + 1]);
                                cur.Positions.Add(vertices[g * 3 + 2]);
                                cur.Remap[g] = local;
                            }
                            face.Add(local);
                        }
                        for (int i = 1; i + 1 < face.Count; i++)
                        {
                            cur.Triangles.Add(face[0]);
                            cur.Triangles.Add(face[i]);
                            cur.Triangles.Add(face[i + 1]);
                        }
                        break;
                    }
                    case "o":
                    case "g":
                        Finish();
                        objectName = rest.Length > 0 ? rest : stem;
                        cur = new Builder { Name = objectName, Material = cur.Material };
                        break;
                    case "usemtl":
                    {
                        string mtl = rest;
                        if (cur.Triangles.Count > 0 && mtl != cur.Material)
                        {
                            // A material change mid-object splits the part, so colors stay per part.
                            Finish();
                            cur = new Builder { Name = mtl.Length > 0 ? $"{objectName} {mtl}" : objectName };
                        }
                        cur.Material = mtl.Length > 0 ? mtl : null;
                        break;
                    }
                    case "mtllib":
                        LoadMtl(dir, rest, materials, scene);
                        break;
                }
            }
            Finish();

            foreach ((Builder b, string? material) in built)
            {
                Rgba? color = null;
                if (material != null && materials.TryGetValue(material, out Rgba? c)) color = c;
                scene.AddPart(b.Name, b.Positions, b.Triangles, color);
            }
            return scene;
        }

        // The whole rest of the line is tried as one file name first (names have spaces), then split.
        private static void LoadMtl(string dir, string rest, Dictionary<string, Rgba?> materials, Scene scene)
        {
            if (rest.Length == 0) return;
            var files = new List<string>();
            string whole = Path.Combine(dir, rest);
            if (File.Exists(whole)) files.Add(whole);
            else
            {
                foreach (string f in Split(rest))
                {
                    string p = Path.Combine(dir, f);
                    if (File.Exists(p)) files.Add(p);
                    else scene.Drop("missing mtl " + f);
                }
            }

            foreach (string file in files)
            {
                string? name = null;
                double[]? kd = null;
                double alpha = 1;

                void Commit()
                {
                    if (name != null)
                        materials[name] = kd == null ? null : Rgba.FromUnit(kd[0], kd[1], kd[2], alpha);
                }

                foreach (string raw in File.ReadLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int sp = IndexOfSpace(line);
                    string key = sp < 0 ? line : line.Substring(0, sp);
                    string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                    string[] t = Split(arg);
                    switch (key)
                    {
                        case "newmtl":
                            Commit();
                            name = arg;
                            kd = null;
                            alpha = 1;
                            break;
                        case "Kd":
                            if (t.Length >= 3 && TryDouble(t[0], out double r) && TryDouble(t[1], out double g) && TryDouble(t[2], out double b))
                                kd = new[] { r, g, b };
                            else if (t.Length >= 1 && TryDouble(t[0], out double gray))
                                kd = new[] { gray, gray, gray };
                            break;
                        case "d":
                            if (t.Length >= 1 && TryDouble(t[^1], out double d)) alpha = d;
                            break;
                        case "Tr":
                            if (t.Length >= 1 && TryDouble(t[^1], out double tr)) alpha = 1 - tr;
                            break;
                        default:
                            if (key.StartsWith("map_", StringComparison.OrdinalIgnoreCase) || key == "bump" || key == "disp" || key == "refl")
                                scene.Drop("textures");
                            break;
                    }
                }
                Commit();
            }
        }

        // Writes path, plus <same stem>.mtl beside it when any part has a color. The caller picks
        // the final names before writing, so the mtllib line already names the final mtl.
        public static void Write(Scene scene, string path)
        {
            bool colored = scene.Parts.Any(p => p.Color != null);
            string mtlName = Path.GetFileNameWithoutExtension(path) + ".mtl";
            var matNames = new string?[scene.Parts.Count];
            if (colored)
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < scene.Parts.Count; i++)
                {
                    if (scene.Parts[i].Color == null) continue;
                    string baseName = Sanitize(scene.Parts[i].Name) + "_mat";
                    string m = baseName;
                    for (int n = 2; !used.Add(m); n++) m = $"{baseName}_{n}";
                    matNames[i] = m;
                }
            }

            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                w.WriteLine("# Typeless");
                if (colored) w.WriteLine("mtllib " + mtlName);
                int offset = 1;
                for (int i = 0; i < scene.Parts.Count; i++)
                {
                    Part p = scene.Parts[i];
                    w.WriteLine("o " + OneLine(p.Name));
                    if (matNames[i] != null) w.WriteLine("usemtl " + matNames[i]);
                    float[] pos = p.Positions;
                    for (int v = 0; v < pos.Length; v += 3)
                        w.WriteLine($"v {F(pos[v])} {F(pos[v + 1])} {F(pos[v + 2])}");
                    int[] tri = p.Triangles;
                    for (int t = 0; t < tri.Length; t += 3)
                        w.WriteLine($"f {tri[t] + offset} {tri[t + 1] + offset} {tri[t + 2] + offset}");
                    offset += p.VertexCount;
                }
            }

            if (!colored) return;
            string mtlPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", mtlName);
            using (var w = new StreamWriter(mtlPath, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                w.WriteLine("# Typeless");
                for (int i = 0; i < scene.Parts.Count; i++)
                {
                    if (scene.Parts[i].Color is not Rgba c) continue;
                    w.WriteLine();
                    w.WriteLine("newmtl " + matNames[i]);
                    w.WriteLine($"Kd {F(c.R / 255f)} {F(c.G / 255f)} {F(c.B / 255f)}");
                    w.WriteLine($"d {F(c.A / 255f)}");
                }
            }
        }

        private static string F(float f) => f.ToString("R", Inv);

        private static string OneLine(string s)
        {
            string t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return t.Length > 0 ? t : "part";
        }

        // Material names without whitespace, so every reader takes them as one token.
        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s.Trim()) sb.Append(char.IsWhiteSpace(ch) || ch == '#' ? '_' : ch);
            return sb.Length > 0 ? sb.ToString() : "part";
        }

        private static int IndexOfSpace(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] == ' ' || s[i] == '\t') return i;
            return -1;
        }

        private static string[] Split(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static float ParseFloat(string s) => float.Parse(s, NumberStyles.Float, Inv);

        private static bool TryDouble(string s, out double d) => double.TryParse(s, NumberStyles.Float, Inv, out d);
    }
}
