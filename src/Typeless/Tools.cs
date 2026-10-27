using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Typeless
{
    // Discovers external backends (lazily, cached) and runs them.
    public static class Tools
    {
        private static readonly Lazy<string?> FfmpegPath = new Lazy<string?>(() => FindFfmpegTool("ffmpeg.exe"));
        private static readonly Lazy<string?> FfprobePath = new Lazy<string?>(FindFfprobe);
        private static readonly Lazy<string?> SofficePath = new Lazy<string?>(FindSoffice);
        private static readonly Lazy<bool> WordInstalled = new Lazy<bool>(FindWord);
        private static readonly Lazy<bool> PowerPointInstalled = new Lazy<bool>(FindPowerPoint);

        public static string? Ffmpeg => FfmpegPath.Value;
        public static string? Ffprobe => FfprobePath.Value;
        public static string? Soffice => SofficePath.Value;
        public static bool HasWord => WordInstalled.Value;
        public static bool HasPowerPoint => PowerPointInstalled.Value;

        public static bool IsAvailable(Kind kind, string target)
        {
            switch (kind)
            {
                case Kind.Image:
                    return target != "webp" || Ffmpeg != null;
                case Kind.Pdf:
                    return target == "png" || target == "jpg" || target == "pptx" || HasWord || Soffice != null;
                case Kind.Document:
                    return HasWord || Soffice != null;
                case Kind.Sheet:
                    return Soffice != null;
                case Kind.Slides:
                    return target == "png" || target == "jpg" ? Soffice != null : HasPowerPoint || Soffice != null;
                case Kind.Audio:
                case Kind.Video:
                    return Ffmpeg != null;
                case Kind.Model:
                    return true;
                default:
                    return false;
            }
        }

        // Demuxers for every audio, video and image source Typeless takes. ffmpeg picks a demuxer by content,
        // and some (dash, ffconcat, hls) open other files that a crafted "video" names, so a download could
        // make ffmpeg read files elsewhere on disk or reach a \\host share. Inputs are limited to these.
        private const string Demuxers =
            "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,asf,flv,mp3,wav,flac,aac,ogg,mpegts," +
            "image2,png_pipe,jpeg_pipe,webp_pipe,bmp_pipe,tiff_pipe,gif,ico";

        // ffprobe takes input options anywhere; ffmpeg needs them before each -i.
        private static IEnumerable<string> Harden(string exe, IEnumerable<string> args)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            bool probe = name.Equals("ffprobe", StringComparison.OrdinalIgnoreCase);
            bool ffmpeg = name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase);
            var guard = new[] { "-format_whitelist", Demuxers, "-protocol_whitelist", "file" };
            if (probe) foreach (string g in guard) yield return g;
            foreach (string a in args)
            {
                if (ffmpeg && a == "-i") foreach (string g in guard) yield return g;
                yield return a;
            }
        }

        // Runs exe to completion and returns its stdout. Throws on timeout or nonzero exit.
        public static string RunProcess(string exe, IEnumerable<string> args, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // Not the clicked file's folder, which may be an untrusted download folder.
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (string a in Harden(exe, args)) psi.ArgumentList.Add(a);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

            p.Start();
            ChildJob.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            int ms = timeout.HasValue ? (int)Math.Min(int.MaxValue, timeout.Value.TotalMilliseconds) : -1;
            if (!p.WaitForExit(ms))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"{Path.GetFileName(exe)} timed out after {timeout}.");
            }
            p.WaitForExit(); // flush async output handlers

            if (p.ExitCode != 0)
            {
                string tail;
                lock (stderr)
                {
                    tail = string.Join(Environment.NewLine, stderr.ToString()
                        .Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .TakeLast(5));
                }
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exe)} failed with exit code {p.ExitCode}." +
                    (tail.Length > 0 ? Environment.NewLine + tail : ""));
            }

            lock (stdout) return stdout.ToString();
        }

        // Like RunProcess, but hands every stdout and stderr line to onLine as it arrives (on a pool thread)
        // and returns stdout. Cancelling kills the process tree and throws OperationCanceledException.
        public static string RunProcessStreaming(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // Not the clicked file's folder, which may be an untrusted download folder.
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (string a in Harden(exe, args)) psi.ArgumentList.Add(a);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                try { onLine?.Invoke(e.Data); } catch { }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderr) stderr.AppendLine(e.Data);
                try { onLine?.Invoke(e.Data); } catch { }
            };

            p.Start();
            ChildJob.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
                p.WaitForExit(); // also flushes async output handlers
            ct.ThrowIfCancellationRequested();

            if (p.ExitCode != 0)
            {
                string tail;
                lock (stderr)
                {
                    tail = string.Join(Environment.NewLine, stderr.ToString()
                        .Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .TakeLast(5));
                }
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exe)} failed with exit code {p.ExitCode}." +
                    (tail.Length > 0 ? Environment.NewLine + tail : ""));
            }

            lock (stdout) return stdout.ToString();
        }

        // exeName is ffmpeg.exe or ffprobe.exe; both ship in the same FFmpeg bin folder.
        private static string? FindFfmpegTool(string exeName)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            return FirstExisting(Path.Combine(AppContext.BaseDirectory, "tools", exeName))
                ?? FindOnPath(exeName)
                ?? FirstExisting(Path.Combine(local, "Microsoft", "WinGet", "Links", exeName))
                ?? FindInWinGetPackages(Path.Combine(local, "Microsoft", "WinGet", "Packages"), exeName)
                ?? FirstExisting(Path.Combine(@"C:\ffmpeg\bin", exeName))
                ?? FirstExisting(Path.Combine(programFiles, "ffmpeg", "bin", exeName));
        }

        // Next to the resolved ffmpeg first (following a WinGet Links symlink), then the same discovery.
        private static string? FindFfprobe()
        {
            string? ffmpeg = Ffmpeg;
            if (ffmpeg != null)
            {
                string? hit = FirstExisting(SafeCombine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe"));
                if (hit != null) return hit;
                try
                {
                    FileSystemInfo? target = new FileInfo(ffmpeg).ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        hit = FirstExisting(SafeCombine(Path.GetDirectoryName(target.FullName) ?? "", "ffprobe.exe"));
                        if (hit != null) return hit;
                    }
                }
                catch { }
            }
            return FindFfmpegTool("ffprobe.exe");
        }

        private static string? FindInWinGetPackages(string packagesDir, string exeName)
        {
            try
            {
                if (!Directory.Exists(packagesDir)) return null;
                foreach (string pkg in Directory.EnumerateDirectories(packagesDir, "*FFmpeg*"))
                {
                    foreach (string sub in SafeDirs(pkg))
                    {
                        string? hit = FirstExisting(Path.Combine(sub, "bin", exeName));
                        if (hit != null) return hit;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"WinGet {exeName} scan failed: " + ex.Message);
            }
            return null;
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.EnumerateDirectories(dir).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        private static string? FindSoffice()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return FirstExisting(Path.Combine(pf, "LibreOffice", "program", "soffice.exe"))
                ?? (pf86.Length > 0 ? FirstExisting(Path.Combine(pf86, "LibreOffice", "program", "soffice.exe")) : null)
                ?? FindOnPath("soffice.exe");
        }

        private static bool FindWord()
        {
            try { return Type.GetTypeFromProgID("Word.Application") != null; }
            catch { return false; }
        }

        // TYPELESS_NO_POWERPOINT=1 forces the LibreOffice fallback (testing).
        private static bool FindPowerPoint()
        {
            if (Environment.GetEnvironmentVariable("TYPELESS_NO_POWERPOINT") == "1") return false;
            try { return Type.GetTypeFromProgID("PowerPoint.Application") != null; }
            catch { return false; }
        }

        // Builds PATH fresh from the registry (Machine + User) plus the process PATH,
        // so tools installed after Explorer started are still found.
        private static string? FindOnPath(string exeName)
        {
            string combined = string.Join(";",
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "",
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "",
                Environment.GetEnvironmentVariable("PATH") ?? "");

            foreach (string raw in combined.Split(';'))
            {
                string dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                // A relative entry such as "." would resolve against the clicked file's folder.
                if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;
                string? hit = FirstExisting(SafeCombine(dir, exeName));
                if (hit != null) return hit;
            }
            return null;
        }

        private static string? SafeCombine(string dir, string file)
        {
            try { return Path.Combine(dir, file); }
            catch { return null; }
        }

        private static string? FirstExisting(string? path)
        {
            try { return path != null && File.Exists(path) ? path : null; }
            catch { return null; }
        }
    }
}
