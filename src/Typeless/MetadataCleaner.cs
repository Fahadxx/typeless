using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace Typeless
{
    // CLEAN: removes metadata (location, camera, dates, software) without touching pixels or streams.
    // jpg, png, webp and gif are rewritten byte by byte; tiff is re-encoded losslessly; bmp and ico
    // carry none and are copied; videos are stream-copied by ffmpeg.
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
                            // A fresh frame carries no metadata; LZW keeps the pixels lossless.
                            BitmapSource image = ImageBackend.Load(src, work);
                            File.WriteAllBytes(output, ImageBackend.Encode(
                                new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }, image));
                            break;
                        case "bmp":
                        case "ico":
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

        // Drops APP1 (EXIF, XMP), APP13 (IPTC/Photoshop) and COM before the scan; keeps APP0, APP2 (ICC),
        // APP14 (Adobe) and every other marker. Everything from SOS on is copied verbatim.
        // An EXIF orientation of 2..8 comes back as a minimal APP1 holding only that tag.
        private static byte[] CleanJpeg(byte[] d)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw BadJpeg();

            var kept = new List<(int Start, int End, byte Marker)>();
            int orientation = 1;
            bool sawExif = false;
            int pos = 2, tail = d.Length;
            while (true)
            {
                if (pos >= d.Length || d[pos] != 0xFF) throw BadJpeg();
                int start = pos;
                while (pos < d.Length && d[pos] == 0xFF) pos++; // fill bytes
                if (pos >= d.Length) throw BadJpeg();
                byte marker = d[pos++];

                // Standalone markers have no length.
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    kept.Add((start, pos, marker));
                    continue;
                }
                // SOS (scan data follows) or an early EOI: the rest goes out as is.
                if (marker == 0xDA || marker == 0xD9)
                {
                    tail = start;
                    break;
                }

                if (pos + 2 > d.Length) throw BadJpeg();
                int len = (d[pos] << 8) | d[pos + 1];
                if (len < 2 || pos + len > d.Length) throw BadJpeg();
                int end = pos + len;

                if (marker == 0xE1)
                {
                    if (!sawExif && IsExif(d, pos + 2, end))
                    {
                        sawExif = true;
                        orientation = ExifOrientation(d, pos + 8, end);
                    }
                }
                else if (marker != 0xED && marker != 0xFE)
                {
                    kept.Add((start, end, marker));
                }
                pos = end;
            }

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 2); // SOI
            int k = 0;
            // EXIF goes right after SOI, or after a leading JFIF APP0.
            if (kept.Count > 0 && kept[0].Marker == 0xE0)
            {
                ms.Write(d, kept[0].Start, kept[0].End - kept[0].Start);
                k = 1;
            }
            if (orientation >= 2 && orientation <= 8)
            {
                byte[] app1 = OrientationApp1(orientation);
                ms.Write(app1, 0, app1.Length);
            }
            for (; k < kept.Count; k++)
                ms.Write(d, kept[k].Start, kept[k].End - kept[k].Start);
            ms.Write(d, tail, d.Length - tail);
            return ms.ToArray();
        }

        private static bool IsExif(byte[] d, int p, int end) =>
            p + 6 <= end && d[p] == (byte)'E' && d[p + 1] == (byte)'x' && d[p + 2] == (byte)'i'
                && d[p + 3] == (byte)'f' && d[p + 4] == 0 && d[p + 5] == 0;

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
        // next IFD offset 0.
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
        private static readonly HashSet<string> PngDropped = new HashSet<string>(StringComparer.Ordinal)
        {
            "tEXt", "zTXt", "iTXt", "eXIf", "tIME",
        };

        // Copies every chunk (CRC included) except the text, EXIF and time chunks, up to IEND.
        private static byte[] CleanPng(byte[] d)
        {
            if (d.Length < 8) throw BadPng();
            for (int i = 0; i < 8; i++)
                if (d[i] != PngSignature[i]) throw BadPng();

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 8);
            int pos = 8;
            while (true)
            {
                if (pos + 12 > d.Length) throw BadPng();
                long len = ((long)d[pos] << 24) | ((long)d[pos + 1] << 16) | ((long)d[pos + 2] << 8) | d[pos + 3];
                long end = pos + 12 + len;
                if (end > d.Length) throw BadPng();
                string type = Encoding.ASCII.GetString(d, pos + 4, 4);
                if (!PngDropped.Contains(type)) ms.Write(d, pos, (int)(end - pos));
                pos = (int)end;
                if (type == "IEND") break;
            }
            return ms.ToArray();
        }

        private static InvalidOperationException BadPng() => new InvalidOperationException("Cannot read this PNG");

        // Drops the EXIF and "XMP " chunks, clears their VP8X flags and recomputes the RIFF size.
        // Chunks keep their even-size padding byte.
        private static byte[] CleanWebp(byte[] d)
        {
            if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WEBP")
                throw BadWebp();
            long riff = 8L + (uint)BitConverter.ToInt32(d, 4);
            int limit = (int)Math.Min(d.Length, riff);

            using var ms = new MemoryStream(d.Length);
            ms.Write(d, 0, 12); // size patched below
            int pos = 12;
            while (pos + 8 <= limit)
            {
                string fourcc = Encoding.ASCII.GetString(d, pos, 4);
                long size = (uint)BitConverter.ToInt32(d, pos + 4);
                long end = pos + 8 + size + (size & 1);
                if (pos + 8 + size > limit) throw BadWebp();
                end = Math.Min(end, limit); // a missing final pad byte
                if (fourcc != "EXIF" && fourcc != "XMP ")
                {
                    long at = ms.Position;
                    ms.Write(d, pos, (int)(end - pos));
                    if (fourcc == "VP8X" && size > 0)
                    {
                        byte[] buf = ms.GetBuffer();
                        buf[at + 8] = (byte)(buf[at + 8] & ~(0x08 | 0x04)); // EXIF and XMP present flags
                    }
                }
                pos = (int)end;
            }

            byte[] result = ms.ToArray();
            BitConverter.GetBytes((uint)(result.Length - 8)).CopyTo(result, 4);
            return result;
        }

        private static InvalidOperationException BadWebp() => new InvalidOperationException("Cannot read this WebP");

        // Walks the GIF blocks and drops Comment Extensions and XMP Application Extensions.
        // Frames, Graphic Control and other Application Extensions (NETSCAPE looping) stay.
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
                    bool xmp = label == 0xFF && pos + 14 <= d.Length && d[pos + 2] == 11
                        && Encoding.ASCII.GetString(d, pos + 3, 11) == "XMP DataXMP";
                    pos = SkipSubBlocks(d, pos + 2);
                    if (label != 0xFE && !xmp) ms.Write(d, start, pos - start);
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

        private static string NewWorkDir()
        {
            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return work;
        }
    }
}
