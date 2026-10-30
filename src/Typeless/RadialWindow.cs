using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Typeless
{
    // Borderless donut at the cursor: one slice per target format, status in the center.
    // More than 9 targets page: 8 plus MORE, then the rest plus BACK.
    public sealed class RadialWindow : Window
    {
        private const double Side = 400, C = 200, RO = 165, RI = 72;
        private const double Disc = 2 * (RI - 6);

        private static readonly Color IdleColor = Color.FromArgb(0xD8, 0x1C, 0x1D, 0x22);
        private static readonly Color AccentColor = Color.FromArgb(0xF0, 0x6C, 0x8C, 0xFF);
        private static readonly Color OkColor = Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80);
        private static readonly Color FailColor = Color.FromArgb(0xFF, 0xF8, 0x71, 0x71);
        private static readonly Brush SliceStroke = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        private static readonly Brush GlyphBrush = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SubBrush = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        // Internal so the resize window shows the same status glyphs.
        internal const string OkGlyph ="", FailGlyph = "";
        private const string MoreGlyph = "", BackGlyph = ""; // More (three dots), Back (arrow)

        // Past 9 targets the radial pages: page 1 is targets 0-7 plus MORE, page 2 the rest plus BACK.
        private const int MoreSlot = -1, BackSlot = -2, PageSize = 8;

        private readonly List<string> targets;
        private readonly string sourceExt;
        private readonly List<string> files = new List<string>();
        private readonly List<Canvas> slices = new List<Canvas>();
        private readonly List<SolidColorBrush> sliceBrushes = new List<SolidColorBrush>();
        private readonly List<bool> sliceDisabled = new List<bool>();
        // Per slice: the targets index it shows, or MoreSlot/BackSlot. Identity when the radial does not page.
        private readonly List<int> visible = new List<int>();
        private int page;
        private bool flipping;
        private readonly Canvas ring;
        private readonly ScaleTransform ringScale;
        private readonly Canvas sliceLayer;
        private readonly ScaleTransform sliceScale;
        private readonly Path spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly Grid center;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock title;
        private readonly TextBlock sub;
        private readonly Canvas root;

        private IntPtr hwnd;
        private long shownAt = Environment.TickCount64;
        private int hovered = -1;
        private bool picked, busy, failed, closing, hiddenWhileBusy, handedOff;
        private DispatcherTimer? idleTimer;

        // Raised on the UI thread when the user picks a target.
        public event Action? Picked;

        // Raised on the UI thread when the user picks a window action slice ("crop", "trim", "pages", "compress", "rotate", "resize", "parts" or "cut") for the file path.
        // No conversion runs: the owner opens the action window and calls CloseForHandoff.
        public event Action<string, string>? ActionRequested;

        public int ExitCode { get; private set; }

        public RadialWindow(string firstFile, IReadOnlyList<string> targets)
        {
            this.targets = targets.ToList();
            files.Add(firstFile);
            sourceExt = Formats.Norm(firstFile);

            Title = "Typeless";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = Side;
            Height = Side;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Alpha 1 (not 0) so the layered window catches clicks on the empty corners.
            root = new Canvas { Width = Side, Height = Side, Background = Frozen(Color.FromArgb(0x01, 0, 0, 0)) };

            ringScale = new ScaleTransform(0.88, 0.88, C, C);
            ring = new Canvas
            {
                Width = Side,
                Height = Side,
                Opacity = 0,
                RenderTransform = ringScale,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 4, Opacity = 0.45 },
            };
            root.Children.Add(ring);

            // Slices sit in their own layer under the disc, so a page flip scales them without the center.
            sliceScale = new ScaleTransform(1, 1, C, C);
            sliceLayer = new Canvas { Width = Side, Height = Side, RenderTransform = sliceScale };
            ring.Children.Add(sliceLayer);

            BuildSlices();

            var disc = new Ellipse
            {
                Width = Disc,
                Height = Disc,
                Fill = Frozen(Color.FromArgb(0xF2, 0x14, 0x14, 0x18)),
                Stroke = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(disc, C - Disc / 2);
            Canvas.SetTop(disc, C - Disc / 2);
            ring.Children.Add(disc);

            statusGlyph = new TextBlock
            {
                FontFamily = IconFont,
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
                Visibility = Visibility.Collapsed,
            };
            title = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            sub = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                MaxWidth = 2 * (RI - 14),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(statusGlyph);
            stack.Children.Add(title);
            stack.Children.Add(sub);
            center = new Grid { Width = Disc, Height = Disc, Background = Brushes.Transparent };
            center.Children.Add(stack);
            Canvas.SetLeft(center, C - Disc / 2);
            Canvas.SetTop(center, C - Disc / 2);
            ring.Children.Add(center);

            spinnerRotate = new RotateTransform(0, C, C);
            spinner = new Path
            {
                Data = ArcGeometry(RI - 2, -90, 10),
                Stroke = Frozen(AccentColor),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = spinnerRotate,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            ring.Children.Add(spinner);

            Content = root;

            root.MouseLeftButtonDown += OnRootMouseDown;
            SourceInitialized += (_, _) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                PlaceAtCursor();
            };
            Loaded += OnLoaded;
            KeyDown += OnKeyDown;
            Deactivated += (_, _) =>
            {
                if (handedOff) return;
                if (Environment.TickCount64 - shownAt < 300) return;
                Dismiss();
            };
            Closing += (_, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                HideWhileBusy();
            };

            ShowIdle();
        }

        // Adds a file from a later Explorer process (multi-select). Ignored once a target is picked.
        // live: arrived over the pipe after launch (false for the launch paths, which SlicesFor already saw).
        public void AddFile(string path, bool live = true)
        {
            if (picked || closing) return;
            if (files.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            if (Formats.TargetsFor(path).Count == 0) return;
            files.Add(path);
            // Classic menu: files arrive one by one, so merge shows up once the set qualifies.
            if (live && !targets.Contains("merge") && Formats.CanMerge(files)) AppendSlice("merge");
            RefreshDisabled();
            if (hovered < 0) ShowIdle();
            else sub.Text = HoverText(TargetAt(hovered));
        }

        // Window actions are not formats: they open their own window for one file.
        private static bool IsWindowAction(string target) =>
            target == "crop" || target == "trim" || target == "pages" || target == "compress" || target == "rotate"
            || target == "resize" || target == "parts" || target == "cut";

        // Instant actions run inside the radial like a conversion, with PdfEdit or MediaBackend instead of Converter.
        private static bool IsInstantAction(string target) => target == "merge" || target == "split" || target == "audio" || target == "clean";

        private static bool IsAction(string target) => IsWindowAction(target) || IsInstantAction(target);

        // Every action except merge works on one file; merge needs every file to be a PDF or image.
        // Decided per target, so a page rebuild dims the same items again. MORE and BACK never dim.
        private bool ShouldDisable(int index)
        {
            if (visible[index] < 0) return false;
            string target = targets[visible[index]];
            if (!IsAction(target)) return false;
            if (target == "merge") return files.Count < 2 || !files.All(Formats.CanMergeItem);
            return files.Count > 1;
        }

        // The slice's target, or "more"/"back" for the page slices.
        private string TargetAt(int index) => visible[index] switch
        {
            MoreSlot => "more",
            BackSlot => "back",
            int t => targets[t],
        };

        // Dims (or restores) slices whose state changed with the file set. Before load, OnLoaded fades them in.
        private void RefreshDisabled()
        {
            for (int i = 0; i < slices.Count; i++)
            {
                bool disable = ShouldDisable(i);
                if (disable == sliceDisabled[i]) continue;
                sliceDisabled[i] = disable;
                slices[i].Cursor = disable ? Cursors.Arrow : Cursors.Hand;
                if (IsLoaded) slices[i].BeginAnimation(OpacityProperty, Anim(disable ? 0.3 : 1, 120));
                sliceBrushes[i].BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(IdleColor, TimeSpan.FromMilliseconds(120)));
                if (hovered == i) hovered = -1;
            }
        }

        private bool IsDisabled(int index) => sliceDisabled[index];

        // Builds every slice for the current page, under the disc and center. Shared by the
        // constructor, page flips, and the live merge append, which rebuilds all geometry for one more slice.
        private void BuildSlices()
        {
            sliceLayer.Children.Clear();
            slices.Clear();
            sliceBrushes.Clear();
            sliceDisabled.Clear();
            visible.Clear();
            if (targets.Count <= 9)
            {
                page = 0;
                visible.AddRange(Enumerable.Range(0, targets.Count));
            }
            else if (page == 0)
            {
                visible.AddRange(Enumerable.Range(0, PageSize));
                visible.Add(MoreSlot);
            }
            else
            {
                visible.AddRange(Enumerable.Range(PageSize, targets.Count - PageSize));
                visible.Add(BackSlot);
            }
            for (int i = 0; i < visible.Count; i++) sliceLayer.Children.Add(BuildSlice(i, visible.Count));
        }

        // Adds an action slice after launch: old slices keep their look, the new one fades in. When the
        // append starts paging, page 1 gets MORE instead, and that is the slice that fades in.
        private void AppendSlice(string action)
        {
            bool wasPaged = targets.Count > 9;
            targets.Add(action);
            BuildSlices();
            hovered = -1;
            if (!IsLoaded) return;
            int fresh = visible.IndexOf(targets.Count - 1);
            if (fresh < 0 && !wasPaged) fresh = visible.IndexOf(MoreSlot);
            for (int i = 0; i < slices.Count; i++)
            {
                double to = IsDisabled(i) ? 0.3 : 1;
                if (i != fresh) slices[i].Opacity = to;
                else slices[i].BeginAnimation(OpacityProperty, Anim(to, 160, new CubicEase { EasingMode = EasingMode.EaseOut }));
            }
            // The cursor may now be over a different slice: re-run hit testing once the new geometry is laid out.
            Dispatcher.BeginInvoke(new Action(() => Mouse.Synchronize()), DispatcherPriority.Loaded);
        }

        // Hands the app over to the action window: close without the focus-loss dismiss logic.
        public void CloseForHandoff()
        {
            handedOff = true;
            CloseAnimated();
        }

        private Canvas BuildSlice(int index, int n)
        {
            double span = 360.0 / n;
            double mid = -90 + index * span;
            Geometry data = n == 1 ? RingGeometry() : SliceGeometry(mid - span / 2, mid + span / 2);

            var brush = new SolidColorBrush(IdleColor);
            sliceBrushes.Add(brush);
            var path = new Path { Data = data, Fill = brush, Stroke = SliceStroke, StrokeThickness = 1 };

            // MORE and BACK read quieter than the targets: dimmed glyph and label.
            bool pageSlice = visible[index] < 0;
            var glyph = new TextBlock
            {
                Text = SliceGlyph(index),
                FontFamily = IconFont,
                FontSize = 18,
                Foreground = pageSlice ? SubBrush : GlyphBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = LabelFor(index),
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = n >= 9 ? 13 : 14,
                Foreground = pageSlice ? SubBrush : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(glyph);
            stack.Children.Add(label);
            // At 9 slices the arc at mid radius is ~83 DIP, so the cell narrows to stay inside its slice.
            double cellW = n >= 9 ? Math.Min(96, 2 * Math.PI * ((RI + RO) / 2) / n - 8) : 96;
            double cellH = 56;
            var cell = new Grid { Width = cellW, Height = cellH, IsHitTestVisible = false };
            cell.Children.Add(stack);
            Point at = Polar((RI + RO) / 2, mid);
            Canvas.SetLeft(cell, at.X - cellW / 2);
            Canvas.SetTop(cell, at.Y - cellH / 2);

            bool disabled = ShouldDisable(index);
            sliceDisabled.Add(disabled);
            var slice = new Canvas { Opacity = 0, Cursor = disabled ? Cursors.Arrow : Cursors.Hand };
            slice.Children.Add(path);
            slice.Children.Add(cell);
            // Slices removed by a rebuild can still get late mouse events: ignore them.
            slice.MouseEnter += (_, _) => { if (slices.Contains(slice)) OnHover(index, true); };
            slice.MouseLeave += (_, _) => { if (slices.Contains(slice)) OnHover(index, false); };
            slice.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!slices.Contains(slice)) return;
                if (failed) CloseAnimated();
                else Pick(index);
            };
            slices.Add(slice);
            return slice;
        }

        private void OnHover(int index, bool enter)
        {
            if (picked || IsDisabled(index)) return;
            sliceBrushes[index].BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(enter ? AccentColor : IdleColor, TimeSpan.FromMilliseconds(120)));
            if (enter)
            {
                hovered = index;
                title.Text = LabelFor(index);
                sub.Text = HoverText(TargetAt(index));
            }
            else if (hovered == index)
            {
                hovered = -1;
                ShowIdle();
            }
        }

        private string HoverText(string target) => target switch
        {
            "crop" => "Crop image",
            "trim" => Formats.KindOf(files[0]) == Kind.Audio ? "Trim audio" : "Trim video",
            "merge" => $"Merge {files.Count} files to PDF",
            "split" => "One PDF per page",
            "pages" => "Pick, reorder, rotate",
            "compress" => "Shrink PDF size",
            "audio" => "Original audio, no re-encode",
            "clean" => "Remove location & camera info",
            "rotate" => "Rotate or flip",
            "resize" => "Lower resolution or frame rate",
            "parts" => "Keep only some objects",
            "cut" => "Cut with a box",
            "more" => $"{targets.Count - PageSize} more options",
            "back" => "Main options",
            _ => $"{sourceExt.ToUpperInvariant()}  →  {target.ToUpperInvariant()}",
        };

        private static string BusyText(string target, int count) => target switch
        {
            "merge" => "Merging...",
            "split" => "Splitting...",
            "audio" => "Extracting...",
            "clean" => "Cleaning...",
            _ => count > 1 ? $"1/{count}" : "Converting...",
        };

        // Slice label: the target upper-cased (MORE, BACK for the page slices).
        private string LabelFor(int index) => TargetAt(index).ToUpperInvariant();

        private string SliceGlyph(int index) => GlyphFor(TargetAt(index));

        private void ShowIdle()
        {
            title.Text = sourceExt.ToUpperInvariant();
            sub.Text = files.Count == 1 ? IOPath.GetFileName(files[0]) : $"{files.Count} files";
        }

        private async void Pick(int index)
        {
            if (picked || busy || closing || flipping || IsDisabled(index)) return;
            if (visible[index] < 0)
            {
                FlipPage(visible[index] == MoreSlot ? 1 : 0);
                return;
            }
            string target = TargetAt(index);
            if (IsWindowAction(target))
            {
                // No conversion: the owner opens the action window and calls CloseForHandoff.
                picked = true;
                handedOff = true;
                idleTimer?.Stop();
                Picked?.Invoke();
                ActionRequested?.Invoke(target, files[0]);
                return;
            }
            picked = true;
            busy = true;
            idleTimer?.Stop();
            Picked?.Invoke();

            HighlightPick(index);

            // Instant actions take the whole file set; conversions skip files without this target.
            bool instant = IsInstantAction(target);
            List<string> work = instant ? files.ToList() : files.Where(f => Formats.TargetsFor(f).Contains(target)).ToList();
            int skipped = files.Count - work.Count;
            title.Text = target.ToUpperInvariant();
            sub.Text = BusyText(target, work.Count);
            StartSpinner();

            try
            {
                List<string> outputs = await Converter.RunSta(() => instant ? RunInstant(work, target) : ConvertAll(work, target));
                busy = false;
                StopSpinner();
                if (hiddenWhileBusy)
                {
                    Close();
                    return;
                }
                ShowStatus("", OkColor, "Saved", instant ? InstantText(outputs) : SuccessText(outputs, work.Count, skipped));
                await Task.Delay(700);
                CloseAnimated();
            }
            catch (Exception ex)
            {
                busy = false;
                failed = true;
                ExitCode = 1;
                StopSpinner();
                Log.Write($"{(instant ? "Action" : "Convert")} failed: {string.Join(", ", work)} -> {target}: {ex}");
                if (hiddenWhileBusy)
                {
                    Close();
                    return;
                }
                ShowStatus("", FailColor, "Failed", ShortMessage(ex.Message));
                sub.TextWrapping = TextWrapping.Wrap;
                sub.MaxHeight = 46;
                center.ToolTip = ex.Message;
                ResetIdleTimer();
            }
        }

        // MORE/BACK, 0, Tab and Backspace: the slices fade and shrink out, the other page grows back in
        // (~150 ms in all). The disc and center stay put.
        private void FlipPage(int to)
        {
            if (targets.Count <= 9 || to == page || flipping || picked || closing) return;
            flipping = true;
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            DoubleAnimation fadeOut = Anim(0, 75, easeIn);
            fadeOut.Completed += (_, _) =>
            {
                ShowPage(to);
                var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
                DoubleAnimation fadeIn = Anim(1, 75, easeOut);
                fadeIn.Completed += (_, _) => flipping = false;
                sliceLayer.BeginAnimation(OpacityProperty, fadeIn);
                sliceScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 75, easeOut));
                sliceScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 75, easeOut));
                // The cursor may now be over a different slice: re-run hit testing once laid out.
                Dispatcher.BeginInvoke(new Action(() => Mouse.Synchronize()), DispatcherPriority.Loaded);
            };
            sliceLayer.BeginAnimation(OpacityProperty, fadeOut);
            sliceScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.9, 75, easeIn));
            sliceScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.9, 75, easeIn));
        }

        // Rebuilds the slices for page to (0 or 1) at their final opacity, without animation.
        private void ShowPage(int to)
        {
            page = to;
            BuildSlices();
            hovered = -1;
            ShowIdle();
            for (int i = 0; i < slices.Count; i++) slices[i].Opacity = IsDisabled(i) ? 0.3 : 1;
        }

        // Picks by index in the full targets list (TYPELESS_AUTOPICK), showing the other page first when needed.
        private void PickTarget(int target)
        {
            if (!visible.Contains(target)) ShowPage(1 - page);
            Pick(visible.IndexOf(target));
        }

        private void HighlightPick(int index)
        {
            for (int j = 0; j < slices.Count; j++)
            {
                if (j != index) slices[j].BeginAnimation(OpacityProperty, Anim(0.3, 120));
            }
            sliceBrushes[index].BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(AccentColor, TimeSpan.FromMilliseconds(120)));
        }

        // Runs on the STA worker. Batch: keep going past failures, then report them together.
        private List<string> ConvertAll(List<string> work, string target)
        {
            var outputs = new List<string>();
            Exception? first = null;
            int failures = 0;
            for (int i = 0; i < work.Count; i++)
            {
                if (work.Count > 1)
                {
                    string progress = $"{i + 1}/{work.Count}";
                    Dispatcher.BeginInvoke(new Action(() => sub.Text = progress));
                }
                try
                {
                    outputs.AddRange(Converter.Convert(work[i], target));
                }
                catch (Exception ex) when (work.Count > 1)
                {
                    failures++;
                    first ??= ex;
                    Log.Write($"Convert failed: {work[i]} -> {target}: {ex}");
                }
            }
            if (first != null)
                throw new InvalidOperationException($"{failures} of {work.Count} files failed. {first.Message}", first);
            return outputs;
        }

        // Runs on the STA worker. Merge goes in natural file name order, as Explorer shows the files.
        private static List<string> RunInstant(List<string> work, string target)
        {
            if (target == "merge") return new List<string> { PdfEdit.Merge(PdfEdit.NaturalOrder(work)) };
            if (target == "split") return PdfEdit.Split(work[0]);
            if (target == "audio") return new List<string> { MediaBackend.ExtractAudio(work[0]) };
            if (target == "clean") return new List<string> { MetadataCleaner.Clean(work[0]) };
            throw new NotSupportedException("Unknown action: " + target);
        }

        private static string InstantText(List<string> outputs) =>
            outputs.Count == 1 ? IOPath.GetFileName(outputs[0]) : $"{outputs.Count} files";

        private static string SuccessText(List<string> outputs, int converted, int skipped)
        {
            if (converted > 1) return skipped > 0 ? $"{converted} files, {skipped} skipped" : $"{converted} files";
            if (skipped > 0) return $"1 file, {skipped} skipped";
            return outputs.Count == 1 ? IOPath.GetFileName(outputs[0]) : $"{outputs.Count} files";
        }

        internal static string ShortMessage(string message)
        {
            string m = message.Trim();
            int dot = m.IndexOf(". ", StringComparison.Ordinal);
            return dot > 0 ? m.Substring(0, dot + 1) : m;
        }

        private void ShowStatus(string glyph, Color color, string titleText, string subText)
        {
            statusGlyph.Text = glyph;
            statusGlyph.Foreground = Frozen(color);
            statusGlyph.Visibility = Visibility.Visible;
            title.Text = titleText;
            sub.Text = subText;
        }

        private void StartSpinner()
        {
            spinner.Visibility = Visibility.Visible;
            spinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopSpinner()
        {
            spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            spinner.Visibility = Visibility.Collapsed;
        }

        private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (busy || closing) return;
            if (failed)
            {
                CloseAnimated();
                return;
            }
            Vector fromCenter = e.GetPosition((IInputElement)sender) - new Point(C, C);
            if (fromCenter.Length > RO + 2) CloseAnimated();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
                return;
            }
            // Paged radial: 0 or Tab toggles the page, Backspace goes back to page 1.
            if (targets.Count > 9 && (e.Key == Key.D0 || e.Key == Key.NumPad0 || e.Key == Key.Tab || e.Key == Key.Back))
            {
                e.Handled = true;
                FlipPage(e.Key == Key.Back ? 0 : 1 - page);
                return;
            }
            // 1-9 pick on the current page; MORE and BACK count as their slice number.
            int index = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) index = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) index = e.Key - Key.NumPad1;
            if (index >= 0 && index < slices.Count)
            {
                e.Handled = true;
                Pick(index);
            }
        }

        // Esc or focus loss: hide while converting (the process exits when done), otherwise close.
        private void Dismiss()
        {
            if (busy) HideWhileBusy();
            else CloseAnimated();
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
            ring.BeginAnimation(OpacityProperty, fade);
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.94, 120, ease));
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.94, 120, ease));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PlaceAtCursor();
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();
            shownAt = Environment.TickCount64;

            var grace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            grace.Tick += (_, _) =>
            {
                grace.Stop();
                OnGraceEnded();
            };
            grace.Start();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ring.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));
            FadeInSlices();
        }

        // Staggered fade-in at load.
        private void FadeInSlices()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < slices.Count; i++)
            {
                DoubleAnimation fadeIn = Anim(IsDisabled(i) ? 0.3 : 1, 160, ease);
                fadeIn.BeginTime = TimeSpan.FromMilliseconds(i * 18);
                slices[i].BeginAnimation(OpacityProperty, fadeIn);
            }
        }

        // Test hook: TYPELESS_AUTOPICK=<0-based index in the full targets list> acts like a click on that slice,
        // after showing page 2 when it is there. -1 when unset or out of range.
        private int AutoPickIndex()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOPICK");
            if (!string.IsNullOrWhiteSpace(auto) && int.TryParse(auto.Trim(), out int n) && n >= 0 && n < targets.Count) return n;
            return -1;
        }

        private void OnGraceEnded()
        {
            if (closing) return;

            int n = AutoPickIndex();
            if (n >= 0)
            {
                PickTarget(n);
                return;
            }

            // Focus safety net: Deactivated is ignored during the grace period.
            if (!busy && !IsActive)
            {
                Activate();
                if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
                var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                check.Tick += (_, _) =>
                {
                    check.Stop();
                    if (!busy && !closing && !IsActive) CloseAnimated();
                };
                check.Start();
            }

            ResetIdleTimer();
        }

        // Auto-close after 30 s without a pick (idle) or after a failure is shown.
        private void ResetIdleTimer()
        {
            if (closing) return;
            if (idleTimer == null)
            {
                idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                idleTimer.Tick += (_, _) =>
                {
                    idleTimer.Stop();
                    if (busy) return;
                    CloseAnimated();
                };
                PreviewMouseMove += (_, _) =>
                {
                    if (idleTimer.IsEnabled && !busy && !failed) ResetIdleTimer();
                };
            }
            idleTimer.Stop();
            idleTimer.Start();
        }

        // Centers the window on the cursor in physical pixels, clamped to the monitor work area.
        private void PlaceAtCursor()
        {
            if (hwnd == IntPtr.Zero) return;
            if (!NativeMethods.TryGetCursorMonitor(out NativeMethods.POINT pt, out NativeMethods.RECT work, out double scale)) return;

            int size = (int)Math.Round(Side * scale);
            int x = Math.Max(work.Left, Math.Min(pt.X - size / 2, work.Right - size));
            int y = Math.Max(work.Top, Math.Min(pt.Y - size / 2, work.Bottom - size));
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Dev only (--preview): puts the never-shown window in a visual state and renders it to PNG.
        internal void RenderPreview(string outPath, string state)
        {
            if (Content is not FrameworkElement rootEl) throw new InvalidOperationException("No content.");

            // Final animation values, set directly.
            ring.BeginAnimation(OpacityProperty, null);
            ring.Opacity = 1;
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ringScale.ScaleX = 1;
            ringScale.ScaleY = 1;
            // page2: the second page of a paged radial (more than 9 items); otherwise the only page.
            if (state == "page2") ShowPage(1);
            for (int i = 0; i < slices.Count; i++)
            {
                slices[i].BeginAnimation(OpacityProperty, null);
                slices[i].Opacity = IsDisabled(i) ? 0.3 : 1;
            }

            string kind = state;
            int arg = 0;
            int colon = state.IndexOf(':');
            if (colon >= 0)
            {
                kind = state.Substring(0, colon);
                int.TryParse(state.Substring(colon + 1), out arg);
            }
            if (slices.Count > 0) arg = Math.Max(0, Math.Min(arg, slices.Count - 1));

            switch (kind)
            {
                case "idle":
                case "page2":
                    break;
                case "hover":
                    if (targets.Count == 0) break;
                    OnHover(arg, true);
                    SetSliceColor(arg, AccentColor);
                    break;
                case "busy":
                    if (targets.Count == 0) break;
                    PreviewPick(arg);
                    title.Text = LabelFor(arg);
                    sub.Text = BusyText(TargetAt(arg), 1);
                    StartSpinner();
                    spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    spinnerRotate.Angle = 40;
                    break;
                case "done":
                    if (targets.Count > 0) PreviewPick(arg);
                    ShowStatus("", OkColor, "Saved", IOPath.GetFileName(files[0]));
                    break;
                case "error":
                    if (targets.Count > 0) PreviewPick(arg);
                    ShowStatus("", FailColor, "Failed", ShortMessage("Sample failure message for preview."));
                    sub.TextWrapping = TextWrapping.Wrap;
                    sub.MaxHeight = 46;
                    break;
                default:
                    throw new ArgumentException("Unknown state: " + state);
            }

            rootEl.Measure(new Size(Side, Side));
            rootEl.Arrange(new Rect(0, 0, Side, Side));
            rootEl.UpdateLayout();

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Frozen(Color.FromArgb(0xFF, 0x5A, 0x60, 0x70)), null, new Rect(0, 0, Side, Side));
                dc.DrawRectangle(new VisualBrush(rootEl) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, Side, Side));
            }
            var bmp = new RenderTargetBitmap(800, 800, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(outPath);
            encoder.Save(fs);
        }

        private void SetSliceColor(int index, Color color)
        {
            sliceBrushes[index].BeginAnimation(SolidColorBrush.ColorProperty, null);
            sliceBrushes[index].Color = color;
        }

        private void PreviewPick(int index)
        {
            picked = true;
            busy = true;
            for (int j = 0; j < slices.Count; j++)
            {
                if (j != index) slices[j].Opacity = 0.3;
            }
            SetSliceColor(index, AccentColor);
        }

        private static Point Polar(double r, double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new Point(C + r * Math.Cos(a), C + r * Math.Sin(a));
        }

        private static double GapDegrees(double r) => Math.Asin(1.5 / r) * 180 / Math.PI;

        // Annular sector from a0 to a1 (degrees, clockwise), with a ~3px gap on each side.
        private static Geometry SliceGeometry(double a0, double a1)
        {
            double go = GapDegrees(RO), gi = GapDegrees(RI);
            double o0 = a0 + go, o1 = a1 - go, i0 = a0 + gi, i1 = a1 - gi;
            var figure = new PathFigure { StartPoint = Polar(RO, o0), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new ArcSegment(Polar(RO, o1), new Size(RO, RO), 0, o1 - o0 > 180, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(Polar(RI, i1), true));
            figure.Segments.Add(new ArcSegment(Polar(RI, i0), new Size(RI, RI), 0, i1 - i0 > 180, SweepDirection.Counterclockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static Geometry RingGeometry()
        {
            var c = new Point(C, C);
            var geometry = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(c, RO, RO), new EllipseGeometry(c, RI, RI));
            geometry.Freeze();
            return geometry;
        }

        private static Geometry ArcGeometry(double r, double a0, double a1)
        {
            var figure = new PathFigure { StartPoint = Polar(r, a0), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment(Polar(r, a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static string GlyphFor(string target) => target switch
        {
            "more" => MoreGlyph,
            "back" => BackGlyph,
            "stl" or "3mf" or "obj" or "ply" or "glb" => "", // Cube (Segoe Fluent Icons F158)
            "rotate" => "", // Rotate: clockwise arrow
            "resize" => "", // Back to window: inward arrows, smaller
            "parts" => "", // SelectAll: dashed box, picks some objects
            "cut" => "", // Cut: scissors
            "clean" => "", // Shield: privacy
            "pages" => "", // ViewAll: page grid
            "merge" => "", // Copy: stacked pages
            "split" => "", // Page
            "compress" => "", // BackToWindow: arrows pointing inward
            "trim" => "",
            "crop" => "",
            "png" or "jpg" or "webp" or "bmp" or "gif" or "tiff" or "ico" => "",
            "mp3" or "wav" or "flac" or "m4a" or "ogg" or "opus" or "audio" => "",
            "mp4" or "mkv" or "webm" or "mov" or "avi" => "",
            _ => "",
        };

        private static DoubleAnimation Anim(double to, int ms, IEasingFunction? ease = null) =>
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
