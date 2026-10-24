using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Typeless
{
    public static class Ui
    {
        // Shows the radial menu for one or more files. Returns the process exit code.
        public static int Run(string[] files)
        {
            List<string> paths = files
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => Path.GetFullPath(f.Trim().Trim('"')))
                .ToList();
            if (paths.Count == 0) return 2;

            using SingleInstance? single = SingleInstance.Acquire();
            if (single == null)
            {
                SingleInstance.Send(paths);
                return 0;
            }

            RadialWindow? window = null;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            single.StartServer(p => dispatcher.BeginInvoke(new Action(() => window?.AddFile(p))));

            IReadOnlyList<string> targets = Formats.TargetsFor(paths[0]);
            if (targets.Count == 0)
            {
                Log.Write($"No targets available for {paths[0]}");
                return 2;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var radial = new RadialWindow(paths[0], SlicesFor(paths));
            window = radial;
            foreach (string p in paths.Skip(1)) radial.AddFile(p, live: false);
            radial.Picked += single.Release;
            radial.Closed += (_, _) => single.Release();

            // Action handoff: the crop, trim, pages, compress, rotate or resize window becomes MainWindow before
            // the radial closes, so the app keeps running.
            CropWindow? crop = null;
            TrimWindow? trim = null;
            PagesWindow? pages = null;
            CompressWindow? compress = null;
            RotateWindow? rotate = null;
            ResizeWindow? resize = null;
            radial.ActionRequested += (action, path) =>
            {
                Window next;
                if (action == "trim") next = trim = new TrimWindow(path);
                else if (action == "pages") next = pages = new PagesWindow(path);
                else if (action == "compress") next = compress = new CompressWindow(path);
                else if (action == "rotate") next = rotate = new RotateWindow(path);
                else if (action == "resize") next = resize = new ResizeWindow(path);
                else next = crop = new CropWindow(path);
                app.MainWindow = next;
                next.Show();
                radial.CloseForHandoff();
            };

            app.Run(radial);
            return crop?.ExitCode ?? trim?.ExitCode ?? pages?.ExitCode ?? compress?.ExitCode ?? rotate?.ExitCode
                ?? resize?.ExitCode ?? radial.ExitCode;
        }

        // Radial order per kind: these first, in this order, then the rest in their existing order.
        // The radial pages past 9 slices (8 plus MORE), so the first 8 here are page 1.
        internal static IReadOnlyList<string> Priority(Kind kind) => kind switch
        {
            Kind.Image => new[] { "png", "jpg", "webp", "pdf", "crop", "rotate", "clean", "gif" },
            Kind.Video => new[] { "mp4", "mkv", "mov", "mp3", "trim", "resize", "audio", "rotate" },
            _ => Array.Empty<string>(),
        };

        // Radial slices: the conversion targets of the first file, then the actions, ordered by Priority.
        // A single file gets "crop" (croppable image), "trim" (audio or video) plus "resize"
        // (Formats.CanResize) and "audio" (video with an audio track), or for a PDF "pages",
        // "split" (2+ pages) and "compress", plus "rotate" (Formats.CanRotate) and "clean"
        // (Formats.CanClean) for an image or video.
        // Two or more files get "merge" when every file is a PDF or image.
        // No cap: the radial pages anything past 9.
        internal static IReadOnlyList<string> SlicesFor(IReadOnlyList<string> paths)
        {
            string path = paths[0];
            IReadOnlyList<string> targets = Formats.TargetsFor(path);
            var actions = new List<string>();
            if (paths.Count != 1)
            {
                if (Formats.CanMerge(paths)) actions.Add("merge");
            }
            else if (Formats.CanCrop(path)) actions.Add("crop");
            else if (Formats.CanTrim(path))
            {
                actions.Add("trim");
                if (Formats.CanResize(path)) actions.Add("resize");
                if (Formats.CanAudio(path) && HasAudio(path)) actions.Add("audio");
            }
            else
            {
                if (Formats.CanPages(path)) actions.Add("pages");
                if (Formats.CanSplit(path) && !IsSinglePage(path)) actions.Add("split");
                if (Formats.CanCompress(path)) actions.Add("compress");
            }
            if (paths.Count == 1 && Formats.CanRotate(path)) actions.Add("rotate");
            if (paths.Count == 1 && Formats.CanClean(path)) actions.Add("clean");
            Kind? kind = Formats.KindOf(path);
            List<string> order = kind != null ? Priority(kind.Value).ToList() : new List<string>();
            // OrderBy is stable, so unlisted items keep their relative order after the listed ones.
            return targets.Concat(actions)
                .OrderBy(s => order.Contains(s) ? order.IndexOf(s) : int.MaxValue)
                .ToList();
        }

        // Hides "audio" only when ffprobe says there is no audio stream. A failed or timed out probe
        // keeps the slice; the error surfaces on click.
        private static bool HasAudio(string video)
        {
            try
            {
                return MediaBackend.AudioCodec(video) != null;
            }
            catch (Exception ex)
            {
                Log.Write($"Audio probe failed for {video}: {ex.Message}");
                return true;
            }
        }

        private const long PageCountMaxBytes = 20L * 1024 * 1024;

        // Unreadable page count: keep split, the error surfaces on click. Over 20 MB the check is
        // skipped (assume 2+ pages): PDFsharp parsing a big scan would delay the menu.
        private static bool IsSinglePage(string pdf)
        {
            try
            {
                if (new FileInfo(pdf).Length > PageCountMaxBytes) return false;
                return PdfEdit.PageCount(pdf) == 1;
            }
            catch (Exception ex)
            {
                Log.Write($"Page count failed for {pdf}: {ex.Message}");
                return false;
            }
        }
    }
}
