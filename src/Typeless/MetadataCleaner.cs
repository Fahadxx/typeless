using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Typeless
{
    // CLEAN: removes metadata (location, camera, dates, software) without touching pixels or streams.
    // jpg, png, webp and gif are rewritten byte by byte, keeping only an allowlist of segments/chunks;
    // ico has its PNG entries cleaned the same way; tiff is re-decoded and written as LZW with its
    // pixel format and ICC profile (single page, 8-bit channels only); bmp carries none and is copied;
    // videos are stream-copied by ffmpeg.
    public static class MetadataCleaner
    {
        // Writes <stem>_clean.<ext> next to src (same format) and returns its path.
        public static string Clean(string src)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanClean(src))
                throw new NotSupportedException($"Cannot clean {Path.GetFileName(src)} (unsupported type or missing ffmpeg).");

            string work = NewWorkDir();
            try
            {
                // Keep the raw source extension so ffmpeg picks the same muxer.
                string output = Path.Combine(work, "out" + Path.GetExtension(src));
                if (Formats.KindOf(src) == Kind.Video)
                {
                    MediaBackend.StripMetadata(src, output);
                }
                else
                {
                    switch (Formats.Norm(src))
                    {
                        case "jpg":
                            File.WriteAllBytes(output, CleanJpeg(File.ReadAllBytes(src)));
                            break;
                        case "png":
                            File.WriteAllBytes(output, CleanPng(File.ReadAllBytes(src)));
                            break;
                        case "webp":
                            File.WriteAllBytes(output, CleanWebp(File.ReadAllBytes(src)));
                            break;
                        case "gif":
                            File.WriteAllBytes(output, CleanGif(File.ReadAllBytes(src)));
                            break;
                        case "tiff":
                            File.WriteAllBytes(output, CleanTiff(src));
                            break;
                        case "ico":
                            File.WriteAllBytes(output, CleanIco(File.ReadAllBytes(src)));
                            break;
                        case "bmp":
                            File.Copy(src, output); // no metadata to remove
                            break;
                        default:
                            throw new NotSupportedException($"Cannot clean {Path.GetFileName(src)}.");
                    }
                }

                string dest = Naming.WithSuffix(src, "clean");
                File.Move(output, dest);
                Log.Write($"clean: {src} -> {dest}");
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // Walks every segment, including those between the scans of a progressive JPEG, and keeps
        // an allowlist of APPn: APP0 JFIF (its thumbnail removed), APP2 ICC_PROFILE and APP14 Adobe.
        // Every other APPn (EXIF, XMP, IPTC, MPF, JUMBF/C2PA...) and COM is dropped. Scan data is
        // copied as is up to the next real marker; everything after the EOI (trailers, motion-photo
        // videos, MPF secondary images) is dropped.
        // An EXIF orientation of 2..8 comes back as a minimal APP1 holding only that tag.
        private static byte[] CleanJpeg(byte[] d)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw BadJpeg();

            var kept = new List<(byte[] Src, int Start, int End, byte Marker)>();
            int orientation = 1;
            bool sawExif = false, sawScan = false;
            int pos = 2;
            while (pos < d.Length)
            {
                if (d[pos] != 0xFF) throw BadJpeg();
                while (pos < d.Length && d[pos] == 0xFF) pos++; // fill bytes, dropped
                if (pos >= d.Length) throw BadJpeg();
                int start = pos - 1;
                byte marker = d[pos++];

                if (marker == 0xD9) // EOI: anything after it is not part of this image
                {
                    kept.Add((d, start, pos, marker));
                    break;
                }
                // Standalone markers have no length.
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    kept.Add((d, start, pos, marker));
                    continue;
                }

                if (pos + 2 > d.Length) throw BadJpeg();
                int len = (d[pos] << 8) | d[pos + 1];
                if (len < 2 || pos + len > d.Length) throw BadJpeg();
                int end = pos + len;

                if (marker == 0xDA) // SOS: header, then entropy-coded data up to the next marker
                {
                    sawScan = true;
                    end = ScanEnd(d, end);
                    kept.Add((d, start, end, marker));
                }
                else if (marker == 0xE1)
                {
                    if (!sawExif && IsExif(d, pos + 2, end))
                    {
                        sawExif = true;
                        orientation = ExifOrientation(d, pos + 8, end);
                    }
                }
                else if (marker == 0xE0)
                {
                    if (StartsWith(d, pos + 2, end, "JFIF\0") && len >= 16)
                    {
                        // Version, units and density only; a thumbnail is a second picture.
                        byte[] jfif = new byte[18];
                        Array.Copy(d, start, jfif, 0, 2);
                        Array.Copy(d, pos + 2, jfif, 4, 12);
                        jfif[3] = 16;
                        kept.Add((jfif, 0, 18, marker));
                    }
                }
                else if (marker == 0xE2 ? StartsWith(d, pos + 2, end, "ICC_PROFILE\0")
                    : marker == 0xEE ? StartsWith(d, pos + 2, end, "Adobe")
                    : (marker < 0xE0 || marker > 0xEF) && marker != 0xFE) // other APPn and COM go
                {
                    kept.Add((d, start, end, marker));
                }
                pos = end;
            }
            if (!sawScan) throw BadJpeg();

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 2); // SOI
            int k = 0;
            // EXIF goes right after SOI, or after a leading JFIF APP0.
            if (kept.Count > 0 && kept[0].Marker == 0xE0)
            {
                ms.Write(kept[0].Src, kept[0].Start, kept[0].End - kept[0].Start);
                k = 1;
            }
            if (orientation >= 2 && orientation <= 8)
            {
                byte[] app1 = OrientationApp1(orientation);
                ms.Write(app1, 0, app1.Length);
            }
            for (; k < kept.Count; k++)
                ms.Write(kept[k].Src, kept[k].Start, kept[k].End - kept[k].Start);
            return ms.ToArray();
        }

        // End of the entropy-coded data starting at p: the first FF (of a fill run) that leads to a
        // marker other than a stuffed 00 or a restart marker, or the end of the file.
        private static int ScanEnd(byte[] d, int p)
        {
            while (p < d.Length)
            {
                if (d[p] != 0xFF) { p++; continue; }
                int q = p + 1;
                while (q < d.Length && d[q] == 0xFF) q++;
                if (q >= d.Length) return d.Length;
                byte m = d[q];
                if (m != 0x00 && (m < 0xD0 || m > 0xD7)) return p;
                p = q + 1;
            }
            return d.Length;
        }

        private static bool StartsWith(byte[] d, int p, int end, string id)
        {
            if (p + id.Length > end) return false;
            for (int i = 0; i < id.Length; i++)
                if (d[p + i] != (byte)id[i]) return false;
            return true;
        }

        private static bool IsExif(byte[] d, int p, int end) => StartsWith(d, p, end, "Exif\0\0");

        // Orientation (tag 0x0112) from IFD0 of the TIFF block at tiff..end; 1 when absent or unreadable.
        private static int ExifOrientation(byte[] d, int tiff, int end)
        {
            if (tiff + 8 > end) return 1;
            bool le;
            if (d[tiff] == (byte)'I' && d[tiff + 1] == (byte)'I') le = true;
            else if (d[tiff] == (byte)'M' && d[tiff + 1] == (byte)'M') le = false;
            else return 1;

            int U16(int p) => le ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
            long U32(int p) => le
                ? (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24))
                : (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);

            if (U16(tiff + 2) != 42) return 1;
            long ifd = tiff + U32(tiff + 4);
            if (ifd + 2 > end) return 1;
            int count = U16((int)ifd);
            for (int i = 0; i < count; i++)
            {
                long e = ifd + 2 + 12L * i;
                if (e + 12 > end) return 1;
                int entry = (int)e;
                if (U16(entry) == 0x0112 && U16(entry + 2) == 3) return U16(entry + 8); // SHORT, value inline
            }
            return 1;
        }

        // FF E1, length, "Exif\0\0", big-endian TIFF header, IFD0 with one Orientation SHORT entry,
        // next IFD offset 0. The TIFF block (from offset 10) doubles as a PNG eXIf payload.
        private static byte[] OrientationApp1(int orientation) => new byte[]
        {
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };

        private static InvalidOperationException BadJpeg() => new InvalidOperationException("Cannot read this JPEG");

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly HashSet<string> PngKept = new HashSet<string>(StringComparer.Ordinal)
        {
            "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs",
            "cICP", "mDCv", "cLLi", "acTL", "fcTL", "fdAT",
        };

        // Copies the image, color and animation chunks (CRC included) up to IEND and drops the rest
        // (text, eXIf, tIME, caBX/C2PA, private and unknown chunks). An eXIf orientation of 2..8 comes
        // back as a minimal eXIf right after IHDR.
        private static byte[] CleanPng(byte[] d)
        {
            if (d.Length < 8) throw BadPng();
            for (int i = 0; i < 8; i++)
                if (d[i] != PngSignature[i]) throw BadPng();

            var kept = new List<(int Start, int End)>();
            int orientation = 1;
            int pos = 8;
            while (true)
            {
                if (pos + 12 > d.Length) throw BadPng();
                long len = ((long)d[pos] << 24) | ((long)d[pos + 1] << 16) | ((long)d[pos + 2] << 8) | d[pos + 3];
                long end = pos + 12 + len;
                if (end > d.Length) throw BadPng();
                string type = Encoding.ASCII.GetString(d, pos + 4, 4);
                if (PngKept.Contains(type)) kept.Add((pos, (int)end));
                else if (type == "eXIf" && orientation == 1) orientation = ExifOrientation(d, pos + 8, (int)end - 4);
                pos = (int)end;
                if (type == "IEND") break;
            }
            if (kept.Count == 0 || Encoding.ASCII.GetString(d, kept[0].Start + 4, 4) != "IHDR") throw BadPng();

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 8);
            ms.Write(d, kept[0].Start, kept[0].End - kept[0].Start);
            if (orientation >= 2 && orientation <= 8)
            {
                byte[] tiff = OrientationApp1(orientation)[10..];
                byte[] chunk = new byte[12 + tiff.Length];
                chunk[3] = (byte)tiff.Length;
                Encoding.ASCII.GetBytes("eXIf").CopyTo(chunk, 4);
                tiff.CopyTo(chunk, 8);
                uint crc = Crc32(chunk, 4, 4 + tiff.Length);
                chunk[^4] = (byte)(crc >> 24); chunk[^3] = (byte)(crc >> 16); chunk[^2] = (byte)(crc >> 8); chunk[^1] = (byte)crc;
                ms.Write(chunk, 0, chunk.Length);
            }
            for (int k = 1; k < kept.Count; k++)
                ms.Write(d, kept[k].Start, kept[k].End - kept[k].Start);
            return ms.ToArray();
        }

        private static uint[]? crcTable;

        // PNG's CRC-32 (ISO 3309) over d[start, start + count).
        private static uint Crc32(byte[] d, int start, int count)
        {
            if (crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                crcTable = table;
            }
            uint crc = 0xFFFFFFFF;
            for (int i = start; i < start + count; i++) crc = crcTable[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        private static InvalidOperationException BadPng() => new InvalidOperationException("Cannot read this PNG");

        private static readonly HashSet<string> WebpKept = new HashSet<string>(StringComparer.Ordinal)
        {
            "VP8X", "VP8 ", "VP8L", "ALPH", "ANIM", "ANMF", "ICCP",
        };

        // Keeps the image, alpha, animation and ICC chunks and drops the rest (EXIF, "XMP ", unknown),
        // clears the VP8X EXIF and XMP flags and recomputes the RIFF size. Chunks keep their even-size
        // padding byte. A RIFF size that does not fit the file falls back to the file length.
        private static byte[] CleanWebp(byte[] d)
        {
            if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WEBP")
                throw BadWebp();
            long riff = 8L + (uint)BitConverter.ToInt32(d, 4);
            int limit = riff < 20 || riff > d.Length ? d.Length : (int)riff;

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 12); // size patched below
            bool still = false, anim = false, frame = false;
            int pos = 12;
            while (pos + 8 <= limit)
            {
                string fourcc = Encoding.ASCII.GetString(d, pos, 4);
                long size = (uint)BitConverter.ToInt32(d, pos + 4);
                long end = pos + 8 + size + (size & 1);
                if (pos + 8 + size > limit) throw BadWebp();
                end = Math.Min(end, limit); // a missing final pad byte
                if (WebpKept.Contains(fourcc))
                {
                    long at = ms.Position;
                    ms.Write(d, pos, (int)(end - pos));
                    if (fourcc == "VP8X" && size > 0)
                    {
                        byte[] buf = ms.GetBuffer();
                        buf[at + 8] = (byte)(buf[at + 8] & ~(0x08 | 0x04)); // EXIF and XMP present flags
                    }
                    if (fourcc == "VP8 " || fourcc == "VP8L") still = true;
                    else if (fourcc == "ANIM") anim = true;
                    else if (fourcc == "ANMF") frame = true;
                }
                pos = (int)end;
            }
            if (!still && !(anim && frame)) throw BadWebp();

            byte[] result = ms.ToArray();
            BitConverter.GetBytes((uint)(result.Length - 8)).CopyTo(result, 4);
            return result;
        }

        private static InvalidOperationException BadWebp() => new InvalidOperationException("Cannot read this WebP");

        // Walks the GIF blocks and keeps frames, Graphic Control and Plain Text Extensions, and the
        // looping (NETSCAPE2.0, ANIMEXTS1.0) and ICC Application Extensions. Comments, XMP, IPTC and
        // any other extension are dropped, and so is anything after the trailer.
        private static byte[] CleanGif(byte[] d)
        {
            if (d.Length < 13 || Encoding.ASCII.GetString(d, 0, 3) != "GIF") throw BadGif();
            int pos = 13;
            if ((d[10] & 0x80) != 0) pos += 3 << ((d[10] & 7) + 1); // global color table
            if (pos > d.Length) throw BadGif();

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, pos);
            while (true)
            {
                if (pos >= d.Length) throw BadGif();
                int start = pos;
                byte block = d[pos];
                if (block == 0x3B) // trailer
                {
                    ms.WriteByte(0x3B);
                    break;
                }
                if (block == 0x2C) // image descriptor, optional local color table, LZW size, data
                {
                    if (pos + 10 > d.Length) throw BadGif();
                    byte flags = d[pos + 9];
                    pos += 10;
                    if ((flags & 0x80) != 0) pos += 3 << ((flags & 7) + 1);
                    pos += 1; // LZW minimum code size
                    pos = SkipSubBlocks(d, pos);
                    ms.Write(d, start, pos - start);
                }
                else if (block == 0x21) // extension: label, then sub-blocks
                {
                    if (pos + 2 > d.Length) throw BadGif();
                    byte label = d[pos + 1];
                    bool keep = label == 0xF9 || label == 0x01
                        || (label == 0xFF && pos + 14 <= d.Length && d[pos + 2] == 11
                            && Encoding.ASCII.GetString(d, pos + 3, 11) is "NETSCAPE2.0" or "ANIMEXTS1.0" or "ICCRGBG1012");
                    pos = SkipSubBlocks(d, pos + 2);
                    if (keep) ms.Write(d, start, pos - start);
                }
                else throw BadGif();
            }
            return ms.ToArray();
        }

        // Returns the position after the zero-length terminator of the sub-blocks starting at pos.
        private static int SkipSubBlocks(byte[] d, int pos)
        {
            while (true)
            {
                if (pos >= d.Length) throw BadGif();
                int n = d[pos];
                pos += 1 + n;
                if (n == 0) return pos;
            }
        }

        private static InvalidOperationException BadGif() => new InvalidOperationException("Cannot read this GIF");

        // 8-bit-per-channel (or lower) formats the TIFF encoder writes back unchanged.
        private static readonly HashSet<PixelFormat> TiffFormats = new HashSet<PixelFormat>
        {
            PixelFormats.BlackWhite, PixelFormats.Gray2, PixelFormats.Gray4, PixelFormats.Gray8,
            PixelFormats.Indexed1, PixelFormats.Indexed2, PixelFormats.Indexed4, PixelFormats.Indexed8,
            PixelFormats.Bgr24, PixelFormats.Rgb24, PixelFormats.Bgr32, PixelFormats.Bgra32, PixelFormats.Pbgra32,
            PixelFormats.Cmyk32,
        };

        // Re-decodes the single page in its own pixel format and writes it as LZW with only its ICC
        // profile. The pixels go through a fresh bitmap so no tag travels along. Multi-page and
        // high bit depth files are refused rather than flattened.
        private static byte[] CleanTiff(string src)
        {
            byte[] bytes = File.ReadAllBytes(src);
            // Pixels come from a decode that ignores the profile (otherwise WIC color-converts them);
            // the profile itself and the orientation from a second one.
            BitmapFrame Decode(BitmapCreateOptions options)
            {
                try
                {
                    var decoder = BitmapDecoder.Create(new MemoryStream(bytes),
                        BitmapCreateOptions.PreservePixelFormat | options, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count != 1)
                        throw new InvalidOperationException("Clean does not support multi-page TIFF");
                    return decoder.Frames[0];
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException("Cannot read this TIFF", ex);
                }
            }

            BitmapFrame frame = Decode(BitmapCreateOptions.IgnoreColorProfile);
            BitmapFrame tagged = Decode(BitmapCreateOptions.None);
            if (!TiffFormats.Contains(frame.Format))
                throw new InvalidOperationException("Clean does not support high bit depth TIFF");

            int w = frame.PixelWidth, h = frame.PixelHeight;
            int stride = (w * frame.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[(long)stride * h];
            frame.CopyPixels(pixels, stride, 0);
            BitmapSource image = BitmapSource.Create(w, h, frame.DpiX, frame.DpiY, frame.Format, frame.Palette, pixels, stride);

            // Orientation 2..8 is kept as the only tag, like the JPEG path.
            BitmapMetadata? meta = null;
            object? o = null;
            try { o = (tagged.Metadata as BitmapMetadata)?.GetQuery("/ifd/{ushort=274}"); } catch { }
            if (o is ushort orientation && orientation >= 2 && orientation <= 8)
            {
                meta = new BitmapMetadata("tiff");
                meta.SetQuery("/ifd/{ushort=274}", orientation);
            }

            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
            encoder.Frames.Add(BitmapFrame.Create(image, null, meta, tagged.ColorContexts));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // Rebuilds the icon directory with every PNG-encoded entry cleaned like a PNG; BMP entries
        // (no metadata) are copied. Sizes and offsets are rewritten, data laid out in entry order.
        private static byte[] CleanIco(byte[] d)
        {
            if (d.Length < 6 || d[0] != 0 || d[1] != 0 || (d[2] != 1 && d[2] != 2) || d[3] != 0) throw BadIco();
            int count = d[4] | (d[5] << 8);
            if (count == 0 || 6 + 16L * count > d.Length) throw BadIco();

            var data = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                int e = 6 + 16 * i;
                long size = (uint)BitConverter.ToInt32(d, e + 8);
                long offset = (uint)BitConverter.ToInt32(d, e + 12);
                if (offset + size > d.Length || offset < 6 + 16L * count) throw BadIco();
                byte[] entry = d[(int)offset..(int)(offset + size)];
                bool png = size >= 8 && entry.AsSpan(0, 8).SequenceEqual(PngSignature);
                data[i] = png ? CleanPng(entry) : entry;
            }

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 6);
            long at = 6 + 16L * count;
            for (int i = 0; i < count; i++)
            {
                ms.Write(d, 6 + 16 * i, 8); // size, colors, planes, bit count
                ms.Write(BitConverter.GetBytes((uint)data[i].Length), 0, 4);
                ms.Write(BitConverter.GetBytes((uint)at), 0, 4);
                at += data[i].Length;
            }
            foreach (byte[] entry in data) ms.Write(entry, 0, entry.Length);
            return ms.ToArray();
        }

        private static InvalidOperationException BadIco() => new InvalidOperationException("Cannot read this icon");

        private static string NewWorkDir()
        {
            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return work;
        }
    }
}
