using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Typeless
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // DLLs load from the app folder and System32 only, never from the clicked file's folder.
            try { NativeMethods.SetDefaultDllDirectories(NativeMethods.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS); } catch { }
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Log.Write("Fatal: " + ex);
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            switch (args[0])
            {
                case "--version":
                    Console.WriteLine(Version);
                    return 0;

                case "--targets":
                    if (args.Length < 2) return Usage();
                    return PrintTargets(args[1]);

                case "--convert":
                    if (args.Length < 3) return Usage();
                    return ConvertFile(args[1], args[2]);

                case "--crop":
                    if (args.Length < 6
                        || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cx)
                        || !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cy)
                        || !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cw)
                        || !int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
                        return Usage();
                    return CropFile(args[1], cx, cy, cw, ch);

                case "--trim":
                    if (args.Length < 4 || args.Length > 5
                        || !TryParseTime(args[2], out double ts)
                        || !TryParseTime(args[3], out double te)
                        || (args.Length == 5 && !string.Equals(args[4], "exact", StringComparison.OrdinalIgnoreCase)))
                        return Usage();
                    return TrimFile(args[1], ts, te, args.Length == 5);

                case "--rotate":
                    if (args.Length < 3 || args.Length > 5
                        || !int.TryParse(args[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int deg)
                        || deg % 90 != 0
                        || args.Skip(3).Any(a => !string.Equals(a, "h", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(a, "v", StringComparison.OrdinalIgnoreCase)))
                        return Usage();
                    return RotateFile(args[1], deg,
                        args.Skip(3).Any(a => string.Equals(a, "h", StringComparison.OrdinalIgnoreCase)),
                        args.Skip(3).Any(a => string.Equals(a, "v", StringComparison.OrdinalIgnoreCase)));

                case "--resize":
                    if (args.Length < 3 || args.Length > 4
                        || !TryParseResizeValue(args[2], out int? rh)
                        || (args.Length == 4 && !TryParseResizeValue(args[3], out _)))
                        return Usage();
                    return ResizeFile(args[1], rh, args.Length == 4 && !IsOrig(args[3]) ? args[3].Trim() : null);

                case "--resize-info":
                    if (args.Length != 2) return Usage();
                    return ResizeInfoFile(args[1]);

                case "--model-info":
                    if (args.Length != 2) return Usage();
                    return ModelInfoFile(args[1]);

                case "--model-parts":
                    if (args.Length != 2) return Usage();
                    return ModelPartsFile(args[1]);

                case "--keep-parts":
                    if (args.Length < 3 || args.Length > 4
                        || (args.Length == 4 && !string.Equals(args[3], "separate", StringComparison.OrdinalIgnoreCase)))
                        return Usage();
                    return KeepPartsFile(args[1], args[2], args.Length == 4);

                case "--probe":
                    if (args.Length < 2) return Usage();
                    return ProbeFile(args[1]);

                case "--audio":
                    if (args.Length < 2) return Usage();
                    return AudioFile(args[1]);

                case "--clean":
                    if (args.Length < 2) return Usage();
                    return CleanFile(args[1]);

                case "--merge":
                    if (args.Length < 3) return Usage();
                    return MergeFiles(args[1..]);

                case "--split":
                    if (args.Length < 2) return Usage();
                    return SplitFile(args[1]);

                case "--pages":
                    if (args.Length < 3) return Usage();
                    if (!TryParsePageSpec(args[2], out List<(int Page, int Rotate)> plan))
                    {
                        Console.Error.WriteLine($"Invalid page spec: {args[2]}");
                        return 2;
                    }
                    return PagesFile(args[1], plan);

                case "--compress":
                    if (args.Length < 2 || args.Length > 3
                        || (args.Length == 3 && !PdfEdit.CompressLevels.Contains(args[2].ToLowerInvariant())))
                        return Usage();
                    return CompressFile(args[1], args.Length == 3 ? args[2].ToLowerInvariant() : "balanced");

                case "--preview":
                    if (args.Length < 3) return Usage();
                    return Preview(args[1], args[2], args.Length > 3 ? args[3] : "idle", args.Length > 4 ? args[4..] : Array.Empty<string>());

                case "--crop-preview":
                    if (args.Length < 3) return Usage();
                    return CropPreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--trim-preview":
                    if (args.Length < 3) return Usage();
                    return TrimPreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--pages-preview":
                    if (args.Length < 3) return Usage();
                    return PagesPreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--compress-preview":
                    if (args.Length < 3) return Usage();
                    return CompressPreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--rotate-preview":
                    if (args.Length < 3) return Usage();
                    return RotatePreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--resize-preview":
                    if (args.Length < 3) return Usage();
                    return ResizePreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--parts-preview":
                    if (args.Length < 3) return Usage();
                    return PartsPreview(args[1], args[2], args.Length > 3 ? args[3] : "idle");

                case "--list-extensions":
                    foreach (string ext in Installer.MenuExtensions)
                        Console.WriteLine(ext.StartsWith(".", StringComparison.Ordinal) ? ext : "." + ext);
                    return 0;

                case "--install":
                    Installer.Install();
                    return 0;

                case "--uninstall":
                    Installer.Uninstall();
                    return 0;

                case "--uninstall-verbs":
                    Installer.UninstallVerbs();
                    return 0;

                case "--register":
                    return Setup.Register();

                case "--unregister":
                    return Setup.Unregister();

                case "--shutdown":
                    return Setup.Shutdown();

                case "--backends":
                    return Setup.Backends();

                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    return 0;

                default:
                    if (args[0].StartsWith("--", StringComparison.Ordinal)) return Usage();
                    return Ui.Run(args);
            }
        }

        public static string Version
        {
            get
            {
                Assembly asm = typeof(Program).Assembly;
                string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrEmpty(v)) v = asm.GetName().Version?.ToString() ?? "0.0.0";
                int plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        private static int PrintTargets(string file)
        {
            IReadOnlyList<string> targets = Formats.TargetsFor(file);
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"Unsupported file type: {file}");
                return 2;
            }
            foreach (string t in targets) Console.WriteLine(t);
            return 0;
        }

        private static int ConvertFile(string file, string ext)
        {
            try
            {
                foreach (string output in Converter.Convert(file, ext)) Console.WriteLine(output);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Convert failed: {file} -> {ext}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int CropFile(string file, int x, int y, int w, int h)
        {
            try
            {
                Console.WriteLine(ImageBackend.Crop(file, x, y, w, h));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Crop failed: {file} {x},{y},{w},{h}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int TrimFile(string file, double start, double end, bool exact)
        {
            try
            {
                Console.WriteLine(MediaBackend.Trim(file, start, end, exact));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Trim failed: {file} {start}-{end} exact={exact}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // Videos go through MediaBackend, everything else through ImageBackend (which rejects
        // unsupported types). An identity transform prints "Nothing to rotate", rc 1.
        private static int RotateFile(string file, int deg, bool flipH, bool flipV)
        {
            try
            {
                Console.WriteLine(Formats.KindOf(file) == Kind.Video
                    ? MediaBackend.Transform(file, deg, flipH, flipV)
                    : ImageBackend.Transform(file, deg, flipH, flipV));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Rotate failed: {file} {deg} h={flipH} v={flipV}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static bool IsOrig(string s) => string.Equals(s.Trim(), "orig", StringComparison.OrdinalIgnoreCase);

        // "orig" (null, unchanged) or a positive whole number.
        private static bool TryParseResizeValue(string s, out int? value)
        {
            value = null;
            if (IsOrig(s)) return true;
            if (!int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v <= 0) return false;
            value = v;
            return true;
        }

        // Prints the output path, then the size change. Nothing to change or not smaller: rc 1 with
        // the message. Not a video, missing ffmpeg/ffprobe, or no such file: rc 2.
        private static int ResizeFile(string file, int? height, string? fps)
        {
            try
            {
                long before = new FileInfo(file).Exists ? new FileInfo(file).Length : 0;
                string dest = MediaBackend.Resize(file, height, fps, null, CancellationToken.None);
                long after = new FileInfo(dest).Length;
                Console.WriteLine(dest);
                int pct = before > 0 ? (int)Math.Round((after - before) * 100.0 / before) : 0;
                Console.WriteLine($"{PdfEdit.HumanSize(before)} -> {PdfEdit.HumanSize(after)} ({(pct > 0 ? "+" : "-")}{Math.Abs(pct)}%)");
                return 0;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is NotSupportedException)
            {
                Log.Write($"Resize failed: {file} {height} {fps}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log.Write($"Resize failed: {file} {height} {fps}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // size=WxH (displayed), fps=, heights= and fps_options= (comma lists, possibly empty).
        private static int ResizeInfoFile(string file)
        {
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException($"File not found: {file}", file);
                if (!Formats.CanResize(file))
                    throw new NotSupportedException($"Cannot resize {Path.GetFileName(file)} (not a video or missing ffmpeg/ffprobe).");
                MediaBackend.ResizeInfo info = MediaBackend.VideoInfo(file);
                Console.WriteLine($"size={info.Width}x{info.Height}");
                Console.WriteLine("fps=" + info.Fps.ToString("0.00", CultureInfo.InvariantCulture));
                Console.WriteLine("heights=" + string.Join(",", info.HeightOptions.Select(h => h.ToString(CultureInfo.InvariantCulture))));
                Console.WriteLine("fps_options=" + string.Join(",", info.FpsOptions));
                return 0;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is NotSupportedException)
            {
                Log.Write($"Resize info failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log.Write($"Resize info failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // parts=, triangles=, vertices=, bbox= (mm), one part= line per part, then dropped=.
        // Not a model or no such file: rc 2. A read error: rc 1 with the message.
        private static int ModelInfoFile(string file)
        {
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException($"File not found: {file}", file);
                if (Formats.KindOf(file) != Kind.Model)
                {
                    Console.Error.WriteLine($"Not a 3D model: {Path.GetFileName(file)}");
                    return 2;
                }
                Scene scene = ModelBackend.Read(file);
                Console.WriteLine("parts=" + scene.Parts.Count.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("triangles=" + scene.TriangleCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("vertices=" + scene.VertexCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("bbox=" + scene.BoundsText());
                foreach (Part p in scene.Parts)
                    Console.WriteLine($"part={p.Name} tris={p.TriangleCount.ToString(CultureInfo.InvariantCulture)} color={(p.Color is Rgba c ? c.Hex : "none")}");
                Console.WriteLine("dropped=" + string.Join(",", scene.Dropped));
                return 0;
            }
            catch (FileNotFoundException ex)
            {
                Log.Write($"Model info failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log.Write($"Model info failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // pieces=, then one piece= line per piece (numbered from 1, ModelParts.Find order).
        // Not a model or no such file: rc 2. A read error: rc 1 with the message.
        private static int ModelPartsFile(string file)
        {
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException($"File not found: {file}", file);
                if (!Formats.CanParts(file))
                {
                    Console.Error.WriteLine($"Not a 3D model: {Path.GetFileName(file)}");
                    return 2;
                }
                List<Piece> pieces = ModelParts.Find(ModelBackend.Read(file));
                Console.WriteLine("pieces=" + pieces.Count.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < pieces.Count; i++)
                {
                    Piece p = pieces[i];
                    Console.WriteLine($"piece={(i + 1).ToString(CultureInfo.InvariantCulture)} part={p.PartIndex.ToString(CultureInfo.InvariantCulture)} "
                        + $"name={p.Name} tris={p.TriangleCount.ToString(CultureInfo.InvariantCulture)} bbox={p.BoundsText()}");
                }
                return 0;
            }
            catch (FileNotFoundException ex)
            {
                Log.Write($"Model parts failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log.Write($"Model parts failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // Prints the output paths. Nothing to change, nothing selected, one object or a write or
        // verify error: rc 1 with the message. Not a model, no such file or an invalid spec: rc 2.
        private static int KeepPartsFile(string file, string spec, bool separate)
        {
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException($"File not found: {file}", file);
                if (!Formats.CanParts(file))
                {
                    Console.Error.WriteLine($"Not a 3D model: {Path.GetFileName(file)}");
                    return 2;
                }
                Scene scene = ModelBackend.Read(file);
                List<Piece> pieces = ModelParts.Find(scene);
                ModelParts.CheckSeveral(pieces);
                List<int> keep;
                try
                {
                    keep = ModelParts.ParseSpec(spec, pieces);
                }
                catch (FormatException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 2;
                }
                foreach (string output in ModelParts.Keep(file, scene, pieces, keep, separate)) Console.WriteLine(output);
                return 0;
            }
            catch (FileNotFoundException ex)
            {
                Log.Write($"Keep parts failed: {file} {spec}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log.Write($"Keep parts failed: {file} {spec} separate={separate}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int AudioFile(string file)
        {
            try
            {
                Console.WriteLine(MediaBackend.ExtractAudio(file));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Audio failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int CleanFile(string file)
        {
            try
            {
                Console.WriteLine(MetadataCleaner.Clean(file));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Clean failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int ProbeFile(string file)
        {
            try
            {
                MediaBackend.ProbeInfo info = MediaBackend.Probe(file);
                Console.WriteLine("duration=" + info.Duration.ToString("0.######", CultureInfo.InvariantCulture));
                Console.WriteLine("fps=" + info.FrameRate.ToString("0.######", CultureInfo.InvariantCulture));
                foreach (double k in info.Keyframes)
                    Console.WriteLine("key=" + k.ToString("0.######", CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Probe failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int MergeFiles(string[] files)
        {
            try
            {
                Console.WriteLine(PdfEdit.Merge(files));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Merge failed: {string.Join(" | ", files)}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int SplitFile(string file)
        {
            try
            {
                foreach (string output in PdfEdit.Split(file)) Console.WriteLine(output);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Split failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int PagesFile(string file, List<(int Page, int Rotate)> plan)
        {
            try
            {
                Console.WriteLine(PdfEdit.Pages(file, plan));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Pages failed: {file}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // Builds in a temp dir, then moves to <stem>_compressed.pdf only when it saves at least 5%.
        private static int CompressFile(string file, string level)
        {
            string work = PdfEdit.NewWorkDir();
            try
            {
                string temp = Path.Combine(work, "out.pdf");
                long after = PdfEdit.Compress(file, level, temp, null, CancellationToken.None);
                long before = new FileInfo(file).Length;
                if (before - after < before * 0.05)
                {
                    Console.Error.WriteLine("Already compact");
                    return 1;
                }
                Console.WriteLine(PdfEdit.SaveCompressed(file, temp));
                int pct = (int)Math.Round((before - after) * 100.0 / before);
                Console.WriteLine($"{PdfEdit.HumanSize(before)} -> {PdfEdit.HumanSize(after)} (-{pct}%)");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Compress failed: {file} {level}: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        private const int MaxPlanPages = 100_000;

        // "3,1-2,5r90,6r180": pages or inclusive ranges (A-B with A > B runs descending),
        // each with an optional rotation r90, r180 or r270 applied to every page of the item.
        // At most MaxPlanPages pages in total.
        internal static bool TryParsePageSpec(string spec, out List<(int Page, int Rotate)> plan)
        {
            plan = new List<(int Page, int Rotate)>();
            foreach (string raw in spec.Split(','))
            {
                string item = raw.Trim();
                int rotate = 0;
                int r = item.IndexOfAny(new[] { 'r', 'R' });
                if (r >= 0)
                {
                    if (!int.TryParse(item.Substring(r + 1), NumberStyles.None, CultureInfo.InvariantCulture, out rotate)
                        || (rotate != 90 && rotate != 180 && rotate != 270))
                        return false;
                    item = item.Substring(0, r);
                }

                string[] ends = item.Split('-');
                if (ends.Length > 2
                    || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int a)
                    || !int.TryParse(ends[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int b))
                    return false;
                // Cap the plan before building it, so "1-2000000000" fails fast instead of filling memory.
                if (plan.Count + Math.Abs((long)b - a) + 1 > MaxPlanPages) return false;
                int step = a <= b ? 1 : -1;
                for (int p = a; ; p += step)
                {
                    plan.Add((p, rotate));
                    if (p == b) break;
                }
            }
            return plan.Count > 0;
        }

        // Seconds ("12.5"), "mm:ss(.fff)" or "hh:mm:ss(.fff)", invariant culture.
        private static bool TryParseTime(string text, out double seconds)
        {
            seconds = 0;
            string[] parts = text.Trim().Split(':');
            if (parts.Length > 3) return false;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                if (last)
                {
                    if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double s)
                        || !double.IsFinite(s))
                        return false;
                    seconds = seconds * 60 + s;
                }
                else
                {
                    if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
                    seconds = seconds * 60 + n;
                }
            }
            return true;
        }

        // More files after the state preview a multi-select (for example the merge slice).
        private static int Preview(string file, string outPng, string state, string[] more)
        {
            IReadOnlyList<string> targets = Formats.TargetsFor(file);
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"Unsupported file type: {file}");
                return 2;
            }
            try
            {
                var all = new List<string> { file };
                all.AddRange(more);
                var window = new RadialWindow(file, Ui.SlicesFor(all));
                foreach (string f in more) window.AddFile(f, live: false);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int CropPreview(string file, string outPng, string state)
        {
            if (!Formats.CanCrop(file))
            {
                Console.Error.WriteLine($"Cannot crop this file type: {file}");
                return 2;
            }
            try
            {
                var window = new CropWindow(file, ImageBackend.LoadForCrop(file));
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Crop preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int TrimPreview(string file, string outPng, string state)
        {
            if (!Formats.CanTrim(file))
            {
                Console.Error.WriteLine($"Cannot trim this file type (or ffmpeg/ffprobe missing): {file}");
                return 2;
            }
            try
            {
                var window = new TrimWindow(file, preview: true);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Trim preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int PagesPreview(string file, string outPng, string state)
        {
            if (!Formats.CanPages(file))
            {
                Console.Error.WriteLine($"Not a PDF: {file}");
                return 2;
            }
            try
            {
                var window = new PagesWindow(file, preview: true);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Pages preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int CompressPreview(string file, string outPng, string state)
        {
            if (!Formats.CanCompress(file))
            {
                Console.Error.WriteLine($"Not a PDF: {file}");
                return 2;
            }
            try
            {
                var window = new CompressWindow(file, preview: true);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Compress preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int RotatePreview(string file, string outPng, string state)
        {
            if (!Formats.CanRotate(file))
            {
                Console.Error.WriteLine($"Cannot rotate this file type (or ffmpeg missing): {file}");
                return 2;
            }
            try
            {
                // Images come decoded; a video frame is grabbed inside RenderPreview.
                var decoded = Formats.CanCrop(file) ? ImageBackend.LoadForCrop(file) : null;
                var window = new RotateWindow(file, decoded, preview: true);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Rotate preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int ResizePreview(string file, string outPng, string state)
        {
            if (!Formats.CanResize(file))
            {
                Console.Error.WriteLine($"Not a video (or ffmpeg/ffprobe missing): {file}");
                return 2;
            }
            try
            {
                var window = new ResizeWindow(file, preview: true);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Resize preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int PartsPreview(string file, string outPng, string state)
        {
            if (!Formats.CanParts(file))
            {
                Console.Error.WriteLine($"Not a 3D model: {file}");
                return 2;
            }
            try
            {
                var window = new PartsWindow(file, preview: true);
                window.RenderPreview(outPng, state);
                Console.WriteLine(outPng);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"Parts preview failed: {ex}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int Usage()
        {
            PrintUsage();
            return 2;
        }

        private static void PrintUsage()
        {
            Console.WriteLine($"Typeless {Version}");
            Console.WriteLine("Usage:");
            Console.WriteLine("  Typeless <file> [more files]     Show the radial menu");
            Console.WriteLine("  Typeless --convert <file> <ext>  Convert headlessly, print output paths");
            Console.WriteLine("  Typeless --crop <file> <x> <y> <w> <h>  Crop an image (pixels), print the output path");
            Console.WriteLine("  Typeless --trim <file> <start> <end> [exact]  Trim audio/video (s or mm:ss.fff), print the output path");
            Console.WriteLine("  Typeless --rotate <file> <deg> [h] [v]  Flip (h, v) then rotate an image or video clockwise (multiple of 90, negative = left), print the output path");
            Console.WriteLine("  Typeless --resize <file> <height|orig> [fps|orig]  Lower a video's resolution (short side) and/or frame rate, print the output path and size change");
            Console.WriteLine("  Typeless --resize-info <file>    Print a video's displayed size, fps, and the smaller heights and frame rates on offer");
            Console.WriteLine("  Typeless --model-info <file>     Print a 3D model's parts, triangles, vertices, bbox (mm), per-part color and what the reader dropped");
            Console.WriteLine("  Typeless --model-parts <file>    Print a 3D model's separate objects (pieces): source part, name, triangles, bbox (mm)");
            Console.WriteLine("  Typeless --keep-parts <file> <spec> [separate]  Keep only some objects (e.g. 1,3-5 or box:x0,y0,x1,y1 in mm), same format, one file or one per object, print the output paths");
            Console.WriteLine("  Typeless --probe <file>          Print duration, frame rate and video keyframe times");
            Console.WriteLine("  Typeless --audio <file>          Save a video's audio track as is (no re-encode), print the output path");
            Console.WriteLine("  Typeless --clean <file>          Remove an image's or video's metadata (location, camera, dates), print the output path");
            Console.WriteLine("  Typeless --merge <file> <file> [...]  Merge PDFs and images (given order) into one PDF, print the output path");
            Console.WriteLine("  Typeless --split <file>          Split a PDF into one PDF per page, print the output paths");
            Console.WriteLine("  Typeless --pages <file> <spec>   Pick, reorder and rotate pages (e.g. 3,1-2,5r90), print the output path");
            Console.WriteLine("  Typeless --compress <file> [light|balanced|strong]  Shrink a PDF's images (default balanced), print the output path and size saved");
            Console.WriteLine("  Typeless --targets <file>       List available target formats");
            Console.WriteLine("  Typeless --preview <file> <out.png> [idle|hover:N|busy|done|error|page2] [more files]  Render the menu to PNG (dev; page2 = MORE page past 9 items)");
            Console.WriteLine("  Typeless --crop-preview <file> <out.png> [idle|select|done|error]  Render the crop window to PNG (dev)");
            Console.WriteLine("  Typeless --trim-preview <file> <out.png> [idle|select|lossless|playing|done|error]  Render the trim window to PNG (dev)");
            Console.WriteLine("  Typeless --pages-preview <file> <out.png> [idle|select|done|error]  Render the pages window to PNG (dev)");
            Console.WriteLine("  Typeless --compress-preview <file> <out.png> [idle|running|ready|done|error]  Render the compress window to PNG (dev)");
            Console.WriteLine("  Typeless --rotate-preview <file> <out.png> [idle|changed|done|error]  Render the rotate window to PNG (dev; changed = rotated right, then mirrored)");
            Console.WriteLine("  Typeless --resize-preview <file> <out.png> [idle|changed|running|done|error]  Render the resize window to PNG (dev; changed = first smaller height and fps)");
            Console.WriteLine("  Typeless --parts-preview <file> <out.png> [idle|select|separate|done|error]  Render the parts window to PNG (dev; select = pieces 1-2 kept, separate = select plus Separate files)");
            Console.WriteLine("  (dev env) TYPELESS_AUTOPICK=<n>  Radial clicks item n (0-based, full list; flips to page 2 if needed)");
            Console.WriteLine("  (dev env) TYPELESS_AUTOCROP=x,y,w,h  TYPELESS_AUTOTRIM=start,end  Fractions; select and save after load");
            Console.WriteLine("  (dev env) TYPELESS_AUTOPAGES=<spec>  Pages window applies the --pages spec and saves after thumbnails load");
            Console.WriteLine("  (dev env) TYPELESS_AUTOCOMPRESS=light|balanced|strong  Compress window selects that level and saves when it is ready");
            Console.WriteLine("  (dev env) TYPELESS_AUTOROTATE=<deg>[,h][,v]  Rotate window presses the flip buttons, then turns deg clockwise, and saves after load (same result as --rotate)");
            Console.WriteLine("  (dev env) TYPELESS_AUTORESIZE=<height|orig>[,<fps|orig>]  Resize window selects those values and saves after load (same result as --resize)");
            Console.WriteLine("  (dev env) TYPELESS_AUTOPARTS=<spec>  Parts window keeps the --keep-parts spec and saves after load (same result as --keep-parts)");
            Console.WriteLine("  (dev env) TYPELESS_PARTS_SEPARATE=1  With TYPELESS_AUTOPARTS, save separate files (same result as --keep-parts ... separate)");
            Console.WriteLine("  (dev env) TYPELESS_AUTOPLAY=1  Trim window plays the range after load, closes at the end");
            Console.WriteLine("  (dev env) TYPELESS_NO_POWERPOINT=1  Skip PowerPoint; convert slides with LibreOffice");
            Console.WriteLine("  Typeless --list-extensions       List the context menu extensions");
            Console.WriteLine("  Typeless --install               Register the Explorer context menu");
            Console.WriteLine("  Typeless --uninstall             Remove the Explorer context menu");
            Console.WriteLine("  Typeless --uninstall-verbs       Remove only the classic context menu verbs");
            Console.WriteLine("  Typeless --register              Add the menu: classic verbs, then the Windows 11 package from Typeless.msix (replaces the verbs); prints menu=top-level or menu=classic");
            Console.WriteLine("  Typeless --unregister            Remove the Windows 11 package and the classic verbs");
            Console.WriteLine("  Typeless --shutdown              Close other running Typeless from this folder (kill after 3 s)");
            Console.WriteLine("  Typeless --backends              Print word=, powerpoint=, libreoffice=, ffmpeg=, ffprobe= (yes/no)");
            Console.WriteLine("  Typeless --version               Print the version");
        }
    }
}
