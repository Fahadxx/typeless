using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // Compact resize panel on the cursor's monitor: a row of resolution pills and a row of frame rate
    // pills, only values below the source (MediaBackend.VideoInfo). Enter re-encodes via
    // MediaBackend.Resize into <stem>_720p.<ext> (_30fps, _720p30) with progress and an ETA; Esc cancels.
    public sealed class ResizeWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, ContentW = 490;
        private const double CloseDelaySeconds = 2.5;

        private static readonly Brush AccentBrush = Frozen(AccentColor);
        private static readonly Brush OkBrush = Frozen(OkColor);
        private static readonly Brush FailBrush = Frozen(FailColor);

        // One pill row: the values (null = Original), their pills, and the selected index.
        private sealed class Row
        {
            public readonly List<Border> Pills = new List<Border>();
            public readonly List<SolidColorBrush> Fills = new List<SolidColorBrush>();
            public WrapPanel Panel = null!;
            public int Selected;
        }

        private readonly string path;
        private readonly bool previewMode;
        private readonly long before;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock dimsText;
        private readonly StackPanel rowsPanel;
        private readonly StackPanel readout;
        private readonly Row heightRow = new Row();
        private readonly Row fpsRow = new Row();
        private readonly TextBlock bigText;
        private readonly TextBlock bigSub;
        private readonly Canvas loadSpinner;
        private readonly RotateTransform loadSpinnerRotate;
        private readonly Grid bar;
        private readonly ScaleTransform barScale;
        private readonly Border status;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock statusTitle;
        private readonly TextBlock statusSub;
        private readonly Border save;
        private readonly TextBlock saveGlyph;
        private readonly TextBlock saveText;
        private readonly Canvas saveSpinner;
        private readonly RotateTransform saveSpinnerRotate;

        private IntPtr hwnd;
        private bool haveMonitor;
        private NativeMethods.RECT work;
        private double scale = 1.0;

        private MediaBackend.ResizeInfo? info;
        private readonly List<int?> heights = new List<int?>();
        private readonly List<string?> rates = new List<string?>();

        private double progress;
        private string eta = "";

        // cancelRequested: Esc or a close while encoding; the window closes once ffmpeg has stopped.
        private bool loading, busy, saved, failed, closing, cancelRequested;

        // Smoothed encode speed (fraction of the duration per second).
        private DateTime etaLastAt, etaStartedAt;
        private double etaLastDone = -1;
        private double etaRate;

        // 1 until a resize is saved (Esc, cancel, nothing to offer and failures all leave nothing).
        public int ExitCode { get; private set; } = 1;

        public ResizeWindow(string path) : this(path, false)
        {
        }

        // Preview (dev): RenderPreview probes synchronously and fakes the states; nothing is encoded or shown.
        internal ResizeWindow(string path, bool preview)
        {
            this.path = path;
            previewMode = preview;
            try
            {
                before = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                // VideoInfo reports the real error.
                Log.Write($"Resize size read failed: {path}: {ex.Message}");
            }

            Title = "Typeless Resize";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name, displayed size and frame rate under it, file size right.
            var nameText = new TextBlock
            {
                Text = IOPath.GetFileName(path),
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ContentW - 110,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            dimsText = new TextBlock
            {
                Text = " ",
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var nameStack = new StackPanel { Orientation = Orientation.Vertical };
            nameStack.Children.Add(nameText);
            nameStack.Children.Add(dimsText);
            var sizeText = new TextBlock
            {
                Text = PdfEdit.HumanSize(before),
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var header = new DockPanel { Margin = new Thickness(2, 0, 2, 12), LastChildFill = true };
            DockPanel.SetDock(sizeText, Dock.Right);
            header.Children.Add(sizeText);
            header.Children.Add(nameStack);

            // Pill rows, filled once the info loads.
            rowsPanel = new StackPanel { Orientation = Orientation.Vertical };
            rowsPanel.Children.Add(MakeRowBlock("Resolution", heightRow));
            rowsPanel.Children.Add(MakeRowBlock("Frame rate", fpsRow));

            // Readout: "1920×1080 30 fps → 1280×720 24 fps", a status line and a thin progress bar under it.
            loadSpinnerRotate = new RotateTransform(0, 8, 8);
            loadSpinner = MakeSpinner(16, 6.5, 2, AccentBrush, loadSpinnerRotate);
            loadSpinner.Margin = new Thickness(0, 0, 8, 0);
            bigText = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 18,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var bigRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            bigRow.Children.Add(loadSpinner);
            bigRow.Children.Add(bigText);
            bigSub = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                Text = " ",
                MaxWidth = ContentW - 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            };
            barScale = new ScaleTransform(0, 1);
            bar = new Grid { Height = 3, Margin = new Thickness(40, 8, 40, 0), Visibility = Visibility.Hidden };
            bar.Children.Add(new Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            });
            bar.Children.Add(new Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = AccentBrush,
                RenderTransform = barScale,
                RenderTransformOrigin = new Point(0, 0.5),
            });
            readout = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 6) };
            readout.Children.Add(bigRow);
            readout.Children.Add(bigSub);
            readout.Children.Add(bar);

            // Toolbar: key hint left, Cancel and Save right.
            var keyHint = new TextBlock
            {
                Text = "Enter to save, Esc to cancel",
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
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
                Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), CancelOrClose, () => cancelRequested);

            saveGlyph = new TextBlock
            {
                Text = RadialWindow.OkGlyph,
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
            save = MakeButton(saveContent, AccentColor, AccentHover, null, Save, () => !CanSave);
            save.Margin = new Thickness(8, 0, 0, 0);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(cancel);
            right.Children.Add(save);
            var toolbar = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            toolbar.Children.Add(right);
            toolbar.Children.Add(keyHint);

            var stack = new StackPanel { Orientation = Orientation.Vertical, Width = ContentW };
            stack.Children.Add(header);
            stack.Children.Add(rowsPanel);
            stack.Children.Add(readout);
            stack.Children.Add(toolbar);

            status = MakeStatus(out statusGlyph, out statusTitle, out statusSub);
            var content = new Grid();
            content.Children.Add(stack);
            content.Children.Add(status);

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
            // A close while encoding cancels the encode first; the window closes once ffmpeg has stopped.
            Closing += (_, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                RequestCancel();
            };
            Closed += (_, _) =>
            {
                closing = true;
                cts.Cancel();
            };

            UpdateAll();
        }

        private bool HasChange => heightRow.Selected > 0 || fpsRow.Selected > 0;

        private bool CanEdit => info != null && !busy && !saved && !failed && !closing;

        private bool CanSave => CanEdit && HasChange;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            if (info == null && !loading) LoadInfo();
        }

        // One ffprobe call on a worker; spinner meanwhile.
        private async void LoadInfo()
        {
            loading = true;
            UpdateAll();
            try
            {
                string src = path;
                MediaBackend.ResizeInfo loaded = await Task.Run(() => MediaBackend.VideoInfo(src));
                loading = false;
                if (closing) return;
                SetInfo(loaded);
            }
            catch (Exception ex)
            {
                loading = false;
                if (closing) return;
                failed = true;
                Log.Write($"Resize info failed: {path}: {ex}");
                UpdateAll();
                ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                return;
            }
            RunAutoResize();
        }

        // Builds both pill rows: Original first, then the smaller values on offer.
        private void SetInfo(MediaBackend.ResizeInfo loaded)
        {
            info = loaded;
            heights.Clear();
            heights.Add(null);
            heights.AddRange(loaded.HeightOptions.Select(h => (int?)h));
            rates.Clear();
            rates.Add(null);
            rates.AddRange(loaded.FpsOptions.Select(f => (string?)f));
            FillRow(heightRow, heights.Select(h => h == null ? "Original" : h.Value.ToString(CultureInfo.InvariantCulture) + "p"));
            FillRow(fpsRow, rates.Select(f => f == null ? "Original" : f + " fps"));
            dimsText.Text = $"{loaded.Width}×{loaded.Height} · {FpsText(loaded.Fps)}";
            if (loaded.HeightOptions.Count == 0 && loaded.FpsOptions.Count == 0)
                Log.Write($"Resize: {path} is already at the smallest size");
            UpdateAll();
        }

        // Test hook: TYPELESS_AUTORESIZE=<height|orig>[,<fps|orig>] selects those pills and saves, the
        // same call as --resize <file> <height|orig> [fps|orig]. Unknown values or no change: rc 1, nothing saved.
        private void RunAutoResize()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTORESIZE");
            if (string.IsNullOrWhiteSpace(auto)) return;
            string[] parts = auto.Split(',');
            string h = parts[0].Trim().ToLowerInvariant();
            string f = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "orig";
            int hi = h == "orig" ? 0 : heights.FindIndex(v => v != null && v.Value.ToString(CultureInfo.InvariantCulture) == h);
            int fi = f == "orig" ? 0 : rates.FindIndex(v => v != null && v == f);
            if (hi < 0 || fi < 0)
            {
                Log.Write($"TYPELESS_AUTORESIZE: unknown value {auto}, nothing saved");
                CloseAnimated();
                return;
            }
            Select(heightRow, hi);
            Select(fpsRow, fi);
            if (!HasChange)
            {
                Log.Write("TYPELESS_AUTORESIZE: no change, nothing saved");
                CloseAnimated();
                return;
            }
            Save();
        }

        // "Resolution" or "Frame rate" over its pill row.
        private static UIElement MakeRowBlock(string label, Row row)
        {
            var title = new TextBlock
            {
                Text = label,
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                Margin = new Thickness(2, 0, 0, 6),
            };
            row.Panel = new WrapPanel { Orientation = Orientation.Horizontal, MinHeight = 24 };
            var block = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 12) };
            block.Children.Add(title);
            block.Children.Add(row.Panel);
            return block;
        }

        private void FillRow(Row row, IEnumerable<string> labels)
        {
            row.Panel.Children.Clear();
            row.Pills.Clear();
            row.Fills.Clear();
            row.Selected = 0;
            foreach (string label in labels) row.Panel.Children.Add(MakePill(row, row.Pills.Count, label));
        }

        // Pill like the crop window's ratio pills: accent when selected.
        private Border MakePill(Row row, int index, string label)
        {
            var brush = new SolidColorBrush(index == row.Selected ? AccentColor : PillIdle);
            var text = new TextBlock
            {
                Text = label,
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
                Margin = new Thickness(0, 0, 6, 6),
                Background = brush,
                Child = text,
                Cursor = Cursors.Hand,
            };
            pill.MouseEnter += (_, _) =>
            {
                if (CanEdit && index != row.Selected) AnimateColor(brush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(brush, index == row.Selected ? AccentColor : PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Select(row, index);
            };
            row.Pills.Add(pill);
            row.Fills.Add(brush);
            return pill;
        }

        private void Select(Row row, int index)
        {
            if (!CanEdit || index < 0 || index >= row.Pills.Count) return;
            row.Selected = index;
            UpdateAll();
        }

        // Whole fps as "30 fps", else two decimals ("29.97 fps").
        private static string FpsText(double fps)
        {
            if (fps <= 0) return "? fps";
            double r = Math.Round(fps);
            return Math.Abs(fps - r) < 0.01
                ? r.ToString("0", CultureInfo.InvariantCulture) + " fps"
                : fps.ToString("0.00", CultureInfo.InvariantCulture) + " fps";
        }

        // Output size the way MediaBackend.Resize computes it: short side to the tier, long side keeps
        // the aspect ratio, both rounded to even, orientation kept.
        private static (int W, int H) TargetSize(MediaBackend.ResizeInfo src, int height)
        {
            static int Even(double x) => Math.Max(2, (int)(Math.Round(x / 2.0, MidpointRounding.AwayFromZero) * 2));
            int shortOut = Even(height);
            int longOut = Even((double)Math.Max(src.Width, src.Height) * shortOut / src.ShortSide);
            return src.Width >= src.Height ? (longOut, shortOut) : (shortOut, longOut);
        }

        private int? SelectedHeight => heightRow.Selected < heights.Count ? heights[heightRow.Selected] : null;

        private string? SelectedFps => fpsRow.Selected < rates.Count ? rates[fpsRow.Selected] : null;

        private void UpdateAll()
        {
            UpdateRow(heightRow);
            UpdateRow(fpsRow);
            rowsPanel.Opacity = CanEdit || saved ? 1 : 0.5;
            UpdateReadout();
            UpdateSave();
        }

        private static void UpdateRow(Row row)
        {
            for (int i = 0; i < row.Fills.Count; i++)
            {
                SolidColorBrush b = row.Fills[i];
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = i == row.Selected ? AccentColor : PillIdle;
            }
        }

        private void UpdateReadout()
        {
            bool spin = loading;
            if (spin && loadSpinner.Visibility != Visibility.Visible) StartSpinner(loadSpinner, loadSpinnerRotate);
            else if (!spin && loadSpinner.Visibility == Visibility.Visible) StopSpinner(loadSpinner, loadSpinnerRotate);
            bar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            barScale.ScaleX = Clamp(progress, 0, 1);

            MediaBackend.ResizeInfo? i = info;
            if (i == null)
            {
                bigText.Text = loading ? "Reading video info" : failed ? "Failed" : " ";
                bigText.Foreground = failed ? FailBrush : SubBrush;
                bigSub.Text = " ";
                return;
            }

            string from = $"{i.Width}×{i.Height} {FpsText(i.Fps)}";
            int? h = SelectedHeight;
            string? f = SelectedFps;
            (int w, int hh) = h is int target ? TargetSize(i, target) : (i.Width, i.Height);
            string to = $"{w}×{hh} {(f != null ? f + " fps" : FpsText(i.Fps))}";
            bigText.Text = $"{from}  →  {to}";
            bigText.Foreground = HasChange ? Brushes.White : SubBrush;
            bigText.FontSize = 18;

            if (busy)
            {
                bigSub.Text = cancelRequested
                    ? "Cancelling..."
                    : $"Encoding {(int)Math.Round(Clamp(progress, 0, 1) * 100)}%{eta}";
                bigSub.Foreground = Brushes.White;
            }
            else if (i.HeightOptions.Count == 0 && i.FpsOptions.Count == 0)
            {
                bigSub.Text = "Already at the smallest size";
                bigSub.Foreground = SubBrush;
            }
            else
            {
                bigSub.Text = HasChange ? "Re-encodes the video, audio is kept as is" : "No change";
                bigSub.Foreground = SubBrush;
            }
        }

        private void UpdateSave()
        {
            save.Opacity = busy || CanSave ? 1 : 0.4;
            save.Cursor = CanSave ? Cursors.Hand : null;
            saveGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            saveText.Text = busy ? "Encoding..." : "Save";
            if (busy && saveSpinner.Visibility != Visibility.Visible) StartSpinner(saveSpinner, saveSpinnerRotate);
            else if (!busy && saveSpinner.Visibility == Visibility.Visible) StopSpinner(saveSpinner, saveSpinnerRotate);
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
                    CancelOrClose();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    Save();
                    break;
            }
        }

        private void CancelOrClose()
        {
            if (busy) RequestCancel();
            else CloseAnimated();
        }

        // Kills ffmpeg (MediaBackend.Resize throws OperationCanceledException); Save closes the window after.
        private void RequestCancel()
        {
            if (!busy || cancelRequested) return;
            cancelRequested = true;
            cts.Cancel();
            UpdateAll();
        }

        private async void Save()
        {
            if (!CanSave) return;
            busy = true;
            progress = 0;
            eta = "";
            etaLastDone = -1;
            UpdateAll();

            string file = path;
            int? h = SelectedHeight;
            string? f = SelectedFps;
            CancellationToken ct = cts.Token;
            // Progress fires on a pool thread; the UI only takes the latest value.
            Action<double> report = p => Dispatcher.BeginInvoke(new Action(() => OnProgress(p)));
            try
            {
                string outPath = await Task.Run(() => MediaBackend.Resize(file, h, f, report, ct));
                busy = false;
                saved = true;
                ExitCode = 0;
                long after = 0;
                try { after = new FileInfo(outPath).Length; } catch { }
                string change = SizeChange(after);
                Log.Write($"Resize saved: {outPath} ({change})");
                UpdateAll();
                bigSub.Text = change;
                bigSub.Foreground = OkBrush;
                if (cancelRequested || !IsVisible)
                {
                    Close();
                    return;
                }
                ShowStatus(RadialWindow.OkGlyph, OkColor, "Saved", IOPath.GetFileName(outPath) + "\n" + change);
                await Task.Delay(TimeSpan.FromSeconds(CloseDelaySeconds));
                CloseAnimated();
            }
            catch (OperationCanceledException)
            {
                busy = false;
                Log.Write($"Resize cancelled: {path}");
                UpdateAll();
                CloseAnimated();
            }
            catch (Exception ex)
            {
                busy = false;
                failed = true;
                Log.Write($"Resize failed: {path} ({h?.ToString(CultureInfo.InvariantCulture) ?? "orig"}, {f ?? "orig"}): {ex}");
                UpdateAll();
                if (cancelRequested || !IsVisible)
                {
                    Close();
                    return;
                }
                ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
            }
        }

        private void OnProgress(double p)
        {
            if (!busy) return;
            progress = Clamp(p, 0, 1);
            eta = EtaText(progress);
            UpdateReadout();
        }

        // " · 42s left", or "" until there is a steady speed (first 2 s, or after a fallback attempt restarts).
        // Exponential smoothing on the encoded fraction.
        private string EtaText(double done)
        {
            DateTime now = DateTime.UtcNow;
            if (etaLastDone < 0 || done < etaLastDone)
            {
                etaLastDone = done;
                etaLastAt = etaStartedAt = now;
                etaRate = 0;
                return "";
            }
            double dt = (now - etaLastAt).TotalSeconds;
            if (dt >= 0.25)
            {
                double rate = (done - etaLastDone) / dt;
                etaRate = etaRate <= 0 ? rate : etaRate * 0.8 + rate * 0.2;
                etaLastDone = done;
                etaLastAt = now;
            }
            if ((now - etaStartedAt).TotalSeconds < 2 || etaRate <= 0 || done >= 1) return "";
            return " · " + FormatEta((1 - done) / etaRate) + " left";
        }

        // 42s, 3m 05s, 1h 02m.
        private static string FormatEta(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Ceiling(Math.Min(seconds, 359999)));
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
            return $"{t.Seconds}s";
        }

        // "12.4 MB -> 5.1 MB (-59%)", like --resize prints it.
        private string SizeChange(long after)
        {
            int pct = before > 0 ? (int)Math.Round((after - before) * 100.0 / before) : 0;
            return $"{PdfEdit.HumanSize(before)} -> {PdfEdit.HumanSize(after)} ({(pct > 0 ? "+" : "-")}{Math.Abs(pct)}%)";
        }

        private void ShowStatus(string glyph, Color color, string titleText, string subText)
        {
            statusGlyph.Text = glyph;
            statusGlyph.Foreground = Frozen(color);
            statusTitle.Text = titleText;
            statusSub.Text = subText;
            status.Visibility = Visibility.Visible;
            // Hidden, not collapsed: the panel keeps its size and the status sits on a clear plate.
            rowsPanel.Visibility = Visibility.Hidden;
            readout.Visibility = Visibility.Hidden;
        }

        private void CloseAnimated()
        {
            if (closing) return;
            if (busy)
            {
                RequestCancel();
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

        // Dev only (--resize-preview): never shown and nothing is encoded. Probes synchronously, then
        // "changed" selects the first height and first fps on offer, "running" fakes 42% with an ETA,
        // "done" and "error" fake the end states.
        internal void RenderPreview(string outPath, string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            SetInfo(MediaBackend.VideoInfo(path));

            root.BeginAnimation(OpacityProperty, null);
            root.Opacity = 1;
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rootScale.ScaleX = 1;
            rootScale.ScaleY = 1;

            void Changed()
            {
                if (heights.Count > 1) heightRow.Selected = 1;
                if (rates.Count > 1) fpsRow.Selected = 1;
            }

            // Output name as Resize would write it (no collision check in preview).
            string PreviewName()
            {
                int? h = SelectedHeight;
                string? f = SelectedFps;
                string suffix = h != null ? h.Value.ToString(CultureInfo.InvariantCulture) + "p" : "";
                if (f != null) suffix = suffix.Length > 0 ? suffix + f : f + "fps";
                return IOPath.GetFileNameWithoutExtension(path) + "_" + suffix + IOPath.GetExtension(path);
            }

            switch (state)
            {
                case "idle":
                    break;
                case "changed":
                    Changed();
                    break;
                case "running":
                    Changed();
                    busy = true;
                    progress = 0.42;
                    eta = " · " + FormatEta(65) + " left";
                    break;
                case "done":
                    Changed();
                    saved = true;
                    UpdateAll();
                    string change = SizeChange((long)(before * 0.41));
                    bigSub.Text = change;
                    bigSub.Foreground = OkBrush;
                    ShowStatus(RadialWindow.OkGlyph, OkColor, "Saved", (HasChange ? PreviewName() : IOPath.GetFileName(path)) + "\n" + change);
                    break;
                case "error":
                    Changed();
                    failed = true;
                    ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage("Sample failure message for preview."));
                    break;
                default:
                    throw new ArgumentException("Unknown state: " + state);
            }
            if (state != "done") UpdateAll();

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
            using var fs = File.Create(outPath);
            encoder.Save(fs);
        }
    }
}
