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
using IOPath = System.IO.Path;
using static Typeless.Theme;

namespace Typeless
{
    // Borderless crop panel on the cursor's monitor: drag a selection, Enter saves <stem>_crop.<ext>.
    public sealed class CropWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, Chrome = 110, WorkShare = 0.85;
        private const double PlaceholderW = 360, PlaceholderH = 240, MinStageSide = 240;
        private const double Tol = 10, MinPx = 8, Arm = 18, Bar = 16;

        private static readonly (string Label, double Ratio)[] Ratios =
        {
            ("Free", 0), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2), ("16:9", 16.0 / 9),
        };

        private enum Grab { None, Move, New, Resize }

        private readonly string path;
        private readonly BitmapSource? preloaded;
        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock nameText;
        private readonly TextBlock dimsText;
        private readonly Grid stage;
        private readonly Image image;
        private readonly Canvas overlay;
        private readonly Path dim;
        private readonly Rectangle selBorder;
        private readonly Line[] thirds = new Line[4];
        private readonly Path brackets;
        private readonly Path edgeBars;
        private readonly Canvas spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly Border status;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock statusTitle;
        private readonly TextBlock statusSub;
        private readonly List<TextBlock> pillTexts = new List<TextBlock>();
        private readonly List<SolidColorBrush> pillBrushes = new List<SolidColorBrush>();
        private readonly TextBlock sizeText;
        private readonly TextBlock saveGlyph;
        private readonly TextBlock saveText;
        private readonly Canvas saveSpinner;
        private readonly RotateTransform saveSpinnerRotate;

        private IntPtr hwnd;
        private bool haveMonitor;
        private NativeMethods.RECT work;
        private double scale = 1.0;
        private double maxStageW = 1200, maxStageH = 700;

        // Image pixels; selection is in image pixel coordinates, display = pixel * k.
        private BitmapSource? bitmap;
        private int pw, ph;
        private double k = 1, dispW = PlaceholderW, dispH = PlaceholderH;
        private double selX, selY, selW, selH;

        private int activePill;
        private bool flipped;
        private double ratio;

        private Grab grab;
        private bool grabL, grabT, grabR, grabB, newStarted;
        private double pressX, pressY, startX, startY, startW, startH;

        private bool busy, saved, failed, closing, hiddenWhileBusy;

        public int ExitCode { get; private set; }

        public CropWindow(string path) : this(path, null)
        {
        }

        // Preview (dev): the image is already decoded, nothing runs async.
        internal CropWindow(string path, BitmapSource? preloaded)
        {
            this.path = path;
            this.preloaded = preloaded;

            Title = "Typeless Crop";
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

            // Stage: image, overlay, spinner and status pill share one cell.
            image = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            dim = new Path { Fill = Frozen(Color.FromArgb(0xA0, 0, 0, 0)) };
            selBorder = new Rectangle { Stroke = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };
            brackets = new Path
            {
                Stroke = Frozen(AccentColor),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Miter,
            };
            edgeBars = new Path { Stroke = Frozen(AccentColor), StrokeThickness = 3 };
            overlay = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            overlay.Children.Add(dim);
            Brush thirdsBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            for (int i = 0; i < thirds.Length; i++)
            {
                thirds[i] = new Line { Stroke = thirdsBrush, StrokeThickness = 1 };
                overlay.Children.Add(thirds[i]);
            }
            overlay.Children.Add(selBorder);
            overlay.Children.Add(brackets);
            overlay.Children.Add(edgeBars);

            spinnerRotate = new RotateTransform(0, 18, 18);
            spinner = MakeSpinner(36, 15, 3, Frozen(AccentColor), spinnerRotate);

            status = MakeStatus(out statusGlyph, out statusTitle, out statusSub);

            stage = new Grid
            {
                Width = PlaceholderW,
                Height = PlaceholderH,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            };
            stage.Children.Add(image);
            stage.Children.Add(overlay);
            stage.Children.Add(spinner);
            stage.Children.Add(status);
            stage.MouseLeftButtonDown += OnStageDown;
            stage.MouseMove += OnStageMove;
            stage.MouseLeftButtonUp += OnStageUp;
            stage.LostMouseCapture += (_, _) => grab = Grab.None;

            // Toolbar: ratio pills left; size, Cancel, Save right.
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < Ratios.Length; i++) pillRow.Children.Add(MakePill(i));

            sizeText = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 12, 0),
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
            Border save = MakeButton(saveContent, AccentColor, AccentHover, null, Save, () => busy);
            save.Margin = new Thickness(8, 0, 0, 0);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(sizeText);
            right.Children.Add(cancel);
            right.Children.Add(save);
            var toolbar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            toolbar.Children.Add(right);
            toolbar.Children.Add(pillRow);

            var content = new StackPanel { Orientation = Orientation.Vertical };
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

            UpdatePills(false);
            sizeText.Text = " ";
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            if (bitmap == null) LoadImage();
        }

        // Decodes on the STA worker; the placeholder stage shows a spinner meanwhile.
        private async void LoadImage()
        {
            StartSpinner(spinner, spinnerRotate);
            try
            {
                BitmapSource loaded = preloaded ?? await Converter.RunSta(() => ImageBackend.LoadForCrop(path));
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
                Log.Write($"Crop load failed: {path}: {ex}");
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                return;
            }
            RunAutoCrop();
        }

        // Test hook: TYPELESS_AUTOCROP=x,y,w,h (fractions 0..1) sets the selection and saves.
        private void RunAutoCrop()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOCROP");
            if (string.IsNullOrWhiteSpace(auto)) return;
            string[] parts = auto.Split(',');
            if (parts.Length != 4) return;
            var f = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return;
            }
            selX = f[0] * pw;
            selY = f[1] * ph;
            selW = f[2] * pw;
            selH = f[3] * ph;
            ClampSelection();
            UpdateOverlay();
            Save();
        }

        private void SetLimits(double workW, double workH)
        {
            maxStageW = Math.Max(160, workW * WorkShare);
            maxStageH = Math.Max(120, workH * WorkShare - Chrome);
        }

        // Fits the image into the stage limits (one scale factor for both axes), selection = full image.
        private void SetImage(BitmapSource loaded)
        {
            bitmap = loaded;
            pw = loaded.PixelWidth;
            ph = loaded.PixelHeight;

            // Never shrink beyond the limits; tiny images (icons) are enlarged to a usable size.
            double grow = Math.Max(1.0, MinStageSide / Math.Max(pw, ph));
            k = Math.Min(Math.Min(maxStageW / pw, maxStageH / ph), grow);
            dispW = pw * k;
            dispH = ph * k;

            stage.Width = dispW;
            stage.Height = dispH;
            overlay.Width = dispW;
            overlay.Height = dispH;
            image.Source = loaded;
            image.Visibility = Visibility.Visible;
            RenderOptions.SetBitmapScalingMode(image, k > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            overlay.Visibility = Visibility.Visible;
            dimsText.Text = $"{pw} × {ph} px";
            nameText.MaxWidth = Math.Max(200, Math.Max(dispW, 520) - 140);

            selX = 0;
            selY = 0;
            selW = pw;
            selH = ph;
            if (ratio > 0) FitRatio();
            UpdateOverlay();
        }

        private Border MakePill(int index)
        {
            var brush = new SolidColorBrush(PillIdle);
            pillBrushes.Add(brush);
            var text = new TextBlock
            {
                Text = Ratios[index].Label,
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pillTexts.Add(text);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Height = 24,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = brush,
                Child = text,
                Cursor = Cursors.Hand,
            };
            pill.MouseEnter += (_, _) =>
            {
                if (index != activePill) AnimateColor(brush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(brush, index == activePill ? AccentColor : PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                PickRatio(index);
            };
            return pill;
        }

        // Clicking the active ratio again flips it (4:3 becomes 3:4); Free and 1:1 do not flip.
        private void PickRatio(int index)
        {
            if (busy || saved || failed || closing) return;
            if (index == activePill)
            {
                if (index < 2) return;
                flipped = !flipped;
            }
            else
            {
                activePill = index;
                flipped = false;
            }
            double r = Ratios[index].Ratio;
            ratio = r > 0 && flipped ? 1 / r : r;
            UpdatePills(true);
            if (bitmap != null && ratio > 0)
            {
                FitRatio();
                UpdateOverlay();
            }
        }

        private void UpdatePills(bool animate)
        {
            for (int j = 0; j < pillTexts.Count; j++)
            {
                string label = Ratios[j].Label;
                if (j == activePill && flipped)
                {
                    string[] parts = label.Split(':');
                    label = parts[1] + ":" + parts[0];
                }
                pillTexts[j].Text = label;
                Color c = j == activePill ? AccentColor : PillIdle;
                if (animate) AnimateColor(pillBrushes[j], c);
                else
                {
                    pillBrushes[j].BeginAnimation(SolidColorBrush.ColorProperty, null);
                    pillBrushes[j].Color = c;
                }
            }
        }

        // Largest rect with the active ratio that fits the image, centered on the selection center.
        private void FitRatio()
        {
            double w = Math.Min(pw, ph * ratio);
            double h = w / ratio;
            double cx = selX + selW / 2, cy = selY + selH / 2;
            selW = w;
            selH = h;
            selX = cx - w / 2;
            selY = cy - h / 2;
            ClampSelection();
        }

        private void OnStageDown(object sender, MouseButtonEventArgs e)
        {
            if (bitmap == null || busy || saved || failed || closing) return;
            e.Handled = true;
            Point p = e.GetPosition(stage);
            Grab hit = HitTest(p, out bool l, out bool t, out bool r, out bool b);
            if (e.ClickCount == 2)
            {
                if (hit == Grab.Move) Save();
                return;
            }

            grab = hit;
            grabL = l;
            grabT = t;
            grabR = r;
            grabB = b;
            newStarted = false;
            pressX = Clamp(p.X / k, 0, pw);
            pressY = Clamp(p.Y / k, 0, ph);
            startX = selX;
            startY = selY;
            startW = selW;
            startH = selH;
            stage.CaptureMouse();
        }

        private void OnStageMove(object sender, MouseEventArgs e)
        {
            if (bitmap == null || busy || saved || failed)
            {
                stage.Cursor = null;
                return;
            }
            Point p = e.GetPosition(stage);
            if (grab == Grab.None)
            {
                Grab hit = HitTest(p, out bool l, out bool t, out bool r, out bool b);
                stage.Cursor = CursorFor(hit, l, t, r, b);
                return;
            }
            ApplyDrag(p);
            UpdateOverlay();
        }

        private void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            if (grab == Grab.None) return;
            grab = Grab.None;
            stage.ReleaseMouseCapture();
        }

        // Display coordinates, 10 DIP tolerance: edges/corners resize, inside moves, outside draws.
        private Grab HitTest(Point p, out bool l, out bool t, out bool r, out bool b)
        {
            double left = selX * k, top = selY * k, right = (selX + selW) * k, bottom = (selY + selH) * k;
            bool nearL = Math.Abs(p.X - left) <= Tol, nearR = Math.Abs(p.X - right) <= Tol;
            bool nearT = Math.Abs(p.Y - top) <= Tol, nearB = Math.Abs(p.Y - bottom) <= Tol;
            if (nearL && nearR)
            {
                if (Math.Abs(p.X - left) <= Math.Abs(p.X - right)) nearR = false;
                else nearL = false;
            }
            if (nearT && nearB)
            {
                if (Math.Abs(p.Y - top) <= Math.Abs(p.Y - bottom)) nearB = false;
                else nearT = false;
            }
            bool inX = p.X >= left - Tol && p.X <= right + Tol;
            bool inY = p.Y >= top - Tol && p.Y <= bottom + Tol;
            l = nearL && inY;
            r = nearR && inY;
            t = nearT && inX;
            b = nearB && inX;
            if (l || r || t || b) return Grab.Resize;
            return p.X > left && p.X < right && p.Y > top && p.Y < bottom ? Grab.Move : Grab.New;
        }

        private static Cursor CursorFor(Grab hit, bool l, bool t, bool r, bool b)
        {
            if (hit == Grab.Move) return Cursors.SizeAll;
            if (hit == Grab.New) return Cursors.Cross;
            if ((l && t) || (r && b)) return Cursors.SizeNWSE;
            if ((r && t) || (l && b)) return Cursors.SizeNESW;
            return l || r ? Cursors.SizeWE : Cursors.SizeNS;
        }

        private void ApplyDrag(Point p)
        {
            double mx = Clamp(p.X / k, 0, pw), my = Clamp(p.Y / k, 0, ph);
            double dx = mx - pressX, dy = my - pressY;
            double x0 = startX, y0 = startY, x1 = startX + startW, y1 = startY + startH;
            switch (grab)
            {
                case Grab.Move:
                    selW = startW;
                    selH = startH;
                    selX = Clamp(x0 + dx, 0, pw - startW);
                    selY = Clamp(y0 + dy, 0, ph - startH);
                    break;
                case Grab.New:
                    // A plain click outside keeps the current selection.
                    if (!newStarted)
                    {
                        if (Math.Abs(dx) * k < 3 && Math.Abs(dy) * k < 3) return;
                        newStarted = true;
                    }
                    ResizeFromAnchor(pressX, pressY, mx, my);
                    break;
                case Grab.Resize:
                    bool horizontal = grabL || grabR, vertical = grabT || grabB;
                    if (horizontal && vertical)
                    {
                        ResizeFromAnchor(grabL ? x1 : x0, grabT ? y1 : y0,
                            Clamp((grabL ? x0 : x1) + dx, 0, pw), Clamp((grabT ? y0 : y1) + dy, 0, ph));
                    }
                    else if (horizontal) ResizeEdge(true, grabL, dx);
                    else ResizeEdge(false, grabT, dy);
                    break;
            }
            ClampSelection();
        }

        // Corner drag (or a new selection): the anchor stays put, the box may flip across it.
        // With a ratio the dominant axis drives the size, clamped to the image keeping the ratio.
        private void ResizeFromAnchor(double ax, double ay, double cx, double cy)
        {
            double minW = Math.Min(MinPx, pw), minH = Math.Min(MinPx, ph);
            bool right = cx >= ax, down = cy >= ay;
            double w = Math.Abs(cx - ax), h = Math.Abs(cy - ay);
            double maxW = right ? pw - ax : ax, maxH = down ? ph - ay : ay;
            if (ratio > 0)
            {
                w = Math.Max(w, h * ratio);
                w = Math.Max(w, Math.Max(minW, minH * ratio));
                w = Math.Min(w, Math.Min(maxW, maxH * ratio));
                h = w / ratio;
            }
            else
            {
                w = Math.Min(Math.Max(w, minW), Math.Max(minW, maxW));
                h = Math.Min(Math.Max(h, minH), Math.Max(minH, maxH));
            }
            selX = right ? ax : ax - w;
            selY = down ? ay : ay - h;
            selW = w;
            selH = h;
        }

        // Edge drag: the opposite edge stays put. With a ratio the other axis follows, centered.
        private void ResizeEdge(bool horizontal, bool low, double delta)
        {
            double minW = Math.Min(MinPx, pw), minH = Math.Min(MinPx, ph);
            if (horizontal)
            {
                double x0 = startX, x1 = startX + startW;
                double w = Clamp(low ? startW - delta : startW + delta, minW, low ? x1 : pw - x0);
                if (ratio > 0)
                {
                    double cy = startY + startH / 2;
                    double h = w / ratio, maxH = 2 * Math.Min(cy, ph - cy);
                    if (h > maxH)
                    {
                        h = maxH;
                        w = h * ratio;
                    }
                    selY = cy - h / 2;
                    selH = h;
                }
                selX = low ? x1 - w : x0;
                selW = w;
            }
            else
            {
                double y0 = startY, y1 = startY + startH;
                double h = Clamp(low ? startH - delta : startH + delta, minH, low ? y1 : ph - y0);
                if (ratio > 0)
                {
                    double cx = startX + startW / 2;
                    double w = h * ratio, maxW = 2 * Math.Min(cx, pw - cx);
                    if (w > maxW)
                    {
                        w = maxW;
                        h = w / ratio;
                    }
                    selX = cx - w / 2;
                    selW = w;
                }
                selY = low ? y1 - h : y0;
                selH = h;
            }
        }

        private void ClampSelection()
        {
            selW = Clamp(selW, 1, pw);
            selH = Clamp(selH, 1, ph);
            selX = Clamp(selX, 0, pw - selW);
            selY = Clamp(selY, 0, ph - selH);
        }

        // Dim outside, border, thirds, corner brackets, edge bars and the size readout, in one pass.
        private void UpdateOverlay()
        {
            if (bitmap == null) return;
            double left = selX * k, top = selY * k, w = selW * k, h = selH * k;
            double right = left + w, bottom = top + h;

            var dimGeometry = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, dispW, dispH)), new RectangleGeometry(new Rect(left, top, w, h)));
            dimGeometry.Freeze();
            dim.Data = dimGeometry;

            Canvas.SetLeft(selBorder, left);
            Canvas.SetTop(selBorder, top);
            selBorder.Width = w;
            selBorder.Height = h;

            SetLine(thirds[0], left + w / 3, top, left + w / 3, bottom);
            SetLine(thirds[1], left + 2 * w / 3, top, left + 2 * w / 3, bottom);
            SetLine(thirds[2], left, top + h / 3, right, top + h / 3);
            SetLine(thirds[3], left, top + 2 * h / 3, right, top + 2 * h / 3);

            // Brackets sit just outside the border (pen center 1.5 DIP out).
            double a = Math.Min(Arm, Math.Min(w, h) / 2), o = 1.5;
            double l = left - o, t = top - o, r = right + o, b = bottom + o;
            var corners = new StreamGeometry();
            using (StreamGeometryContext ctx = corners.Open())
            {
                Polyline(ctx, new Point(l, t + a), new Point(l, t), new Point(l + a, t));
                Polyline(ctx, new Point(r - a, t), new Point(r, t), new Point(r, t + a));
                Polyline(ctx, new Point(r, b - a), new Point(r, b), new Point(r - a, b));
                Polyline(ctx, new Point(l + a, b), new Point(l, b), new Point(l, b - a));
            }
            corners.Freeze();
            brackets.Data = corners;

            var bars = new StreamGeometry();
            using (StreamGeometryContext ctx = bars.Open())
            {
                double room = 2 * Arm + Bar + 8;
                if (w >= room)
                {
                    double mx = left + w / 2;
                    Segment(ctx, new Point(mx - Bar / 2, t), new Point(mx + Bar / 2, t));
                    Segment(ctx, new Point(mx - Bar / 2, b), new Point(mx + Bar / 2, b));
                }
                if (h >= room)
                {
                    double my = top + h / 2;
                    Segment(ctx, new Point(l, my - Bar / 2), new Point(l, my + Bar / 2));
                    Segment(ctx, new Point(r, my - Bar / 2), new Point(r, my + Bar / 2));
                }
            }
            bars.Freeze();
            edgeBars.Data = bars;

            sizeText.Text = $"{(int)Math.Round(selW)} × {(int)Math.Round(selH)}";
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (failed && !busy)
            {
                e.Handled = true;
                CloseAnimated();
                return;
            }
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
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
                case Key.Left:
                    e.Handled = true;
                    Nudge(-step, 0);
                    break;
                case Key.Right:
                    e.Handled = true;
                    Nudge(step, 0);
                    break;
                case Key.Up:
                    e.Handled = true;
                    Nudge(0, -step);
                    break;
                case Key.Down:
                    e.Handled = true;
                    Nudge(0, step);
                    break;
            }
        }

        private void Nudge(double dx, double dy)
        {
            if (bitmap == null || busy || saved || grab != Grab.None) return;
            selX = Clamp(selX + dx, 0, pw - selW);
            selY = Clamp(selY + dy, 0, ph - selH);
            UpdateOverlay();
        }

        private async void Save()
        {
            if (bitmap == null || busy || saved || failed || closing) return;
            int x = Math.Clamp((int)Math.Floor(selX), 0, pw - 1);
            int y = Math.Clamp((int)Math.Floor(selY), 0, ph - 1);
            int w = Math.Clamp((int)Math.Round(selW), 1, pw - x);
            int h = Math.Clamp((int)Math.Round(selH), 1, ph - y);

            busy = true;
            grab = Grab.None;
            stage.ReleaseMouseCapture();
            stage.Cursor = null;
            SetSaving(true);

            BitmapSource src = bitmap;
            string file = path;
            try
            {
                string outPath = await Converter.RunSta(() => ImageBackend.Crop(file, src, x, y, w, h));
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
                Log.Write($"Crop failed: {path}: {ex}");
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

        // Dev only (--crop-preview): never shown; sized for a 1920x1080 work area at 100% and rendered to PNG.
        internal void RenderPreview(string outPath, string state)
        {
            if (preloaded == null) throw new InvalidOperationException("Preview needs a decoded image.");
            SetLimits(1920, 1080);
            SetImage(preloaded);

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
                    activePill = 2;
                    flipped = false;
                    ratio = Ratios[2].Ratio;
                    UpdatePills(false);
                    double w = 0.65 * pw, h = w / ratio;
                    if (h > 0.65 * ph)
                    {
                        h = 0.65 * ph;
                        w = h * ratio;
                    }
                    selX = 0.15 * pw;
                    selY = 0.15 * ph;
                    selW = w;
                    selH = h;
                    ClampSelection();
                    UpdateOverlay();
                    break;
                case "done":
                    saved = true;
                    ShowStatus("", OkColor, "Saved",
                        IOPath.GetFileNameWithoutExtension(path) + "_crop" + IOPath.GetExtension(path));
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

        private static void Polyline(StreamGeometryContext ctx, Point p0, Point p1, Point p2)
        {
            ctx.BeginFigure(p0, false, false);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }

        private static void Segment(StreamGeometryContext ctx, Point p0, Point p1)
        {
            ctx.BeginFigure(p0, false, false);
            ctx.LineTo(p1, true, false);
        }

        private static void SetLine(Line line, double x1, double y1, double x2, double y2)
        {
            line.X1 = x1;
            line.Y1 = y1;
            line.X2 = x2;
            line.Y2 = y2;
        }
    }
}
