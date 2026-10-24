using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;
using static Typeless.Theme;

namespace Typeless
{
    // Borderless trim panel on the cursor's monitor: drag two handles, Enter saves <stem>_trim.<ext>.
    public sealed class TrimWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, WorkShare = 0.70, MinBarW = 560, MaxBarW = 1100;
        private const double PreviewShare = 0.55, VideoBarH = 56, AudioBarH = 96, HandleW = 10, HandleTol = 6;
        private const double MinLen = 0.1, DragSlop = 3, EndSlack = 0.05, FineFactor = 0.1, AudioStep = 0.01;
        private const int StripTiles = 12, MaxTicks = 400, ScrubMs = 16;

        private static readonly TimeSpan StripTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ProxyTimeout = TimeSpan.FromMinutes(10);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private enum Hold { None, Start, End, Body }

        // Worker result: probe info, the encoded filmstrip (video) or waveform (audio), and the frame at 0.
        private sealed record LoadResult(MediaBackend.ProbeInfo Info, byte[]? Strip, byte[]? Frame);

        private readonly string path;
        private readonly bool previewMode;
        private readonly bool isVideo;
        private readonly string tempDir;
        private readonly double barH;

        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock nameText;
        private readonly TextBlock durText;
        private readonly Grid previewBox;
        private readonly Image previewImage;
        private readonly Grid timeline;
        private readonly Image stripImage;
        private readonly Canvas marks;
        private readonly Rectangle dimLeft;
        private readonly Rectangle dimRight;
        private readonly Rectangle ghostFill;
        private readonly Line ghostLine;
        private readonly Rectangle selTop;
        private readonly Rectangle selBottom;
        private readonly Path ticks;
        private readonly Rectangle hoverLine;
        private readonly Rectangle playLine;
        private readonly Ellipse playCap;
        private readonly Border startHandle;
        private readonly Border endHandle;
        private readonly SolidColorBrush startBrush;
        private readonly SolidColorBrush endBrush;
        private readonly Rectangle startGrip;
        private readonly Rectangle endGrip;
        private readonly Canvas spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly Border status;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock statusTitle;
        private readonly TextBlock statusSub;
        private readonly Canvas bubbleLayer;
        private readonly Border bubble;
        private readonly TextBlock bubbleText;
        private readonly SolidColorBrush losslessBrush;
        private readonly TextBlock readout;
        private readonly TextBlock ghostText;
        private readonly TextBlock saveGlyph;
        private readonly TextBlock saveText;
        private readonly Canvas saveSpinner;
        private readonly RotateTransform saveSpinnerRotate;
        private readonly MediaElement player;
        private readonly DispatcherTimer playTimer;
        private readonly Border playButton;
        private readonly SolidColorBrush playBrush;
        private readonly TextBlock playGlyph;
        private readonly Canvas playSpinner;
        private readonly RotateTransform playSpinnerRotate;
        private readonly TextBlock timeText;
        private readonly DispatcherTimer scrubTimer;

        private IntPtr hwnd;
        private bool haveMonitor;
        private NativeMethods.RECT work;
        private double scale = 1.0;
        private double barW = MaxBarW;

        // Times in seconds, on the frame grid. The handles always sit at startTime/endTime;
        // CutStart is where the cut really starts (the keyframe before startTime when Lossless).
        private MediaBackend.ProbeInfo? info;
        private double duration;
        private List<double> keys = new List<double>();
        private double startTime, endTime;
        private bool lossless, activeEnd;

        private Hold hold;
        private double pressT, pressS, pressE;
        private double? hoverT;
        private double previewT;

        // A press only becomes a drag past DragSlop; a press that never does is a click (seek).
        private Point pressPt;
        private bool dragging, pressOnHandle;

        // Drag pointer time is relative to an anchor, re-set when Shift toggles fine mode, so the
        // handle never jumps. lastX/lastT are the previous move, the anchor for the next toggle.
        private double anchorX, anchorT, lastX, lastT;
        private bool fine;

        // Scrubbing the open MediaElement: at most one Position assignment per ScrubMs; a pending
        // time is flushed by scrubTimer so the last position of a drag that stops always lands.
        private long lastScrub;
        private double? scrubPending;

        // Playback: playT is the playhead; a failed open gets one proxy attempt before play is disabled.
        private double playT;
        private bool playing, mediaReady, proxyBuilding, proxyTried, playFailed, playheadShown, playSpinning;
        private bool autoplay;

        // Frame pump: at most one ffmpeg grab in flight; the latest wanted time wins.
        private double? wantedT;
        private double shownT = -1;
        private bool grabbing, loading;

        private bool busy, saved, failed, closing, hiddenWhileBusy, closedDone;

        public int ExitCode { get; private set; }

        public TrimWindow(string path) : this(path, false)
        {
        }

        // Preview (dev): loads synchronously inside RenderPreview, never shown.
        internal TrimWindow(string path, bool preview)
        {
            this.path = path;
            previewMode = preview;
            isVideo = Formats.KindOf(path) == Kind.Video;
            barH = isVideo ? VideoBarH : AudioBarH;
            tempDir = IOPath.Combine(IOPath.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));

            Title = "Typeless Trim";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name left, duration right.
            nameText = new TextBlock
            {
                Text = IOPath.GetFileName(path),
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            durText = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
            DockPanel.SetDock(durText, Dock.Right);
            header.Children.Add(durText);
            header.Children.Add(nameText);

            spinnerRotate = new RotateTransform(0, 18, 18);
            spinner = MakeSpinner(36, 15, 3, Frozen(AccentColor), spinnerRotate);

            // Preview (video only): the current frame letterboxed in a black 16:9 box.
            previewImage = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(previewImage, BitmapScalingMode.HighQuality);
            previewBox = new Grid
            {
                Background = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            };
            previewBox.Children.Add(previewImage);

            // Playback sits over the frame image (video) or as an invisible 1x1 (audio: a
            // collapsed MediaElement may not play). Once open it is also scrubbed while handles are
            // dragged; the ffmpeg frame image shows before that, or when playback is unavailable.
            player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                Stretch = Stretch.Uniform,
                Volume = 1,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            player.MediaOpened += OnMediaOpened;
            player.MediaFailed += OnMediaFailed;
            player.MediaEnded += (_, _) =>
            {
                if (playing) ReachEnd(duration);
            };
            if (isVideo) previewBox.Children.Add(player);
            else
            {
                player.Width = 1;
                player.Height = 1;
                player.HorizontalAlignment = HorizontalAlignment.Left;
                player.VerticalAlignment = VerticalAlignment.Top;
            }
            playTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            playTimer.Tick += (_, _) => OnPlayTick();
            scrubTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(ScrubMs) };
            scrubTimer.Tick += (_, _) => FlushScrub();

