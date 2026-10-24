using System;
using System.Collections.Generic;
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
    // Compact compress panel on the cursor's monitor: three level cards that compress in the
    // background (one at a time, each into its own temp file) and show the real size saved;
    // Enter moves the selected result to <stem>_compressed.pdf.
    public sealed class CompressWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, ContentW = 490, CardGap = 8;
        private const double CompactShare = 0.05;

        private static readonly Color CardIdle = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF);
        private static readonly Color CardHover = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        private static readonly Brush AccentBrush = Frozen(AccentColor);
        private static readonly Brush OkBrush = Frozen(OkColor);
        private static readonly Brush FailBrush = Frozen(FailColor);

        private enum State { Waiting, Running, Done, Compact, Failed }

        // One level card. Temp is its own file in the window temp dir; Size is valid once Done or Compact.
        private sealed class Level
        {
            public string Name = "", Label = "", Hint = "";
            public State State;
            public double Progress;
            public long Size;
            public string Temp = "";
            public string Error = "";
            public Border Root = null!;
            public SolidColorBrush Fill = null!;
            public FrameworkElement Body = null!;
            public TextBlock StateText = null!;
            public Grid Bar = null!;
            public ScaleTransform BarScale = null!;
        }

        private readonly string path;
        private readonly bool previewMode;
        private readonly long before;
        private readonly string tempDir = "";
        private readonly List<Level> levels = new List<Level>();
        private readonly List<Level> queue = new List<Level>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock bigText;
        private readonly TextBlock pctText;
        private readonly TextBlock bigSub;
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

        private Level selected;
        private Task<long>? running;
        private bool pumping, autoMode;

        // saveRequested: Save was pressed before the selected level finished; it saves on completion.
        private bool saveRequested, busy, saved, failed, closing, hiddenWhileBusy;

        public int ExitCode { get; private set; }

        public CompressWindow(string path) : this(path, false)
        {
        }

        // Preview (dev): the card states are set by RenderPreview, nothing is compressed or shown.
        internal CompressWindow(string path, bool preview)
        {
            this.path = path;
            previewMode = preview;
            try
            {
                before = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                // Compress itself reports the real error on the first level.
                Log.Write($"Compress size read failed: {path}: {ex.Message}");
            }
            if (!preview) tempDir = PdfEdit.NewWorkDir();

            Title = "Typeless Compress";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name left, original size right.
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
                VerticalAlignment = VerticalAlignment.Center,
            };
            var sizeText = new TextBlock
            {
                Text = PdfEdit.HumanSize(before),
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new DockPanel { Margin = new Thickness(2, 0, 2, 12), LastChildFill = true };
            DockPanel.SetDock(sizeText, Dock.Right);
            header.Children.Add(sizeText);
            header.Children.Add(nameText);

            // Cards left to right; the queue order is separate (balanced, strong, light).
            var cards = new Grid();
            string[] labels = { "Light", "Balanced", "Strong" };
            string[] hints = { "Best quality", "Recommended", "Smallest" };
            for (int i = 0; i < PdfEdit.CompressLevels.Length; i++)
            {
                cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Level lv = MakeLevel(PdfEdit.CompressLevels[i], labels[i], hints[i], i + 1);
                lv.Root.Margin = new Thickness(i == 0 ? 0 : CardGap / 2, 0, i == labels.Length - 1 ? 0 : CardGap / 2, 0);
                Grid.SetColumn(lv.Root, i);
                cards.Children.Add(lv.Root);
                levels.Add(lv);
            }
            selected = levels[1];
            queue.Add(levels[1]);
            queue.Add(levels[2]);
            queue.Add(levels[0]);

            // Big readout for the selected card: "12.4 MB to 3.1 MB  -75%", a status line under it.
            bigText = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 22,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pctText = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 22,
                Foreground = OkBrush,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bigRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            bigRow.Children.Add(bigText);
            bigRow.Children.Add(pctText);
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
            var readout = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 18, 0, 8) };
            readout.Children.Add(bigRow);
            readout.Children.Add(bigSub);

            // Toolbar: key hint left, Cancel and Save right.
            var keyHint = new TextBlock
            {
                Text = "1 2 3 to choose",
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
            save = MakeButton(saveContent, AccentColor, AccentHover, null, Save, () => busy || !CanSaveSelected);
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
            stack.Children.Add(cards);
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
            Closing += (_, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                HideWhileBusy();
            };
            Closed += (_, _) =>
            {
                // Cancel the running level, give it a moment to stop and delete its temp file, then the dir.
                closing = true;
                cts.Cancel();
                Task<long>? r = running;
                if (r != null)
                {
                    try { r.Wait(1500); } catch { }
                }
                Cleanup();
            };

            UpdateAll();
        }

        private bool CanEdit => !busy && !saved && !failed && !closing && !saveRequested;

        private bool CanSaveSelected => selected.State != State.Compact && selected.State != State.Failed;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            // Before the pump starts, so an auto-selected level runs first.
            RunAutoCompress();
            Pump();
        }

        // Test hook: TYPELESS_AUTOCOMPRESS=light|balanced|strong selects that level and saves when it is ready.
        private void RunAutoCompress()
        {
            string? name = Environment.GetEnvironmentVariable("TYPELESS_AUTOCOMPRESS");
            if (string.IsNullOrWhiteSpace(name)) return;
            Level? lv = levels.FirstOrDefault(l => l.Name == name.Trim().ToLowerInvariant());
            if (lv == null)
            {
                Log.Write($"TYPELESS_AUTOCOMPRESS: unknown level {name}");
                return;
            }
            autoMode = true;
            Select(lv);
            Save();
        }

        // One level at a time from the front of the queue, on a worker. Progress<T> is created on
        // the UI thread, so its reports land here too.
        private async void Pump()
        {
            if (pumping || previewMode) return;
            pumping = true;
            try
            {
                while (!closing && !cts.IsCancellationRequested && queue.Count > 0)
                {
                    Level lv = queue[0];
                    queue.RemoveAt(0);
                    lv.State = State.Running;
                    lv.Progress = 0;
                    UpdateAll();

                    var progress = new Progress<double>(p =>
                    {
                        if (lv.State != State.Running || closing) return;
                        lv.Progress = p;
                        UpdateAll();
                    });
                    string src = path, name = lv.Name, temp = lv.Temp;
                    CancellationToken ct = cts.Token;
                    try
                    {
                        running = Task.Run(() => PdfEdit.Compress(src, name, temp, progress, ct));
                        long after = await running;
                        lv.Size = after;
                        lv.State = before - after < before * CompactShare ? State.Compact : State.Done;
                        if (lv.State == State.Compact)
                        {
                            try { File.Delete(temp); } catch { }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lv.State = State.Waiting;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lv.State = State.Failed;
                        lv.Error = ex.Message;
                        Log.Write($"Compress {name} failed: {path}: {ex}");
                    }
                    finally
                    {
                        running = null;
                    }
                    if (closing) break;
                    UpdateAll();
                    if (saveRequested && lv == selected) FinishPending(lv);
                }
            }
            finally
            {
                pumping = false;
                if (closing) Cleanup();
            }
        }

        // The level Save was waiting on has finished: save it, or explain why not.
        private void FinishPending(Level lv)
        {
            saveRequested = false;
            if (lv.State == State.Done)
            {
                SaveDone(lv);
                return;
            }
            if (lv.State == State.Failed)
            {
                failed = true;
                ExitCode = 1;
                UpdateAll();
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(lv.Error));
                return;
            }
            UpdateAll();
            if (!autoMode) return;
            Log.Write($"TYPELESS_AUTOCOMPRESS: {lv.Name} is already compact, nothing saved");
            ExitCode = 1;
            CloseAnimated();
        }

        // Card: label and key number, hint, live state, and a thin accent progress bar.
        private Level MakeLevel(string name, string label, string hint, int key)
        {
            var lv = new Level { Name = name, Label = label, Hint = hint };
            if (!previewMode) lv.Temp = IOPath.Combine(tempDir, name + ".pdf");

            var labelText = new TextBlock
            {
                Text = label,
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var keyText = new TextBlock
            {
                Text = key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var top = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(keyText, Dock.Right);
            top.Children.Add(keyText);
            top.Children.Add(labelText);
            var hintText = new TextBlock
            {
                Text = hint,
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                Margin = new Thickness(0, 2, 0, 0),
            };
            lv.StateText = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 14, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            lv.BarScale = new ScaleTransform(0, 1);
            lv.Bar = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Hidden };
            lv.Bar.Children.Add(new Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            });
            lv.Bar.Children.Add(new Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = AccentBrush,
                RenderTransform = lv.BarScale,
                RenderTransformOrigin = new Point(0, 0.5),
            });

            var body = new StackPanel { Orientation = Orientation.Vertical };
            body.Children.Add(top);
            body.Children.Add(hintText);
            body.Children.Add(lv.StateText);
            body.Children.Add(lv.Bar);
            lv.Body = body;

            lv.Fill = new SolidColorBrush(CardIdle);
            lv.Root = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = lv.Fill,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 10, 12, 10),
                Child = body,
                Cursor = Cursors.Hand,
            };
            lv.Root.MouseEnter += (_, _) =>
            {
                if (CanEdit) AnimateColor(lv.Fill, CardHover);
            };
            lv.Root.MouseLeave += (_, _) => AnimateColor(lv.Fill, CardIdle);
            lv.Root.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Select(lv);
            };
            return lv;
        }

        // A level that has not started moves to the front of the queue; the running one keeps going.
        private void Select(Level lv)
        {
            if (!CanEdit) return;
            selected = lv;
            if (lv.State == State.Waiting && queue.Remove(lv)) queue.Insert(0, lv);
            UpdateAll();
        }

        private int SavedPercent(long size) => before <= 0 ? 0 : (int)Math.Round((before - size) * 100.0 / before);

        private static int ProgressPercent(Level lv) => (int)Math.Round(Clamp(lv.Progress, 0, 1) * 100);

        private void UpdateAll()
        {
            foreach (Level lv in levels) UpdateLevel(lv);
            UpdateReadout();
            UpdateSave();
        }

        private void UpdateLevel(Level lv)
        {
            lv.Root.BorderBrush = lv == selected ? AccentBrush : Brushes.Transparent;
            lv.Body.Opacity = lv.State == State.Compact ? 0.5 : 1;
            lv.Bar.Visibility = lv.State == State.Running ? Visibility.Visible : Visibility.Hidden;
            lv.BarScale.ScaleX = Clamp(lv.Progress, 0, 1);
            switch (lv.State)
            {
                case State.Waiting:
                    lv.StateText.Text = "Waiting";
                    lv.StateText.Foreground = SubBrush;
                    break;
                case State.Running:
                    lv.StateText.Text = $"Compressing {ProgressPercent(lv)}%";
                    lv.StateText.Foreground = Brushes.White;
                    break;
                case State.Done:
                    lv.StateText.Text = $"{PdfEdit.HumanSize(lv.Size)}  -{SavedPercent(lv.Size)}%";
                    lv.StateText.Foreground = OkBrush;
                    break;
                case State.Compact:
                    lv.StateText.Text = "Already compact";
                    lv.StateText.Foreground = SubBrush;
                    break;
                case State.Failed:
                    lv.StateText.Text = "Failed";
                    lv.StateText.Foreground = FailBrush;
                    break;
            }
        }

        private void UpdateReadout()
        {
            Level lv = selected;
            pctText.Visibility = lv.State == State.Done ? Visibility.Visible : Visibility.Collapsed;
            bigSub.Text = " ";
            switch (lv.State)
            {
                case State.Waiting:
                    bigText.Text = "Waiting";
                    bigText.Foreground = SubBrush;
                    break;
                case State.Running:
                    bigText.Text = $"Compressing {ProgressPercent(lv)}%";
                    bigText.Foreground = Brushes.White;
                    break;
                case State.Done:
                    bigText.Text = $"{PdfEdit.HumanSize(before)} to {PdfEdit.HumanSize(lv.Size)}";
                    bigText.Foreground = Brushes.White;
                    pctText.Text = $"-{SavedPercent(lv.Size)}%";
                    break;
                case State.Compact:
                    bigText.Text = "Already compact";
                    bigText.Foreground = SubBrush;
                    bigSub.Text = "This level saves less than 5%";
                    break;
                case State.Failed:
                    bigText.Text = "Failed";
                    bigText.Foreground = FailBrush;
                    bigSub.Text = RadialWindow.ShortMessage(lv.Error);
                    break;
            }
        }

        // Save shows the selected level's progress while it waits on it, and dims when it cannot save.
        private void UpdateSave()
        {
            bool spin = busy || saveRequested;
            save.Opacity = spin || CanSaveSelected ? 1 : 0.4;
            save.Cursor = spin || !CanSaveSelected ? null : Cursors.Hand;
            saveGlyph.Visibility = spin ? Visibility.Collapsed : Visibility.Visible;
            if (busy) saveText.Text = "Saving...";
            else if (saveRequested)
                saveText.Text = selected.State == State.Running ? $"Compressing {ProgressPercent(selected)}%" : "Waiting...";
            else saveText.Text = "Save";
            if (spin && saveSpinner.Visibility != Visibility.Visible) StartSpinner(saveSpinner, saveSpinnerRotate);
            else if (!spin && saveSpinner.Visibility == Visibility.Visible) StopSpinner(saveSpinner, saveSpinnerRotate);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (failed && !busy)
            {
                e.Handled = true;
                CloseAnimated();
                return;
            }
            int index = levels.IndexOf(selected);
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
                case Key.D1:
                case Key.NumPad1:
                    e.Handled = true;
                    Select(levels[0]);
                    break;
                case Key.D2:
                case Key.NumPad2:
                    e.Handled = true;
                    Select(levels[1]);
                    break;
                case Key.D3:
                case Key.NumPad3:
                    e.Handled = true;
                    Select(levels[2]);
                    break;
                case Key.Left:
                case Key.Right:
                    e.Handled = true;
                    Select(levels[Math.Clamp(index + (e.Key == Key.Left ? -1 : 1), 0, levels.Count - 1)]);
                    break;
            }
        }

        // Done: the temp file moves now. Waiting or running: Save waits on that level (moved to the
        // front of the queue) and saves when it finishes.
        private void Save()
        {
            if (!CanEdit || !CanSaveSelected) return;
            Level lv = selected;
            if (lv.State == State.Done)
            {
                SaveDone(lv);
                return;
            }
            saveRequested = true;
            if (lv.State == State.Waiting && queue.Remove(lv)) queue.Insert(0, lv);
            UpdateAll();
            Pump();
        }

        // The other levels are no longer needed, so the queue is cancelled before the move.
        private async void SaveDone(Level lv)
        {
            busy = true;
            cts.Cancel();
            UpdateAll();

            string file = path, temp = lv.Temp;
            try
            {
                string outPath = await Task.Run(() => PdfEdit.SaveCompressed(file, temp));
                busy = false;
                saved = true;
                UpdateAll();
                Log.Write($"Compress saved: {outPath} ({lv.Name}, {PdfEdit.HumanSize(before)} -> {PdfEdit.HumanSize(lv.Size)})");
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
                UpdateAll();
                Log.Write($"Compress save failed: {path}: {ex}");
                if (hiddenWhileBusy)
                {
                    Close();
                    return;
                }
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
            }
        }

        private void ShowStatus(string glyph, Color color, string titleText, string subText)
        {
            statusGlyph.Text = glyph;
            statusGlyph.Foreground = Frozen(color);
            statusTitle.Text = titleText;
            statusSub.Text = subText;
            status.Visibility = Visibility.Visible;
        }

        // Temp level files; retried when the pump finishes after the window closed.
        private void Cleanup()
        {
            if (tempDir.Length == 0) return;
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch { }
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
            cts.Cancel();
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

        // Dev only (--compress-preview): never shown and nothing is compressed. "ready" and "done"
        // use plausible sizes derived from the file size (light 62%, balanced 31%, strong 18%).
        internal void RenderPreview(string outPath, string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            root.BeginAnimation(OpacityProperty, null);
            root.Opacity = 1;
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rootScale.ScaleX = 1;
            rootScale.ScaleY = 1;

            void Ready()
            {
                double[] share = { 0.62, 0.31, 0.18 };
                for (int i = 0; i < levels.Count; i++)
                {
                    levels[i].State = State.Done;
                    levels[i].Size = (long)(before * share[i]);
                }
            }

            switch (state)
            {
                case "idle":
                    break;
                case "running":
                    levels[1].State = State.Running;
                    levels[1].Progress = 0.42;
                    break;
                case "ready":
                    Ready();
                    break;
                case "done":
                    Ready();
                    saved = true;
                    ShowStatus("", OkColor, "Saved",
                        IOPath.GetFileNameWithoutExtension(path) + "_compressed" + IOPath.GetExtension(path));
                    break;
                case "error":
                    levels[1].State = State.Running;
                    levels[1].Progress = 0.42;
                    failed = true;
                    ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage("Sample failure message for preview."));
                    break;
                default:
                    throw new ArgumentException("Unknown state: " + state);
            }
            UpdateAll();

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
