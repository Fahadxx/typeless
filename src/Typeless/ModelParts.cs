using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Typeless
{
    // One connected object inside a source part: triangles that share a vertex position
    // (exact float32 bits) belong together. Never spans two parts.
    public sealed class Piece
    {
        public int PartIndex { get; init; }
        public int[] Triangles { get; init; } = Array.Empty<int>();   // triangle indices into the part, ascending
        public double[] Min { get; init; } = new double[3];
        public double[] Max { get; init; } = new double[3];
        public string Name { get; init; } = "";

        public int TriangleCount => Triangles.Length;

        // Same format as Scene.BoundsText.
        public string BoundsText()
        {
            static string F(double d) => ((float)d).ToString("R", CultureInfo.InvariantCulture);
            return $"{F(Min[0])},{F(Min[1])},{F(Min[2])}..{F(Max[0])},{F(Max[1])},{F(Max[2])}";
        }
    }

    // The parts action: find the objects in a model, keep some of them whole. Nothing is cut or
    // moved, so kept vertices keep their float32 bits and triangles keep their order and winding.
    public static class ModelParts
    {
        // Pieces of every part, ordered by part, then by lowest triangle index. Union-find over
        // position keys (not vertex indices, which glb, obj and ply duplicate at hard edges).
        public static List<Piece> Find(Scene scene)
        {
            var pieces = new List<Piece>();
            for (int pi = 0; pi < scene.Parts.Count; pi++)
            {
                Part part = scene.Parts[pi];
                float[] pos = part.Positions;
                int[] tris = part.Triangles;
                int triCount = part.TriangleCount;
                if (triCount == 0) continue;

                // Vertex index -> position key id.
                int vertices = part.VertexCount;
                var keyOf = new int[vertices];
                var ids = new Dictionary<(int, int, int), int>(vertices);
                for (int v = 0; v < vertices; v++)
                {
                    int o = v * 3;
                    var key = (BitConverter.SingleToInt32Bits(pos[o]), BitConverter.SingleToInt32Bits(pos[o + 1]),
                        BitConverter.SingleToInt32Bits(pos[o + 2]));
                    if (!ids.TryGetValue(key, out int id))
                    {
                        id = ids.Count;
                        ids[key] = id;
                    }
                    keyOf[v] = id;
                }

                var parent = new int[ids.Count];
                for (int i = 0; i < parent.Length; i++) parent[i] = i;
                for (int t = 0; t < triCount; t++)
                {
                    int a = keyOf[tris[t * 3]];
                    Union(parent, a, keyOf[tris[t * 3 + 1]]);
                    Union(parent, a, keyOf[tris[t * 3 + 2]]);
                }

                // Local piece number per triangle, numbered in order of first triangle, plus counts.
                var localOfRoot = new int[parent.Length];
                Array.Fill(localOfRoot, -1);
                var triPiece = new int[triCount];
                var counts = new List<int>();
                for (int t = 0; t < triCount; t++)
                {
                    int root = Root(parent, keyOf[tris[t * 3]]);
                    int local = localOfRoot[root];
                    if (local < 0)
                    {
                        local = counts.Count;
                        localOfRoot[root] = local;
                        counts.Add(0);
                    }
                    counts[local]++;
                    triPiece[t] = local;
                }

                int k = counts.Count;
                var lists = new int[k][];
                var fill = new int[k];
                var mins = new double[k][];
                var maxs = new double[k][];
                for (int i = 0; i < k; i++)
                {
                    lists[i] = new int[counts[i]];
                    mins[i] = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
                    maxs[i] = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
                }
                for (int t = 0; t < triCount; t++)
                {
                    int local = triPiece[t];
                    lists[local][fill[local]++] = t;
                    double[] min = mins[local], max = maxs[local];
                    for (int c = 0; c < 3; c++)
                    {
                        int o = tris[t * 3 + c] * 3;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            double d = pos[o + axis];
                            if (d < min[axis]) min[axis] = d;
                            if (d > max[axis]) max[axis] = d;
                        }
                    }
                }

                for (int i = 0; i < k; i++)
                {
                    string name = part.Name.Length == 0
                        ? $"piece {(pieces.Count + 1).ToString(CultureInfo.InvariantCulture)}"
                        : k > 1 ? $"{part.Name} #{(i + 1).ToString(CultureInfo.InvariantCulture)}" : part.Name;
                    pieces.Add(new Piece { PartIndex = pi, Triangles = lists[i], Min = mins[i], Max = maxs[i], Name = name });
                }
            }
            return pieces;
        }

        private static int Root(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];   // path halving
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Root(parent, a);
            b = Root(parent, b);
            if (a == b) return;
            if (a < b) parent[b] = a; else parent[a] = b;
        }

        // A new scene with one part per kept piece, holding only the vertices it references: raw
        // floats in first-use order, triangles in order with the same winding, the part's name and color.
        public static Scene Extract(Scene src, IReadOnlyList<Piece> keep) => Extract(src, keep, new int[]?[src.Parts.Count]);

        // maps: one vertex remap per source part, allocated on first use and all -1 again after each
        // piece, so separate mode shares them across pieces (O(total triangles), not pieces x vertices).
        private static Scene Extract(Scene src, IReadOnlyList<Piece> keep, int[]?[] maps)
        {
            var scene = new Scene();
            foreach (Piece piece in keep)
            {
                Part part = src.Parts[piece.PartIndex];
                int[]? remap = maps[piece.PartIndex];
                if (remap == null)
                {
                    remap = new int[part.VertexCount];
                    Array.Fill(remap, -1);
                    maps[piece.PartIndex] = remap;
                }

                float[] pos = part.Positions;
                int[] tris = part.Triangles;
                var outTris = new int[piece.Triangles.Length * 3];
                var outPos = new List<float>();
                var used = new List<int>();
                int n = 0;
                foreach (int t in piece.Triangles)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int v = tris[t * 3 + c];
                        if (remap[v] < 0)
                        {
                            remap[v] = outPos.Count / 3;
                            used.Add(v);
                            outPos.Add(pos[v * 3]);
                            outPos.Add(pos[v * 3 + 1]);
                            outPos.Add(pos[v * 3 + 2]);
                        }
                        outTris[n++] = remap[v];
                    }
                }
                foreach (int v in used) remap[v] = -1;   // reset only the touched entries for the next piece

                scene.Parts.Add(new Part { Name = part.Name, Positions = outPos.ToArray(), Triangles = outTris, Color = part.Color });
            }
            foreach (string d in src.Dropped) scene.Drop(d);
            return scene;
        }

        // "Only one object found" when there is nothing to choose from.
        internal static void CheckSeveral(IReadOnlyList<Piece> pieces)
        {
            if (pieces.Count == 0) throw new InvalidOperationException("No triangles found");
            if (pieces.Count == 1) throw new InvalidOperationException("Only one object found");
        }

        // Reads src, finds its pieces and keeps those at pieceIndices (0-based, Find order).
        public static List<string> Keep(string src, IReadOnlyList<int> pieceIndices, bool separate)
        {
            Scene scene = ModelBackend.Read(src);
            return Keep(src, scene, Find(scene), pieceIndices, separate);
        }

        // One file: <stem>_parts.<ext>. Separate: <stem>_part_1.<ext>, _part_2, ... in kept order.
        // Same format as src, raw extension kept. Every file is written into one work dir and
        // verified before any is moved; if one fails nothing is saved.
        public static List<string> Keep(string src, Scene scene, IReadOnlyList<Piece> pieces, IReadOnlyList<int> keep, bool separate)
        {
            CheckSeveral(pieces);
            var chosen = new List<Piece>();
            var seen = new HashSet<int>();
            foreach (int i in keep)
            {
                if (i < 0 || i >= pieces.Count)
                    throw new ArgumentException($"Piece {(i + 1).ToString(CultureInfo.InvariantCulture)} does not exist (1..{pieces.Count.ToString(CultureInfo.InvariantCulture)}).");
                if (seen.Add(i)) chosen.Add(pieces[i]);
            }
            if (chosen.Count == 0) throw new ArgumentException("Nothing selected");
            if (!separate && chosen.Count == pieces.Count) throw new ArgumentException("Nothing to change");

            string target = Formats.Norm(src);
            string ext = Path.GetExtension(src).TrimStart('.');
            string dir = Path.GetDirectoryName(Path.GetFullPath(src)) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(src);

            var outputs = new List<(Scene Scene, string Name)>();
            if (separate)
            {
                var maps = new int[]?[scene.Parts.Count];
                for (int i = 0; i < chosen.Count; i++)
                    outputs.Add((Extract(scene, new[] { chosen[i] }, maps), $"{stem}_part_{(i + 1).ToString(CultureInfo.InvariantCulture)}"));
            }
            else
            {
                outputs.Add((Extract(scene, chosen), $"{stem}_parts"));
            }

            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var moves = new List<(string Temp, string Final)>();
                long tris = 0;
                foreach ((Scene sub, string name) in outputs)
                {
                    bool mtl = target == "obj" && sub.Parts.Any(p => p.Color != null);
                    (string finalMain, string? finalMtl) = ModelBackend.PickNames(dir, name, ext, mtl, taken);
                    taken.Add(finalMain);
                    if (finalMtl != null) taken.Add(finalMtl);

                    string tempMain = Path.Combine(work, Path.GetFileName(finalMain));
                    ModelBackend.Write(sub, tempMain, target);
                    ModelBackend.Verify(sub, tempMain, target);
                    moves.Add((tempMain, finalMain));
                    if (finalMtl != null)
                    {
                        string tempMtl = Path.Combine(work, Path.GetFileName(finalMtl));
                        if (!File.Exists(tempMtl))
                            throw new InvalidOperationException("Verification failed: the mtl file was not written.");
                        moves.Add((tempMtl, finalMtl));
                    }
                    tris += sub.TriangleCount;
                }

                Log.Write($"Model parts: {Path.GetFileName(src)}: kept {chosen.Count} of {pieces.Count} pieces, "
                    + $"{(separate ? "separate" : "one file")}, files={moves.Count}, triangles={tris}");
                if (scene.Dropped.Count > 0) Log.Write("Model: reader dropped " + string.Join(", ", scene.Dropped));

                var finals = new List<string>(moves.Count);
                try
                {
                    foreach ((string temp, string final) in moves)
                    {
                        File.Move(temp, final);
                        finals.Add(final);
                    }
                }
                catch
                {
                    // No partial output: remove what was already moved.
                    foreach (string f in finals) { try { File.Delete(f); } catch { } }
                    throw;
                }
                return finals;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // "1,3-5" (1-based piece numbers and inclusive ranges) or "box:x0,y0,x1,y1" in mm (pieces whose
        // XY bbox lies fully inside the box). Returns 0-based indices in piece order, no repeats.
        // The box may select nothing (Keep then says "Nothing selected"); a bad spec throws FormatException.
        public static List<int> ParseSpec(string spec, IReadOnlyList<Piece> pieces)
        {
            string s = spec.Trim();
            var result = new List<int>();
            if (s.StartsWith("box:", StringComparison.OrdinalIgnoreCase))
            {
                string[] v = s.Substring(4).Split(',');
                var c = new double[4];
                if (v.Length != 4) throw new FormatException($"Invalid parts spec: {spec} (box needs x0,y0,x1,y1)");
                for (int i = 0; i < 4; i++)
                {
                    if (!double.TryParse(v[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c[i]) || !double.IsFinite(c[i]))
                        throw new FormatException($"Invalid parts spec: {spec}");
                }
                double x0 = Math.Min(c[0], c[2]), x1 = Math.Max(c[0], c[2]);
                double y0 = Math.Min(c[1], c[3]), y1 = Math.Max(c[1], c[3]);
                for (int i = 0; i < pieces.Count; i++)
                {
                    Piece p = pieces[i];
                    if (p.Min[0] >= x0 && p.Max[0] <= x1 && p.Min[1] >= y0 && p.Max[1] <= y1) result.Add(i);
                }
                return result;
            }

            var picked = new bool[pieces.Count];
            foreach (string raw in s.Split(','))
            {
                string[] ends = raw.Trim().Split('-');
                if (ends.Length > 2
                    || !int.TryParse(ends[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int a)
                    || !int.TryParse(ends[^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int b))
                    throw new FormatException($"Invalid parts spec: {spec}");
                if (a > b) (a, b) = (b, a);
                if (a < 1 || b > pieces.Count)
                    throw new FormatException(
                        $"Invalid parts spec: {spec} (pieces are 1..{pieces.Count.ToString(CultureInfo.InvariantCulture)})");
                for (int n = a; n <= b; n++) picked[n - 1] = true;
            }
            for (int i = 0; i < picked.Length; i++)
                if (picked[i]) result.Add(i);
            return result;
        }
    }
}
