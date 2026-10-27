using System;
using System.Threading;
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
using IOPath = System.IO.Path;
using static Typeless.Theme;

namespace Typeless
{
    // Compact rotate/flip panel on the cursor's monitor: a square preview (image, or a video frame at
    // 10%) that animates each step; Enter saves <stem>_rotated.<ext> via ImageBackend/MediaBackend.Transform.
    public sealed class RotateWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, StageShare = 0.55, MinStageSide = 240;
        private const double PlaceholderSide = 320, ContentMinW = 520;
        private const int StepMs = 180;
        private const string RotateGlyph = ""; // Rotate (clockwise arrow), as in the pages window

        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(20);

        private readonly string path;
        private readonly bool isVideo;
        private readonly bool previewMode;
        private readonly BitmapSource? preloaded;
        private readonly string tempDir;
        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock nameText;
        private readonly TextBlock dimsText;
        private readonly Grid stage;
        private readonly Image image;
        private readonly RotateTransform viewRotate;
        private readonly ScaleTransform viewScale;
        private readonly Canvas spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly Border status;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock statusTitle;
        private readonly TextBlock statusSub;
        private readonly TextBlock readout;
        private readonly Border save;
        private readonly TextBlock saveGlyph;
        private readonly TextBlock saveText;
        private readonly Canvas saveSpinner;
        private readonly RotateTransform saveSpinnerRotate;

        private IntPtr hwnd;
        private bool haveMonitor;
        private NativeMethods.RECT work;
        private double scale = 1.0;
        private double maxSide = 594;

        private BitmapSource? bitmap;
        private int pw, ph;

        // Canonical pair, RotateSpec convention: mirror horizontally first (hflip), then turn deg clockwise.
        private int deg;
        private bool hflip;

        // What the preview shows, in view space: turn by angle (accumulated, never wrapped), then scale
        // by (sx, sy). Kept apart from the canonical pair so each step animates as the user sees it.
        private double angle;
        private int sx = 1, sy = 1;

        private bool loading, busy, saved, failed, closing, closedDone, hiddenWhileBusy;

        public int ExitCode { get; private set; }

        public RotateWindow(string path) : this(path, null, false)
        {
        }

        // Preview (dev): nothing is shown or saved. Images come decoded; a video frame is grabbed in RenderPreview.
        internal RotateWindow(string path, BitmapSource? preloaded, bool preview)
        {
            this.path = path;
            this.preloaded = preloaded;
            previewMode = preview;
            isVideo = Formats.KindOf(path) == Kind.Video;
            tempDir = IOPath.Combine(IOPath.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));

