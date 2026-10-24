using System.IO;

namespace Typeless
{
    public static class Naming
    {
        // report.pdf + docx -> report_docx.docx; collisions get " (2)", " (3)", ...;
        // multi-page outputs pass page and get report_png_1.png, report_png_2.png, ...
        public static string Final(string src, string target, int? page = null)
        {
            string stem = Path.GetFileNameWithoutExtension(src);
            string name = $"{stem}_{target}" + (page is int p ? $"_{p}" : "");
            return Unique(src, name, target);
        }

        // a.JPEG + crop -> a_crop.JPEG (raw extension kept); collisions get " (2)", " (3)", ...
        public static string WithSuffix(string src, string suffix)
        {
            string stem = Path.GetFileNameWithoutExtension(src);
            string ext = Path.GetExtension(src).TrimStart('.');
            return Unique(src, $"{stem}_{suffix}", ext);
        }

        // a.pdf + split + 2 -> a_split_2.pdf (raw extension kept)
        public static string WithSuffix(string src, string suffix, int page)
        {
            return WithSuffix(src, $"{suffix}_{page}");
        }

        // scan.png + merged + pdf -> scan_merged.pdf (extension forced)
        public static string WithSuffix(string src, string suffix, string ext)
        {
            return Unique(src, $"{Path.GetFileNameWithoutExtension(src)}_{suffix}", ext);
        }

        private static string Unique(string src, string name, string ext)
        {
            return UniqueIn(Path.GetDirectoryName(Path.GetFullPath(src)) ?? ".", name, ext);
        }

        // dir + name + ext -> dir\name.ext, or dir\name (2).ext, ... when taken.
        public static string UniqueIn(string dir, string name, string ext)
        {
            string dot = ext.Length > 0 ? "." + ext : "";
            string candidate = Path.Combine(dir, $"{name}{dot}");
            for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
                candidate = Path.Combine(dir, $"{name} ({n}){dot}");
            return candidate;
        }
    }
}
