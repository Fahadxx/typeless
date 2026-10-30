using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Typeless
{
    // What ModelCut.Cut made: the pieces inside and outside the box (one part per source part per
    // side, empty sides left out), the number of closed cut outlines that were filled and the
    // number of outlines that did not close (holes in the mesh), which stay open.
    public sealed class CutResult
    {
        public Scene Inside { get; init; } = new Scene();
        public Scene Outside { get; init; } = new Scene();
        public int Caps { get; init; }
        public int OpenLoops { get; init; }
    }

    // The cut left outlines that could not be filled and the caller did not allow open faces.
    public sealed class CutOpenException : InvalidOperationException
    {
        public int Loops { get; }

        public CutOpenException(int loops) : base(Text(loops)) => Loops = loops;

        private static string Text(int loops) => loops == 1
            ? "1 cut face could not be filled: the model has gaps"
            : $"{loops.ToString(CultureInfo.InvariantCulture)} cut faces could not be filled: the model has gaps";
    }

    // The cut action: a rectangle seen along the x, y or z axis, extended through the whole model,
    // makes a box. Triangles are split on the box walls and the cut faces are filled, so a closed
    // model gives two closed pieces. Vertices off the cuts keep their float32 bits, and a part that
    // lies fully on one side is copied whole.
    public static class ModelCut
    {
        private const byte Inside = 0, Outside = 1, Cap = 2;

        // The rectangle axes (a, b) for a view axis: z (top) a = X, b = Y; y (front) a = X, b = Z;
        // x (side) a = Y, b = Z. Axis indices 0, 1, 2 are X, Y, Z.
        public static (int A, int B) Axes(char axis) => char.ToLowerInvariant(axis) switch
        {
            'z' => (0, 1),
            'y' => (0, 2),
            'x' => (1, 2),
            _ => throw new ArgumentException($"Invalid cut axis: {axis}"),
        };

        // The box is a0 <= a <= a1, b0 <= b <= b1 (mm, either order), unlimited along the view axis.
        public static CutResult Cut(Scene scene, char axis, double a0, double b0, double a1, double b1)
        {
            (int ia, int ib) = Axes(axis);
            if (a0 > a1) (a0, a1) = (a1, a0);
            if (b0 > b1) (b0, b1) = (b1, b0);
            // Wall order a0, a1, b0, b1. Lower walls keep a > w inside, upper walls a < w.
            var walls = new (int Axis, double Value, bool Lower)[] { (ia, a0, true), (ia, a1, false), (ib, b0, true), (ib, b1, false) };

            var inside = new Scene();
            var outside = new Scene();
            int caps = 0, open = 0;
            var active = new List<(int Axis, double Value, bool Lower)>(4);
            foreach (Part part in scene.Parts)
            {
                if (part.TriangleCount == 0) continue;
                (double[] min, double[] max) = Bounds(part);
                if (max[ia] <= a0 || min[ia] >= a1 || max[ib] <= b0 || min[ib] >= b1)
                {
                    outside.Parts.Add(Copy(part));
                    continue;
                }

                // Only walls that pass through the part's bounds cut; a wall at or past them is skipped.
                active.Clear();
                foreach (var w in walls)
                    if (w.Lower ? w.Value > min[w.Axis] : w.Value < max[w.Axis]) active.Add(w);
                if (active.Count == 0)
                {
                    inside.Parts.Add(Copy(part));
                    continue;
                }

                var mesh = new Mesh(part);
                foreach (var w in active) mesh.Clip(w.Axis, w.Value, w.Lower);
                caps += mesh.Caps;
                open += mesh.Open;
                mesh.Emit(part, inside, outside);
            }
            foreach (string d in scene.Dropped)
            {
                inside.Drop(d);
                outside.Drop(d);
            }
            return new CutResult { Inside = inside, Outside = outside, Caps = caps, OpenLoops = open };
        }

        // Bounding box over the vertices the triangles reference.
        private static (double[] Min, double[] Max) Bounds(Part part)
        {
            var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            float[] pos = part.Positions;
            foreach (int v in part.Triangles)
            {
                int o = v * 3;
                for (int k = 0; k < 3; k++)
                {
                    double c = pos[o + k];
                    if (c < min[k]) min[k] = c;
                    if (c > max[k]) max[k] = c;
                }
            }
            return (min, max);
        }

        // The part as is: same floats, same triangles, same name and color.
        private static Part Copy(Part part) => new Part
        {
            Name = part.Name,
            Positions = (float[])part.Positions.Clone(),
            Triangles = (int[])part.Triangles.Clone(),
            Color = part.Color,
        };

        // Appends one triangle with its label; a triangle with two equal corners has no area and is dropped.
        private static void Add3(List<int> tris, List<byte> labels, int a, int b, int c, byte label)
        {
            if (a == b || b == c || a == c) return;
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
            labels.Add(label);
        }

        // One closed cut outline projected on the wall plane, oriented so that outer outlines have a
        // positive area and holes a negative one.
        private sealed class Ring
        {
            public int[] V = Array.Empty<int>();
            public double[] X = Array.Empty<double>(), Y = Array.Empty<double>();
            public double Area, MinX, MinY, MaxX, MaxY;
            public readonly List<Ring> Holes = new List<Ring>();

            public bool Contains(double px, double py)
            {
                if (px < MinX || px > MaxX || py < MinY || py > MaxY) return false;
                bool inside = false;
                for (int i = 0, j = X.Length - 1; i < X.Length; j = i++)
                {
                    if ((Y[i] > py) != (Y[j] > py) && px < (X[j] - X[i]) * (py - Y[i]) / (Y[j] - Y[i]) + X[i])
                        inside = !inside;
                }
                return inside;
            }
        }

        // The working mesh of one part that straddles a wall: positions deduplicated by exact bits
        // (so shared corners have one index even where the source duplicates them), and every
        // triangle labeled inside (inside every wall so far), outside or cap.
        private sealed class Mesh
        {
            private readonly List<float> pos;
            private readonly Dictionary<(int, int, int), int> ids;
            private List<int> tris;
            private List<byte> labels;
            public int Caps, Open;

            public Mesh(Part part)
            {
                int n = part.VertexCount;
                pos = new List<float>(n * 3 + 3072);
                ids = new Dictionary<(int, int, int), int>(n);
                float[] src = part.Positions;
                var map = new int[n];
                for (int v = 0; v < n; v++) map[v] = Add(src[v * 3], src[v * 3 + 1], src[v * 3 + 2]);
                int[] t = part.Triangles;
                tris = new List<int>(t.Length + t.Length / 8 + 3);
                labels = new List<byte>(t.Length / 3 + t.Length / 24 + 1);
                for (int i = 0; i < t.Length; i += 3) Add3(tris, labels, map[t[i]], map[t[i + 1]], map[t[i + 2]], Inside);
            }

            private int Add(float x, float y, float z)
            {
                var key = (BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y), BitConverter.SingleToInt32Bits(z));
                if (ids.TryGetValue(key, out int i)) return i;
                i = pos.Count / 3;
                pos.Add(x);
                pos.Add(y);
                pos.Add(z);
                ids[key] = i;
                return i;
            }

            // Any vertex (inside, outside or from an earlier wall) with exactly this coordinate.
            private bool OnPlane(int k, float w)
            {
                for (int o = k; o < pos.Count; o += 3)
                    if (pos[o] == w) return true;   // float ==, so -0 and +0 both count
                return false;
            }

            // Canonical order of two vertices by position bits (x, then y, then z), so both triangles
            // that share an edge interpolate it from the same end.
            private bool Before(int i, int j)
            {
                for (int c = 0; c < 3; c++)
                {
                    int bi = BitConverter.SingleToInt32Bits(pos[i * 3 + c]), bj = BitConverter.SingleToInt32Bits(pos[j * 3 + c]);
                    if (bi != bj) return bi < bj;
                }
                return i < j;
            }

            // The vertex where edge i-j crosses the wall: the wall coordinate is exactly w, the other
            // two are interpolated in double from the canonical end and cast once.
            private int Cross(int i, int j, int k, float w, Dictionary<long, int> cache)
            {
                long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                if (cache.TryGetValue(key, out int m)) return m;
                int p = i, q = j;
                if (Before(q, p)) (p, q) = (q, p);
                double pk = pos[p * 3 + k], qk = pos[q * 3 + k];
                double t = (w - pk) / (qk - pk);
                var r = new float[3];
                for (int c = 0; c < 3; c++)
                {
                    if (c == k) { r[c] = w; continue; }
                    double a = pos[p * 3 + c], b = pos[q * 3 + c];
                    r[c] = (float)(a + (b - a) * t);
                }
                m = Add(r[0], r[1], r[2]);
                cache[key] = m;
                return m;
            }

            // Splits every triangle at the wall on axis k (lower: inside is > wall, else < wall), then
            // fills the cut outlines of the inside and cap triangles, so the next wall clips the caps too.
            public void Clip(int k, double wall, bool lower)
            {
                // No vertex may lie on the wall: classification is strict, so there are no zero-length
                // cut segments and no t of 0 or 1.
                float w = (float)wall;
                while (OnPlane(k, w)) w = MathF.BitIncrement(w);
                int baseV = pos.Count / 3;

                var cache = new Dictionary<long, int>();
                var outTris = new List<int>(tris.Count + tris.Count / 8 + 3);
                var outLabels = new List<byte>(labels.Count + labels.Count / 8 + 1);
                var segFrom = new List<int>();
                var segTo = new List<int>();
                var v = new int[3];
                var f = new bool[3];
                var ip = new int[4];
                var op = new int[4];
                int count = labels.Count;
                for (int t = 0; t < count; t++)
                {
                    byte label = labels[t];
                    for (int c = 0; c < 3; c++)
                    {
                        v[c] = tris[t * 3 + c];
                        float x = pos[v[c] * 3 + k];
                        f[c] = lower ? x > w : x < w;
                    }
                    if (f[0] == f[1] && f[1] == f[2])
                    {
                        if (f[0]) Add3(outTris, outLabels, v[0], v[1], v[2], label);
                        else if (label != Cap) Add3(outTris, outLabels, v[0], v[1], v[2], Outside);
                        continue;   // a cap fully beyond the wall would sit between two outside pieces
                    }

                    // Walk the edges in winding order: the inside fragment gets ..., exit, entry, ...
                    // and the beyond fragment ..., entry, exit, ... (both convex, fanned).
                    int ni = 0, no = 0, entry = -1, exit = -1;
                    for (int e = 0; e < 3; e++)
                    {
                        int e2 = e == 2 ? 0 : e + 1;
                        if (f[e]) ip[ni++] = v[e]; else op[no++] = v[e];
                        if (f[e] != f[e2])
                        {
                            int m = Cross(v[e], v[e2], k, w, cache);
                            ip[ni++] = m;
                            op[no++] = m;
                            if (f[e]) exit = m; else entry = m;
                        }
                    }
                    for (int j = 1; j + 1 < ni; j++) Add3(outTris, outLabels, ip[0], ip[j], ip[j + 1], label);
                    if (label != Cap)
                        for (int j = 1; j + 1 < no; j++) Add3(outTris, outLabels, op[0], op[j], op[j + 1], Outside);
                    // The inside fragment has the edge exit -> entry on the wall; the cap needs it the
                    // other way round, entry -> exit, so the closed inside surface pairs every edge.
                    if (label != Outside && entry != exit)
                    {
                        segFrom.Add(entry);
                        segTo.Add(exit);
                    }
                }
                tris = outTris;
                labels = outLabels;
                Fill(segFrom, segTo, baseV, k, lower);
            }

            // Chains the cut segments into outlines by vertex, then triangulates the closed ones in
            // the wall plane, holes included. Every segment vertex is new at this wall (index >= baseV).
            private void Fill(List<int> from, List<int> to, int baseV, int k, bool lower)
            {
                int n = from.Count;
                if (n == 0) return;
                int span = pos.Count / 3 - baseV;
                var head = new int[span];
                Array.Fill(head, -1);
                var link = new int[n];
                var indeg = new int[span];
                for (int s = n - 1; s >= 0; s--)
                {
                    int a = from[s] - baseV;
                    link[s] = head[a];
                    head[a] = s;
                    indeg[to[s] - baseV]++;
                }

                var used = new bool[n];
                var chain = new List<int>();
                var loops = new List<int[]>();

                // Follows unused segments from s; true when it comes back to its start vertex. A vertex
                // may have several outgoing segments where crossings merged.
                bool Walk(int s)
                {
                    chain.Clear();
                    int start = from[s];
                    chain.Add(start);
                    used[s] = true;
                    int cur = to[s];
                    while (cur != start)
                    {
                        int x = cur - baseV;
                        int q = head[x];
                        while (q >= 0 && used[q]) q = link[q];
                        head[x] = q;
                        if (q < 0) return false;
                        chain.Add(cur);
                        used[q] = true;
                        cur = to[q];
                    }
                    return true;
                }

                // Open chains from their start first, so one gap is not counted twice.
                for (int s = 0; s < n; s++)
                {
                    if (used[s] || indeg[from[s] - baseV] != 0) continue;
                    Walk(s);
                    Open++;
                }
                for (int s = 0; s < n; s++)
                {
                    if (used[s]) continue;
                    if (!Walk(s))
                    {
                        Open++;
                        continue;
                    }
                    if (chain.Count >= 3) loops.Add(chain.ToArray());   // a -> b -> a is a fin that cancels out
                }
                if (loops.Count == 0) return;

                // Plane axes (u, v) with u x v = +k. The caps face the beyond side: -k for a lower wall.
                // There each outline is walked backwards, so outer outlines come out counter-clockwise in
                // (u, v) like on an upper wall, and the triangles are flipped back at the end.
                int u = k == 0 ? 1 : k == 1 ? 2 : 0;
                int vAxis = k == 0 ? 2 : k == 1 ? 0 : 1;
                bool flip = lower;
                var outers = new List<Ring>();
                var holes = new List<Ring>();
                foreach (int[] loop in loops)
                {
                    if (flip) Array.Reverse(loop);
                    var ring = new Ring { V = loop, X = new double[loop.Length], Y = new double[loop.Length] };
                    ring.MinX = ring.MinY = double.PositiveInfinity;
                    ring.MaxX = ring.MaxY = double.NegativeInfinity;
                    for (int i = 0; i < loop.Length; i++)
                    {
                        double x = pos[loop[i] * 3 + u], y = pos[loop[i] * 3 + vAxis];
                        ring.X[i] = x;
                        ring.Y[i] = y;
                        if (x < ring.MinX) ring.MinX = x;
                        if (x > ring.MaxX) ring.MaxX = x;
                        if (y < ring.MinY) ring.MinY = y;
                        if (y > ring.MaxY) ring.MaxY = y;
                    }
                    double area = 0;
                    for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                        area += ring.X[j] * ring.Y[i] - ring.X[i] * ring.Y[j];
                    ring.Area = area / 2;
                    if (ring.Area >= 0) outers.Add(ring); else holes.Add(ring);   // zero area: filled with flat triangles
                }

                // Each hole goes to the smallest outer outline that contains it.
                foreach (Ring hole in holes)
                {
                    Ring? best = null;
                    foreach (Ring o in outers)
                    {
                        if (best != null && o.Area >= best.Area) continue;
                        if (o.Contains(hole.X[0], hole.Y[0])) best = o;
                    }
                    if (best == null) Open++;   // a hole with no outline around it cannot be filled
                    else best.Holes.Add(hole);
                }

                foreach (Ring o in outers)
                {
                    int total = o.V.Length;
                    foreach (Ring h in o.Holes) total += h.V.Length;
                    var xy = new double[total * 2];
                    var vid = new int[total];
                    var starts = new List<int>(o.Holes.Count + 1);
                    int at = 0;
                    void Put(Ring r)
                    {
                        starts.Add(at);
                        for (int i = 0; i < r.V.Length; i++, at++)
                        {
                            vid[at] = r.V[i];
                            xy[at * 2] = r.X[i];
                            xy[at * 2 + 1] = r.Y[i];
                        }
                    }
                    Put(o);
                    foreach (Ring h in o.Holes) Put(h);

                    List<int> res = Earcut.Triangulate(xy, starts);
                    for (int i = 0; i + 2 < res.Count; i += 3)
                    {
                        int a = vid[res[i]], b = vid[res[i + 1]], c = vid[res[i + 2]];
                        if (flip) (b, c) = (c, b);
                        Add3(tris, labels, a, b, c, Cap);
                    }
                    Caps += 1 + o.Holes.Count;
                }
            }

            // Inside piece: inside and cap triangles. Outside piece: outside triangles plus the caps
            // reversed, so the two pieces together are the source surface. A part that ended up all on
            // one side is copied whole instead.
            public void Emit(Part src, Scene inside, Scene outside)
            {
                int nIn = 0, nOut = 0, nCap = 0;
                foreach (byte l in labels)
                {
                    if (l == Inside) nIn++;
                    else if (l == Outside) nOut++;
                    else nCap++;
                }
                if (nIn + nCap == 0)
                {
                    outside.Parts.Add(Copy(src));
                    return;
                }
                if (nOut + nCap == 0)
                {
                    inside.Parts.Add(Copy(src));
                    return;
                }
                Part? a = Build(src, true);
                if (a != null) inside.Parts.Add(a);
                Part? b = Build(src, false);
                if (b != null) outside.Parts.Add(b);
            }

            // Only the vertices the side uses, raw floats in first-use order.
            private Part? Build(Part src, bool insideSide)
            {
                var remap = new int[pos.Count / 3];
                Array.Fill(remap, -1);
                var outPos = new List<float>();
                var outTris = new List<int>();
                int Map(int v)
                {
                    if (remap[v] < 0)
                    {
                        remap[v] = outPos.Count / 3;
                        outPos.Add(pos[v * 3]);
                        outPos.Add(pos[v * 3 + 1]);
                        outPos.Add(pos[v * 3 + 2]);
                    }
                    return remap[v];
                }
                for (int t = 0; t < labels.Count; t++)
                {
                    byte l = labels[t];
                    if (insideSide ? l == Outside : l == Inside) continue;
                    int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                    if (a == b || b == c || a == c) continue;
                    if (!insideSide && l == Cap) (b, c) = (c, b);
                    outTris.Add(Map(a));
                    outTris.Add(Map(b));
                    outTris.Add(Map(c));
                }
                if (outTris.Count == 0) return null;
                return new Part { Name = src.Name, Positions = outPos.ToArray(), Triangles = outTris.ToArray(), Color = src.Color };
            }
        }

        // keep: both, inside or outside. Names <stem>_inside.<ext> and <stem>_outside.<ext>, same
        // format as src, raw extension kept. Every file is written into one work dir and verified
        // before any is moved; if one fails nothing is saved.
        public static (List<string> Paths, CutResult Result) Save(string src, Scene scene, char axis,
            double a0, double b0, double a1, double b1, string keep, bool allowOpen)
        {
            keep = keep.Trim().ToLowerInvariant();
            if (keep != "both" && keep != "inside" && keep != "outside")
                throw new ArgumentException($"Invalid keep: {keep} (both, inside or outside)");
            if (a0 > a1) (a0, a1) = (a1, a0);
            if (b0 > b1) (b0, b1) = (b1, b0);
            if (scene.TriangleCount == 0) throw new InvalidOperationException("No triangles found");

            CutResult result = Cut(scene, axis, a0, b0, a1, b1);
            if (result.Inside.TriangleCount == 0) throw new ArgumentException("Nothing inside the box");
            if (result.Outside.TriangleCount == 0) throw new ArgumentException("Everything is inside the box");
            if (result.OpenLoops > 0 && !allowOpen) throw new CutOpenException(result.OpenLoops);

            string target = Formats.Norm(src);
            string ext = Path.GetExtension(src).TrimStart('.');
            string dir = Path.GetDirectoryName(Path.GetFullPath(src)) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(src);

            var outputs = new List<(Scene Scene, string Name)>();
            if (keep != "outside") outputs.Add((result.Inside, $"{stem}_inside"));
            if (keep != "inside") outputs.Add((result.Outside, $"{stem}_outside"));

            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var moves = new List<(string Temp, string Final)>();
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
                }

                static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);
                Log.Write($"Model cut: {Path.GetFileName(src)}: axis={char.ToLowerInvariant(axis)} box={F(a0)},{F(b0)}..{F(a1)},{F(b1)} "
                    + $"inside={result.Inside.TriangleCount} outside={result.Outside.TriangleCount} caps={result.Caps} "
                    + $"open={result.OpenLoops}, keep={keep}, files={moves.Count}");
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
                return (finals, result);
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // "x", "y" or "z", any case.
        public static char ParseAxis(string text)
        {
            string s = text.Trim().ToLowerInvariant();
            if (s == "x" || s == "y" || s == "z") return s[0];
            throw new FormatException($"Invalid cut axis: {text} (x, y or z)");
        }

        // "a0,b0,a1,b1" in mm, invariant culture, finite, corners in either order. A rectangle with no
        // width or no height is invalid.
        public static (double A0, double B0, double A1, double B1) ParseBox(string spec)
        {
            string[] v = spec.Trim().Split(',');
            if (v.Length != 4) throw new FormatException($"Invalid cut box: {spec} (needs a0,b0,a1,b1)");
            var c = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(v[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c[i]) || !double.IsFinite(c[i]))
                    throw new FormatException($"Invalid cut box: {spec}");
            }
            if (c[0] == c[2] || c[1] == c[3]) throw new FormatException($"Invalid cut box: {spec} (no width or no height)");
            return (c[0], c[1], c[2], c[3]);
        }

        // Ear clipping with hole bridging, a port of mapbox earcut (ISC license) with these changes:
        // - Rings are linked in the order given (outer counter-clockwise, holes clockwise), never
        //   reoriented by their area, so the triangles keep the outline's edge directions.
        // - Point filtering removes only exact duplicates and back-tracking spikes, never collinear
        //   points: cut outlines are full of them and each one is a vertex a neighbour triangle uses.
        //   Zero-area ears are still never clipped.
        // - Local intersection curing only takes strict crossings (it drops two boundary edges).
        // - Passes and splits run from an explicit stack, not recursion.
        // - A remnant that cannot be split (all collinear) is closed with a fan of zero-area triangles,
        //   so every boundary edge still has its partner.
        // Triangles come out counter-clockwise.
        internal static class Earcut
        {
            private sealed class Node
            {
                public readonly int I;
                public readonly double X, Y;
                public Node Prev = null!, Next = null!;
                public int Z;
                public Node? PrevZ, NextZ;
                public bool Steiner;

                public Node(int i, double x, double y)
                {
                    I = i;
                    X = x;
                    Y = y;
                }
            }

            // xy: x, y per point. ringStarts: the first point of each ring; ring 0 is the outer ring.
            // Returns point indices, 3 per triangle.
            public static List<int> Triangulate(double[] xy, IReadOnlyList<int> ringStarts)
            {
                var tris = new List<int>();
                int n = xy.Length / 2;
                int outerEnd = ringStarts.Count > 1 ? ringStarts[1] : n;
                Node? outer = LinkRing(xy, 0, outerEnd);
                if (outer == null || outer.Next == outer.Prev) return tris;
                if (ringStarts.Count > 1) outer = EliminateHoles(xy, ringStarts, outer, n);

                // z-order hashing for larger outlines.
                double minX = 0, minY = 0, invSize = 0;
                if (n > 80)
                {
                    minX = double.PositiveInfinity;
                    minY = double.PositiveInfinity;
                    double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                    for (int i = 0; i < n; i++)
                    {
                        double x = xy[i * 2], y = xy[i * 2 + 1];
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                    invSize = Math.Max(maxX - minX, maxY - minY);
                    invSize = invSize != 0 ? 32767 / invSize : 0;
                }

                Run(outer, tris, minX, minY, invSize);
                return tris;
            }

            private static Node? LinkRing(double[] xy, int start, int end)
            {
                Node? last = null;
                for (int i = start; i < end; i++) last = Insert(i, xy[i * 2], xy[i * 2 + 1], last);
                if (last != null && Same(last, last.Next))
                {
                    Remove(last);
                    last = last.Next;
                }
                return last;
            }

            // earcutLinked: pass 0 plain, pass 1 after filtering, pass 2 after curing, then split.
            private static void Run(Node? start, List<int> tris, double minX, double minY, double invSize)
            {
                var work = new Stack<(Node? Ear, int Pass)>();
                work.Push((start, 0));
                while (work.Count > 0)
                {
                    (Node? ear, int pass) = work.Pop();
                    if (ear == null) continue;
                    if (pass == 0 && invSize != 0) IndexCurve(ear, minX, minY, invSize);

                    Node stop = ear;
                    while (ear.Prev != ear.Next)
                    {
                        Node prev = ear.Prev, next = ear.Next;
                        if (invSize != 0 ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear))
                        {
                            tris.Add(prev.I);
                            tris.Add(ear.I);
                            tris.Add(next.I);
                            Remove(ear);
                            ear = next.Next;
                            stop = next.Next;
                            continue;
                        }
                        ear = next;
                        if (ear == stop)
                        {
                            if (pass == 0) work.Push((Filter(ear, null), 1));
                            else if (pass == 1) work.Push((CureLocalIntersections(Filter(ear, null), tris), 2));
                            else SplitEarcut(ear, tris, work);
                            break;
                        }
                    }
                }
            }

            private static bool IsEar(Node ear)
            {
                Node a = ear.Prev, b = ear, c = ear.Next;
                if (Area(a, b, c) >= 0) return false;   // reflex or zero area
                double ax = a.X, bx = b.X, cx = c.X, ay = a.Y, by = b.Y, cy = c.Y;
                double x0 = Math.Min(ax, Math.Min(bx, cx)), y0 = Math.Min(ay, Math.Min(by, cy));
                double x1 = Math.Max(ax, Math.Max(bx, cx)), y1 = Math.Max(ay, Math.Max(by, cy));
                Node p = c.Next;
                while (p != a)
                {
                    if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1
                        && PointInTriangleExceptFirst(ax, ay, bx, by, cx, cy, p.X, p.Y)
                        && Area(p.Prev, p, p.Next) >= 0) return false;
                    p = p.Next;
                }
                return true;
            }

            private static bool IsEarHashed(Node ear, double minX, double minY, double invSize)
            {
                Node a = ear.Prev, b = ear, c = ear.Next;
                if (Area(a, b, c) >= 0) return false;
                double ax = a.X, bx = b.X, cx = c.X, ay = a.Y, by = b.Y, cy = c.Y;
                double x0 = Math.Min(ax, Math.Min(bx, cx)), y0 = Math.Min(ay, Math.Min(by, cy));
                double x1 = Math.Max(ax, Math.Max(bx, cx)), y1 = Math.Max(ay, Math.Max(by, cy));
                int minZ = ZOrder(x0, y0, minX, minY, invSize), maxZ = ZOrder(x1, y1, minX, minY, invSize);

                bool Blocks(Node q) =>
                    q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1 && q != a && q != c
                    && PointInTriangleExceptFirst(ax, ay, bx, by, cx, cy, q.X, q.Y)
                    && Area(q.Prev, q, q.Next) >= 0;

                Node? p = ear.PrevZ, n = ear.NextZ;
                while (p != null && p.Z >= minZ && n != null && n.Z <= maxZ)
                {
                    if (Blocks(p)) return false;
                    p = p.PrevZ;
                    if (Blocks(n)) return false;
                    n = n.NextZ;
                }
                while (p != null && p.Z >= minZ)
                {
                    if (Blocks(p)) return false;
                    p = p.PrevZ;
                }
                while (n != null && n.Z <= maxZ)
                {
                    if (Blocks(n)) return false;
                    n = n.NextZ;
                }
                return true;
            }

            // Clips a - p - p.next - b where a-p and p.next-b cross. Strict crossings only.
            private static Node CureLocalIntersections(Node start, List<int> tris)
            {
                Node p = start;
                do
                {
                    Node a = p.Prev, b = p.Next.Next;
                    if (!Same(a, b) && CrossesStrictly(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
                    {
                        tris.Add(a.I);
                        tris.Add(p.I);
                        tris.Add(b.I);
                        Remove(p);
                        Remove(p.Next);
                        p = start = b;
                    }
                    p = p.Next;
                } while (p != start);
                return Filter(p, null);
            }

            // Splits the polygon at a valid diagonal and queues both halves; with none, closes the rest
            // with a fan (zero-area triangles when it is all collinear).
            private static void SplitEarcut(Node start, List<int> tris, Stack<(Node? Ear, int Pass)> work)
            {
                bool flat = true;
                Node s = start;
                do
                {
                    if (Area(s.Prev, s, s.Next) != 0) { flat = false; break; }
                    s = s.Next;
                } while (s != start);

                if (!flat)
                {
                    Node a = start;
                    do
                    {
                        Node b = a.Next.Next;
                        while (b != a.Prev)
                        {
                            if (a.I != b.I && IsValidDiagonal(a, b))
                            {
                                Node c = SplitPolygon(a, b);
                                a = Filter(a, a.Next);
                                c = Filter(c, c.Next);
                                work.Push((c, 0));
                                work.Push((a, 0));
                                return;
                            }
                            b = b.Next;
                        }
                        a = a.Next;
                    } while (a != start);
                }

                for (Node p = start.Next; p.Next != start; p = p.Next)
                {
                    tris.Add(start.I);
                    tris.Add(p.I);
                    tris.Add(p.Next.I);
                }
            }

            private static Node EliminateHoles(double[] xy, IReadOnlyList<int> ringStarts, Node outer, int n)
            {
                var queue = new List<Node>();
                for (int i = 1; i < ringStarts.Count; i++)
                {
                    int start = ringStarts[i];
                    int end = i + 1 < ringStarts.Count ? ringStarts[i + 1] : n;
                    Node? list = LinkRing(xy, start, end);
                    if (list == null) continue;
                    if (list == list.Next) list.Steiner = true;
                    queue.Add(Leftmost(list));
                }
                queue.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y != q.Y ? p.Y.CompareTo(q.Y) : p.I.CompareTo(q.I));
                foreach (Node hole in queue) outer = EliminateHole(hole, outer);
                return outer;
            }

            private static Node EliminateHole(Node hole, Node outer)
            {
                Node? bridge = FindHoleBridge(hole, outer);
                if (bridge == null) return outer;
                Node bridgeReverse = SplitPolygon(bridge, hole);
                Filter(bridgeReverse, bridgeReverse.Next);
                return Filter(bridge, bridge.Next);
            }

            // David Eberly's algorithm for finding a bridge between a hole and the outer polygon.
            private static Node? FindHoleBridge(Node hole, Node outer)
            {
                Node p = outer;
                double hx = hole.X, hy = hole.Y;
                double qx = double.NegativeInfinity;
                Node? m = null;

                // A segment hit by a ray from the hole's leftmost point to the left; its endpoint with
                // the lesser x is the candidate, unless the ray hits a vertex.
                if (Same(hole, p)) return p;
                do
                {
                    if (Same(hole, p.Next)) return p.Next;
                    if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
                    {
                        double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                        if (x <= hx && x > qx)
                        {
                            qx = x;
                            m = p.X < p.Next.X ? p : p.Next;
                            if (x == hx) return m;   // the hole touches the outer segment
                        }
                    }
                    p = p.Next;
                } while (p != outer);
                if (m == null) return null;

                // Look for points inside the triangle hole point, segment intersection and endpoint;
                // take the one with the smallest angle to the ray as the connection.
                Node stop = m;
                double mx = m.X, my = m.Y;
                double tanMin = double.PositiveInfinity;
                p = m;
                do
                {
                    if (hx >= p.X && p.X >= mx && hx != p.X
                        && PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
                    {
                        double tan = Math.Abs(hy - p.Y) / (hx - p.X);
                        if (LocallyInside(p, hole)
                            && (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p))))))
                        {
                            m = p;
                            tanMin = tan;
                        }
                    }
                    p = p.Next;
                } while (p != stop);
                return m;
            }

            private static bool SectorContainsSector(Node m, Node p) =>
                Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;

            private static void IndexCurve(Node start, double minX, double minY, double invSize)
            {
                Node p = start;
                do
                {
                    if (p.Z == 0) p.Z = ZOrder(p.X, p.Y, minX, minY, invSize);
                    p.PrevZ = p.Prev;
                    p.NextZ = p.Next;
                    p = p.Next;
                } while (p != start);
                p.PrevZ!.NextZ = null;
                p.PrevZ = null;
                SortLinked(p);
            }

            // Simon Tatham's linked list merge sort on the z links.
            private static Node? SortLinked(Node? list)
            {
                int numMerges;
                int inSize = 1;
                do
                {
                    Node? p = list;
                    list = null;
                    Node? tail = null;
                    numMerges = 0;
                    while (p != null)
                    {
                        numMerges++;
                        Node? q = p;
                        int pSize = 0;
                        for (int i = 0; i < inSize; i++)
                        {
                            pSize++;
                            q = q.NextZ;
                            if (q == null) break;
                        }
                        int qSize = inSize;
                        while (pSize > 0 || (qSize > 0 && q != null))
                        {
                            Node e;
                            if (pSize != 0 && (qSize == 0 || q == null || p!.Z <= q.Z))
                            {
                                e = p!;
                                p = p!.NextZ;
                                pSize--;
                            }
                            else
                            {
                                e = q!;
                                q = q!.NextZ;
                                qSize--;
                            }
                            if (tail != null) tail.NextZ = e;
                            else list = e;
                            e.PrevZ = tail;
                            tail = e;
                        }
                        p = q;
                    }
                    if (tail != null) tail.NextZ = null;
                    inSize *= 2;
                } while (numMerges > 1);
                return list;
            }

            // z-order of a point, coordinates mapped to 15 bits.
            private static int ZOrder(double px, double py, double minX, double minY, double invSize)
            {
                int x = (int)((px - minX) * invSize);
                int y = (int)((py - minY) * invSize);
                x = (x | (x << 8)) & 0x00FF00FF;
                x = (x | (x << 4)) & 0x0F0F0F0F;
                x = (x | (x << 2)) & 0x33333333;
                x = (x | (x << 1)) & 0x55555555;
                y = (y | (y << 8)) & 0x00FF00FF;
                y = (y | (y << 4)) & 0x0F0F0F0F;
                y = (y | (y << 2)) & 0x33333333;
                y = (y | (y << 1)) & 0x55555555;
                return x | (y << 1);
            }

            private static Node Leftmost(Node start)
            {
                Node p = start, leftmost = start;
                do
                {
                    if (p.X < leftmost.X || (p.X == leftmost.X && p.Y < leftmost.Y)) leftmost = p;
                    p = p.Next;
                } while (p != start);
                return leftmost;
            }

            private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
                (cx - px) * (ay - py) >= (ax - px) * (cy - py)
                && (ax - px) * (by - py) >= (bx - px) * (ay - py)
                && (bx - px) * (cy - py) >= (cx - px) * (by - py);

            private static bool PointInTriangleExceptFirst(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
                !(ax == px && ay == py) && PointInTriangle(ax, ay, bx, by, cx, cy, px, py);

            // A diagonal a-b that lies inside the polygon and crosses no edge.
            private static bool IsValidDiagonal(Node a, Node b) =>
                a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b)
                && ((LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b)
                        && (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0))
                    || (Same(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0));

            // Signed area of triangle p, q, r (negative when counter-clockwise).
            private static double Area(Node p, Node q, Node r) =>
                (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);

            private static bool Same(Node p, Node q) => p.X == q.X && p.Y == q.Y;

            private static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

            private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
            {
                int o1 = Sign(Area(p1, q1, p2));
                int o2 = Sign(Area(p1, q1, q2));
                int o3 = Sign(Area(p2, q2, p1));
                int o4 = Sign(Area(p2, q2, q1));
                if (o1 != o2 && o3 != o4) return true;
                if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
                if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
                if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
                if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
                return false;
            }

            private static bool CrossesStrictly(Node p1, Node q1, Node p2, Node q2)
            {
                int o1 = Sign(Area(p1, q1, p2));
                int o2 = Sign(Area(p1, q1, q2));
                int o3 = Sign(Area(p2, q2, p1));
                int o4 = Sign(Area(p2, q2, q1));
                return o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0 && o1 != o2 && o3 != o4;
            }

            // q lies on segment p-r (given the three are collinear).
            private static bool OnSegment(Node p, Node q, Node r) =>
                q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

            private static bool IntersectsPolygon(Node a, Node b)
            {
                Node p = a;
                do
                {
                    if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b)) return true;
                    p = p.Next;
                } while (p != a);
                return false;
            }

            private static bool LocallyInside(Node a, Node b) =>
                Area(a.Prev, a, a.Next) < 0
                    ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0
                    : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;

            private static bool MiddleInside(Node a, Node b)
            {
                Node p = a;
                bool inside = false;
                double px = (a.X + b.X) / 2, py = (a.Y + b.Y) / 2;
                do
                {
                    if ((p.Y > py) != (p.Next.Y > py) && p.Next.Y != p.Y
                        && px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X)
                        inside = !inside;
                    p = p.Next;
                } while (p != a);
                return inside;
            }

            // Links a to b with a two-way bridge; returns the copy of b on the other side.
            private static Node SplitPolygon(Node a, Node b)
            {
                var a2 = new Node(a.I, a.X, a.Y);
                var b2 = new Node(b.I, b.X, b.Y);
                Node an = a.Next, bp = b.Prev;
                a.Next = b;
                b.Prev = a;
                a2.Next = an;
                an.Prev = a2;
                b2.Next = a2;
                a2.Prev = b2;
                bp.Next = b2;
                b2.Prev = bp;
                return b2;
            }

            // Removes exact duplicates and back-tracking spikes (prev and next at the same point). Never
            // collinear points.
            private static Node Filter(Node start, Node? end)
            {
                end ??= start;
                Node p = start;
                bool again;
                do
                {
                    again = false;
                    if (!p.Steiner && (Same(p, p.Next) || Same(p.Prev, p.Next)))
                    {
                        Remove(p);
                        p = end = p.Prev;
                        if (p == p.Next) break;
                        again = true;
                    }
                    else
                    {
                        p = p.Next;
                    }
                } while (again || p != end);
                return end;
            }

            private static Node Insert(int i, double x, double y, Node? last)
            {
                var p = new Node(i, x, y);
                if (last == null)
                {
                    p.Prev = p;
                    p.Next = p;
                }
                else
                {
                    p.Next = last.Next;
                    p.Prev = last;
                    last.Next.Prev = p;
                    last.Next = p;
                }
                return p;
            }

            private static void Remove(Node p)
            {
                p.Next.Prev = p.Prev;
                p.Prev.Next = p.Next;
                if (p.PrevZ != null) p.PrevZ.NextZ = p.NextZ;
                if (p.NextZ != null) p.NextZ.PrevZ = p.PrevZ;
            }
        }
    }
}
