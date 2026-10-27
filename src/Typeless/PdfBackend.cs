using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Typeless
{
    public static class PdfBackend
    {
        // Writes output file(s) into work and returns them in page order.
        public static List<string> Convert(string src, string target, string work)
        {
            switch (target)
            {
                case "png":
                case "jpg":
                    return RenderPages(src, target, work);
                case "docx":
                case "txt":
                    return new List<string> { OfficeBackend.ConvertDocument(src, target, work) };
                case "pptx":
                    {
                        // Picture slides: one 2x page render per slide.
                        string output = Path.Combine(work, "out.pptx");
                        PptxWriter.WritePictureDeck(RenderPages(src, "png", work), output);
                        return new List<string> { output };
                    }
                default:
                    throw new NotSupportedException($"PDF target not supported: {target}");
            }
        }

        // Render caps in output pixels: about 50 MP per page and 16384 px per side (huge MediaBoxes used tens of GB).
        private const double MaxRenderPixels = 50e6;
        private const double MaxRenderSide = 16384;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        // The renderer sizes its output in DIPs times the system scale (measured: 2x at 192 dpi), so the caps
        // divide by it.
        private static double SystemScale()
        {
            try { return Math.Max(1, GetDpiForSystem() / 96.0); } catch { return 1; }
        }

        // Renders every page at 2x (about 192 dpi) to work\page_NNNN.png or .jpg, in page order.
        // Large pages render smaller (MaxRenderPixels, MaxRenderSide), keeping the aspect ratio.
        public static List<string> RenderPages(string pdfPath, string target, string work)
        {
            if (target != "png" && target != "jpg")
                throw new NotSupportedException($"Page render target not supported: {target}");

            // StorageFile cannot open paths over MAX_PATH, so open a copy in work (a short temp path) unless the
            // file is already there (OfficeBackend's intermediate PDF).
            string full = Path.GetFullPath(pdfPath);
            string open = full;
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(work)),
                    StringComparison.OrdinalIgnoreCase))
            {
                open = Path.Combine(work, "render_source.pdf");
                File.Copy(full, open, overwrite: true);
            }

            PdfDocument doc;
            try
            {
                StorageFile file = StorageFile.GetFileFromPathAsync(open).AsTask().GetAwaiter().GetResult();
                doc = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Write($"PDF load failed for {pdfPath}: {ex}");
                throw new InvalidOperationException(
                    $"Cannot open {Path.GetFileName(pdfPath)}. It may be password protected or damaged.", ex);
            }

            if (doc.IsPasswordProtected)
                throw new InvalidOperationException($"{Path.GetFileName(pdfPath)} is password protected.");

            double k = SystemScale();
            var outputs = new List<string>((int)doc.PageCount);
            for (uint i = 0; i < doc.PageCount; i++)
            {
                string output = Path.Combine(work, $"page_{i + 1:D4}.{target}");
                using (PdfPage page = doc.GetPage(i))
                using (var ras = new InMemoryRandomAccessStream())
                {
                    // 2x the page size in DIPs, shrunk uniformly when the output would pass the caps.
                    double w = Math.Max(1, page.Size.Width) * 2, h = Math.Max(1, page.Size.Height) * 2;
                    double fit = Math.Min(Math.Min(1, Math.Sqrt(MaxRenderPixels / (w * k * h * k))),
                        Math.Min(MaxRenderSide / (w * k), MaxRenderSide / (h * k)));
                    double side = Math.Max(1, Math.Floor(MaxRenderSide / k));
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Clamp(Math.Round(w * fit), 1, side),
                        DestinationHeight = (uint)Math.Clamp(Math.Round(h * fit), 1, side),
                    };
                    page.RenderToStreamAsync(ras, options).AsTask().GetAwaiter().GetResult();

                    string png = target == "png" ? output : Path.Combine(work, $"render_{i + 1:D4}.png");
                    using (Stream s = ras.AsStream())
                    using (var fs = new FileStream(png, FileMode.Create, FileAccess.Write))
                    {
                        s.Seek(0, SeekOrigin.Begin);
                        s.CopyTo(fs);
                    }

                    if (target == "jpg")
                    {
                        ImageBackend.SaveJpeg(ImageBackend.Flatten(ImageBackend.Load(png, work)), output);
                        try { File.Delete(png); } catch { }
                    }
                }
                outputs.Add(output);
            }
            return outputs;
        }
    }
}
