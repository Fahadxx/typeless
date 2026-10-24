using System;
using System.Collections.Generic;
using System.IO;
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

        // Renders every page at 2x (about 192 dpi) to work\page_NNNN.png or .jpg, in page order.
        public static List<string> RenderPages(string pdfPath, string target, string work)
        {
            if (target != "png" && target != "jpg")
                throw new NotSupportedException($"Page render target not supported: {target}");

            PdfDocument doc;
            try
            {
                StorageFile file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask().GetAwaiter().GetResult();
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

            var outputs = new List<string>((int)doc.PageCount);
            for (uint i = 0; i < doc.PageCount; i++)
            {
                string output = Path.Combine(work, $"page_{i + 1:D4}.{target}");
                using (PdfPage page = doc.GetPage(i))
                using (var ras = new InMemoryRandomAccessStream())
                {
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * 2)),
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