            // Timeline: filmstrip or waveform, dimmed outside the range, accent edges, handles, ticks.
            stripImage = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(stripImage, BitmapScalingMode.HighQuality);

            Brush dimBrush = Frozen(Color.FromArgb(0xA0, 0, 0, 0));
            dimLeft = new Rectangle { Fill = dimBrush };
            dimRight = new Rectangle { Fill = dimBrush };
            // Lossless ghost: the stretch from the keyframe to the start handle is also kept,
            // shown undimmed with an accent tint and a dashed accent line at the real start.
            ghostFill = new Rectangle
            {
                Fill = Frozen(Color.FromArgb(0x33, AccentColor.R, AccentColor.G, AccentColor.B)),
                Visibility = Visibility.Collapsed,
            };
            ghostLine = new Line
            {
                X1 = 1,
                X2 = 1,
                Y1 = 0,
                Y2 = barH,
                Stroke = Frozen(AccentColor),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 2, 1.5 },
                Visibility = Visibility.Collapsed,
            };
            selTop = new Rectangle { Fill = Frozen(AccentColor) };
            selBottom = new Rectangle { Fill = Frozen(AccentColor) };
            ticks = new Path { Fill = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)) };
            hoverLine = new Rectangle
            {
                Width = 2,
                Fill = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                Visibility = Visibility.Collapsed,
            };
            // Playhead: solid white line with a small cap, above the handles; hidden until first play or seek.
            playLine = new Rectangle { Width = 2, Fill = Brushes.White, Visibility = Visibility.Collapsed };
            playCap = new Ellipse { Width = 8, Height = 8, Fill = Brushes.White, Visibility = Visibility.Collapsed };
            Panel.SetZIndex(playLine, 3);
            Panel.SetZIndex(playCap, 3);
            startBrush = new SolidColorBrush(AccentColor);
            endBrush = new SolidColorBrush(AccentColor);
            startHandle = MakeHandle(startBrush, out startGrip);
            endHandle = MakeHandle(endBrush, out endGrip);

            marks = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            marks.Children.Add(dimLeft);
            marks.Children.Add(dimRight);
            marks.Children.Add(ghostFill);
            marks.Children.Add(ghostLine);
            marks.Children.Add(selTop);
            marks.Children.Add(selBottom);
            marks.Children.Add(ticks);
            marks.Children.Add(hoverLine);
            marks.Children.Add(startHandle);
            marks.Children.Add(endHandle);
            marks.Children.Add(playLine);
            marks.Children.Add(playCap);

            timeline = new Grid
            {
                Height = barH,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            };
            timeline.Children.Add(stripImage);
            timeline.Children.Add(marks);
            timeline.MouseLeftButtonDown += OnBarDown;
            timeline.MouseMove += OnBarMove;
            timeline.MouseLeftButtonUp += OnBarUp;
            timeline.LostMouseCapture += (_, _) => EndHold();
            timeline.MouseLeave += (_, _) =>
            {
                hoverT = null;
                hoverLine.Visibility = Visibility.Collapsed;
            };

            if (isVideo) previewBox.Children.Add(spinner);
            else
            {
                timeline.Children.Add(player);
                timeline.Children.Add(spinner);
            }

            // Time bubble above the dragged handle. It lives on an unclipped layer over the timeline
            // (the timeline itself is clipped to its rounded rect) and overhangs above it.
            bubbleText = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = Brushes.White,
            };
            bubble = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = Frozen(Color.FromArgb(0xE6, 0x14, 0x14, 0x18)),
                Padding = new Thickness(6, 2, 6, 2),
                Child = bubbleText,
                Visibility = Visibility.Collapsed,
            };
            bubbleLayer = new Canvas { IsHitTestVisible = false, Height = barH, HorizontalAlignment = HorizontalAlignment.Center };
            bubbleLayer.Children.Add(bubble);
            var timelineHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            timelineHost.Children.Add(timeline);
            timelineHost.Children.Add(bubbleLayer);

            var body = new StackPanel { Orientation = Orientation.Vertical };
            if (isVideo) body.Children.Add(previewBox);
            body.Children.Add(timelineHost);

            // Toolbar: Lossless pill (video only), play/pause and current time left; readout, Cancel, Save right.
            losslessBrush = new SolidColorBrush(PillIdle);
            Border losslessPill = MakeLosslessPill();
            losslessPill.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            playBrush = new SolidColorBrush(PillIdle);
            playGlyph = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 14,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            playSpinnerRotate = new RotateTransform(0, 7, 7);
            playSpinner = MakeSpinner(14, 5.5, 2, Brushes.White, playSpinnerRotate);
            playButton = MakePlayPill();
            timeText = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Text = " ",
            };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pillRow.Children.Add(losslessPill);
            pillRow.Children.Add(playButton);
            pillRow.Children.Add(timeText);

            readout = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                Text = " ",
            };
            // Second line, only while Lossless moves the real start to an earlier keyframe.
            ghostText = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = Visibility.Collapsed,
            };
            var readoutStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 12, 0),
            };
            readoutStack.Children.Add(readout);
            readoutStack.Children.Add(ghostText);
            // Fixed-height host so the ghost line never resizes (and recenters) the window.
            var readoutHost = new Grid { Height = 32, VerticalAlignment = VerticalAlignment.Center };
            readoutHost.Children.Add(readoutStack);
            var cancelText = new TextBlock
            {
                Text = "Cancel",
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Border cancel = MakeButton(cancelText, GhostIdle, GhostHover,
                Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), CloseAnimated, () => busy);

            saveGlyph = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            saveSpinnerRotate = new RotateTransform(0, 6, 6);
            saveSpinner = MakeSpinner(12, 5, 2, Brushes.White, saveSpinnerRotate);
            saveSpinner.Margin = new Thickness(0, 0, 6, 0);
            saveText = new TextBlock
            {
                Text = "Save",
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var saveContent = new StackPanel { Orientation = Orientation.Horizontal };
            saveContent.Children.Add(saveGlyph);
            saveContent.Children.Add(saveSpinner);
            saveContent.Children.Add(saveText);
            Border save = MakeButton(saveContent, AccentColor, AccentHover, null, Save, () => busy);
            save.Margin = new Thickness(8, 0, 0, 0);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(readoutHost);
            right.Children.Add(cancel);
            right.Children.Add(save);
            var toolbar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            toolbar.Children.Add(right);
            toolbar.Children.Add(pillRow);

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(header);
            stack.Children.Add(body);
            stack.Children.Add(toolbar);

            // The status pill overlays the whole content (an audio timeline is too short to hold it).
            status = MakeStatus(out statusGlyph, out statusTitle, out statusSub);
            var content = new Grid();
            content.Children.Add(stack);
            content.Children.Add(status);

            // The shadow sits on a sibling plate so the effect does not re-render the images.
            var plate = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = DiscBrush,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 4, Opacity = 0.45 },
            };
            var panel = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = DiscBrush,
                BorderBrush = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Pad),
                Child = content,
            };

            rootScale = new ScaleTransform(0.94, 0.94);
            root = new Grid
            {
                Margin = new Thickness(ShadowMargin),
                Opacity = 0,
                RenderTransform = rootScale,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            root.Children.Add(plate);
            root.Children.Add(panel);
            frame = new Grid();
            frame.Children.Add(root);
            Content = frame;

            SetLayout(1920, 1080);

            SourceInitialized += (_, _) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                haveMonitor = NativeMethods.TryGetCursorMonitor(out _, out work, out scale);
                if (haveMonitor) SetLayout((work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
                CenterOnMonitor();
            };
            SizeChanged += (_, _) => CenterOnMonitor();
            Loaded += OnLoaded;
            PreviewKeyDown += OnKeyDown;
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!failed || busy) return;
                e.Handled = true;
                CloseAnimated();
            };
            Closing += (_, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                HideWhileBusy();
            };
            Closed += (_, _) =>
            {
                closedDone = true;
                ClosePlayer();
                Cleanup();
            };

            string? play = Environment.GetEnvironmentVariable("TYPELESS_AUTOPLAY");
            autoplay = !preview && (play == "1" || play?.Contains(',') == true)
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPELESS_AUTOTRIM"));
            UpdatePlayUi();
        }

        private bool Ready => info != null;

        // Lossless (stream copy) only applies to video; audio copy already cuts exactly.
        private bool Lossless => isVideo && lossless;

        // Copy mode can only start on a keyframe. A keyframe under half a frame after the handle
        // counts as on it (keyframe pts and the frame grid can differ by a hair).
        private double CutStart => Lossless ? Snap(startTime + FrameStep / 2) : startTime;

        // One frame (video) or 0.01 s (audio, or a video with no known frame rate).
        private double FrameStep => info != null && info.FrameRate > 0 ? 1 / info.FrameRate : AudioStep;

        // Rounds to the frame grid; the only quantization handle times ever get.
        private double Q(double t) => Math.Round(t / FrameStep) * FrameStep;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            if (!Ready && !loading) LoadMedia();
        }

        // Probes and renders the strip on a worker; the spinner shows meanwhile.
        private async void LoadMedia()
        {
            loading = true;
            StartSpinner(spinner, spinnerRotate);
            try
            {
                string src = path, dir = tempDir;
                LoadResult result = await Task.Run(() => LoadWork(src, dir));
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                Apply(result);
                OpenPlayer(path);
            }
            catch (Exception ex)
            {
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                failed = true;
                ExitCode = 1;
                Log.Write($"Trim load failed: {path}: {ex}");
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                return;
            }
            finally
            {
                loading = false;
                if (closedDone) Cleanup();
            }
            RunAutoTrim();
        }

        // Test hooks: TYPELESS_AUTOTRIM=a,b (fractions of the duration) sets the range and saves;
        // TYPELESS_AUTOPLAY=a,b sets the range only (autoplay then plays it). Both cut in Accurate
        // mode unless TYPELESS_LOSSLESS=1.
        private void RunAutoTrim()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOTRIM");
            bool save = !string.IsNullOrWhiteSpace(auto);
            if (!save && autoplay) auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOPLAY");
            if (string.IsNullOrWhiteSpace(auto)) return;
            string[] parts = auto.Split(',');
            if (parts.Length != 2) return;
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, Inv, out double a)) return;
            if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, Inv, out double b)) return;
            if (isVideo && Environment.GetEnvironmentVariable("TYPELESS_LOSSLESS") == "1") SetLossless(true);
            SetRange(a * duration, b * duration);
            if (save) Save();
        }

        // Both ends on the frame grid, end clamped after the start.
        private void SetRange(double start, double end)
        {
            startTime = Clamp(Q(start), 0, duration - MinLen);
            endTime = Clamp(Q(end), startTime + MinLen, duration);
            UpdateTimeline();
        }

        // Runs on a worker: probe, then one ffmpeg call for the filmstrip (video) or waveform (audio).
        private static LoadResult LoadWork(string src, string dir)
        {
            MediaBackend.ProbeInfo probe = MediaBackend.Probe(src);
            string ffmpeg = Tools.Ffmpeg
                ?? throw new InvalidOperationException(
                    "ffmpeg not found. Install it (for example: winget install Gyan.FFmpeg) and try again.");
            System.IO.Directory.CreateDirectory(dir);

            if (probe.HasVideo)
            {
                string strip = IOPath.Combine(dir, "strip.png");
                string fps = (StripTiles / probe.Duration).ToString("0.########", Inv);
                byte[]? stripBytes = RunImage(ffmpeg, new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", "0", "-i", src,
                    "-vf", $"fps={fps},scale=160:-2,tile={StripTiles}x1", "-frames:v", "1", "-an", "-update", "1", strip,
                }, strip, "filmstrip");
                return new LoadResult(probe, stripBytes, GrabFrame(src, dir, 0));
            }

            string wave = IOPath.Combine(dir, "wave.png");
            byte[]? waveBytes = RunImage(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", src,
                "-filter_complex", "showwavespic=s=1200x120:colors=0x6C8CFF", "-frames:v", "1", "-update", "1", wave,
            }, wave, "waveform");
            return new LoadResult(probe, waveBytes, null);
        }

        // A missing strip or waveform is not fatal: the timeline falls back to a plain dark bar.
        private static byte[]? RunImage(string ffmpeg, string[] args, string file, string what)
        {
            try
            {
                Tools.RunProcess(ffmpeg, args, StripTimeout);
                return ReadAndDelete(file);
            }
            catch (Exception ex)
            {
                Log.Write($"Trim {what} failed: {ex.Message}");
                return null;
            }
        }

        // Single frame at t as PNG bytes, or null (past the end, or ffmpeg failed).
        private static byte[]? GrabFrame(string src, string dir, double t)
        {
            string? ffmpeg = Tools.Ffmpeg;
            if (ffmpeg == null) return null;
            string file = IOPath.Combine(dir, "frame.png");
            try
            {
                Tools.RunProcess(ffmpeg, new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", Secs(t), "-i", src,
                    "-frames:v", "1", "-vf", "scale='min(960,iw)':-2", "-an", "-update", "1", file,
                }, FrameTimeout);
                return ReadAndDelete(file);
            }
            catch (Exception ex)
            {
                Log.Write($"Trim frame grab failed at {Secs(t)}: {ex.Message}");
                return null;
            }
        }

        private static byte[]? ReadAndDelete(string file)
        {
            if (!System.IO.File.Exists(file)) return null;
            byte[] bytes = System.IO.File.ReadAllBytes(file);
            try { System.IO.File.Delete(file); } catch { }
            return bytes.Length > 0 ? bytes : null;
        }

        // Decodes on the calling (UI) thread into a frozen bitmap that no longer needs the bytes.
        private static BitmapSource? Decode(byte[]? bytes)
        {
            if (bytes == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Write($"Trim image decode failed: {ex.Message}");
                return null;
            }
        }

        private void Apply(LoadResult result)
        {
            info = result.Info;
            duration = result.Info.Duration;
            keys = new List<double>(result.Info.Keyframes);
            startTime = 0;
            endTime = duration;
            activeEnd = false;
            durText.Text = Fmt(duration);

            BitmapSource? strip = Decode(result.Strip);
            if (strip != null) stripImage.Source = strip;
            BitmapSource? first = Decode(result.Frame);
            if (first != null)
            {
                previewImage.Source = first;
                shownT = 0;
            }
            previewT = 0;
            playT = 0;
            marks.Visibility = Visibility.Visible;
            UpdateTicks();
            UpdateTimeline();
            UpdatePlayUi();
        }

        // 70% of the work width (clamped), preview 16:9 capped at 55% of the work height.
        private void SetLayout(double workW, double workH)
        {
            barW = Clamp(workW * WorkShare, MinBarW, MaxBarW);
            double previewH = Math.Max(120, Math.Min(barW * 9 / 16, workH * PreviewShare));
            previewBox.Width = barW;
            previewBox.Height = previewH;
            previewBox.Clip = Rounded(barW, previewH);
            timeline.Width = barW;
            timeline.Clip = Rounded(barW, barH);
            stripImage.Width = barW;
            stripImage.Height = barH;
            marks.Width = barW;
            marks.Height = barH;
            bubbleLayer.Width = barW;
            hoverLine.Height = barH;
            playLine.Height = barH;
            nameText.MaxWidth = Math.Max(200, barW - 140);
            if (Ready)
            {
                UpdateTicks();
                UpdateTimeline();
            }
        }

        private static Geometry Rounded(double w, double h)
        {
            var g = new RectangleGeometry(new Rect(0, 0, w, h), 8, 8);
            g.Freeze();
            return g;
        }

        private Border MakeHandle(SolidColorBrush brush, out Rectangle grip)
        {
            grip = new Rectangle
            {
                Width = 2,
                Height = 18,
                RadiusX = 1,
                RadiusY = 1,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return new Border
            {
                Width = HandleW,
                Height = barH,
                CornerRadius = new CornerRadius(4),
                Background = brush,
                Child = grip,
            };
        }

        // Toggle pill, same look as the crop ratio pills.
        private Border MakeLosslessPill()
        {
            var text = new TextBlock
            {
                Text = "Lossless",
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Height = 24,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = losslessBrush,
                Child = text,
                Cursor = Cursors.Hand,
                ToolTip = "Stream copy, no re-encode. Start moves to the previous keyframe.",
            };
            pill.MouseEnter += (_, _) =>
            {
                if (!lossless) AnimateColor(losslessBrush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(losslessBrush, lossless ? AccentColor : PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ToggleLossless();
            };
            return pill;
        }

        // Round play/pause pill, same colors as Lossless; the spinner replaces the glyph while a proxy builds.
        private Border MakePlayPill()
        {
            var content = new Grid();
            content.Children.Add(playGlyph);
            content.Children.Add(playSpinner);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(14),
                Width = 32,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                Background = playBrush,
                Child = content,
                Cursor = Cursors.Hand,
                ToolTip = "Play / pause (Space)",
            };
            pill.MouseEnter += (_, _) =>
            {
                if (CanPlay) AnimateColor(playBrush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(playBrush, PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                TogglePlay();
            };
            return pill;
        }

        // The handles stay put; only the real start (ghost marker) and the save mode change.
        private void ToggleLossless()
        {
            if (!isVideo || busy || saved || failed || closing) return;
            lossless = !lossless;
            AnimateColor(losslessBrush, lossless ? AccentColor : PillIdle);
            UpdateTimeline();
        }

        // Without animation (test hook and preview).
        private void SetLossless(bool on)
        {
            lossless = on;
            losslessBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            losslessBrush.Color = on ? AccentColor : PillIdle;
            UpdateTimeline();
        }

        // Greatest keyframe at or before t, else 0.
        private double Snap(double t)
        {
            int lo = 0, hi = keys.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid] <= t + 1e-6)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return best >= 0 ? keys[best] : 0;
        }

        private double X(double t) => duration > 0 ? t / duration * barW : 0;

        private double T(double x) => Clamp(x / barW, 0, 1) * duration;

        // Handles sit inside the range: start's left edge on the start, end's right edge on the end.
        private double StartHandleLeft(double xs) => Clamp(xs, 0, barW - HandleW);

        private double EndHandleLeft(double xe) => Clamp(xe - HandleW, 0, barW - HandleW);

        private Hold HitTest(double x)
        {
            double xs = X(startTime), xe = X(endTime);
            double sl = StartHandleLeft(xs), el = EndHandleLeft(xe);
            bool onS = x >= sl - HandleTol && x <= sl + HandleW + HandleTol;
            bool onE = x >= el - HandleTol && x <= el + HandleW + HandleTol;
            if (onS && onE) return x >= (sl + el + HandleW) / 2 ? Hold.End : Hold.Start;
            if (onS) return Hold.Start;
            if (onE) return Hold.End;
            return x > xs && x < xe ? Hold.Body : Hold.None;
        }

        private void OnBarDown(object sender, MouseButtonEventArgs e)
        {
            if (!Ready || busy || saved || failed || closing) return;
            e.Handled = true;
            pressPt = e.GetPosition(timeline);
            double x = pressPt.X;
            Hold hit = HitTest(x);

            // Dragging on the empty timeline moves the nearest handle there; a plain click only seeks.
            pressOnHandle = hit == Hold.Start || hit == Hold.End;
            if (hit == Hold.None) hit = x < X(startTime) ? Hold.Start : Hold.End;

            hold = hit;
            dragging = false;
            pressT = T(x);
            pressS = startTime;
            pressE = endTime;
            // A grabbed handle starts from its own time, not the pointer's (the hit zone is wider
            // than the handle), so it never jumps on the first move.
            fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            anchorX = lastX = x;
            anchorT = lastT = !pressOnHandle ? pressT : hit == Hold.End ? endTime : startTime;
            if (pressOnHandle) activeEnd = hit == Hold.End;
            hoverLine.Visibility = Visibility.Collapsed;
            timeline.CaptureMouse();
            UpdateTimeline();
        }

        // Under DragSlop of movement with no handle grabbed, the release seeks the playhead to the press point.
        private void OnBarUp(object sender, MouseButtonEventArgs e)
        {
            if (hold == Hold.None) return;
            bool click = !dragging && !pressOnHandle;
            EndHold();
            if (click) Seek(T(pressPt.X));
        }

        private void OnBarMove(object sender, MouseEventArgs e)
        {
            if (!Ready)
            {
                timeline.Cursor = null;
                return;
            }
            double x = e.GetPosition(timeline).X;
            hoverT = T(x);
            if (hold != Hold.None)
            {
                if (!dragging)
                {
                    Vector moved = e.GetPosition(timeline) - pressPt;
                    if (moved.Length < DragSlop) return;
                    dragging = true;
                    PausePlay();
                    if (hold == Hold.Start) activeEnd = false;
                    else if (hold == Hold.End) activeEnd = true;
                }
                ApplyHold(x);
                return;
            }
            if (busy || saved || failed)
            {
                timeline.Cursor = null;
                hoverLine.Visibility = Visibility.Collapsed;
                return;
            }
            Canvas.SetLeft(hoverLine, Clamp(x, 0, barW) - 1);
            Canvas.SetTop(hoverLine, 0);
            hoverLine.Visibility = Visibility.Visible;
            Hold hit = HitTest(x);
            timeline.Cursor = hit == Hold.Start || hit == Hold.End ? Cursors.SizeWE
                : hit == Hold.Body ? Cursors.SizeAll : Cursors.Hand;
        }

        // Pointer time for a drag: the anchor time plus the mouse travel since the anchor, at full
        // speed or FineFactor while Shift is held. A Shift toggle re-anchors at the previous move.
        private double DragTime(double x)
        {
            bool nowFine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (nowFine != fine)
            {
                fine = nowFine;
                anchorX = lastX;
                anchorT = Clamp(lastT, 0, duration);
            }
            double speed = fine ? FineFactor : 1;
            double t = anchorT + (x - anchorX) * speed / barW * duration;
            lastX = x;
            lastT = t;
            return t;
        }

        // Handles follow the pointer continuously, quantized only to the frame grid.
        // A body drag keeps the range length exactly.
        private void ApplyHold(double x)
        {
            double t = DragTime(x);
            switch (hold)
            {
                case Hold.Start:
                    startTime = Clamp(Q(t), 0, endTime - MinLen);
                    ShowFrame(startTime);
                    break;
                case Hold.End:
                    endTime = Clamp(Q(t), startTime + MinLen, duration);
                    ShowFrame(endTime);
                    break;
                case Hold.Body:
                    double len = pressE - pressS;
                    double s = Clamp(Q(pressS + (t - pressT)), 0, duration - len);
                    startTime = s;
                    endTime = s + len;
                    ShowFrame(activeEnd ? endTime : startTime);
                    break;
            }
            UpdateTimeline();
        }

        // Drag end lands the last scrub position and hides the time bubble.
        private void EndHold()
        {
            if (hold == Hold.None) return;
            bool wasDragging = dragging;
            hold = Hold.None;
            dragging = false;
            if (timeline.IsMouseCaptured) timeline.ReleaseMouseCapture();
            if (wasDragging && scrubPending.HasValue) FlushScrub();
            UpdateTimeline();
        }

        private void UpdateTimeline()
        {
            if (!Ready) return;
            double cut = CutStart, xs = X(startTime), xe = X(endTime);
            bool ghost = Lossless && cut < startTime - FrameStep / 2;
            double xg = ghost ? X(cut) : xs;
            SetRect(dimLeft, 0, 0, xg, barH);
            SetRect(ghostFill, xg, 0, xs - xg, barH);
            Canvas.SetLeft(ghostLine, Clamp(xg - 1, 0, barW - 2));
            Canvas.SetTop(ghostLine, 0);
            ghostFill.Visibility = ghost ? Visibility.Visible : Visibility.Collapsed;
            ghostLine.Visibility = ghostFill.Visibility;
            SetRect(dimRight, xe, 0, barW - xe, barH);
            SetRect(selTop, xs, 0, xe - xs, 2);
            SetRect(selBottom, xs, barH - 2, xe - xs, 2);

            Canvas.SetLeft(startHandle, StartHandleLeft(xs));
            Canvas.SetLeft(endHandle, EndHandleLeft(xe));
            Canvas.SetTop(startHandle, 0);
            Canvas.SetTop(endHandle, 0);
            SetHandleLook(startHandle, startBrush, startGrip, !activeEnd);
            SetHandleLook(endHandle, endBrush, endGrip, activeEnd);

            readout.Text = $"{Fmt(startTime)}  to  {Fmt(endTime)}   ({(endTime - startTime).ToString("0.00", Inv)} s)";
            ghostText.Text = ghost ? $"Lossless starts at {Fmt(cut)} (keyframe)" : "";
            ghostText.Visibility = ghost ? Visibility.Visible : Visibility.Collapsed;
            UpdateBubble();
            UpdatePlayhead();
        }

        // Time bubble centered over the dragged handle (the active one for a body drag),
        // overhanging above the timeline and clamped inside its width.
        private void UpdateBubble()
        {
            if (!dragging || hold == Hold.None)
            {
                bubble.Visibility = Visibility.Collapsed;
                return;
            }
            bool end = hold == Hold.End || (hold == Hold.Body && activeEnd);
            double cx = end ? EndHandleLeft(X(endTime)) + HandleW / 2 : StartHandleLeft(X(startTime)) + HandleW / 2;
            bubbleText.Text = Fmt(end ? endTime : startTime);
            bubble.Visibility = Visibility.Visible;
            bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = bubble.DesiredSize;
            Canvas.SetLeft(bubble, Clamp(cx - size.Width / 2, 0, Math.Max(0, barW - size.Width)));
            Canvas.SetTop(bubble, -size.Height - 4);
        }

        private void UpdatePlayhead()
        {
            if (!Ready) return;
            timeText.Text = Fmt(playT);
            Visibility vis = playheadShown ? Visibility.Visible : Visibility.Collapsed;
            playLine.Visibility = vis;
            playCap.Visibility = vis;
            if (!playheadShown) return;
            double x = Clamp(X(playT), 0, barW);
            Canvas.SetLeft(playLine, Clamp(x - 1, 0, barW - 2));
            Canvas.SetTop(playLine, 0);
            Canvas.SetLeft(playCap, Clamp(x - 4, 0, barW - 8));
            Canvas.SetTop(playCap, 0);
        }

        // The active handle is brighter with a solid white grip and sits on top.
        private static void SetHandleLook(Border handle, SolidColorBrush brush, Rectangle grip, bool active)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = active ? AccentHover : AccentColor;
            grip.Opacity = active ? 1 : 0.55;
            Panel.SetZIndex(handle, active ? 2 : 1);
        }

        // Keyframe ticks (2x6) along the bottom edge; skipped for audio and very dense keyframes.
        private void UpdateTicks()
        {
            if (!isVideo || keys.Count == 0 || keys.Count > MaxTicks)
            {
                ticks.Data = null;
                return;
            }
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                foreach (double k in keys)
                {
                    double x = Clamp(X(k) - 1, 0, barW - 2), y = barH - 6;
                    ctx.BeginFigure(new Point(x, y), true, true);
                    ctx.LineTo(new Point(x + 2, y), false, false);
                    ctx.LineTo(new Point(x + 2, barH), false, false);
                    ctx.LineTo(new Point(x, barH), false, false);
                }
            }
            geometry.Freeze();
            ticks.Data = geometry;
        }

        private bool CanScrub => isVideo && mediaReady && !playFailed && !previewMode && !closing;

        // Shows the frame at t: scrubs the open, paused MediaElement when it can (a seek during
        // playback would move the playback), else an ffmpeg grab.
        private void ShowFrame(double t)
        {
            if (!CanScrub || playing)
            {
                RequestFrame(t);
                return;
            }
            previewT = t;
            ShowPlayer(true);
            scrubPending = t;
            if (Environment.TickCount64 - lastScrub >= ScrubMs) FlushScrub();
            else if (!scrubTimer.IsEnabled) scrubTimer.Start();
        }

        private void FlushScrub()
        {
            scrubTimer.Stop();
            double? pending = scrubPending;
            scrubPending = null;
            if (!pending.HasValue || !CanScrub || playing) return;
            double t = Clamp(pending.Value, 0, duration);
            lastScrub = Environment.TickCount64;
            player.Position = TimeSpan.FromSeconds(t);
        }

        private void RequestFrame(double t)
        {
            previewT = t;
            if (!playing) ShowPlayer(false);
            if (!isVideo || previewMode) return;
            wantedT = t;
            if (!grabbing) PumpFrames();
        }

        // One grab in flight; when it lands, grab again if the wanted time moved meanwhile.
        private async void PumpFrames()
        {
            grabbing = true;
            try
            {
                while (wantedT.HasValue && !closing)
                {
                    double t = Clamp(wantedT.Value, 0, Math.Max(0, duration - 0.05));
                    wantedT = null;
                    if (Math.Abs(t - shownT) < 0.001) continue;
                    string src = path, dir = tempDir;
                    byte[]? bytes = await Task.Run(() => GrabFrame(src, dir, t));
                    if (closing) break;
                    BitmapSource? img = Decode(bytes);
                    if (img == null) continue;
                    previewImage.Source = img;
                    shownT = t;
                }
            }
            finally
            {
                grabbing = false;
                if (closedDone) Cleanup();
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (failed && !busy)
            {
                e.Handled = true;
                CloseAnimated();
                return;
            }
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : FrameStep;
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    CloseAnimated();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    Save();
                    break;
                case Key.Space:
                    e.Handled = true;
                    TogglePlay();
                    break;
                case Key.Left:
                    e.Handled = true;
                    Nudge(-step);
                    break;
                case Key.Right:
                    e.Handled = true;
                    Nudge(step);
                    break;
                case Key.Tab:
                    e.Handled = true;
                    SetActive(!activeEnd);
                    break;
                case Key.S:
                    e.Handled = true;
                    SetActive(false);
                    break;
                case Key.E:
                    e.Handled = true;
                    SetActive(true);
                    break;
                case Key.OemOpenBrackets:
                    e.Handled = true;
                    SetStartAt(hoverT ?? previewT);
                    break;
                case Key.OemCloseBrackets:
                    e.Handled = true;
                    SetEndAt(hoverT ?? previewT);
                    break;
            }
        }

        private bool CanEdit => Ready && !busy && !saved && !failed && !closing && hold == Hold.None;

        private void SetActive(bool end)
        {
            if (!CanEdit) return;
            activeEnd = end;
            UpdateTimeline();
            ShowFrame(activeEnd ? endTime : startTime);
        }

        // Moves the active handle by delta (one frame, or 1 s with Shift), on the frame grid.
        private void Nudge(double delta)
        {
            if (!CanEdit) return;
            if (activeEnd) endTime = Clamp(Q(endTime + delta), startTime + MinLen, duration);
            else startTime = Clamp(Q(startTime + delta), 0, endTime - MinLen);
            UpdateTimeline();
            ShowFrame(activeEnd ? endTime : startTime);
        }

        private void SetStartAt(double t)
        {
            if (!CanEdit) return;
            startTime = Clamp(Q(t), 0, endTime - MinLen);
            activeEnd = false;
            UpdateTimeline();
            ShowFrame(startTime);
        }

        private void SetEndAt(double t)
        {
            if (!CanEdit) return;
            endTime = Clamp(Q(t), startTime + MinLen, duration);
            activeEnd = true;
            UpdateTimeline();
            ShowFrame(endTime);
        }

        private bool CanPlay => Ready && mediaReady && !playFailed && !busy && !saved && !failed && !closing;

        // Pause() makes the Manual element open the media now; MediaOpened or MediaFailed follows.
        private void OpenPlayer(string file)
        {
            if (previewMode || closing || closedDone) return;
            try
            {
                player.Source = new Uri(file);
                player.Pause();
            }
            catch (Exception ex)
            {
                Log.Write($"trim play: open failed: {ex.Message}");
                playFailed = true;
                UpdatePlayUi();
            }
        }

        private void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            if (closing || closedDone) return;
            // Some containers open with audio only instead of failing: treat that as a failure too.
            if (info != null && info.HasVideo && !player.HasVideo && !proxyTried)
            {
                Log.Write("trim play: opened without video");
                BuildProxy();
                return;
            }
            mediaReady = true;
            Log.Write("trim play: opened");
            if (isVideo && !playing)
            {
                // Play then Pause renders the first frame so a seek can show it (ScrubbingEnabled).
                player.Play();
                player.Pause();
                player.Position = TimeSpan.FromSeconds(playT);
            }
            UpdatePlayUi();
            if (!autoplay) return;
            playT = CutStart;
            StartPlay();
        }

        // Windows cannot decode it (some mkv/webm/flv): one proxy attempt, then play is disabled.
        private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            if (closing || closedDone) return;
            Log.Write($"trim play: media failed: {e.ErrorException?.Message}");
            playTimer.Stop();
            playing = false;
            mediaReady = false;
            ShowPlayer(false);
            if (proxyTried) playFailed = true;
            else BuildProxy();
            UpdatePlayUi();
        }

        // h264/aac proxy in the temp dir; no -ss, so its timestamps map 1:1 onto the source.
        private async void BuildProxy()
        {
            proxyTried = true;
            proxyBuilding = true;
            UpdatePlayUi();
            string src = path, dir = tempDir;
            bool video = isVideo;
            try
            {
                string proxy = await Task.Run(() => MakeProxy(src, dir, video));
                if (closing || closedDone) return;
                Log.Write("trim play: proxy");
                player.Source = new Uri(proxy);
                player.Pause();
            }
            catch (Exception ex)
            {
                Log.Write($"trim play: proxy failed: {ex.Message}");
                playFailed = true;
            }
            finally
            {
                proxyBuilding = false;
                UpdatePlayUi();
                if (closedDone) Cleanup();
            }
        }

        private static string MakeProxy(string src, string dir, bool video)
        {
            string ffmpeg = Tools.Ffmpeg ?? throw new InvalidOperationException("ffmpeg not found.");
            System.IO.Directory.CreateDirectory(dir);
            string file = IOPath.Combine(dir, video ? "proxy.mp4" : "proxy.m4a");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", src };
            if (video)
            {
                // Even width (x264 with yuv420p needs it), at most 960.
                args.AddRange(new[]
                {
                    "-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "26",
                    "-vf", "scale='trunc(min(960,iw)/2)*2':-2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k",
                });
            }
            else args.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", "192k" });
            args.AddRange(new[] { "-movflags", "+faststart", file });
            Tools.RunProcess(ffmpeg, args, ProxyTimeout);
            if (!System.IO.File.Exists(file) || new System.IO.FileInfo(file).Length == 0)
                throw new InvalidOperationException("ffmpeg wrote no proxy.");
            return file;
        }

        private void TogglePlay()
        {
            if (playing) PausePlay();
            else StartPlay();
        }

        // Starts at the playhead, or at the real cut start when the playhead is outside the range.
        private void StartPlay()
        {
            if (!CanPlay || playing || hold != Hold.None) return;
            scrubTimer.Stop();
            scrubPending = null;
            double s = CutStart;
            if (playT < s - 1e-6 || playT >= endTime - EndSlack) playT = s;
            player.Position = TimeSpan.FromSeconds(playT);
            player.Play();
            playing = true;
            playheadShown = true;
            ShowPlayer(true);
            playTimer.Start();
            UpdatePlayhead();
            UpdatePlayUi();
        }

        private void PausePlay()
        {
            if (!playing || previewMode) return;
            playing = false;
            playTimer.Stop();
            player.Pause();
            playT = Clamp(player.Position.TotalSeconds, 0, duration);
            UpdatePlayhead();
            UpdatePlayUi();
        }

        private void OnPlayTick()
        {
            if (!playing) return;
            double t = player.Position.TotalSeconds;
            if (t >= endTime)
            {
                ReachEnd(t);
                return;
            }
            playT = t;
            UpdatePlayhead();
        }

        // Pauses at the end handle and rewinds to the start, so Space replays the kept range.
        private async void ReachEnd(double t)
        {
            playing = false;
            playTimer.Stop();
            player.Pause();
            playT = CutStart;
            player.Position = TimeSpan.FromSeconds(playT);
            UpdatePlayhead();
            UpdatePlayUi();
            Log.Write($"trim play: reached end at {t.ToString("0.###", Inv)}");
            if (!autoplay) return;
            autoplay = false;
            await Task.Delay(500);
            CloseAnimated();
        }

        // Click seek: the playhead moves there; while paused the player shows that frame.
        private void Seek(double t)
        {
            if (!Ready || busy || saved || failed || closing) return;
            playT = Clamp(t, 0, duration);
            playheadShown = true;
            UpdatePlayhead();
            if (mediaReady)
            {
                player.Position = TimeSpan.FromSeconds(playT);
                ShowPlayer(true);
            }
            else RequestFrame(playT);
        }

        // Video only: the player over the frame image, or the image (ffmpeg grabs) over the player.
        private void ShowPlayer(bool on)
        {
            if (!isVideo) return;
            bool video = on && mediaReady;
            player.Opacity = video ? 1 : 0;
            previewImage.Opacity = video ? 0 : 1;
        }

        private void UpdatePlayUi()
        {
            bool wait = !playFailed && (proxyBuilding || (proxyTried && !mediaReady));
            if (wait != playSpinning)
            {
                if (wait) StartSpinner(playSpinner, playSpinnerRotate);
                else StopSpinner(playSpinner, playSpinnerRotate);
                playSpinning = wait;
            }
            playGlyph.Visibility = wait ? Visibility.Hidden : Visibility.Visible;
            playGlyph.Text = playing ? "" : "";
            playButton.Opacity = previewMode || wait || CanPlay ? 1 : 0.4;
            playButton.ToolTip = playFailed ? "Playback is not supported for this file."
                : wait ? "Preparing playback..." : "Play / pause (Space)";
        }

        private async void Save()
        {
            if (!Ready || busy || saved || failed || closing) return;
            EndHold();
            // Accurate re-encodes at the handles; Lossless copies from the keyframe (audio always copies).
            double s = CutStart, e = endTime;
            bool cutExact = isVideo && !lossless;

            PausePlay();
            busy = true;
            timeline.Cursor = null;
            hoverLine.Visibility = Visibility.Collapsed;
            SetSaving(true);
            UpdatePlayUi();

            string file = path;
            try
            {
                string outPath = await Converter.RunSta(() => MediaBackend.Trim(file, s, e, cutExact));
                busy = false;
                saved = true;
                SetSaving(false);
                if (hiddenWhileBusy)
                {
                    Close();
                    return;
                }
                ShowStatus("", OkColor, "Saved", IOPath.GetFileName(outPath));
                await Task.Delay(700);
                CloseAnimated();
            }
            catch (Exception ex)
            {
                busy = false;
                failed = true;
                ExitCode = 1;
                SetSaving(false);
                Log.Write($"Trim failed: {path}: {ex}");
                if (hiddenWhileBusy)
                {
                    Close();
                    return;
                }
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
            }
        }

        private void SetSaving(bool on)
        {
            saveGlyph.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            saveText.Text = on ? "Saving..." : "Save";
            if (on) StartSpinner(saveSpinner, saveSpinnerRotate);
            else StopSpinner(saveSpinner, saveSpinnerRotate);
        }

        private void ShowStatus(string glyph, Color color, string titleText, string subText)
        {
            statusGlyph.Text = glyph;
            statusGlyph.Foreground = Frozen(color);
            statusTitle.Text = titleText;
            statusSub.Text = subText;
            status.Visibility = Visibility.Visible;
        }

        private void HideWhileBusy()
        {
            hiddenWhileBusy = true;
            if (IsVisible) Hide();
        }

        private void CloseAnimated()
        {
            if (closing) return;
            if (busy)
            {
                HideWhileBusy();
                return;
            }
            closing = true;
            if (!IsVisible)
            {
                Close();
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            DoubleAnimation fade = Anim(0, 120, ease);
            fade.Completed += (_, _) => Close();
            root.BeginAnimation(OpacityProperty, fade);
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.94, 120, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.94, 120, ease));
        }

        // Releases the media (it locks the proxy file); must run before the temp dir is deleted.
        private void ClosePlayer()
        {
            playTimer.Stop();
            scrubTimer.Stop();
            playing = false;
            try
            {
                player.Stop();
                player.Close();
                player.Source = null;
            }
            catch (Exception ex)
            {
                Log.Write($"trim play: close failed: {ex.Message}");
            }
        }

        // Temp strip/frame/proxy files; retried when a worker finishes after the window closed.
        private void Cleanup()
        {
            if (loading || grabbing || proxyBuilding) return;
            try
            {
                if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }

        // Centers the window on the cursor's monitor work area, in physical pixels.
        private void CenterOnMonitor()
        {
            if (hwnd == IntPtr.Zero || !haveMonitor) return;
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                w = frame.DesiredSize.Width;
                h = frame.DesiredSize.Height;
            }
            int pxW = (int)Math.Round(w * scale), pxH = (int)Math.Round(h * scale);
            int x = Math.Max(work.Left, work.Left + (work.Right - work.Left - pxW) / 2);
            int y = Math.Max(work.Top, work.Top + (work.Bottom - work.Top - pxH) / 2);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Dev only (--trim-preview): never shown; loads synchronously, sized for a 1920x1080 work
        // area at 100% and rendered to PNG.
        internal void RenderPreview(string outPath, string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            try
            {
                SetLayout(1920, 1080);
                Apply(LoadWork(path, tempDir));

                root.BeginAnimation(OpacityProperty, null);
                root.Opacity = 1;
                rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                rootScale.ScaleX = 1;
                rootScale.ScaleY = 1;

                switch (state)
                {
                    case "idle":
                        break;
                    case "select":
                        // Accurate mode: the cut starts exactly at the start handle.
                        SetRange(0.20 * duration, 0.65 * duration);
                        if (isVideo)
                        {
                            BitmapSource? img = Decode(GrabFrame(path, tempDir, startTime));
                            if (img != null) previewImage.Source = img;
                        }
                        Canvas.SetLeft(hoverLine, X(0.45 * duration) - 1);
                        Canvas.SetTop(hoverLine, 0);
                        hoverLine.Visibility = Visibility.Visible;
                        break;
                    case "lossless":
                        // Same range with Lossless on (ghost marker and label), shown mid-drag of the
                        // start handle so the time bubble is visible too.
                        if (isVideo) SetLossless(true);
                        SetRange(0.20 * duration, 0.65 * duration);
                        if (isVideo)
                        {
                            BitmapSource? img = Decode(GrabFrame(path, tempDir, startTime));
                            if (img != null) previewImage.Source = img;
                        }
                        hold = Hold.Start;
                        dragging = true;
                        UpdateTimeline();
                        break;
                    case "playing":
                        // No Source in preview: only the playing look (pause glyph, playhead, time).
                        SetRange(0.20 * duration, 0.65 * duration);
                        playT = startTime + 0.40 * (endTime - startTime);
                        if (isVideo)
                        {
                            BitmapSource? img = Decode(GrabFrame(path, tempDir, playT));
                            if (img != null) previewImage.Source = img;
                        }
                        playing = true;
                        playheadShown = true;
                        UpdatePlayhead();
                        UpdatePlayUi();
                        break;
                    case "done":
                        saved = true;
                        ShowStatus("", OkColor, "Saved",
                            IOPath.GetFileNameWithoutExtension(path) + "_trim" + IOPath.GetExtension(path));
                        break;
                    case "error":
                        failed = true;
                        ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage("Sample failure message for preview."));
                        break;
                    default:
                        throw new ArgumentException("Unknown state: " + state);
                }

                frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Size size = frame.DesiredSize;
                frame.Arrange(new Rect(size));
                frame.UpdateLayout();

                var background = new DrawingVisual();
                using (DrawingContext dc = background.RenderOpen())
                {
                    dc.DrawRectangle(Frozen(Color.FromArgb(0xFF, 0x5A, 0x60, 0x70)), null, new Rect(size));
                }
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2),
                    192, 192, PixelFormats.Pbgra32);
                bmp.Render(background);
                bmp.Render(frame);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = System.IO.File.Create(outPath);
                encoder.Save(fs);
            }
            finally
            {
                Cleanup();
            }
        }

        // m:ss.ff (h:mm:ss.ff from 1 h), rounded to centiseconds first so 59.996 reads 1:00.00.
        private static string Fmt(double t)
        {
            long cs = (long)Math.Round(Math.Max(0, t) * 100);
            long h = cs / 360000, m = cs / 6000 % 60, s = cs / 100 % 60, f = cs % 100;
            return h > 0
                ? string.Format(Inv, "{0}:{1:00}:{2:00}.{3:00}", h, m, s, f)
                : string.Format(Inv, "{0}:{1:00}.{2:00}", m, s, f);
        }

        private static string Secs(double s) => s.ToString("0.######", Inv);

        private static void SetRect(Rectangle r, double x, double y, double w, double h)
        {
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, h);
        }
    }
}
