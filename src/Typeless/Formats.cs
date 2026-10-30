using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typeless
{
    public enum Kind { Image, Pdf, Document, Sheet, Slides, Audio, Video, Model }

    public static class Formats
    {
        public sealed record Entry(Kind Kind, string[] Sources, string[] Targets);

        // The single source of truth for the menu, the registry, and the converter.
        // Sources are raw extensions (aliases included). Targets are normalized, max 8.
        public static readonly IReadOnlyList<Entry> Table = new[]
        {
            new Entry(Kind.Image,
                new[] { "png", "jpg", "jpeg", "jfif", "bmp", "gif", "tif", "tiff", "webp", "ico", "heic", "heif", "avif" },
                new[] { "png", "jpg", "webp", "bmp", "gif", "tiff", "ico", "pdf" }),
            new Entry(Kind.Pdf,
                new[] { "pdf" },
                new[] { "docx", "png", "jpg", "txt", "pptx" }),
            new Entry(Kind.Document,
                new[] { "docx", "doc", "odt", "rtf", "txt" },
                new[] { "pdf", "docx", "odt", "rtf", "txt", "html", "png", "jpg" }),
            new Entry(Kind.Sheet,
                new[] { "xlsx", "xls", "ods", "csv" },
                new[] { "pdf", "xlsx", "ods", "csv", "html" }),
            new Entry(Kind.Slides,
                new[] { "pptx", "ppt", "odp" },
                new[] { "pdf", "pptx", "odp", "png", "jpg" }),
            new Entry(Kind.Audio,
                new[] { "mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma" },
                new[] { "mp3", "wav", "flac", "m4a", "ogg", "opus" }),
            new Entry(Kind.Video,
                new[] { "mp4", "mov", "mkv", "avi", "webm", "wmv", "flv", "m4v" },
                new[] { "mp4", "mkv", "webm", "mov", "avi", "gif", "mp3", "wav" }),
            // 3D models, built in (ModelBackend); TargetsFor drops the source's own format.
            new Entry(Kind.Model,
                new[] { "stl", "3mf", "obj", "ply", "glb" },
                new[] { "stl", "3mf", "obj", "ply", "glb" }),
        };

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jpeg"] = "jpg",
            ["jfif"] = "jpg",
            ["tif"] = "tiff",
            ["htm"] = "html",
            ["heif"] = "heic",
        };

        private static readonly Dictionary<string, Entry> BySource = BuildIndex();

        private static Dictionary<string, Entry> BuildIndex()
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (Entry e in Table)
                foreach (string s in e.Sources)
                    map[Norm(s)] = e;
            return map;
        }

        // Accepts "png", ".PNG", or "C:\x\photo.png"; returns lowercase, no dot, aliases resolved.
        public static string Norm(string extOrPath)
        {
            if (string.IsNullOrWhiteSpace(extOrPath)) return "";
            string s = extOrPath.Trim();
            if (s.IndexOfAny(new[] { '.', '\\', '/' }) >= 0) s = Path.GetExtension(s);
            s = s.TrimStart('.').ToLowerInvariant();
            return Aliases.TryGetValue(s, out string? alias) ? alias : s;
        }

        public static Kind? KindOf(string path)
        {
            return BySource.TryGetValue(Norm(path), out Entry? e) ? e.Kind : null;
        }

        public static IReadOnlyList<string> TargetsFor(string path)
        {
            string own = Norm(path);
            if (!BySource.TryGetValue(own, out Entry? e)) return Array.Empty<string>();
            return e.Targets
                .Where(t => t != own && Tools.IsAvailable(e.Kind, t))
                .ToList();
        }

        // Crop is an action, not a format: same-format output for these sources only.
        public static bool CanCrop(string path)
        {
            switch (Norm(path))
            {
                case "png":
                case "jpg":
                case "bmp":
                case "gif":
                case "tiff":
                case "ico":
                    return true;
                case "webp":
                    return Tools.Ffmpeg != null;
                default:
                    return false;
            }
        }

        // Trim is an action too: same-container output for audio and video, needs ffmpeg and ffprobe.
        public static bool CanTrim(string path)
        {
            Kind? kind = KindOf(path);
            return (kind == Kind.Audio || kind == Kind.Video) && Tools.Ffmpeg != null && Tools.Ffprobe != null;
        }

        // Audio is an instant action for a video: stream-copies its audio track, needs ffmpeg and ffprobe.
        public static bool CanAudio(string path) =>
            KindOf(path) == Kind.Video && Tools.Ffmpeg != null && Tools.Ffprobe != null;

        // Resize is a window action for a video: re-encodes at a lower resolution or frame rate,
        // needs ffmpeg and ffprobe (the window reads the size and fps before offering anything).
        public static bool CanResize(string path) =>
            KindOf(path) == Kind.Video && Tools.Ffmpeg != null && Tools.Ffprobe != null;

        // Rotate is a window action: the crop images (same-format output), or a video with ffmpeg
        // (ffprobe is optional; without it the video re-encodes instead of a display matrix copy).
        public static bool CanRotate(string path) =>
            CanCrop(path) || (KindOf(path) == Kind.Video && Tools.Ffmpeg != null);

        // Clean is an instant action: byte-level for most images (heic, heif and avif are not offered),
        // an ffmpeg stream copy for a video.
        public static bool CanClean(string path) => Norm(path) switch
        {
            "jpg" or "png" or "webp" or "gif" or "tiff" or "bmp" or "ico" => true,
            _ => KindOf(path) == Kind.Video && Tools.Ffmpeg != null,
        };

        // Merge takes PDFs and the images ImageBackend decodes without extra codecs (the crop set;
        // heic, heif and avif are left out to be safe).
        public static bool CanMergeItem(string path) => Norm(path) == "pdf" || CanCrop(path);

        // Merge is a multi-file action: 2+ files, every one a PDF or image.
        public static bool CanMerge(IReadOnlyList<string> paths) => paths.Count >= 2 && paths.All(CanMergeItem);

        // Split is an action for a PDF; Ui.SlicesFor also hides it for a one-page PDF.
        public static bool CanSplit(string path) => Norm(path) == "pdf";

        // Pages (pick, reorder, rotate) is a window action for any PDF, one page included.
        public static bool CanPages(string path) => Norm(path) == "pdf";

        // Compress is built in (PDFsharp), so any PDF qualifies with no external tool.
        public static bool CanCompress(string path) => Norm(path) == "pdf";

        // Parts (keep only some objects) is a window action for any 3D model, built in. The object
        // count is only known once the window reads the file.
        public static bool CanParts(string path) => KindOf(path) == Kind.Model;

        // Cut (split a model with a box, fill the cut faces) is a window action for any 3D model,
        // built in. Whether the cut faces close is only known once the cut runs.
        public static bool CanCut(string path) => KindOf(path) == Kind.Model;

        public static IEnumerable<string> AllSourceExtensions =>
            Table.SelectMany(e => e.Sources).Distinct(StringComparer.Ordinal);
    }
}
