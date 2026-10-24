using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Typeless
{
    // 3MF: core spec plus Production extension components on read (p:path into other model parts,
    // nested component and build item transforms baked in double), core spec only on write.
    // Use through ModelBackend.Read / Write.
    internal static class ThreeMf
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private const string CoreNs = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
        private const string ModelRelType = "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";
        private const string DefaultRoot = "3D/3dmodel.model";
        private const int MaxDepth = 64;

        private sealed class Component
        {
            public string? Path;      // normalized part name, null = same file as the parent object
            public string ObjectId = "";
            public double[] Transform = Identity();
        }

        private sealed class ObjDef
        {
            public string Id = "";
            public string Type = "model";
            public string? Name;
            public string? Pid;
            public int PIndex = -1;
            public float[] Positions = Array.Empty<float>();
            public int[] Triangles = Array.Empty<int>();
            public List<Component> Components = new List<Component>();

            // Triangle properties: the first explicit (pid, p1) seen, and whether they vary.
            public bool SawDefault, SawProp, Varied;
            public string? FirstPid;
            public int FirstP1 = -1;
        }

        private sealed class ModelFile
        {
            public string PartName = "";
            public string Unit = "millimeter";
            public readonly Dictionary<string, ObjDef> Objects = new Dictionary<string, ObjDef>(StringComparer.Ordinal);
            // Property groups by id: basematerials and colorgroup hold colors, other groups hold null.
            public readonly Dictionary<string, List<Rgba?>> Groups = new Dictionary<string, List<Rgba?>>(StringComparer.Ordinal);
            public readonly List<(string? Path, string ObjectId, double[] Transform)> Items = new List<(string?, string, double[])>();
        }

        // Atomized names, so the hot vertex/triangle loops compare references instead of strings.
        private sealed class Atoms
        {
            public readonly string Vertex, Triangle, X, Y, Z, V1, V2, V3, Pid, P1, P2, P3, PaintColor, MmuSegmentation,
                PaintSupports, PaintSeam, CustomSupports, CustomSeam, Core, Empty;

            public Atoms(XmlNameTable nt)
            {
                Vertex = nt.Add("vertex"); Triangle = nt.Add("triangle");
                X = nt.Add("x"); Y = nt.Add("y"); Z = nt.Add("z");
                V1 = nt.Add("v1"); V2 = nt.Add("v2"); V3 = nt.Add("v3");
                Pid = nt.Add("pid"); P1 = nt.Add("p1"); P2 = nt.Add("p2"); P3 = nt.Add("p3");
                PaintColor = nt.Add("paint_color"); MmuSegmentation = nt.Add("mmu_segmentation");
                PaintSupports = nt.Add("paint_supports"); PaintSeam = nt.Add("paint_seam");
                CustomSupports = nt.Add("custom_supports"); CustomSeam = nt.Add("custom_seam");
                Core = nt.Add(CoreNs); Empty = nt.Add("");
            }
        }

        private sealed class Reader
        {
            public ZipArchive Zip = null!;
            public Dictionary<string, ZipArchiveEntry> Entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, ModelFile> Files = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
            public NameTable Names = new NameTable();
            public Atoms A = null!;
            public Scene Scene = new Scene();
            public string RootUnit = "millimeter";
            public string FileName = "";
        }

        public static Scene Read(string path)
        {
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                var rd = new Reader { Zip = zip, FileName = Path.GetFileName(path) };
                rd.A = new Atoms(rd.Names);
                foreach (ZipArchiveEntry e in zip.Entries)
                    rd.Entries[Normalize(e.FullName)] = e;

                NotePackageExtras(rd);
                string rootName = FindRootPart(rd);
                ModelFile root = Load(rd, rootName, true);
                rd.RootUnit = root.Unit;
                double scale = UnitScale(root.Unit);
                double[] unit = Identity();
                unit[0] = scale; unit[4] = scale; unit[8] = scale;

                if (root.Items.Count == 0) throw new InvalidDataException("the build has no items");
                foreach ((string? itemPath, string objectId, double[] t) in root.Items)
                {
                    ModelFile file = itemPath != null ? Load(rd, itemPath, false) : root;
                    Emit(rd, file, objectId, Mul(t, unit), null, 0);
                }
                return rd.Scene;
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException("Not a valid 3MF file: " + ex.Message, ex);
            }
            catch (XmlException ex)
            {
                throw new InvalidOperationException("Not a valid 3MF file: " + ex.Message, ex);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Not a valid 3MF file: bad number (" + ex.Message + ")", ex);
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("Not a valid 3MF file: number out of range (" + ex.Message + ")", ex);
            }
        }

        // Logs package parts Typeless does not carry over: thumbnails, metadata and slicer configs.
        private static void NotePackageExtras(Reader rd)
        {
            foreach (string name in rd.Entries.Keys)
            {
                if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".model", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                if (name.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf("Slic3r", StringComparison.OrdinalIgnoreCase) >= 0)
                    rd.Scene.Drop("slicer settings and filament colors");
                else
                    rd.Scene.Drop("3mf metadata and thumbnails");
            }
        }

        private static string FindRootPart(Reader rd)
        {
            if (rd.Entries.TryGetValue("_rels/.rels", out ZipArchiveEntry? rels))
            {
                using Stream s = rels.Open();
                using XmlReader r = XmlReader.Create(s, Settings(null));
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                    string? type = r.GetAttribute("Type");
                    string? target = r.GetAttribute("Target");
                    if (target == null || !string.Equals(type, ModelRelType, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Normalize(target);
                    if (rd.Entries.ContainsKey(name)) return name;
                }
            }
            if (rd.Entries.ContainsKey(DefaultRoot)) return DefaultRoot;
            throw new InvalidDataException("no 3D model part in the package");
        }

        private static XmlReaderSettings Settings(XmlNameTable? nt) => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
            NameTable = nt,
        };

        private static ModelFile Load(Reader rd, string partName, bool isRoot)
        {
            if (rd.Files.TryGetValue(partName, out ModelFile? cached)) return cached;
            if (!rd.Entries.TryGetValue(partName, out ZipArchiveEntry? entry))
                throw new InvalidDataException($"missing model part /{partName}");

            var file = new ModelFile { PartName = partName };
            using (Stream raw = entry.Open())
            using (var buffered = new BufferedStream(raw, 1 << 16))
            using (XmlReader r = XmlReader.Create(buffered, Settings(rd.Names)))
                Parse(rd, r, file, isRoot, entry.Length);

            if (!isRoot && !string.Equals(file.Unit, rd.RootUnit, StringComparison.Ordinal))
                Log.Write($"3MF: {rd.FileName}: /{partName} says unit={file.Unit}, the root unit {rd.RootUnit} applies");
            rd.Files[partName] = file;
            return file;
        }

        private static void Parse(Reader rd, XmlReader r, ModelFile file, bool isRoot, long entryLength)
        {
            Atoms a = rd.A;
            Scene scene = rd.Scene;
            ObjDef? obj = null;
            List<float>? pos = null;
            List<int>? tris = null;
            List<Rgba?>? group = null;
            bool firstMesh = true;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    string end = r.LocalName;
                    if (end == "object" && obj != null)
                    {
                        FinishObject(file, obj, pos, tris);
                        obj = null; pos = null; tris = null;
                    }
                    else if (end == "basematerials" || end == "colorgroup") group = null;
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;

                string name = r.LocalName;
                string ns = r.NamespaceURI;
                bool core = ReferenceEquals(ns, a.Core) || ReferenceEquals(ns, a.Empty) || ns == CoreNs || ns.Length == 0;

                // Hot paths first: millions of these per file.
                if (ReferenceEquals(name, a.Vertex) && core && pos != null)
                {
                    float x = 0, y = 0, z = 0;
                    int got = 0;
                    while (r.MoveToNextAttribute())
                    {
                        string an = r.LocalName;
                        if (ReferenceEquals(an, a.X)) { x = ParseFloat(r.Value); got |= 1; }
                        else if (ReferenceEquals(an, a.Y)) { y = ParseFloat(r.Value); got |= 2; }
                        else if (ReferenceEquals(an, a.Z)) { z = ParseFloat(r.Value); got |= 4; }
                    }
                    if (got != 7) throw new InvalidDataException("vertex without x, y and z");
                    pos.Add(x); pos.Add(y); pos.Add(z);
                    continue;
                }
                if (ReferenceEquals(name, a.Triangle) && core && tris != null && obj != null)
                {
                    ReadTriangle(r, a, obj, tris, scene);
                    continue;
                }

                switch (name)
                {
                    case "model":
                        if (!core) break;
                        file.Unit = (r.GetAttribute("unit") ?? "millimeter").Trim().ToLowerInvariant();
                        UnitScale(file.Unit); // throws on an unknown unit
                        break;
                    case "metadata":
                        if (isRoot && core) scene.Drop("3mf metadata and thumbnails");
                        break;
                    case "basematerials":
                    case "colorgroup":
                    {
                        string id = r.GetAttribute("id") ?? "";
                        group = new List<Rgba?>();
                        file.Groups[id] = group;
                        if (r.IsEmptyElement) group = null;
                        break;
                    }
                    case "base":
                        group?.Add(ParseColor(r.GetAttribute("displaycolor")));
                        break;
                    case "color":
                        group?.Add(ParseColor(r.GetAttribute("color")));
                        break;
                    case "texture2d":
                        scene.Drop("textures");
                        break;
                    case "texture2dgroup":
                    case "compositematerials":
                    case "multiproperties":
                        scene.Drop(name == "texture2dgroup" ? "textures" : "3mf " + name);
                        file.Groups[r.GetAttribute("id") ?? ""] = new List<Rgba?>();
                        break;
                    case "beamlattice":
                        scene.Drop("3mf beam lattice");
                        break;
                    case "slicestack":
                        scene.Drop("3mf slices");
                        break;
                    case "object":
                    {
                        if (!core) break;
                        obj = new ObjDef
                        {
                            Id = r.GetAttribute("id") ?? throw new InvalidDataException("object without id"),
                            Type = (r.GetAttribute("type") ?? "model").Trim().ToLowerInvariant(),
                            Name = NullIfBlank(r.GetAttribute("name")),
                            Pid = NullIfBlank(r.GetAttribute("pid")),
                        };
                        string? pindex = r.GetAttribute("pindex");
                        if (pindex != null) obj.PIndex = int.Parse(pindex, NumberStyles.Integer, Inv);
                        else if (obj.Pid != null) obj.PIndex = 0;
                        if (r.IsEmptyElement)
                        {
                            FinishObject(file, obj, null, null);
                            obj = null;
                        }
                        break;
                    }
                    case "mesh":
                        if (!core || obj == null) break;
                        int vcap = 1024, tcap = 2048;
                        if (firstMesh)
                        {
                            // Rough sizes from the uncompressed part: about 45 bytes per vertex and per triangle.
                            vcap = (int)Math.Clamp(entryLength / 45, 1024, 1L << 26);
                            tcap = (int)Math.Clamp(entryLength / 22, 2048, 1L << 26);
                            firstMesh = false;
                        }
                        pos = new List<float>(vcap);
                        tris = new List<int>(tcap);
                        break;
                    case "component":
                    {
                        if (!core || obj == null) break;
                        var c = new Component();
                        string? oid = null;
                        while (r.MoveToNextAttribute())
                        {
                            switch (r.LocalName)
                            {
                                case "objectid": oid = r.Value; break;
                                case "transform": c.Transform = ParseTransform(r.Value); break;
                                case "path": c.Path = NullIfBlank(r.Value) is string p ? Normalize(p) : null; break;
                            }
                        }
                        c.ObjectId = oid ?? throw new InvalidDataException("component without objectid");
                        obj.Components.Add(c);
                        break;
                    }
                    case "item":
                    {
                        if (!core || !isRoot) break;
                        string? oid = null, path = null;
                        double[] t = Identity();
                        while (r.MoveToNextAttribute())
                        {
                            switch (r.LocalName)
                            {
                                case "objectid": oid = r.Value; break;
                                case "transform": t = ParseTransform(r.Value); break;
                                case "path": path = NullIfBlank(r.Value) is string p ? Normalize(p) : null; break;
                            }
                        }
                        file.Items.Add((path, oid ?? throw new InvalidDataException("build item without objectid"), t));
                        break;
                    }
                }
            }
            if (obj != null) throw new InvalidDataException("unterminated object");
        }

        private static void ReadTriangle(XmlReader r, Atoms a, ObjDef obj, List<int> tris, Scene scene)
        {
            int v1 = -1, v2 = -1, v3 = -1, p1 = -1, p2 = -1, p3 = -1;
            string? pid = null;
            while (r.MoveToNextAttribute())
            {
                string an = r.LocalName;
                if (ReferenceEquals(an, a.V1)) v1 = ParseInt(r.Value);
                else if (ReferenceEquals(an, a.V2)) v2 = ParseInt(r.Value);
                else if (ReferenceEquals(an, a.V3)) v3 = ParseInt(r.Value);
                else if (ReferenceEquals(an, a.Pid)) pid = r.Value;
                else if (ReferenceEquals(an, a.P1)) p1 = ParseInt(r.Value);
                else if (ReferenceEquals(an, a.P2)) p2 = ParseInt(r.Value);
                else if (ReferenceEquals(an, a.P3)) p3 = ParseInt(r.Value);
                else if (ReferenceEquals(an, a.PaintColor) || ReferenceEquals(an, a.MmuSegmentation))
                {
                    if (r.Value.Length > 0) scene.Drop("3mf per-triangle colors");
                }
                else if (ReferenceEquals(an, a.PaintSupports) || ReferenceEquals(an, a.PaintSeam)
                    || ReferenceEquals(an, a.CustomSupports) || ReferenceEquals(an, a.CustomSeam))
                {
                    if (r.Value.Length > 0) scene.Drop("3mf painted supports and seams");
                }
            }
            if (v1 < 0 || v2 < 0 || v3 < 0) throw new InvalidDataException("triangle without v1, v2 and v3");
            tris.Add(v1); tris.Add(v2); tris.Add(v3);

            if (pid == null && p1 < 0 && p2 < 0 && p3 < 0)
            {
                obj.SawDefault = true;
                return;
            }
            string? kpid = pid ?? obj.Pid;
            int k1 = p1 >= 0 ? p1 : obj.PIndex;
            if ((p2 >= 0 && p2 != k1) || (p3 >= 0 && p3 != k1)) obj.Varied = true;
            if (!obj.SawProp)
            {
                obj.SawProp = true;
                obj.FirstPid = kpid;
                obj.FirstP1 = k1;
            }
            else if (k1 != obj.FirstP1 || !string.Equals(kpid, obj.FirstPid, StringComparison.Ordinal))
                obj.Varied = true;
        }

        private static void FinishObject(ModelFile file, ObjDef obj, List<float>? pos, List<int>? tris)
        {
            if (pos != null) obj.Positions = pos.ToArray();
            if (tris != null)
            {
                if (tris.Count % 3 != 0) throw new InvalidDataException($"object {obj.Id} has a partial triangle");
                obj.Triangles = tris.ToArray();
                int n = obj.Positions.Length / 3;
                foreach (int v in obj.Triangles)
                    if (v >= n) throw new InvalidDataException($"object {obj.Id}: vertex index {v} out of range (0..{n - 1})");
            }
            if (obj.SawProp && obj.SawDefault
                && (obj.FirstP1 != obj.PIndex || !string.Equals(obj.FirstPid, obj.Pid, StringComparison.Ordinal)))
                obj.Varied = true;
            file.Objects[obj.Id] = obj;
        }

        // m maps the object's local coordinates to world millimeters. Mesh objects become parts,
        // components recurse with their transform applied first.
        private static void Emit(Reader rd, ModelFile file, string id, double[] m, string? inheritedName, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("components nest too deep (cycle?)");
            if (!file.Objects.TryGetValue(id, out ObjDef? obj))
                throw new InvalidDataException($"reference to missing object {id} in /{file.PartName}");

            if (obj.Type == "other")
            {
                Log.Write($"3MF: {rd.FileName}: skipped object {id} (type=other)");
                return;
            }
            if (obj.Type != "model")
                Log.Write($"3MF: {rd.FileName}: object {id} type={obj.Type} included");

            string? name = obj.Name ?? inheritedName;
            if (obj.Triangles.Length >= 3)
            {
                if (obj.Varied) rd.Scene.Drop("3mf per-triangle colors");
                Rgba? color = ResolveColor(file, obj);
                float[] baked = Bake(obj.Positions, m);
                rd.Scene.AddPart(name ?? $"object {id}", baked, obj.Triangles, color);
            }
            foreach (Component c in obj.Components)
            {
                ModelFile child = c.Path != null ? Load(rd, c.Path, false) : file;
                Emit(rd, child, c.ObjectId, Mul(c.Transform, m), name, depth + 1);
            }
        }

        private static Rgba? ResolveColor(ModelFile file, ObjDef obj)
        {
            string? pid = obj.Pid;
            int index = obj.PIndex;
            if (pid == null && obj.SawProp && !obj.SawDefault && !obj.Varied)
            {
                pid = obj.FirstPid;
                index = obj.FirstP1;
            }
            if (pid == null || index < 0) return null;
            if (!file.Groups.TryGetValue(pid, out List<Rgba?>? g) || index >= g.Count) return null;
            return g[index];
        }

        // Identity: the float32 coordinates are reused as they are (bit exact, no double round trip).
        private static float[] Bake(float[] src, double[] m)
        {
            if (IsIdentity(m)) return src;
            var dst = new float[src.Length];
            double m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5];
            double m6 = m[6], m7 = m[7], m8 = m[8], m9 = m[9], m10 = m[10], m11 = m[11];
            for (int i = 0; i + 2 < src.Length; i += 3)
            {
                double x = src[i], y = src[i + 1], z = src[i + 2];
                dst[i] = (float)(x * m0 + y * m3 + z * m6 + m9);
                dst[i + 1] = (float)(x * m1 + y * m4 + z * m7 + m10);
                dst[i + 2] = (float)(x * m2 + y * m5 + z * m8 + m11);
            }
            return dst;
        }

        // 3x4 row-vector matrices stored m00 m01 m02 m10 m11 m12 m20 m21 m22 m30 m31 m32:
        // p' = [x y z 1] * M. Mul(a, b) applies a first, then b.
        private static double[] Mul(double[] a, double[] b)
        {
            var c = new double[12];
            for (int r = 0; r < 4; r++)
            {
                for (int col = 0; col < 3; col++)
                {
                    double s = a[r * 3] * b[col] + a[r * 3 + 1] * b[3 + col] + a[r * 3 + 2] * b[6 + col];
                    if (r == 3) s += b[9 + col];
                    c[r * 3 + col] = s;
                }
            }
            return c;
        }

        private static double[] Identity() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };

        private static bool IsIdentity(double[] m) =>
            m[0] == 1 && m[1] == 0 && m[2] == 0 && m[3] == 0 && m[4] == 1 && m[5] == 0
            && m[6] == 0 && m[7] == 0 && m[8] == 1 && m[9] == 0 && m[10] == 0 && m[11] == 0;

        private static double[] ParseTransform(string s)
        {
            string[] t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 12) throw new InvalidDataException($"transform needs 12 numbers, got {t.Length}");
            var m = new double[12];
            for (int i = 0; i < 12; i++) m[i] = double.Parse(t[i], NumberStyles.Float, Inv);
            return m;
        }

        private static double UnitScale(string unit) => unit switch
        {
            "micron" => 0.001,
            "millimeter" => 1,
            "centimeter" => 10,
            "inch" => 25.4,
            "foot" => 304.8,
            "meter" => 1000,
            _ => throw new InvalidDataException($"unknown unit '{unit}'"),
        };

        // "#RRGGBB" or "#RRGGBBAA"; anything else is no color.
        private static Rgba? ParseColor(string? s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return null;
            if (!uint.TryParse(s, NumberStyles.AllowHexSpecifier, Inv, out uint v)) return null;
            if (s.Length == 6) v = (v << 8) | 0xFF;
            return new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        // Package part name to zip entry key: no leading slash, forward slashes, percent-decoded.
        private static string Normalize(string name)
        {
            string n = name.Replace('\\', '/').TrimStart('/');
            if (n.IndexOf('%') >= 0)
            {
                try { n = Uri.UnescapeDataString(n); } catch (UriFormatException) { }
            }
            return n;
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static float ParseFloat(string s) => float.Parse(s, NumberStyles.Float, Inv);

        private static int ParseInt(string s) => int.Parse(s, NumberStyles.Integer, Inv);

        // ---- writer ----

        // Core spec only: millimeters, one object per part, one identity build item per object,
        // basematerials with one base per distinct color.
        public static void Write(Scene scene, string path)
        {
            var colors = new List<Rgba>();
            var colorIndex = new Dictionary<Rgba, int>();
            foreach (Part p in scene.Parts)
            {
                if (p.Color is Rgba c && !colorIndex.ContainsKey(c))
                {
                    colorIndex[c] = colors.Count;
                    colors.Add(c);
                }
            }

            var xs = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                IndentChars = "",
                NewLineChars = "\n",
                CloseOutput = false,
            };

            using FileStream fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            using (Stream s = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal).Open())
            using (XmlWriter w = XmlWriter.Create(s, xs))
            {
                const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
                w.WriteStartDocument();
                w.WriteStartElement("Types", ns);
                w.WriteStartElement("Default", ns);
                w.WriteAttributeString("Extension", "rels");
                w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
                w.WriteEndElement();
                w.WriteStartElement("Default", ns);
                w.WriteAttributeString("Extension", "model");
                w.WriteAttributeString("ContentType", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndDocument();
            }

            using (Stream s = zip.CreateEntry("_rels/.rels", CompressionLevel.Optimal).Open())
            using (XmlWriter w = XmlWriter.Create(s, xs))
            {
                const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
                w.WriteStartDocument();
                w.WriteStartElement("Relationships", ns);
                w.WriteStartElement("Relationship", ns);
                w.WriteAttributeString("Target", "/" + DefaultRoot);
                w.WriteAttributeString("Id", "rel0");
                w.WriteAttributeString("Type", ModelRelType);
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndDocument();
            }

            var objectIds = new List<int>();
            using (Stream raw = zip.CreateEntry(DefaultRoot, CompressionLevel.Optimal).Open())
            using (var s = new BufferedStream(raw, 1 << 16))
            using (XmlWriter w = XmlWriter.Create(s, xs))
            {
                w.WriteStartDocument();
                w.WriteStartElement("model", CoreNs);
                w.WriteAttributeString("unit", "millimeter");
                w.WriteAttributeString("xml", "lang", null, "en-US");

                w.WriteStartElement("metadata", CoreNs);
                w.WriteAttributeString("name", "Application");
                w.WriteString("Typeless");
                w.WriteEndElement();

                w.WriteStartElement("resources", CoreNs);
                if (colors.Count > 0)
                {
                    w.WriteStartElement("basematerials", CoreNs);
                    w.WriteAttributeString("id", "1");
                    foreach (Rgba c in colors)
                    {
                        w.WriteStartElement("base", CoreNs);
                        w.WriteAttributeString("name", c.Hex);
                        w.WriteAttributeString("displaycolor", c.A == 255 ? c.Hex.Substring(0, 7) : c.Hex);
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }

                int nextId = 2;
                foreach (Part p in scene.Parts)
                {
                    if (p.TriangleCount == 0) continue;
                    int id = nextId++;
                    objectIds.Add(id);
                    w.WriteStartElement("object", CoreNs);
                    w.WriteAttributeString("id", id.ToString(Inv));
                    w.WriteAttributeString("type", "model");
                    w.WriteAttributeString("name", XmlSafe(p.Name));
                    if (p.Color is Rgba c)
                    {
                        w.WriteAttributeString("pid", "1");
                        w.WriteAttributeString("pindex", colorIndex[c].ToString(Inv));
                    }
                    WriteMesh(w, p);
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // resources

                w.WriteStartElement("build", CoreNs);
                foreach (int id in objectIds)
                {
                    w.WriteStartElement("item", CoreNs);
                    w.WriteAttributeString("objectid", id.ToString(Inv));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement(); // model
                w.WriteEndDocument();
            }
        }

        // Triangles that repeat a vertex index are invalid in 3MF (lib3mf rejects them), so such a
        // triangle gets a copy of the repeated vertex: same coordinates, distinct index, same count.
        private static void WriteMesh(XmlWriter w, Part p)
        {
            float[] pos = p.Positions;
            int[] tri = p.Triangles;
            var extra = new List<int>(); // source vertex index for each appended copy
            int vertexCount = p.VertexCount;
            var fixedTris = tri;
            for (int t = 0; t < tri.Length; t += 3)
            {
                int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                if (a != b && b != c && a != c) continue;
                if (ReferenceEquals(fixedTris, tri)) fixedTris = (int[])tri.Clone();
                if (b == a) { fixedTris[t + 1] = vertexCount + extra.Count; extra.Add(b); }
                if (c == a || c == b) { fixedTris[t + 2] = vertexCount + extra.Count; extra.Add(c); }
            }

            w.WriteStartElement("mesh", CoreNs);
            w.WriteStartElement("vertices", CoreNs);
            for (int v = 0; v < pos.Length; v += 3) WriteVertex(w, pos, v);
            foreach (int v in extra) WriteVertex(w, pos, v * 3);
            w.WriteEndElement();
            w.WriteStartElement("triangles", CoreNs);
            for (int t = 0; t < fixedTris.Length; t += 3)
            {
                w.WriteStartElement("triangle", CoreNs);
                w.WriteAttributeString("v1", fixedTris[t].ToString(Inv));
                w.WriteAttributeString("v2", fixedTris[t + 1].ToString(Inv));
                w.WriteAttributeString("v3", fixedTris[t + 2].ToString(Inv));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteVertex(XmlWriter w, float[] pos, int o)
        {
            w.WriteStartElement("vertex", CoreNs);
            w.WriteAttributeString("x", pos[o].ToString("R", Inv));
            w.WriteAttributeString("y", pos[o + 1].ToString("R", Inv));
            w.WriteAttributeString("z", pos[o + 2].ToString("R", Inv));
            w.WriteEndElement();
        }

        // Names without characters XML cannot hold; never empty.
        private static string XmlSafe(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(ch).Append(s[i + 1]);
                    i++;
                }
                else if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
            }
            string t = sb.ToString().Trim();
            return t.Length > 0 ? t : "part";
        }
    }
}
