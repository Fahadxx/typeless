using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typeless
{
    public static class OfficeBackend
    {
        private static readonly TimeSpan SofficeTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan WordTimeout = TimeSpan.FromSeconds(90);

        // Handles Kind.Document, Kind.Sheet and Kind.Slides. Writes output file(s) into work
        // and returns them in page order.
        public static List<string> Convert(Kind kind, string src, string target, string work)
        {
            if (target == "png" || target == "jpg")
            {
                if (kind == Kind.Sheet) throw new NotSupportedException($"Spreadsheet target not supported: {target}");
                string pdf = kind == Kind.Document ? ConvertDocument(src, "pdf", work) : LibreOffice(kind, src, "pdf", work);
                return PdfBackend.RenderPages(pdf, target, work);
            }

            string output = kind switch
            {
                Kind.Document => ConvertDocument(src, target, work),
                Kind.Slides => ConvertSlides(src, target, work),
                _ => LibreOffice(kind, src, target, work),
            };
            return new List<string> { output };
        }

        // Converts slides with PowerPoint when available, falling back to LibreOffice.
        private static string ConvertSlides(string src, string target, string work)
        {
            if (Tools.HasPowerPoint)
            {
                try
                {
                    return PowerPoint(src, target, work);
                }
                catch (Exception ex) when (Tools.Soffice != null)
                {
                    Log.Write($"PowerPoint conversion failed for {src} -> {target}, using LibreOffice: {ex}");
                }
            }
            return LibreOffice(Kind.Slides, src, target, work);
        }

        // Converts a document (or a pdf source) with Word when available, falling back to LibreOffice.
        // Returns the single output file inside work.
        public static string ConvertDocument(string src, string target, string work)
        {
            if (Tools.HasWord)
            {
                try
                {
                    return Word(src, target, work);
                }
                catch (Exception ex) when (Tools.Soffice != null)
                {
                    Log.Write($"Word conversion failed for {src} -> {target}, using LibreOffice: {ex}");
                }
            }
            return LibreOffice(Kind.Document, src, target, work);
        }

        // Pids of the WINWORD instance started by a Word stage, shared with the timeout watcher.
        private sealed class WordRun
        {
            public volatile int[]? Ours;
        }

        // Runs the Word COM work on its own STA thread with a hard deadline. On timeout, kills the
        // Word we started and any PDF reflow helper started during this stage, then throws so the
        // caller falls back to LibreOffice.
        private static string Word(string src, string target, string work)
        {
            DateTime stageStart = DateTime.Now;
            var run = new WordRun();
            string? result = null;
            ExceptionDispatchInfo? error = null;

            var thread = new Thread(() =>
            {
                try { result = WordCore(src, target, work, run); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            })
            { IsBackground = true, Name = "WordConversion" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!thread.Join(WordTimeout))
            {
                Log.Write($"Word timed out after {WordTimeout.TotalSeconds:0}s for {src} -> {target}; killing processes.");
                int[]? ours = run.Ours;
                if (ours != null)
                {
                    foreach (int pid in ours) KillPid(pid);
                }
                KillStartedSince("PDFREFLOW", stageStart);
                throw new TimeoutException("Word timed out");
            }

            error?.Throw();
            return result ?? throw new InvalidOperationException("Word produced no output.");
        }

        private static void KillPid(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch { }
        }

        private static void KillStartedSince(string processName, DateTime since)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(processName); } catch { return; }
            foreach (Process p in procs)
            {
                try
                {
                    if (p.StartTime >= since.AddSeconds(-1)) p.Kill();
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private static string WordCore(string src, string target, string work, WordRun run)
        {
            string output = Path.Combine(work, "out." + target);
            Type type = Type.GetTypeFromProgID("Word.Application")
                ?? throw new InvalidOperationException("Microsoft Word is not installed.");

            HashSet<int> before = WordPids();
            dynamic word = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not start Microsoft Word.");
            int[] ours = WordPids().Except(before).ToArray();
            run.Ours = ours;
            bool owned = ours.Length > 0;

            dynamic? doc = null;
            object? oldAlerts = null;
            object? oldSecurity = null;
            try
            {
                if (owned) word.Visible = false;
                try { oldAlerts = word.DisplayAlerts; } catch { }
                word.DisplayAlerts = 0;
                try { oldSecurity = word.AutomationSecurity; word.AutomationSecurity = 3; } catch { }

                using (new WordDialogWatchdog())
                {
                    doc = word.Documents.Open(FileName: src, ConfirmConversions: false, ReadOnly: true,
                        AddToRecentFiles: false, Visible: false, NoEncodingDialog: true);
                }

                switch (target)
                {
                    case "pdf":
                        doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17);
                        break;
                    case "docx":
                        doc.SaveAs2(FileName: output, FileFormat: 16, AddToRecentFiles: false);
                        break;
                    case "rtf":
                        doc.SaveAs2(FileName: output, FileFormat: 6, AddToRecentFiles: false);
                        break;
                    case "odt":
                        doc.SaveAs2(FileName: output, FileFormat: 23, AddToRecentFiles: false);
                        break;
                    case "html":
                        doc.SaveAs2(FileName: output, FileFormat: 10, AddToRecentFiles: false);
                        break;
                    case "txt":
                        doc.SaveAs2(FileName: output, FileFormat: 7, Encoding: 65001, AddToRecentFiles: false);
                        break;
                    default:
                        throw new NotSupportedException($"Word target not supported: {target}");
                }
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(SaveChanges: 0); } catch { }
                    try { Marshal.FinalReleaseComObject(doc); } catch { }
                }
                if (owned)
                {
                    try { word.Quit(SaveChanges: 0); } catch { }
                }
                else
                {
                    // Shared with the user's own Word: put their settings back.
                    try { if (oldAlerts != null) word.DisplayAlerts = oldAlerts; } catch { }
                    try { if (oldSecurity != null) word.AutomationSecurity = oldSecurity; } catch { }
                }
                try { Marshal.FinalReleaseComObject(word); } catch { }
                if (owned) EnsureExited(ours);
            }

            if (!File.Exists(output)) throw new InvalidOperationException("Word produced no output.");
            return output;
        }

        private static HashSet<int> WordPids()
        {
            var pids = new HashSet<int>();
            foreach (Process p in Process.GetProcessesByName("WINWORD"))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
            return pids;
        }

        // Never leave a hidden Word we started running.
        private static void EnsureExited(int[] pids)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using Process p = Process.GetProcessById(pid);
                    if (!p.WaitForExit(10000)) p.Kill();
                }
                catch { }
            }
        }

        // POWERPNT pids seen before the stage and the one it started, shared with the timeout watcher.
        private sealed class PowerPointRun
        {
            public volatile HashSet<int>? Before;
            public volatile int[]? Ours;
        }

        // Same shape as Word(): STA thread with a hard deadline. PowerPoint is a single-instance
        // COM server, so on timeout only a POWERPNT this stage started is killed, never the user's.
        private static string PowerPoint(string src, string target, string work)
        {
            var run = new PowerPointRun();
            string? result = null;
            ExceptionDispatchInfo? error = null;

            var thread = new Thread(() =>
            {
                try { result = PowerPointCore(src, target, work, run); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            })
            { IsBackground = true, Name = "PowerPointConversion" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!thread.Join(WordTimeout))
            {
                Log.Write($"PowerPoint timed out after {WordTimeout.TotalSeconds:0}s for {src} -> {target}; killing processes.");
                int[]? ours = run.Ours;
                HashSet<int>? before = run.Before;
                // Hung while starting: with no PowerPoint running before, any new POWERPNT is ours.
                if (ours == null && before != null && before.Count == 0)
                    ours = PowerPointPids().ToArray();
                if (ours != null)
                {
                    foreach (int pid in ours) KillPid(pid);
                }
                throw new TimeoutException("PowerPoint timed out");
            }

            error?.Throw();
            string output = result ?? throw new InvalidOperationException("PowerPoint produced no output.");
            Log.Write($"PowerPoint: {src} -> {target}");
            return output;
        }

        private static string PowerPointCore(string src, string target, string work, PowerPointRun run)
        {
            string output = Path.Combine(work, "out." + target);
            int format = target switch
            {
                "pdf" => 32,  // ppSaveAsPDF
                "pptx" => 24, // ppSaveAsOpenXMLPresentation
                "odp" => 35,  // ppSaveAsOpenDocumentPresentation
                _ => throw new NotSupportedException($"PowerPoint target not supported: {target}"),
            };
            Type type = Type.GetTypeFromProgID("PowerPoint.Application")
                ?? throw new InvalidOperationException("Microsoft PowerPoint is not installed.");

            // Creating PowerPoint.Application attaches to a running PowerPoint if there is one.
            HashSet<int> before = PowerPointPids();
            run.Before = before;
            dynamic app = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not start Microsoft PowerPoint.");
            int[] ours = before.Count == 0 ? PowerPointPids().ToArray() : Array.Empty<int>();
            run.Ours = ours;
            bool owned = before.Count == 0;

            dynamic? pres = null;
            object? oldAlerts = null;
            try
            {
                // Setting Visible = false throws in PowerPoint; WithWindow: msoFalse keeps it hidden.
                try { oldAlerts = app.DisplayAlerts; app.DisplayAlerts = 1; } catch { }

                // FileName, ReadOnly: msoTrue, Untitled: msoTrue, WithWindow: msoFalse.
                pres = app.Presentations.Open(src, -1, -1, 0);
                pres.SaveAs(output, format);
            }
            finally
            {
                if (pres != null)
                {
                    try { pres.Close(); } catch { }
                    try { Marshal.FinalReleaseComObject(pres); } catch { }
                }
                if (owned)
                {
                    try { app.Quit(); } catch { }
                }
                else
                {
                    // Shared with the user's own PowerPoint: put their setting back.
                    try { if (oldAlerts != null) app.DisplayAlerts = oldAlerts; } catch { }
                }
                try { Marshal.FinalReleaseComObject(app); } catch { }
                if (owned) EnsureExited(ours);
            }

            if (!File.Exists(output)) throw new InvalidOperationException("PowerPoint produced no output.");
            return output;
        }

        private static HashSet<int> PowerPointPids()
        {
            var pids = new HashSet<int>();
            foreach (Process p in Process.GetProcessesByName("POWERPNT"))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
            return pids;
        }

        private static string LibreOffice(Kind kind, string src, string target, string work)
        {
            string soffice = Tools.Soffice
                ?? throw new NotSupportedException("This conversion requires LibreOffice, which was not found.");

            bool pdfSource = Formats.Norm(src) == "pdf";
            string spec = target switch
            {
                "pdf" => kind switch
                {
                    Kind.Sheet => "pdf:calc_pdf_Export",
                    Kind.Slides => "pdf:impress_pdf_Export",
                    _ => "pdf:writer_pdf_Export",
                },
                "docx" => "docx:MS Word 2007 XML",
                "odt" => "odt",
                "rtf" => "rtf",
                "txt" => "txt:Text (encoded):UTF8",
                "html" => "html",
                "xlsx" => "xlsx:Calc MS Excel 2007 XML",
                "ods" => "ods",
                "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1",
                "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
                "odp" => "odp",
                _ => throw new NotSupportedException($"LibreOffice target not supported: {target}"),
            };

            string profile = Path.Combine(Log.AppDataDir, "lo-profile");
            Directory.CreateDirectory(profile);
            string outDir = Path.Combine(work, "lo");
            Directory.CreateDirectory(outDir);

            var args = new List<string>
            {
                "-env:UserInstallation=" + new Uri(profile).AbsoluteUri,
                "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
            };
            if (pdfSource) args.Add("--infilter=writer_pdf_import");
            args.AddRange(new[] { "--convert-to", spec, "--outdir", outDir, src });

            string stdout = Tools.RunProcess(soffice, args, SofficeTimeout);

            string stem = Path.GetFileNameWithoutExtension(src);
            List<string> hits = Directory.EnumerateFiles(outDir)
                .Where(f => string.Equals(Path.GetExtension(f).TrimStart('.'), target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            string? output = hits.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
                ?? hits.FirstOrDefault();

            if (output == null)
                throw new InvalidOperationException(
                    $"LibreOffice produced no {target} output." +
                    (stdout.Trim().Length > 0 ? Environment.NewLine + stdout.Trim() : ""));
            return output;
        }
    }
}
