using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Typeless
{
    public static class MediaBackend
    {
        private const string GifFilter =
            "fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan AudioProbeTimeout = TimeSpan.FromSeconds(5);

        // Resize quality, tuned by measurement: crf 16 (ReencodeAttempts) on a low-bitrate source can
        // come out bigger than the original, so resize uses its own, lower quality per codec.
        private const string ResizeX264Crf = "20";
        private const string ResizeX264Preset = "medium";
        private const string ResizeVp9Crf = "30";
        private const string ResizeWmvQ = "2";

        // Resolution tiers (displayed short side) and frame rates a resize can offer, largest first.
        private static readonly int[] ResizeHeights = { 2160, 1440, 1080, 720, 480, 360, 240 };
        private static readonly int[] ResizeRates = { 60, 50, 30, 25, 24, 15 };

        // NTSC base rates (n * 1000/1001); a source within 0.1 fps of one is treated as NTSC.
        private static readonly int[] NtscBases = { 24, 30, 48, 60, 120 };

        // FrameRate is 0 for audio or when ffprobe reports none.
        public sealed record ProbeInfo(double Duration, IReadOnlyList<double> Keyframes, bool HasVideo, double FrameRate);

        // A video's displayed size (display matrix rotation applied), its frame rate (0 when unknown),
        // and the smaller heights and frame rates a resize may pick. FpsOptions are whole-number labels
        // ("30"); Resize maps a label to the ffmpeg rate (30000/1001 on NTSC sources).
        public sealed record ResizeInfo(int Width, int Height, double Fps, bool Ntsc,
            IReadOnlyList<int> HeightOptions, IReadOnlyList<string> FpsOptions)
        {
            public int ShortSide => Math.Min(Width, Height);
        }

        // One ffmpeg try: a log label plus the args between the input and the output.
        private sealed record Attempt(string Label, string[] Args);

        // Handles Kind.Audio and Kind.Video via Tools.Ffmpeg. Writes output file(s) into work
        // and returns them.
        public static List<string> Convert(Kind kind, string src, string target, string work)
        {
            string ffmpeg = Tools.Ffmpeg
                ?? throw new InvalidOperationException(
                    "ffmpeg not found. Install it (for example: winget install Gyan.FFmpeg) and try again.");

            string output = Path.Combine(work, "out." + target);

            // Video container switch: lossless stream copy first, visually lossless re-encode last.
            if (kind == Kind.Video && (target is "mp4" or "mkv" or "mov" or "avi" or "webm"))
            {
                var attempts = new List<Attempt>
                {
                    new Attempt("video copy", new[] { "-map", "0", "-c", "copy" }),
                    new Attempt("video copy (v+a)", new[] { "-map", "0:v", "-map", "0:a?", "-c", "copy" }),
                };
                attempts.AddRange(ReencodeAttempts(target, "video reencode"));
                RunAttempts(ffmpeg, new[] { "-i", src }, attempts, Faststart(target), output, $"{src} -> {target}");
                return new List<string> { output };
            }

            string[] encode = target switch
            {
                "mp3" => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
                "wav" => new[] { "-vn", "-c:a", "pcm_s16le" },
                "flac" => new[] { "-vn", "-c:a", "flac" },
                "m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
                "ogg" => new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
                "opus" => new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" },
                "gif" => new[] { "-an", "-vf", GifFilter, "-loop", "0" },
                _ => throw new NotSupportedException($"Cannot convert {kind} to {target}."),
            };

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", src };
            args.AddRange(encode);
            args.Add(output);

            Tools.RunProcess(ffmpeg, args); // no timeout: videos can be long
            return new List<string> { output };
        }

        // Duration in seconds, sorted video keyframe times (empty for audio) and the video frame rate.
        public static ProbeInfo Probe(string src)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            string ffprobe = RequireFfprobe();

            double duration = Duration(ffprobe, src);
            bool hasVideo = Formats.KindOf(src) == Kind.Video;
            var keys = new List<double>();
            double fps = 0;
            if (hasVideo)
            {
                fps = ParseRate(Tools.RunProcess(ffprobe, new[]
                {
                    "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", src,
                }, ProbeTimeout));
                string raw = Tools.RunProcess(ffprobe, new[]
                {
                    "-v", "error", "-skip_frame", "nokey", "-select_streams", "v:0",
                    "-show_entries", "frame=pts_time", "-of", "csv=p=0", src,
                }, ProbeTimeout);
                foreach (string line in raw.Split('\n'))
                    if (TryParseSeconds(line, out double t)) keys.Add(t);
            }
            return new ProbeInfo(duration, keys.Distinct().OrderBy(t => t).ToList(), hasVideo, fps);
        }

        // ffprobe rate "30000/1001" or "25" as frames per second; 0 when unknown ("0/0", "N/A").
        private static double ParseRate(string raw)
        {
            string field = raw.Trim().Split('\n')[0].Trim().Split(',')[0].Trim();
            string[] parts = field.Split('/');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return 0;
            double den = 1;
            if (parts.Length > 1 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out den)) return 0;
            double fps = den > 0 ? num / den : 0;
            return double.IsFinite(fps) && fps > 0 && fps < 1000 ? fps : 0;
        }

        // Cuts [start, end] (seconds) into <stem>_trim.<ext> next to src, same container.
        // Copy mode starts on the keyframe at or before start (callers snap for video);
        // exact mode re-encodes video. Audio is always copied, which already cuts exactly.
        public static string Trim(string src, double start, double end, bool exact, CancellationToken ct = default)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanTrim(src))
                throw new NotSupportedException($"Cannot trim {Path.GetFileName(src)} (unsupported type or missing ffmpeg/ffprobe).");
            string ffmpeg = Tools.Ffmpeg!;

            double duration = Duration(RequireFfprobe(), src);
            start = Math.Clamp(start, 0, duration);
            end = Math.Clamp(end, 0, duration);
            if (!(end - start >= 0.05)) throw new ArgumentException("End must be after start.");

            string container = Formats.Norm(src) == "m4v" ? "mp4" : Formats.Norm(src);
            List<Attempt> attempts = exact && Formats.KindOf(src) == Kind.Video
                ? ReencodeAttempts(container, "trim reencode")
                : new List<Attempt>
                {
                    new Attempt("trim copy", new[] { "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero" }),
                    new Attempt("trim copy (v+a)", new[]
                    {
                        "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero",
                    }),
                };

            string work = NewWorkDir();
            try
            {
                // Keep the raw source extension so ffmpeg picks the same muxer.
                string output = Path.Combine(work, "out" + Path.GetExtension(src));
                string from = Secs(start), to = Secs(end);
                RunAttempts(ffmpeg, new[] { "-ss", from, "-to", to, "-i", src }, attempts,
                    Faststart(container), output, $"{src} {from}-{to}", ct: ct);

                string dest = Naming.WithSuffix(src, "trim");
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // Codec name of the first audio stream ("aac", "opus", ...), or null when there is none.
        // Header only, so it is quick; the short timeout keeps the radial from waiting on it.
        public static string? AudioCodec(string src)
        {
            string raw = Tools.RunProcess(RequireFfprobe(), new[]
            {
                "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", src,
            }, AudioProbeTimeout);
            string codec = raw.Trim().Split('\n')[0].Trim().Split(',')[0].Trim();
            return codec.Length > 0 ? codec : null;
        }

        // Saves the first audio stream of a video as is (stream copy) into <stem>_audio.<ext>,
        // the container picked from the codec.
        public static string ExtractAudio(string src)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanAudio(src))
                throw new NotSupportedException($"Cannot extract audio from {Path.GetFileName(src)} (not a video or missing ffmpeg/ffprobe).");
            string ffmpeg = Tools.Ffmpeg!;

            string codec = AudioCodec(src) ?? throw new InvalidOperationException("No audio track");
            string ext = AudioContainer(codec);

            string work = NewWorkDir();
            try
            {
                string output = Path.Combine(work, "out." + ext);
                var attempts = new List<Attempt> { new Attempt("audio copy", new[] { "-vn", "-map", "0:a:0", "-c", "copy" }) };
                string[] tail = ext == "m4a" ? new[] { "-movflags", "+faststart" } : Array.Empty<string>();
                RunAttempts(ffmpeg, new[] { "-i", src }, attempts, tail, output, $"{src} ({codec}) -> {ext}");

                string dest = Naming.WithSuffix(src, "audio", ext);
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // Rotates/flips a video (RotateSpec convention: mirror first, then turn clockwise) into
        // <stem>_rotated.<ext>, same container. mp4/mov/m4v first try a lossless stream copy that only
        // rewrites the display matrix; other containers, a failed copy, or an unreadable current
        // rotation re-encode with hflip/transpose at ReencodeAttempts quality.
        public static string Transform(string src, int rotate, bool flipH, bool flipV, CancellationToken ct = default)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (Formats.KindOf(src) != Kind.Video || !Formats.CanRotate(src))
                throw new NotSupportedException($"Cannot rotate {Path.GetFileName(src)} (not a video or missing ffmpeg).");
            RotateSpec spec = RotateSpec.Normalize(rotate, flipH, flipV);
            if (spec.IsIdentity) throw new ArgumentException("Nothing to rotate");
            string ffmpeg = Tools.Ffmpeg!;

            string container = Formats.Norm(src) == "m4v" ? "mp4" : Formats.Norm(src);
            string work = NewWorkDir();
            try
            {
                // Keep the raw source extension so ffmpeg picks the same muxer.
                string output = Path.Combine(work, "out" + Path.GetExtension(src));
                string what = $"{src} {spec.Degrees}{(spec.HFlip ? " hflip" : "")}";
                bool copied = false;

                if (container is "mp4" or "mov" && CurrentRotation(src) is int existing)
                {
                    try
                    {
                        RunAttempts(ffmpeg, DisplayMatrixInput(src, spec, existing),
                            new List<Attempt> { new Attempt("rotate copy", new[] { "-map", "0", "-c", "copy" }) },
                            Faststart(container), output, what, ct: ct);
                        copied = true;
                    }
                    catch (InvalidOperationException ex)
                    {
                        string reason = ex.Message.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                        Log.Write($"rotate copy failed, trying next: {reason}");
                        try { File.Delete(output); } catch { }
                    }
                }

                // ffmpeg autorotates on decode, so the filter starts from what the user saw (any
                // existing display matrix applied) and the output carries no display matrix.
                if (!copied)
                {
                    RunAttempts(ffmpeg, new[] { "-i", src },
                        ReencodeAttempts(container, "rotate reencode", RotateFilter(spec)),
                        Faststart(container), output, what, ct: ct);
                }

                string dest = Naming.WithSuffix(src, "rotated");
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // Input options that replace the display matrix (ffmpeg 8 -display_* options set it outright,
        // they do not compose with the source's), so the current rotation e is folded in here.
        // ffmpeg's -display_rotation is counter-clockwise, and -display_hflip mirrors AFTER that
        // rotation. Wanted: turn cw by e (what plays today), then the spec: mirror, turn cw r.
        //   no mirror: cw e + r, so -display_rotation = -(e + r).
        //   mirror:    R(r) H R(e) = H R(-r) R(e) = H R(e - r): turn cw (e - r), then mirror,
        //              so -display_rotation = r - e plus -display_hflip.
        // An existing flip in the source matrix is ignored (rare; the result would lose it).
        private static string[] DisplayMatrixInput(string src, RotateSpec spec, int existing)
        {
            int cw = spec.HFlip ? existing - spec.Degrees : existing + spec.Degrees;
            string ccw = RotateSpec.Mod360(-cw).ToString(CultureInfo.InvariantCulture);
            return spec.HFlip
                ? new[] { "-display_rotation", ccw, "-display_hflip", "-i", src }
                : new[] { "-display_rotation", ccw, "-i", src };
        }

        // Mirror first, then turn clockwise: transpose=clock is 90 cw, cclock is 90 ccw (270 cw).
        // A mirror plus a half turn is just a vertical flip.
        private static string RotateFilter(RotateSpec spec) => (spec.HFlip, spec.Degrees) switch
        {
            (false, 90) => "transpose=clock",
            (false, 180) => "hflip,vflip",
            (false, 270) => "transpose=cclock",
            (true, 0) => "hflip",
            (true, 90) => "hflip,transpose=clock",
            (true, 180) => "vflip",
            (true, 270) => "hflip,transpose=cclock",
            _ => throw new ArgumentException("Nothing to rotate"),
        };

        // Clockwise rotation the source's display matrix applies today (0 when it has none), or null
        // when ffprobe is missing, fails, or reports an angle that is not a multiple of 90. ffprobe's
        // rotation is counter-clockwise (a portrait phone clip shows -90), hence the negation.
        private static int? CurrentRotation(string src)
        {
            if (Tools.Ffprobe == null) return null;
            try
            {
                string raw = Tools.RunProcess(Tools.Ffprobe, new[]
                {
                    "-v", "error", "-select_streams", "v:0", "-show_entries", "stream_side_data=rotation",
                    "-of", "default=nw=1:nk=1", src,
                }, ProbeTimeout);
                foreach (string line in raw.Split('\n'))
                {
                    if (!double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ccw)
                        || !double.IsFinite(ccw))
                        continue;
                    int cw = -(int)Math.Round(ccw);
                    return cw % 90 == 0 ? RotateSpec.Mod360(cw) : null;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write($"rotate: could not read the current rotation of {src}, re-encoding: {ex.Message}");
                return null;
            }
        }

        // One ffprobe call on the first real video stream (V skips attached cover pictures, like the
        // encode's -map 0:V): width, height, frame rates and the display matrix rotation. The size is
        // the displayed one (swapped for a quarter turn); fps is avg_frame_rate, else r_frame_rate.
        public static ResizeInfo VideoInfo(string src)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (Formats.KindOf(src) != Kind.Video)
                throw new NotSupportedException($"Cannot resize {Path.GetFileName(src)} (not a video).");

            string raw = Tools.RunProcess(RequireFfprobe(), new[]
            {
                "-v", "error", "-select_streams", "V:0",
                "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate:stream_side_data=rotation",
                "-of", "default=nw=1", src,
            }, ProbeTimeout);

            // Fields come in ffprobe's own order, so match by key; unknown keys and N/A are ignored.
            int width = 0, height = 0;
            double avg = 0, rframe = 0, rotation = 0;
            foreach (string line in raw.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "width":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0) width = w;
                        break;
                    case "height":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h > 0) height = h;
                        break;
                    case "avg_frame_rate":
                        avg = ParseRate(value);
                        break;
                    case "r_frame_rate":
                        rframe = ParseRate(value);
                        break;
                    case "rotation":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rot)
                            && double.IsFinite(rot))
                            rotation = rot;
                        break;
                }
            }
            if (width == 0 || height == 0)
                throw new NotSupportedException($"Cannot resize {Path.GetFileName(src)} (no video track).");

            if (Math.Abs((int)Math.Round(rotation / 90.0)) % 2 == 1) (width, height) = (height, width);

            // Phone VFR footage lies in r_frame_rate, so the average wins when ffprobe has one.
            double fps = avg > 0 ? avg : rframe;
            // NTSC when closer to n*1000/1001 than to n itself (so a true 24 fps is not NTSC).
            bool ntsc = fps > 0 && NtscBases.Any(n =>
            {
                double rate = n * 1000.0 / 1001.0;
                return Math.Abs(fps - rate) < 0.1 && Math.Abs(fps - rate) < Math.Abs(fps - n);
            });

            int shortSide = Math.Min(width, height);
            List<int> heights = ResizeHeights.Where(t => t < shortSide).ToList();
            List<string> rates = fps > 0
                ? ResizeRates.Where(r => r < fps - 0.5).Select(r => r.ToString(CultureInfo.InvariantCulture)).ToList()
                : new List<string>();
            return new ResizeInfo(width, height, fps, ntsc, heights, rates);
        }

        // Re-encodes a video at a lower resolution (height = the displayed short side) and/or frame rate
        // (a whole-number label such as "30") into <stem>_720p.<ext>, _30fps or _720p30, same container.
        // ffmpeg autorotates before -vf, so the scale works on the displayed frame and the output carries
        // no display matrix. progress gets 0..1 on a pool thread; cancelling kills ffmpeg and saves nothing.
        public static string Resize(string src, int? height, string? fps, Action<double>? progress, CancellationToken ct)
        {
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);
            if (!Formats.CanResize(src))
                throw new NotSupportedException($"Cannot resize {Path.GetFileName(src)} (not a video or missing ffmpeg/ffprobe).");
            if (height == null && fps == null) throw new ArgumentException("Nothing to change");
            string ffmpeg = Tools.Ffmpeg!;

            int? rate = null;
            if (fps != null)
            {
                if (!int.TryParse(fps.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int f) || f <= 0)
                    throw new ArgumentException($"Invalid frame rate: {fps}");
                rate = f;
            }
            if (height is <= 0) throw new ArgumentException($"Invalid height: {height}");

            ResizeInfo info = VideoInfo(src);
            // Same predicates as HeightOptions and FpsOptions, so the CLI and the window agree.
            if ((height is int hv && hv >= info.ShortSide) || (rate is int rv && !(info.Fps > 0 && rv < info.Fps - 0.5)))
                throw new ArgumentException("Only smaller sizes");

            var filters = new List<string>();
            string suffix = "";
            if (height is int target)
            {
                // Short side to the target, long side keeps the aspect ratio; both even for the encoders.
                int shortOut = Even(target);
                if (shortOut >= info.ShortSide) throw new ArgumentException("Only smaller sizes");
                int longOut = Even((double)Math.Max(info.Width, info.Height) * shortOut / info.ShortSide);
                bool landscape = info.Width >= info.Height;
                int w = landscape ? longOut : shortOut, h = landscape ? shortOut : longOut;
                filters.Add($"scale={w}:{h}:flags=lanczos");
                suffix = shortOut.ToString(CultureInfo.InvariantCulture) + "p";
            }
            if (rate is int fr)
            {
                filters.Add("fps=" + FpsRate(fr, info.Ntsc));
                string label = fr.ToString(CultureInfo.InvariantCulture);
                suffix = suffix.Length > 0 ? suffix + label : label + "fps";
            }

            string container = Formats.Norm(src) == "m4v" ? "mp4" : Formats.Norm(src);
            string[] map = { "-map", "0:V", "-map", "0:a?", "-vf", string.Join(",", filters) };
            string[] video = ResizeVideoArgs(container);
            var attempts = new List<Attempt>
            {
                new Attempt("resize", map.Concat(video).Concat(new[] { "-c:a", "copy" }).ToArray()),
                new Attempt("resize (audio reencode)", map.Concat(video).Concat(ReencodeAudio(container)).ToArray()),
            };

            double duration = 0;
            try { duration = Duration(src); }
            catch (Exception ex) { Log.Write($"resize: no duration for progress: {ex.Message}"); }
            // -progress lines: out_time_us (and out_time_ms, also microseconds) per block, progress=end last.
            // A fallback attempt restarts from its own first block.
            Action<string> onLine = line =>
            {
                if (progress == null) return;
                if (line.Trim() == "progress=end") { progress(1); return; }
                if (duration <= 0) return;
                int eq = line.IndexOf('=');
                if (eq <= 0) return;
                string key = line.Substring(0, eq).Trim();
                if (key != "out_time_us" && key != "out_time_ms") return;
                if (long.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long us))
                    progress(Math.Clamp(us / 1e6 / duration, 0, 1));
            };

            string work = NewWorkDir();
            try
            {
                // Keep the raw source extension so ffmpeg picks the same muxer.
                string output = Path.Combine(work, "out" + Path.GetExtension(src));
                RunAttempts(ffmpeg, new[] { "-progress", "pipe:1", "-nostats", "-i", src }, attempts,
                    Faststart(container), output, $"{src} -> {suffix}", onLine, ct);
                ct.ThrowIfCancellationRequested();

                string dest = Naming.WithSuffix(src, suffix);
                File.Move(output, dest);
                return dest;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // ffmpeg fps= value for a whole-number label: the /1001 rate for 24, 30 and 60 on NTSC sources,
        // so 29.97 to 24 lands on 23.976 without judder.
        private static string FpsRate(int fps, bool ntsc) =>
            ntsc && fps is 24 or 30 or 60
                ? (fps * 1000).ToString(CultureInfo.InvariantCulture) + "/1001"
                : fps.ToString(CultureInfo.InvariantCulture);

        // Rounds to the nearest even number, at least 2.
        private static int Even(double x) => Math.Max(2, (int)(Math.Round(x / 2.0, MidpointRounding.AwayFromZero) * 2));

        // Video codec per container like ReencodeAttempts, at the resize quality constants.
        private static string[] ResizeVideoArgs(string container) => container switch
        {
            "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", ResizeVp9Crf, "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4" },
            "wmv" => new[] { "-c:v", "wmv2", "-q:v", ResizeWmvQ },
            _ => new[] { "-c:v", "libx264", "-crf", ResizeX264Crf, "-preset", ResizeX264Preset },
        };

        // Stream-copies src into output (same container) without global, per-stream or chapter metadata.
        // Used by MetadataCleaner. Data streams (GoPro gpmd, phone GPS tracks) and attachments are
        // dropped too, since they can carry location; subtitles stay. +bitexact on the output keeps
        // ffmpeg from writing its own Lavf encoder tag. The display matrix (rotation) is stream side
        // data, not metadata, so -map_metadata leaves it in place and the video keeps its orientation.
        internal static void StripMetadata(string src, string output)
        {
            string ffmpeg = Tools.Ffmpeg
                ?? throw new InvalidOperationException(
                    "ffmpeg not found. Install it (for example: winget install Gyan.FFmpeg) and try again.");
            string[] strip =
            {
                "-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1", "-fflags", "+bitexact",
            };
            var attempts = new List<Attempt>
            {
                new Attempt("clean copy", new[] { "-map", "0", "-map", "-0:d?", "-map", "-0:t?", "-c", "copy" }.Concat(strip).ToArray()),
                new Attempt("clean copy (v+a)", new[] { "-map", "0:v", "-map", "0:a?", "-c", "copy" }.Concat(strip).ToArray()),
            };
            string container = Formats.Norm(src) == "m4v" ? "mp4" : Formats.Norm(src);
            RunAttempts(ffmpeg, new[] { "-i", src }, attempts, Faststart(container), output, src);
        }

        // Container that takes the codec without a re-encode; Matroska takes anything else.
        private static string AudioContainer(string codec) => codec switch
        {
            "aac" or "alac" => "m4a",
            "mp3" => "mp3",
            "opus" => "opus",
            "vorbis" => "ogg",
            "flac" => "flac",
            "ac3" => "ac3",
            "eac3" => "eac3",
            _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => "wav",
            _ => "mka",
        };

        // Re-encode fallback: video at visually lossless quality (no scaling, frames passed through),
        // audio copied when the container takes it, otherwise encoded at a high bitrate.
        // videoFilter (a -vf chain) is for Transform; frames and quality are otherwise unchanged.
        private static List<Attempt> ReencodeAttempts(string container, string label, string? videoFilter = null)
        {
            string[] map = videoFilter == null
                ? new[] { "-map", "0:v", "-map", "0:a?" }
                : new[] { "-map", "0:v", "-map", "0:a?", "-vf", videoFilter };
            string[] video = container switch
            {
                "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "18", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4" },
                "wmv" => new[] { "-c:v", "wmv2", "-q:v", "2" },
                _ => new[] { "-c:v", "libx264", "-crf", "16", "-preset", "slow" }, // keeps source pix_fmt when x264 can
            };
            string[] audio = ReencodeAudio(container);
            string[] passthrough = { "-fps_mode", "passthrough" };

            return new List<Attempt>
            {
                new Attempt(label, map.Concat(video).Concat(passthrough).Concat(new[] { "-c:a", "copy" }).ToArray()),
                new Attempt(label, map.Concat(video).Concat(passthrough).Concat(audio).ToArray()),
            };
        }

        // Audio encoder when the container cannot take the source audio as is: high bitrate per container.
        private static string[] ReencodeAudio(string container) => container switch
        {
            "webm" => new[] { "-c:a", "libopus", "-b:a", "256k" },
            "avi" => new[] { "-c:a", "libmp3lame", "-b:a", "320k" },
            "wmv" => new[] { "-c:a", "wmav2", "-b:a", "320k" },
            _ => new[] { "-c:a", "aac", "-b:a", "320k" },
        };

        // Runs the attempts in order until one succeeds. A failed attempt's partial output is
        // deleted before the next; the last failure propagates. With onLine or a cancellable ct the
        // run streams (lines to onLine, process tree killed on cancel, OperationCanceledException).
        private static void RunAttempts(string ffmpeg, string[] input, List<Attempt> attempts,
            string[] tail, string output, string what, Action<string>? onLine = null, CancellationToken ct = default)
        {
            for (int i = 0; i < attempts.Count; i++)
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
                args.AddRange(input);
                args.AddRange(attempts[i].Args);
                args.AddRange(tail);
                args.Add(output);
                try
                {
                    // no timeout: videos can be long
                    if (onLine != null || ct.CanBeCanceled) Tools.RunProcessStreaming(ffmpeg, args, onLine, ct);
                    else Tools.RunProcess(ffmpeg, args);
                    Log.Write($"{attempts[i].Label}: {what}");
                    return;
                }
                catch (InvalidOperationException ex) when (i < attempts.Count - 1)
                {
                    string reason = ex.Message.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                    Log.Write($"{attempts[i].Label} failed, trying next: {reason}");
                    try { File.Delete(output); } catch { }
                }
            }
        }

        private static string[] Faststart(string container) =>
            container is "mp4" or "mov" ? new[] { "-movflags", "+faststart" } : Array.Empty<string>();

        // Container duration in seconds only (no keyframe scan, unlike Probe).
        public static double Duration(string src) => Duration(RequireFfprobe(), Path.GetFullPath(src));

        private static double Duration(string ffprobe, string src)
        {
            string raw = Tools.RunProcess(ffprobe, new[]
            {
                "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", src,
            }, ProbeTimeout);
            if (!TryParseSeconds(raw, out double duration) || duration <= 0)
                throw new InvalidOperationException($"Could not read the duration of {Path.GetFileName(src)}.");
            return duration;
        }

        // Parses the first comma separated field of ffprobe csv output ("1.000000," or "N/A").
        private static bool TryParseSeconds(string line, out double seconds)
        {
            string field = line.Trim().Split(',')[0].Trim();
            return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                && double.IsFinite(seconds);
        }

        // Six decimals, matching ffprobe pts_time, so a snapped start never rounds below its keyframe.
        private static string Secs(double s) => s.ToString("0.######", CultureInfo.InvariantCulture);

        private static string RequireFfprobe() => Tools.Ffprobe
            ?? throw new InvalidOperationException(
                "ffprobe not found. Install ffmpeg (for example: winget install Gyan.FFmpeg) and try again.");

        private static string NewWorkDir()
        {
            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return work;
        }
    }
}