            Title = "Typeless Rotate";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name left, result size right (images only).
            nameText = new TextBlock
            {
                Text = IOPath.GetFileName(path),
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ContentMinW - 140,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dimsText = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
            DockPanel.SetDock(dimsText, Dock.Right);
            header.Children.Add(dimsText);
            header.Children.Add(nameText);

            // Stage: a square, so a quarter turn still fits. Rotate first, then scale, both about the center.
            viewRotate = new RotateTransform(0);
            viewScale = new ScaleTransform(1, 1);
            var view = new TransformGroup();
            view.Children.Add(viewRotate);
            view.Children.Add(viewScale);
            image = new Image
            {
                Stretch = Stretch.Fill,
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = view,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            spinnerRotate = new RotateTransform(0, 18, 18);
            spinner = MakeSpinner(36, 15, 3, Frozen(AccentColor), spinnerRotate);

            status = MakeStatus(out statusGlyph, out statusTitle, out statusSub);

            stage = new Grid
            {
                Width = PlaceholderSide,
                Height = PlaceholderSide,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
                ClipToBounds = true,
            };
            stage.Children.Add(image);
            stage.Children.Add(spinner);
            stage.Children.Add(status);

            // Toolbar: the four tool pills and the readout left; Cancel and Save right.
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(MakeTool(GlyphIcon(true), "Rotate left ([ or Left)", RotateLeft));
            tools.Children.Add(MakeTool(GlyphIcon(false), "Rotate right (] or Right)", RotateRight));
            tools.Children.Add(MakeTool(FlipIcon(false), "Flip horizontal (H)", FlipH));
            tools.Children.Add(MakeTool(FlipIcon(true), "Flip vertical (V)", FlipV));

            readout = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 12, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

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
                Text = "", // CheckMark
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
            save = MakeButton(saveContent, AccentColor, AccentHover, null, Save, () => busy || IsIdentity);
            save.Margin = new Thickness(8, 0, 0, 0);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(cancel);
            right.Children.Add(save);
            var toolbar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(tools, Dock.Left);
            toolbar.Children.Add(right);
            toolbar.Children.Add(tools);
            toolbar.Children.Add(readout);

            var content = new StackPanel { Orientation = Orientation.Vertical, MinWidth = ContentMinW };
            content.Children.Add(header);
            content.Children.Add(stage);
            content.Children.Add(toolbar);

            // The shadow sits on a sibling plate so the effect does not re-render the image.
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

            SourceInitialized += (_, _) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                haveMonitor = NativeMethods.TryGetCursorMonitor(out _, out work, out scale);
                if (haveMonitor) SetLimits((work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
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
                Cleanup();
            };

            UpdateUi();
        }

        private bool IsIdentity => deg == 0 && !hflip;

        private bool CanEdit => bitmap != null && !busy && !saved && !failed && !closing;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            if (bitmap == null && !loading) LoadPreview();
        }

        // Decodes the image on the STA worker, or grabs the video frame on a worker; spinner meanwhile.
        private async void LoadPreview()
        {
            loading = true;
            StartSpinner(spinner, spinnerRotate);
            try
            {
                string src = path, dir = tempDir;
                BitmapSource loaded = preloaded
                    ?? (isVideo
                        ? await Task.Run(() => LoadVideoFrame(src, dir))
                        : await Converter.RunSta(() => ImageBackend.LoadForCrop(src)));
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                SetImage(loaded);
            }
            catch (Exception ex)
            {
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                failed = true;
                ExitCode = 1;
                Log.Write($"Rotate load failed: {path}: {ex}");
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                return;
            }
            finally
            {
                loading = false;
                if (closedDone) Cleanup();
            }
            RunAutoRotate();
        }

        // Test hook: TYPELESS_AUTOROTATE=<deg>[,h][,v] presses the buttons and saves: flips first (h, v,
        // at identity, so view and source flips agree), then deg clockwise in quarter turns (negative =
        // left). Same result as --rotate <file> <deg> [h] [v]. Identity logs and closes with rc 1.
        private void RunAutoRotate()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOROTATE");
            if (string.IsNullOrWhiteSpace(auto)) return;
            string[] parts = auto.Split(',');
            if (!int.TryParse(parts[0].Trim(), out int turn) || turn % 90 != 0)
            {
                Log.Write($"TYPELESS_AUTOROTATE: bad value {auto}");
                return;
            }
            bool h = false, v = false;
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                if (p == "h") h = true;
                else if (p == "v") v = true;
            }
            if (h) FlipH();
            if (v) FlipV();
            for (int i = 0; i < Math.Abs(turn / 90) % 4; i++)
            {
                if (turn > 0) RotateRight();
                else RotateLeft();
            }
            if (IsIdentity)
            {
                Log.Write("TYPELESS_AUTOROTATE: no change, nothing saved");
                ExitCode = 1;
                CloseAnimated();
                return;
            }
            Save();
        }

        // Runs on a worker: one ffmpeg frame at 10% of the duration (the first frame when ffprobe is
        // missing or fails). ffmpeg autorotates, so the frame looks as players show it.
        private static BitmapSource LoadVideoFrame(string src, string dir)
        {
            string ffmpeg = Tools.Ffmpeg
                ?? throw new InvalidOperationException(
                    "ffmpeg not found. Install it (for example: winget install Gyan.FFmpeg) and try again.");
            double t = 0;
            try
            {
                t = MediaBackend.Duration(src) * 0.1;
            }
            catch (Exception ex)
            {
                Log.Write($"Rotate duration probe failed, using the first frame: {ex.Message}");
            }
            System.IO.Directory.CreateDirectory(dir);
            string file = IOPath.Combine(dir, "frame.png");
            void Grab(double at) => Tools.RunProcess(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-ss", at.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture), "-i", src,
                "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-an", "-update", "1", file,
            }, FrameTimeout);
            try
            {
                Grab(t);
            }
            catch (Exception ex) when (t > 0)
            {
                Log.Write($"Rotate frame grab at {t:0.###} s failed, trying the first frame: {ex.Message}");
            }
            // A grab past the last decodable frame can succeed with no file: fall back to the first frame.
            if (!System.IO.File.Exists(file) && t > 0) Grab(0);
            if (!System.IO.File.Exists(file)) throw new InvalidOperationException("Could not read a video frame.");
            byte[] bytes = System.IO.File.ReadAllBytes(file);
            try { System.IO.File.Delete(file); } catch { }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new System.IO.MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // Height share, capped by the width so a portrait monitor still fits the panel.
        private void SetLimits(double workW, double workH)
        {
            maxSide = Math.Max(MinStageSide, Math.Min(workH * StageShare, workW * 0.85 - 2 * (Pad + ShadowMargin)));
        }

        // Fits the longer side into the square stage, so every quarter turn fits without resizing.
        private void SetImage(BitmapSource loaded)
        {
            bitmap = loaded;
            pw = loaded.PixelWidth;
            ph = loaded.PixelHeight;

            // Tiny images (icons) are enlarged to a usable size.
            double longest = Math.Max(pw, ph);
            double k = Math.Min(maxSide / longest, Math.Max(1.0, MinStageSide / longest));
            image.Width = pw * k;
            image.Height = ph * k;
            image.Source = loaded;
            image.Visibility = Visibility.Visible;
            RenderOptions.SetBitmapScalingMode(image, k > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            double side = Math.Max(MinStageSide, longest * k);
            stage.Width = side;
            stage.Height = side;
            nameText.MaxWidth = Math.Max(ContentMinW, side) - 140;
            UpdateUi();
        }

        // Each button is a step applied to what the user sees now. The canonical pair C = R(deg) H^hflip
        // (H first) becomes step * C; the view (angle, sx, sy) shows S(sx, sy) R(angle).
        //   Rotate right: R(90) C, so deg += 90. In the view, a mirror reverses a turn (H R(t) = R(-t) H),
        //     so angle moves by +90 when sx * sy = 1 and -90 when the view is mirrored once.
        //   Flip horizontal: H R(deg) H^h = R(-deg) H^(h+1), so deg = -deg and hflip toggles; view sx = -sx.
        //   Flip vertical: V = R(180) H, so V R(deg) H^h = R(180 - deg) H^(h+1): deg = 180 - deg, hflip
        //     toggles; view sy = -sy.
        // Example, Right then H: (90, no flip) becomes (270, hflip). An arrow pointing up turns right, then
        // the mirror points it left. Canonical: the mirror keeps it up, 270 clockwise points it left.
        private void RotateRight() => Turn(90);

        private void RotateLeft() => Turn(-90);

        private void Turn(int by)
        {
            if (!CanEdit) return;
            deg = RotateSpec.Mod360(deg + by);
            angle += sx * sy * by;
            Step();
        }

        private void FlipH()
        {
            if (!CanEdit) return;
            deg = RotateSpec.Mod360(-deg);
            hflip = !hflip;
            sx = -sx;
            Step();
        }

        private void FlipV()
        {
            if (!CanEdit) return;
            deg = RotateSpec.Mod360(180 - deg);
            hflip = !hflip;
            sy = -sy;
            Step();
        }

        // To-only animations, so a quick second press continues from wherever the first one is.
        private void Step()
        {
            if (previewMode)
            {
                viewRotate.Angle = angle;
                viewScale.ScaleX = sx;
                viewScale.ScaleY = sy;
            }
            else
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                viewRotate.BeginAnimation(RotateTransform.AngleProperty, Anim(angle, StepMs, ease));
                viewScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(sx, StepMs, ease));
                viewScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(sy, StepMs, ease));
            }
            UpdateUi();
        }

        // Read as "turn, then mirror": with a mirror, C = R(deg) H = H R(-deg), so the turn shown is -deg.
        private string Describe()
        {
            if (!hflip)
            {
                return deg switch
                {
                    90 => "Rotated 90° right",
                    180 => "Rotated 180°",
                    270 => "Rotated 90° left",
                    _ => "No change",
                };
            }
            return RotateSpec.Mod360(-deg) switch
            {
                90 => "Rotated 90° right, mirrored",
                180 => "Flipped vertically",
                270 => "Rotated 90° left, mirrored",
                _ => "Mirrored",
            };
        }

        private void UpdateUi()
        {
            readout.Text = Describe();
            readout.Foreground = IsIdentity ? SubBrush : Brushes.White;
            bool quarter = deg % 180 == 90;
            dimsText.Text = bitmap != null && !isVideo ? $"{(quarter ? ph : pw)} × {(quarter ? pw : ph)} px" : " ";
            save.Opacity = busy || !IsIdentity ? 1 : 0.4;
            save.Cursor = busy || IsIdentity ? null : Cursors.Hand;
        }

        // Tool pill: icon only, hover like the crop ratio pills, tooltip names the key.
        private Border MakeTool(UIElement icon, string tip, Action click)
        {
            var brush = new SolidColorBrush(PillIdle);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Width = 38,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                Background = brush,
                Child = icon,
                Cursor = Cursors.Hand,
                ToolTip = tip,
            };
            pill.MouseEnter += (_, _) =>
            {
                if (CanEdit) AnimateColor(brush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(brush, PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            return pill;
        }

        // Rotate (U+E7AD) for right; mirrored for left.
        private static UIElement GlyphIcon(bool left) => new TextBlock
        {
            Text = RotateGlyph,
            FontFamily = IconFont,
            FontSize = 14,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new ScaleTransform(left ? -1 : 1, 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        // Drawn flip icon: a filled and an outlined triangle mirrored across a dashed axis; vertical is the
        // same icon turned a quarter.
        private static UIElement FlipIcon(bool vertical)
        {
            var box = new Canvas
            {
                Width = 16,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(vertical ? 90 : 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            box.Children.Add(new Path
            {
                Data = Geometry.Parse("M 6.5,2.5 L 6.5,13.5 L 1,13.5 Z"),
                Fill = Brushes.White,
            });
            box.Children.Add(new Path
            {
                Data = Geometry.Parse("M 9.5,2.5 L 9.5,13.5 L 15,13.5 Z"),
                Stroke = Brushes.White,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
            });
            box.Children.Add(new Line
            {
                X1 = 8,
                Y1 = 0.5,
                X2 = 8,
                Y2 = 15.5,
                Stroke = SubBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 1.5, 1.5 },
            });
            return box;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (failed && !busy)
            {
                e.Handled = true;
                CloseAnimated();
                return;
            }
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
                case Key.OemOpenBrackets:
                case Key.Left:
                    e.Handled = true;
                    RotateLeft();
                    break;
                case Key.OemCloseBrackets:
                case Key.Right:
                    e.Handled = true;
                    RotateRight();
                    break;
                case Key.H:
                    e.Handled = true;
                    FlipH();
                    break;
                case Key.V:
                    e.Handled = true;
                    FlipV();
                    break;
            }
        }

        // The lossless display matrix path only exists for mp4/mov/m4v and needs ffprobe (MediaBackend.Transform).
        private bool LosslessVideo => Formats.Norm(path) is "mp4" or "mov" or "m4v" && Tools.Ffprobe != null;

        private async void Save()
        {
            if (!CanEdit || IsIdentity) return;
            busy = true;
            SetSaving(true);

            string file = path;
            int d = deg;
            bool h = hflip;
            saveCts = new CancellationTokenSource();
            CancellationToken ct = saveCts.Token;
            try
            {
                string outPath = isVideo
                    ? await Task.Run(() => MediaBackend.Transform(file, d, h, false, ct))
                    : await Converter.RunSta(() => ImageBackend.Transform(file, d, h, false));
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancel or Esc while saving: ffmpeg was killed and nothing is saved.
                busy = false;
                ExitCode = 1;
                Log.Write($"Rotate cancelled: {path}");
                Close();
            }
            catch (Exception ex)
            {
                busy = false;
                failed = true;
                ExitCode = 1;
                SetSaving(false);
                Log.Write($"Rotate failed: {path} ({d}{(h ? " hflip" : "")}): {ex}");
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
            saveText.Text = !on ? "Save" : isVideo && !LosslessVideo ? "Re-encoding..." : "Saving...";
            if (on) StartSpinner(saveSpinner, saveSpinnerRotate);
            else StopSpinner(saveSpinner, saveSpinnerRotate);
            UpdateUi();
        }

        private void ShowStatus(string glyph, Color color, string titleText, string subText)
        {
            statusGlyph.Text = glyph;
            statusGlyph.Foreground = Frozen(color);
            statusTitle.Text = titleText;
            statusSub.Text = subText;
            status.Visibility = Visibility.Visible;
        }

        // The grabbed video frame; retried when the loader finishes after the window closed.
        private void Cleanup()
        {
            if (loading) return;
            try
            {
                if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }

        // Closing while a save runs cancels it (the ffmpeg encode is killed); the window closes once it stops.
        private CancellationTokenSource? saveCts;

        private void HideWhileBusy()
        {
            hiddenWhileBusy = true;
            if (IsVisible) Hide();
            try { saveCts?.Cancel(); } catch { }
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

        // Dev only (--rotate-preview): never shown; loads synchronously (a video frame via ffmpeg), sized
        // for a 1920x1080 work area at 100% and rendered to PNG. "changed" presses Right, then H.
        internal void RenderPreview(string outPath, string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            try
            {
                SetLimits(1920, 1080);
                SetImage(preloaded ?? LoadVideoFrame(path, tempDir));

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
                    case "changed":
                        RotateRight();
                        FlipH();
                        break;
                    case "done":
                        RotateRight();
                        saved = true;
                        ShowStatus("", OkColor, "Saved",
                            IOPath.GetFileNameWithoutExtension(path) + "_rotated" + IOPath.GetExtension(path));
                        break;
                    case "error":
                        RotateRight();
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
    }
}
