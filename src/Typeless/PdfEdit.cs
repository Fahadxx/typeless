using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Typeless
{
    // Page-level PDF edits (merge, split, pages) via PDFsharp. Pages are copied, never rasterized.
    public static class PdfEdit
    {
        public static int PageCount(string src)
        {
            src = Existing(src);
            using PdfDocument doc = Open(src);
            return doc.PageCount;
        }

        // Natural file name order ("scan 2" before "scan 10"), as Explorer sorts. Used by the UI; the CLI keeps its order.
        public static List<string> NaturalOrder(IEnumerable<string> paths)
        {
            return paths.OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(NativeMethods.StrCmpLogicalW)).ToList();
        }

        // PDFs and images, in the given order, into <first stem>_merged.pdf next to the first input.
        // Images go through ImageBackend.SavePdf so pages match the single-image pdf target.
        public static string Merge(IReadOnlyList<string> inputs)
        {
            if (inputs.Count < 2) throw new ArgumentException("Merge needs at least 2 files.");
            var files = inputs.Select(Existing).ToList();
            foreach (string f in files)
            {
                if (Formats.Norm(f) != "pdf" && Formats.KindOf(f) != Kind.Image)
                    throw new NotSupportedException($"Cannot merge {Path.GetFileName(f)} (not a PDF or image).");
            }

            string work = NewWorkDir();
            try
            {
                string output = Path.Combine(work, "out.pdf");
                var sources = new List<PdfDocument>();
                try
                {
                    using var outDoc = new PdfDocument();
                    for (int i = 0; i < files.Count; i++)
                    {
                        string pdf = files[i];
                        if (Formats.Norm(pdf) != "pdf")
                        {
                            pdf = Path.Combine(work, $"img_{i + 1}.pdf");
                            ImageBackend.SavePdf(ImageBackend.Load(files[i], work), pdf);
                        }
                        PdfDocument doc = Open(pdf, files[i]);
                        sources.Add(doc);
                        for (int p = 0; p < doc.PageCount; p++) outDoc.AddPage(doc.Pages[p]);
                    }
                    outDoc.Save(output);
                }
                finally
                {
                    foreach (PdfDocument d in sources) d.Dispose();
                }

                string dest = Naming.WithSuffix(files[0], "merged", "pdf");
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // One PDF per page: <stem>_split_1.pdf, <stem>_split_2.pdf, ... in page order.
        public static List<string> Split(string src)
        {
            src = Existing(src);
            string work = NewWorkDir();
            try
            {
                var temps = new List<string>();
                using (PdfDocument doc = Open(src))
                {
                    if (doc.PageCount == 0) throw new InvalidOperationException($"{Path.GetFileName(src)} has no pages.");
                    for (int p = 0; p < doc.PageCount; p++)
                    {
                        string output = Path.Combine(work, $"page_{p + 1:D4}.pdf");
                        using (var one = new PdfDocument())
                        {
                            one.AddPage(doc.Pages[p]);
                            one.Save(output);
                        }
                        temps.Add(output);
                    }
                }

                var finals = new List<string>(temps.Count);
                try
                {
                    for (int i = 0; i < temps.Count; i++)
                    {
                        string dest = Naming.WithSuffix(src, "split", i + 1);
                        File.Move(temps[i], dest);
                        finals.Add(dest);
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

        // plan: 1-based source pages in output order; Rotate (multiple of 90) adds to the page's existing /Rotate.
        public static string Pages(string src, IReadOnlyList<(int Page, int Rotate)> plan)
        {
            src = Existing(src);
            if (plan.Count == 0) throw new ArgumentException("No pages selected.");
            foreach ((int _, int rotate) in plan)
            {
                if (rotate % 90 != 0) throw new ArgumentException($"Rotation must be a multiple of 90: {rotate}");
            }

            string work = NewWorkDir();
            try
            {
                string output = Path.Combine(work, "out.pdf");
                using (PdfDocument doc = Open(src))
                using (var outDoc = new PdfDocument())
                {
                    foreach ((int page, int rotate) in plan)
                    {
                        if (page < 1 || page > doc.PageCount)
                            throw new ArgumentException($"Page {page} is out of range (1-{doc.PageCount}).");
                        PdfPage added = outDoc.AddPage(doc.Pages[page - 1]);
                        if (rotate != 0) added.Rotate = ((added.Rotate + rotate) % 360 + 360) % 360;
                    }
                    outDoc.Save(output);
                }

                string dest = Naming.WithSuffix(src, "pages");
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        public static readonly string[] CompressLevels = { "light", "balanced", "strong" };

        // Max effective image dpi and JPEG quality per level (Decisions in tasks/prd-pdf.md).
        // Not q75: measured, WPF's JpegBitmapEncoder writes the same size at QualityLevel 75 as at 90 (75 acts like
        // the default), so balanced uses 70.
        private static (int Dpi, int Quality) LevelOf(string level) => level.ToLowerInvariant() switch
        {
            "light" => (200, 85),
            "balanced" => (150, 70),
            "strong" => (96, 60),
            _ => throw new ArgumentException($"Unknown compress level: {level} (light, balanced or strong)."),
        };

        // Re-encodes only images that pass the safe-only rule (IsSafeImage); fonts, text, vectors and every
        // other stream keep their bytes. Unfiltered page and form content streams get lossless Flate.
        // Writes tempOut and returns its size; tempOut is deleted on failure or cancel.
        public static long Compress(string src, string level, string tempOut, IProgress<double>? progress, CancellationToken ct)
        {
            src = Existing(src);
            (int maxDpi, int quality) = LevelOf(level);
            try
            {
                int done = 0, replaced = 0;
                List<PdfDictionary> candidates;
                using (PdfDocument doc = Open(src, mode: PdfDocumentOpenMode.Modify))
                {
                    var all = doc.Internals.GetAllObjects();
                    var maskTargets = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
                    foreach (PdfObject o in all)
                    {
                        if (o is not PdfDictionary d || d.Stream == null || NameOf(d.Elements["/Subtype"]) != "/Image") continue;
                        foreach (string key in new[] { "/SMask", "/Mask" })
                            if (d.Elements[key] is PdfReference r && r.Value is PdfObject t) maskTargets.Add(t);
                    }
                    candidates = all.OfType<PdfDictionary>().Where(d => IsSafeImage(d, maskTargets)).ToList();

                    // Largest drawn size per image in points; images never seen are assumed to fill the page width.
                    var drawn = new Dictionary<PdfDictionary, (double W, double H)>(ReferenceEqualityComparer.Instance);
                    var contents = new List<PdfDictionary.PdfStream>();
                    var budget = new ScanBudget(ct);
                    double pageWidth = 0;
                    for (int p = 0; p < doc.PageCount; p++)
                    {
                        ct.ThrowIfCancellationRequested();
                        PdfPage page = doc.Pages[p];
                        if (Resolve(Inherited(page, "/MediaBox")) is PdfArray box && box.Elements.Count == 4)
                            pageWidth = Math.Max(pageWidth, Math.Abs(NumOf(box.Elements[2]) - NumOf(box.Elements[0])));
                        try
                        {
                            var streams = new List<PdfDictionary.PdfStream>();
                            PdfItem? c = Resolve(page.Elements["/Contents"]);
                            if (c is PdfDictionary { Stream: { } one }) streams.Add(one);
                            else if (c is PdfArray parts)
                            {
                                for (int i = 0; i < parts.Elements.Count; i++)
                                    if (Resolve(parts.Elements[i]) is PdfDictionary { Stream: { } s }) streams.Add(s);
                            }
                            contents.AddRange(streams);

                            using var ms = new MemoryStream();
                            foreach (PdfDictionary.PdfStream s in streams)
                            {
                                byte[] b = s.UnfilteredValue;
                                ms.Write(b, 0, b.Length);
                                ms.WriteByte((byte)'\n');
                            }
                            if (!budget.Exhausted && budget.Charge(ms.Length))
                            {
                                ScanContent(ms.ToArray(), Resolve(Inherited(page, "/Resources")) as PdfDictionary,
                                    Matrix.Identity, drawn, 0, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), budget);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Write($"Compress: content scan failed on page {p + 1} of {src}: {ex.Message}");
                        }
                    }
                    if (pageWidth <= 0) pageWidth = 612;

                    // An unfinished scan knows no safe drawn size (unseen images would count as page width and seen
                    // ones may be drawn larger later), so no image is re-encoded; content streams still get Flate.
                    if (budget.Exhausted)
                    {
                        Log.Write($"Compress: content scan stopped at its budget in {src}, images kept as is");
                        candidates.Clear();
                    }

                    foreach (PdfDictionary img in candidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            (double W, double H)? size = drawn.TryGetValue(img, out var seen) ? seen : null;
                            if (Reencode(img, size, pageWidth, maxDpi, quality))
                                replaced++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Write($"Compress: image kept as is in {src}: {ex.Message}");
                        }
                        done++;
                        progress?.Report((double)done / candidates.Count);
                    }

                    // Lossless Flate for unfiltered page and form content streams only; Zip skips filtered ones.
                    foreach (PdfDictionary.PdfStream s in contents) s.Zip();
                    foreach (PdfObject o in all)
                    {
                        if (o is PdfDictionary { Stream: { } fs } f && NameOf(f.Elements["/Subtype"]) == "/Form"
                            && !f.Elements.ContainsKey("/Filter"))
                            fs.Zip();
                    }

                    ct.ThrowIfCancellationRequested();
                    doc.Options.CompressContentStreams = true;
                    doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;
                    doc.Save(tempOut);
                }
                progress?.Report(1.0);

                long before = new FileInfo(src).Length;
                long after = new FileInfo(tempOut).Length;
                Log.Write($"compress {level}: {src} {HumanSize(before)} -> {HumanSize(after)}, {replaced} of {candidates.Count} images re-encoded");
                return after;
            }
            catch
            {
                try { File.Delete(tempOut); } catch { }
                throw;
            }
        }

        // Moves a Compress result to <stem>_compressed.pdf next to src (raw extension, " (2)" rule).
        public static string SaveCompressed(string src, string tempOut)
        {
            src = Existing(src);
            string dest = Naming.WithSuffix(src, "compressed");
            File.Move(tempOut, dest);
            return dest;
        }

        // "812.4 KB", "3.1 MB".
        public static string HumanSize(long bytes)
        {
            return bytes >= 1024 * 1024
                ? (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        // Safe-only rule: 8-bit DeviceRGB or DeviceGray (a plain name), no filter, DCTDecode or FlateDecode
        // (Flate without a predictor), no SMask, Mask, ImageMask or Decode, and not itself a mask of another image.
        private static bool IsSafeImage(PdfDictionary d, HashSet<PdfObject> maskTargets)
        {
            if (d.Stream == null || NameOf(d.Elements["/Subtype"]) != "/Image" || maskTargets.Contains(d)) return false;
            if (NumOf(d.Elements["/BitsPerComponent"]) != 8) return false;
            if (!(NumOf(d.Elements["/Width"]) >= 1) || !(NumOf(d.Elements["/Height"]) >= 1)) return false;
            string? cs = NameOf(d.Elements["/ColorSpace"]);
            if (cs != "/DeviceRGB" && cs != "/DeviceGray") return false;
            foreach (string key in new[] { "/SMask", "/Mask", "/ImageMask", "/Decode" })
                if (d.Elements.ContainsKey(key)) return false;

            string? filter = FilterOf(d);
            if (filter != "" && filter != "/DCTDecode" && filter != "/FlateDecode") return false;

            PdfItem? parms = Resolve(d.Elements["/DecodeParms"]);
            if (parms is PdfArray pa) parms = pa.Elements.Count == 1 ? Resolve(pa.Elements[0]) : pa;
            if (parms != null && parms is not PdfNull)
            {
                if (filter != "/FlateDecode" || parms is not PdfDictionary dp) return false;
                if (dp.Elements.ContainsKey("/Predictor") && NumOf(dp.Elements["/Predictor"]) > 1) return false;
            }
            return true;
        }

        // "" for no filter, the name for a single filter (plain or a one-item array), null otherwise.
        private static string? FilterOf(PdfDictionary d)
        {
            PdfItem? f = Resolve(d.Elements["/Filter"]);
            if (f == null || f is PdfNull) return "";
            if (f is PdfArray a) return a.Elements.Count == 1 ? NameOf(a.Elements[0]) : null;
            return NameOf(f);
        }

        // Decodes, downscales when above maxDpi, re-encodes as JPEG, and swaps the stream only when smaller.
        // Flate images (lossless, like screenshots and line art) are only re-encoded when they need downscaling,
        // so they never pick up JPEG artifacts at full resolution.
        private static bool Reencode(PdfDictionary img, (double W, double H)? drawn, double pageWidth, int maxDpi, int quality)
        {
            int w = (int)NumOf(img.Elements["/Width"]), h = (int)NumOf(img.Elements["/Height"]);
            bool gray = NameOf(img.Elements["/ColorSpace"]) == "/DeviceGray";
            string filter = FilterOf(img) ?? "";

            // Effective dpi on the less resolved axis, so one uniform scale keeps the aspect and never undershoots maxDpi.
            double dpi = drawn is { } d
                ? Math.Min(w / (d.W / 72), h / (d.H / 72))
                : w / (pageWidth / 72);
            if (filter == "/FlateDecode" && !(dpi > maxDpi)) return false;

            PdfDictionary.PdfStream stream = img.Stream ?? throw new InvalidOperationException("Image has no stream.");
            byte[] current = stream.Value;

            BitmapSource bitmap;
            if (filter == "/DCTDecode")
            {
                using var ms = new MemoryStream(current);
                var decoder = new JpegBitmapDecoder(ms,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                bitmap = decoder.Frames[0];
                PixelFormat f = bitmap.Format;
                bool ok = gray ? f == PixelFormats.Gray8 : f == PixelFormats.Bgr24 || f == PixelFormats.Bgr32 || f == PixelFormats.Rgb24;
                if (!ok || bitmap.PixelWidth != w || bitmap.PixelHeight != h) return false;
            }
            else
            {
                int stride = w * (gray ? 1 : 3);
                byte[] pixels = new byte[(long)stride * h];
                if (filter == "/FlateDecode")
                {
                    // Read exactly the pixel bytes; a short stream leaves the image as is.
                    using var zs = new ZLibStream(new MemoryStream(current), CompressionMode.Decompress);
                    int read = 0;
                    while (read < pixels.Length)
                    {
                        int n = zs.Read(pixels, read, pixels.Length - read);
                        if (n <= 0) return false;
                        read += n;
                    }
                }
                else
                {
                    if (current.Length < pixels.Length) return false;
                    Buffer.BlockCopy(current, 0, pixels, 0, pixels.Length);
                }
                bitmap = BitmapSource.Create(w, h, 96, 96, gray ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, pixels, stride);
            }
            if (!gray && bitmap.Format != PixelFormats.Bgr24) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);

            if (dpi > maxDpi)
            {
                double s = maxDpi / dpi;
                int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
                bitmap = ImageBackend.ScaleTo(bitmap, nw, nh);
                PixelFormat want = gray ? PixelFormats.Gray8 : PixelFormats.Bgr24;
                if (bitmap.Format != want) bitmap = new FormatConvertedBitmap(bitmap, want, null, 0);
            }

            byte[] jpeg = ImageBackend.Encode(new JpegBitmapEncoder { QualityLevel = quality }, bitmap);
            if (jpeg.Length >= current.Length) return false;

            // The JPEG must decode to the same channel count and size we write in the dictionary.
            using (var check = new MemoryStream(jpeg))
            {
                BitmapFrame frame = new JpegBitmapDecoder(check, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                bool ok = gray ? frame.Format == PixelFormats.Gray8 : frame.Format == PixelFormats.Bgr24 || frame.Format == PixelFormats.Bgr32;
                if (!ok || frame.PixelWidth != bitmap.PixelWidth || frame.PixelHeight != bitmap.PixelHeight) return false;
            }

            stream.Value = jpeg;
            img.Elements.SetInteger("/Length", jpeg.Length);
            img.Elements.SetName("/Filter", "/DCTDecode");
            img.Elements.Remove("/DecodeParms");
            img.Elements.SetInteger("/Width", bitmap.PixelWidth);
            img.Elements.SetInteger("/Height", bitmap.PixelHeight);
            return true;
        }

        // Minimal content stream walk: tracks the CTM over q, Q and cm, and records the drawn size of each
        // image on Do (unit square through the CTM). Form XObjects are entered with their /Matrix.
        // Strings, comments, hex strings and inline image data are skipped; anything else is ignored.
        // Stops early once the shared budget runs out (forms drawing forms many times over).
        private static void ScanContent(byte[] data, PdfDictionary? resources, Matrix ctm,
            Dictionary<PdfDictionary, (double W, double H)> drawn, int depth, HashSet<PdfDictionary> chain, ScanBudget budget)
        {
            var stack = new Stack<Matrix>();
            var nums = new List<double>();
            string? name = null;
            int i = 0, len = data.Length;
            while (i < len && !budget.Exhausted)
            {
                budget.Tick();
                byte c = data[i];
                if (IsWhite(c)) { i++; continue; }
                if (c == '%')
                {
                    while (i < len && data[i] != '\n' && data[i] != '\r') i++;
                    continue;
                }
                if (c == '(')
                {
                    int nest = 0;
                    for (; i < len; i++)
                    {
                        if (data[i] == '\\') { i++; continue; }
                        if (data[i] == '(') nest++;
                        else if (data[i] == ')' && --nest == 0) { i++; break; }
                    }
                    continue;
                }
                if (c == '<')
                {
                    if (i + 1 < len && data[i + 1] == '<') { i += 2; continue; }
                    while (i < len && data[i] != '>') i++;
                    i++;
                    continue;
                }
                if (c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == ')') { i++; continue; }

                int start = i++;
                while (i < len && !IsWhite(data[i]) && !IsDelimiter(data[i])) i++;
                string token = Encoding.Latin1.GetString(data, start, i - start);

                if (c == '/') { name = token; continue; }
                if (c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9'))
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) nums.Add(v);
                    continue;
                }

                switch (token)
                {
                    case "q":
                        stack.Push(ctm);
                        break;
                    case "Q":
                        if (stack.Count > 0) ctm = stack.Pop();
                        break;
                    case "cm":
                        if (nums.Count >= 6)
                        {
                            int k = nums.Count - 6;
                            var m = new Matrix(nums[k], nums[k + 1], nums[k + 2], nums[k + 3], nums[k + 4], nums[k + 5]);
                            ctm = Matrix.Multiply(m, ctm);
                        }
                        break;
                    case "Do":
                        if (name != null && resources?.Elements.GetDictionary("/XObject")?.Elements.GetObject(name) is PdfDictionary x)
                            DrawXObject(x, resources, ctm, drawn, depth, chain, budget);
                        break;
                    case "ID":
                        // Inline image data runs to whitespace + EI + whitespace (or the end).
                        i++;
                        while (i + 2 < len && !(IsWhite(data[i]) && data[i + 1] == 'E' && data[i + 2] == 'I'
                            && (i + 3 >= len || IsWhite(data[i + 3]) || IsDelimiter(data[i + 3]))))
                            i++;
                        i = Math.Min(len, i + 3);
                        break;
                }
                nums.Clear();
                name = null;
            }
        }

        private static void DrawXObject(PdfDictionary x, PdfDictionary? resources, Matrix ctm,
            Dictionary<PdfDictionary, (double W, double H)> drawn, int depth, HashSet<PdfDictionary> chain, ScanBudget budget)
        {
            string? subtype = NameOf(x.Elements["/Subtype"]);
            if (subtype == "/Image")
            {
                double w = Math.Sqrt(ctm.M11 * ctm.M11 + ctm.M12 * ctm.M12);
                double h = Math.Sqrt(ctm.M21 * ctm.M21 + ctm.M22 * ctm.M22);
                if (w < 0.01 || h < 0.01) return;
                drawn[x] = drawn.TryGetValue(x, out var old) ? (Math.Max(old.W, w), Math.Max(old.H, h)) : (w, h);
            }
            else if (subtype == "/Form" && x.Stream != null && depth < 8 && budget.Charge(x.Stream.Length) && chain.Add(x))
            {
                Matrix m = Matrix.Identity;
                if (x.Elements.GetArray("/Matrix") is PdfArray a && a.Elements.Count == 6)
                {
                    m = new Matrix(NumOf(a.Elements[0]), NumOf(a.Elements[1]), NumOf(a.Elements[2]),
                        NumOf(a.Elements[3]), NumOf(a.Elements[4]), NumOf(a.Elements[5]));
                }
                PdfDictionary? res = x.Elements.GetDictionary("/Resources") ?? resources;
                byte[] data = x.Stream.UnfilteredValue;
                if (budget.AddBytes(data.Length))
                    ScanContent(data, res, Matrix.Multiply(m, ctm), drawn, depth + 1, chain, budget);
                chain.Remove(x);
            }
        }

        // Per-document cap on form XObject visits and content bytes scanned, so nested forms that each draw the
        // next many times (40^8 scans) stop instead of hanging. Also polls the cancel token from the scan loop.
        private sealed class ScanBudget
        {
            private const int MaxForms = 500_000;
            private const long MaxBytes = 256L * 1024 * 1024;
            private readonly CancellationToken ct;
            private int forms, ticks;
            private long bytes;

            public ScanBudget(CancellationToken ct) => this.ct = ct;

            public bool Exhausted { get; private set; }

            // Counts one content stream (a page's or a form's, n stored bytes); false once over the budget.
            public bool Charge(long n)
            {
                if (Exhausted) return false;
                if (++forms > MaxForms) Exhausted = true;
                return AddBytes(n);
            }

            // Counts n bytes about to be scanned (a form's decoded stream).
            public bool AddBytes(long n)
            {
                bytes += Math.Max(0, n);
                if (bytes > MaxBytes) Exhausted = true;
                return !Exhausted;
            }

            public void Tick()
            {
                if ((++ticks & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            }
        }

        private static bool IsWhite(byte b) => b == ' ' || b == '\n' || b == '\r' || b == '\t' || b == '\f' || b == 0;

        private static bool IsDelimiter(byte b) =>
            b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%';

        // Walks /Parent for inheritable page keys (/Resources, /MediaBox) without creating entries.
        private static PdfItem? Inherited(PdfDictionary page, string key)
        {
            PdfDictionary? d = page;
            for (int n = 0; d != null && n < 64; n++)
            {
                PdfItem? v = d.Elements[key];
                if (v != null) return v;
                d = d.Elements.GetDictionary("/Parent");
            }
            return null;
        }

        private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

        private static string? NameOf(PdfItem? item) => Resolve(item) switch
        {
            PdfName n => n.Value,
            PdfNameObject n => n.Value,
            _ => null,
        };

        // NaN when missing or not a number.
        private static double NumOf(PdfItem? item) => Resolve(item) switch
        {
            PdfInteger i => i.Value,
            PdfLongInteger l => l.Value,
            PdfReal r => r.Value,
            PdfIntegerObject i => i.Value,
            PdfLongIntegerObject l => l.Value,
            PdfRealObject r => r.Value,
            _ => double.NaN,
        };

        private static string Existing(string path)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}", path);
            return path;
        }

        // Opens for page import (Compress uses Modify); shown is the user's file name in messages
        // (differs for image temp PDFs).
        // Owner-restricted PDFs (opened with the empty user password) are refused in every mode, as Modify
        // already does, so no edit strips the author's restrictions. The page tree is checked before doc.Pages.
        private static PdfDocument Open(string path, string? shown = null, PdfDocumentOpenMode mode = PdfDocumentOpenMode.Import)
        {
            string name = Path.GetFileName(shown ?? path);
            // Modify mode flattens the page tree while opening, so a damaged tree is caught by an Import open first.
            if (mode != PdfDocumentOpenMode.Import)
                using (Open(path, shown)) { }

            PdfDocument doc;
            try
            {
                doc = PdfReader.Open(path, mode);
            }
            catch (Exception ex)
            {
                Log.Write($"PDF open failed for {shown ?? path}: {ex}");
                if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{name} is password protected.", ex);
                throw new InvalidOperationException(
                    $"Cannot open {name}. It may be password protected or damaged.", ex);
            }

            try
            {
                if (!HasOwnerPermissions(doc) && !PermitsAssembly(doc))
                    throw new InvalidOperationException($"{name} is password protected.");
                CheckPageTree(doc);
                return doc;
            }
            catch (Exception ex)
            {
                Log.Write($"PDF refused: {shown ?? path}: {ex.Message}");
                doc.Dispose();
                throw;
            }
        }

        // PdfSecuritySettings.HasOwnerPermissions (false when an encrypted PDF opened with the user password)
        // has an internal getter in PDFsharp 6.2.4, so it is read by reflection; if that ever fails, nothing is refused.
        private static readonly System.Reflection.MethodInfo? OwnerPermissionsGetter =
            typeof(PdfSharp.Pdf.Security.PdfSecuritySettings).GetProperty("HasOwnerPermissions")?.GetGetMethod(nonPublic: true);

        private static bool HasOwnerPermissions(PdfDocument doc)
        {
            try
            {
                return OwnerPermissionsGetter?.Invoke(doc.SecuritySettings, null) is not false;
            }
            catch (Exception ex)
            {
                Log.Write($"PDF owner permission check failed: {ex.Message}");
                return true;
            }
        }

        // An owner password that still grants page assembly (most bank statements, invoices, tickets) does not
        // stop Split, Pages or Merge; only one that forbids it does.
        private static readonly System.Reflection.PropertyInfo? TrailerProperty = typeof(PdfDocument).GetProperty("Trailer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        private static bool PermitsAssembly(PdfDocument doc)
        {
            // PDFsharp's Permit* getters return its own settings, not the file's, so read /P from /Encrypt:
            // bit 4 (modify) or bit 11 (assemble) allows inserting, removing and rotating pages.
            try
            {
                // PdfDocument.Trailer is internal in PDFsharp 6.2.4.
                if (TrailerProperty?.GetValue(doc) is not PdfDictionary trailer
                    || Resolve(trailer.Elements["/Encrypt"]) is not PdfDictionary encrypt) return false;
                double p = NumOf(encrypt.Elements["/P"]);
                if (double.IsNaN(p)) return false;
                long bits = (long)p; // some writers store /P unsigned (4294967292 for -4)
                return (bits & (1 << 3)) != 0 || (bits & (1 << 10)) != 0;
            }
            catch (Exception ex)
            {
                Log.Write($"PDF assembly permission check failed: {ex.Message}");
                return false;
            }
        }

        private const int MaxTreeDepth = 64;
        private const int MaxTreeNodes = 2_000_000;

        // PDFsharp flattens the page tree recursively and overflows the stack (uncatchable) on a /Kids cycle,
        // so walk it first: every node with /Kids must be reached once, within depth and node caps.
        private static void CheckPageTree(PdfDocument doc)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            int nodes = 0;
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            if (Resolve(doc.Internals.Catalog.Elements["/Pages"]) is PdfDictionary root) stack.Push((root, 0));
            while (stack.Count > 0)
            {
                (PdfDictionary node, int depth) = stack.Pop();
                if (++nodes > MaxTreeNodes) throw new InvalidDataException("The PDF's page tree is damaged.");
                if (Resolve(node.Elements["/Kids"]) is not PdfArray kids) continue;
                if (depth >= MaxTreeDepth || !seen.Add(node)) throw new InvalidDataException("The PDF's page tree is damaged.");
                for (int i = 0; i < kids.Elements.Count; i++)
                    if (Resolve(kids.Elements[i]) is PdfDictionary kid) stack.Push((kid, depth + 1));
            }
        }

        internal static string NewWorkDir()
        {
            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return work;
        }
    }
}
