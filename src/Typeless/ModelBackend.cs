using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typeless
{
    // Kind.Model: stl, 3mf, obj, ply, glb, all built in. Every output is read back with Typeless's
    // own reader and compared (triangle count exact, bbox within tolerance) before it is moved
    // to its final name, so a bad write never reaches the user's folder.
    public static class ModelBackend
    {
        private const double BboxTolerance = 1e-4;     // mm
        private const double GlbBboxTolerance = 1e-3;  // mm, glb goes through a node transform

        public static Scene Read(string path)
        {
            return Formats.Norm(path) switch
            {
                "stl" => Stl.Read(path),
                "obj" => Obj.Read(path),
                "ply" => Ply.Read(path),
                "3mf" => ThreeMf.Read(path),
                "glb" => Glb.Read(path),
                _ => throw new NotSupportedException($"Not a 3D model: {Path.GetFileName(path)}"),
            };
        }

        // Writes s to path in the target format. obj also writes <same stem>.mtl beside it when
        // any part has a color. Adds what the target cannot hold to s.Dropped.
        public static void Write(Scene s, string path, string target)
        {
            switch (Formats.Norm(target))
            {
                case "stl": Stl.Write(s, path); break;
                case "obj": Obj.Write(s, path); break;
                case "ply": Ply.Write(s, path); break;
                case "3mf": ThreeMf.Write(s, path); break;
                case "glb": Glb.Write(s, path); break;
                default: throw new NotSupportedException($"Not a 3D model format: {target}");
            }
        }

        // Reads src, writes target into work, verifies the written file, then moves it to its final
        // name next to src. Returns the final paths (the obj first, then its mtl when there is one).
        public static List<string> Convert(string src, string target, string work)
        {
            target = Formats.Norm(target);
            Scene scene = Read(src);
            int sourceTris = scene.TriangleCount;
            if (sourceTris == 0) throw new InvalidOperationException("No triangles found");
            List<string> readerDropped = scene.Dropped.ToList();

            bool mtl = target == "obj" && scene.Parts.Any(p => p.Color != null);
            string dir = Path.GetDirectoryName(Path.GetFullPath(src)) ?? ".";
            (string finalMain, string? finalMtl) = PickNames(dir, $"{Path.GetFileNameWithoutExtension(src)}_{target}", target, mtl);

            string tempMain = Path.Combine(work, Path.GetFileName(finalMain));
            string? tempMtl = finalMtl != null ? Path.Combine(work, Path.GetFileName(finalMtl)) : null;

            Write(scene, tempMain, target);
            List<string> writerDropped = scene.Dropped.Except(readerDropped).ToList();

            Verify(scene, tempMain, target);
            if (tempMtl != null && !File.Exists(tempMtl))
                throw new InvalidOperationException("Verification failed: the mtl file was not written.");

            Log.Write($"Model: {Path.GetFileName(src)} -> {target}: writer={target}, parts={scene.Parts.Count}, "
                + $"triangles={sourceTris}, vertices={scene.VertexCount}, bbox={scene.BoundsText()}");
            if (readerDropped.Count > 0) Log.Write("Model: reader dropped " + string.Join(", ", readerDropped));
            if (writerDropped.Count > 0) Log.Write($"Model: {target} cannot hold " + string.Join(", ", writerDropped));

            var finals = new List<string>();
            File.Move(tempMain, finalMain);
            finals.Add(finalMain);
            if (tempMtl != null && finalMtl != null)
            {
                File.Move(tempMtl, finalMtl);
                finals.Add(finalMtl);
            }
            return finals;
        }

        // dir\<name>.<ext>, or the first free " (n)" variant (the Naming rule). A colored obj needs
        // <name>.obj and <name>.mtl both free, so the variant is chosen for the pair and the mtllib
        // line names the final mtl. taken holds names already picked in the same batch (they do not
        // exist yet), so several outputs never collide with each other.
        internal static (string Main, string? Mtl) PickNames(string dir, string name, string ext, bool mtl,
            ISet<string>? taken = null)
        {
            for (int n = 1; ; n++)
            {
                string stem = n == 1 ? name : $"{name} ({n})";
                string main = Path.Combine(dir, $"{stem}.{ext}");
                string? m = mtl ? Path.Combine(dir, stem + ".mtl") : null;
                if (Free(main, taken) && (m == null || Free(m, taken))) return (main, m);
            }
        }

        private static bool Free(string path, ISet<string>? taken) =>
            !File.Exists(path) && !Directory.Exists(path) && (taken == null || !taken.Contains(path));

        // Reads the written file back: exact triangle count, bbox within tolerance (target is normalized).
        internal static void Verify(Scene expected, string written, string target)
        {
            if (!File.Exists(written)) throw new InvalidOperationException("Verification failed: nothing was written.");
            Scene back;
            try
            {
                back = Read(written);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                throw new InvalidOperationException("Verification failed: the output cannot be read back (" + ex.Message + ")", ex);
            }

            if (back.TriangleCount != expected.TriangleCount)
                throw new InvalidOperationException(
                    $"Verification failed: {back.TriangleCount} triangles written, {expected.TriangleCount} expected.");

            double tol = target == "glb" ? GlbBboxTolerance : BboxTolerance;
            (double[] emin, double[] emax) = expected.Bounds();
            (double[] bmin, double[] bmax) = back.Bounds();
            for (int k = 0; k < 3; k++)
            {
                if (!(Math.Abs(emin[k] - bmin[k]) <= tol) || !(Math.Abs(emax[k] - bmax[k]) <= tol))
                    throw new InvalidOperationException(
                        $"Verification failed: bounding box {back.BoundsText()} does not match {expected.BoundsText()}.");
            }
        }
    }
}
