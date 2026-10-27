using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Typeless
{
    public static class ImageBackend
    {
        private static readonly int[] IcoSizes = { 256, 64, 48, 32, 16 };
        private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(5);

        // Writes output file(s) into work and returns them in page order.
        public static List<string> Convert(string src, string target, string work)
        {
            string output = Path.Combine(work, "out." + target);
            switch (target)
            {
                case "png":
                case "jpg":
                case "bmp":
                case "gif":
                case "tiff":
                case "ico":
                case "webp":
                case "pdf":
                    Write(Load(src, work), target, output, work);
                    break;
                default:
                    throw new NotSupportedException($"Image target not supported: {target}");
            }
            return new List<string> { output };
        }

        private static void Write(BitmapSource image, string target, string output, string work, int jpegQuality = 92)
        {
            switch (target)
            {
                case "png":
                    SavePng(image, output);
                    break;
                case "jpg":
                    SaveJpeg(Flatten(image), output, jpegQuality);
                    break;
                case "bmp":
                    Save(new BmpBitmapEncoder(), Flatten(image), output);
                    break;
                case "gif":
                    Save(new GifBitmapEncoder(), image, output);
                    break;
                case "tiff":
                    Save(new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }, image, output);
                    break;
                case "ico":
                    SaveIco(image, output);
                    break;
                case "webp":
                    SaveWebp(image, output, work);
                    break;
                case "pdf":
                    SavePdf(image, output);
                    break;
                default:
                    throw new NotSupportedException($"Image target not supported: {target}");
            }
        }

        // Decodes src (same pixels Crop uses) in its own temp work dir, for the crop UI to display.
        public static BitmapSource LoadForCrop(string path)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}", path);
            string work = NewWorkDir();
            try
            {
                return Load(path, work);
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        public static string Crop(string src, int x, int y, int w, int h)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanCrop(src))
                throw new NotSupportedException($"Cannot crop {Path.GetFileName(src)} (unsupported type or missing backend).");
            return Crop(src, LoadForCrop(src), x, y, w, h);
        }

        // Crops an already loaded bitmap (pixel coordinates), saves in the source format next to src.
        public static string Crop(string src, BitmapSource loaded, int x, int y, int w, int h)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanCrop(src))
                throw new NotSupportedException($"Cannot crop {Path.GetFileName(src)} (unsupported type or missing backend).");

            int pw = loaded.PixelWidth, ph = loaded.PixelHeight;
            x = Math.Clamp(x, 0, pw - 1);
            y = Math.Clamp(y, 0, ph - 1);
            w = Math.Clamp(w, 1, pw - x);
            h = Math.Clamp(h, 1, ph - y);

            string work = NewWorkDir();
            try
            {
                var cropped = new CroppedBitmap(loaded, new Int32Rect(x, y, w, h));
                cropped.Freeze();

                string target = Formats.Norm(src);
                string output = Path.Combine(work, "out." + target);
                Write(cropped, target, output, work);

                string dest = Naming.WithSuffix(src, "crop");
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // Rotates/flips src (RotateSpec convention: mirror first, then turn clockwise) into
        // <stem>_rotated.<ext>, same format, jpg at q95. Decoded EXIF-oriented, so the output pixels
        // are upright and it carries no orientation tag.
        public static string Transform(string src, int rotate, bool flipH, bool flipV)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanCrop(src))
                throw new NotSupportedException($"Cannot rotate {Path.GetFileName(src)} (unsupported type or missing backend).");
            RotateSpec spec = RotateSpec.Normalize(rotate, flipH, flipV);
            if (spec.IsIdentity) throw new ArgumentException("Nothing to rotate");

            string work = NewWorkDir();
            try
            {
                BitmapSource image = Load(src, work);
                if (spec.HFlip) image = Freeze(new TransformedBitmap(image, new ScaleTransform(-1, 1)));
                if (spec.Degrees != 0) image = Freeze(new TransformedBitmap(image, new RotateTransform(spec.Degrees)));

                string target = Formats.Norm(src);
                string output = Path.Combine(work, "out." + target);
                Write(image, target, output, work, jpegQuality: 95);

                string dest = Naming.WithSuffix(src, "rotated");
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        private static string NewWorkDir()
        {
            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return work;
        }

        // Decodes the first frame as frozen Bgra32 with EXIF orientation applied.
        // Falls back to ffmpeg when WIC has no codec (heic, avif, webp without extensions).
        public static BitmapSource Load(string path, string work)
        {
            try
            {
                return Decode(path);
            }
            catch (Exception ex) when (ex is not IOException && ex is not UnauthorizedAccessException && ex is not ImageTooLargeException)
            {
                string? ffmpeg = Tools.Ffmpeg;
                if (ffmpeg == null)
                    throw new NotSupportedException(
                        $"Windows cannot decode {Path.GetFileName(path)} and ffmpeg was not found. " +
                        "Install the HEIF/AV1/WebP image extensions or ffmpeg.", ex);

                Log.Write($"WIC decode failed for {path}, trying ffmpeg: {ex.Message}");
                string decoded = Path.Combine(work, "decoded.png");
                Tools.RunProcess(ffmpeg, new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-frames:v", "1", decoded,
                }, FfmpegTimeout);
                return Decode(decoded);
            }
        }

        // Decodes above this many pixels are refused (a 99-byte PNG can declare 20000x20000, about 5 GB).
        private const long MaxPixels = 250_000_000;

        private sealed class ImageTooLargeException : InvalidOperationException
        {
            public ImageTooLargeException(string message) : base(message) { }
        }

        private static BitmapSource Decode(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // No OnLoad cache: the size is checked before any pixels are decoded; the stream stays open until
            // the WriteableBitmap copy below.
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            BitmapFrame frame = decoder.Frames[0];
            if (decoder is IconBitmapDecoder)
            {
                // Icons hold several sizes; use the largest.
                foreach (BitmapFrame f in decoder.Frames)
                    if ((long)f.PixelWidth * f.PixelHeight > (long)frame.PixelWidth * frame.PixelHeight) frame = f;
            }
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
                throw new ImageTooLargeException($"The image is too large ({frame.PixelWidth}\u00D7{frame.PixelHeight}).");

            BitmapSource image = frame;
            Transform? orient = Orientation(frame);
            if (orient != null) image = new TransformedBitmap(image, orient);

            if (image.Format != PixelFormats.Bgra32)
                image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

            // Materialize pixels while the stream is still open.
            var result = new WriteableBitmap(image);
            result.Freeze();
            return result;
        }

        // EXIF orientation 2..8 as the transform that makes the pixels upright, in the RotateSpec convention
        // (mirror horizontally first, then turn clockwise): 2 mirror, 3 180, 4 mirror+180 (flip vertical),
        // 5 mirror+270 (transpose), 6 90, 7 mirror+90 (transverse), 8 270. Null for 1 or no tag.
        private static Transform? Orientation(BitmapFrame frame)
        {
            ushort o = 1;
            try
            {
                if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort v) o = v;
            }
            catch { }

            (bool mirror, int deg) = o switch
            {
                2 => (true, 0),
                3 => (false, 180),
                4 => (true, 180),
                5 => (true, 270),
                6 => (false, 90),
                7 => (true, 90),
                8 => (false, 270),
                _ => (false, 0),
            };
            if (!mirror && deg == 0) return null;
            if (!mirror) return new RotateTransform(deg);
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(-1, 1));
            if (deg != 0) group.Children.Add(new RotateTransform(deg));
            return group;
        }

        // Alpha-blends a Bgra32 image over white and returns an opaque Bgr24 image at 96 dpi.
        public static BitmapSource Flatten(BitmapSource bgra)
        {
            if (bgra.Format != PixelFormats.Bgra32)
                bgra = new FormatConvertedBitmap(bgra, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            int srcStride = w * 4;
            int dstStride = (w * 3 + 3) & ~3;
            var src = new byte[(long)srcStride * h];
            bgra.CopyPixels(src, srcStride, 0);
            var dst = new byte[(long)dstStride * h];

            for (int y = 0; y < h; y++)
            {
                int si = y * srcStride;
                int di = y * dstStride;
                for (int x = 0; x < w; x++, si += 4, di += 3)
                {
                    int a = src[si + 3];
                    int inv = 255 * (255 - a);
                    dst[di] = (byte)((src[si] * a + inv + 127) / 255);
                    dst[di + 1] = (byte)((src[si + 1] * a + inv + 127) / 255);
                    dst[di + 2] = (byte)((src[si + 2] * a + inv + 127) / 255);
                }
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, dst, dstStride);
            result.Freeze();
            return result;
        }

        public static void SaveJpeg(BitmapSource image, string path, int quality = 92)
        {
            Save(new JpegBitmapEncoder { QualityLevel = quality }, image, path);
        }

        public static void SavePng(BitmapSource image, string path)
        {
            Save(new PngBitmapEncoder(), image, path);
        }

        private static void Save(BitmapEncoder encoder, BitmapSource image, string path)
        {
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        internal static byte[] Encode(BitmapEncoder encoder, BitmapSource image)
        {
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static void SaveWebp(BitmapSource image, string output, string work)
        {
            string ffmpeg = Tools.Ffmpeg
                ?? throw new NotSupportedException("Converting to webp requires ffmpeg, which was not found.");
            string tmp = Path.Combine(work, "tmp.png");
            SavePng(image, tmp);
            Tools.RunProcess(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", tmp, "-c:v", "libwebp", "-quality", "90", output,
            }, FfmpegTimeout);
        }

        private static void SaveIco(BitmapSource image, string output)
        {
            var entries = new List<byte[]>(IcoSizes.Length);
            foreach (int size in IcoSizes)
                entries.Add(Encode(new PngBitmapEncoder(), SquareIcon(image, size)));

            using var fs = new FileStream(output, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)entries.Count);

            int offset = 6 + 16 * entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                int size = IcoSizes[i];
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(entries[i].Length);
                bw.Write(offset);
                offset += entries[i].Length;
            }
            foreach (byte[] data in entries) bw.Write(data);
        }

        // Scales so the longer side equals size, then centers it on a transparent size x size canvas.
        private static BitmapSource SquareIcon(BitmapSource image, int size)
        {
            BitmapSource scaled = image;
            // Halve repeatedly first so large downscales keep detail.
            while (Math.Max(scaled.PixelWidth, scaled.PixelHeight) >= size * 2)
                scaled = Freeze(new TransformedBitmap(scaled, new ScaleTransform(0.5, 0.5)));

            int longest = Math.Max(scaled.PixelWidth, scaled.PixelHeight);
            if (longest != size)
            {
                double s = (double)size / longest;
                scaled = Freeze(new TransformedBitmap(scaled, new ScaleTransform(s, s)));
            }
            if (scaled.Format != PixelFormats.Bgra32)
                scaled = Freeze(new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0));

            int sw = Math.Min(size, scaled.PixelWidth), sh = Math.Min(size, scaled.PixelHeight);
            int srcStride = scaled.PixelWidth * 4;
            var src = new byte[(long)srcStride * scaled.PixelHeight];
            scaled.CopyPixels(src, srcStride, 0);

            int dstStride = size * 4;
            var dst = new byte[(long)dstStride * size];
            int ox = (size - sw) / 2, oy = (size - sh) / 2;
            for (int y = 0; y < sh; y++)
                Buffer.BlockCopy(src, y * srcStride, dst, (oy + y) * dstStride + ox * 4, sw * 4);

            return Freeze(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, dst, dstStride));
        }

        // Downscales to about w x h, halving first as SquareIcon does; read the result's PixelWidth/Height
        // for the exact size. Used by PdfEdit.Compress.
        internal static BitmapSource ScaleTo(BitmapSource image, int w, int h)
        {
            BitmapSource scaled = image;
            while (scaled.PixelWidth >= w * 2 && scaled.PixelHeight >= h * 2)
                scaled = Freeze(new TransformedBitmap(scaled, new ScaleTransform(0.5, 0.5)));
            if (scaled.PixelWidth != w || scaled.PixelHeight != h)
            {
                scaled = Freeze(new TransformedBitmap(scaled,
                    new ScaleTransform((double)w / scaled.PixelWidth, (double)h / scaled.PixelHeight)));
            }
            return scaled;
        }

        private static BitmapSource Freeze(BitmapSource b)
        {
            b.Freeze();
            return b;
        }

        // PDF pages may be at most 14400 points per side.
        private const double MaxPagePoints = 14400;

        // Writes a one-page PDF with the image embedded as a JPEG, sized at 72/96 of its pixel size, scaled down
        // (pixels kept) when a side would pass MaxPagePoints. Also used by PdfEdit.Merge for image pages.
        internal static void SavePdf(BitmapSource image, string output)
        {
            BitmapSource flat = Flatten(image);
            byte[] jpeg = Encode(new JpegBitmapEncoder { QualityLevel = 92 }, flat);
            int pw = flat.PixelWidth, ph = flat.PixelHeight;
            double pt = Math.Min(0.75, MaxPagePoints / Math.Max(pw, ph));
            string w = Math.Min(MaxPagePoints, pw * pt).ToString("0.##", CultureInfo.InvariantCulture);
            string h = Math.Min(MaxPagePoints, ph * pt).ToString("0.##", CultureInfo.InvariantCulture);
            string content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";

            using var ms = new MemoryStream();
            var offsets = new long[6];
            void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

            Ascii("%PDF-1.4\n");
            ms.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

            offsets[1] = ms.Position;
            Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = ms.Position;
            Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            offsets[3] = ms.Position;
            Ascii($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                  "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            offsets[4] = ms.Position;
            Ascii($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {pw} /Height {ph} " +
                  $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            ms.Write(jpeg, 0, jpeg.Length);
            Ascii("\nendstream\nendobj\n");
            offsets[5] = ms.Position;
            Ascii($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

            long xref = ms.Position;
            var sb = new StringBuilder();
            sb.Append("xref\n0 6\n0000000000 65535 f \n");
            for (int i = 1; i <= 5; i++)
                sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n")
              .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
            Ascii(sb.ToString());

            File.WriteAllBytes(output, ms.ToArray());
        }
    }
}
