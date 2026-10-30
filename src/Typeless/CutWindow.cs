using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // Cut panel on the cursor's monitor: the model seen from the top, front or side. A dragged box is
    // extended through the whole model along the view axis; Enter cuts along its sides and saves
    // <stem>_inside.<ext> and/or <stem>_outside.<ext> via ModelCut.Save; Esc clears the box, then closes.
    public sealed class CutWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, StageShare = 0.5, ViewMargin = 16;
        private const double MinStageW = 520, MinStageH = 240, PlaceholderH = 340;
        private const double DragThreshold = 4, CloseDelaySeconds = 2.5;

        private static readonly Brush AccentBrush = Frozen(AccentColor);
        private static readonly Brush FailBrush = Frozen(FailColor);
        private static readonly Brush DimBrush = Frozen(Color.FromArgb(0x99, 0x14, 0x14, 0x18));
        private const string Hint = "Drag a box: the model is cut along its sides";

        // View pills: Top looks down Z, Front along +Y (viewer at -Y), Side along -X (viewer at +X).
        private static readonly char[] ViewAxes = { 'z', 'y', 'x' };
        private static readonly string[] ViewLabels = { "Top", "Front", "Side" };
        private static readonly string[] Keeps = { "both", "inside", "outside" };
        private static readonly string[] KeepLabels = { "Both pieces", "Inside only", "Outside only" };

        // Premultiplied BGRA per shade: accent scaled by the facing; shade 0 = empty.
        private static readonly uint[] Lut = BuildLut();

        // One view, built once on the worker: per device pixel the facing shade of the nearest surface.
        // Screen (DIP) = (Ox + (a - A0) * K, Oy + (B1 - b) * K), (a, b) from ModelCut.Axes.
        private sealed class View
        {
            public char Axis;
            public double K, Ox, Oy, A0, B1, W, H, S;
            public int Pw, Ph;
            public byte[] Shade = Array.Empty<byte>();
            public WriteableBitmap? Bitmap;   // made on the UI thread on first show
        }

        private sealed class LoadResult
        {
            public Scene Scene = null!;
            public double[] Min = null!, Max = null!;
            public View View = null!;
        }

        private readonly string path;
        private readonly bool previewMode;
        private readonly long before;

        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock dimsText;
        private readonly StackPanel content;
        private readonly Grid stage;
        private readonly Image image;
        private readonly Canvas overlay;
        private readonly System.Windows.Shapes.Path dim;
        private readonly RectangleGeometry dimStage = new RectangleGeometry(), dimHole = new RectangleGeometry();
        private readonly Rectangle outline;
        private readonly TextBlock sizeLabel;
        private readonly Canvas spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly Border status;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock statusTitle;
        private readonly TextBlock statusSub;
        private readonly SolidColorBrush[] viewFills = new SolidColorBrush[3];
        private readonly SolidColorBrush[] keepFills = new SolidColorBrush[3];
        private readonly StackPanel viewRow;
        private readonly StackPanel keepRow;
        private readonly TextBlock bigText;
        private readonly TextBlock bigSub;
        private readonly Border save;
        private readonly TextBlock saveGlyph;
        private readonly TextBlock saveText;
        private readonly Canvas saveSpinner;
        private readonly RotateTransform saveSpinnerRotate;

        private IntPtr hwnd;
        private bool haveMonitor;
        private NativeMethods.RECT work;
        private double scale = 1.0;
        private double maxW = 1550, maxH = 516;

        private Scene? scene;
        private double[] bmin = new double[3], bmax = new double[3];
        private readonly Dictionary<char, View> views = new Dictionary<char, View>();
        private View? view;
        private char viewAxis = 'z';
        private string keep = "both";

        // The box in model mm along the view's (a, b) axes, a0 < a1 and b0 < b1.
        private bool hasBox;
        private double boxA0, boxB0, boxA1, boxB1;

        // Open faces: the last cut could not fill every outline; the next Save allows them.
        private bool openPending;
        // Why the last Save did not save (empty side, open faces), shown white under the readout.
        private string? message;

        // Mouse: pressed on the stage, past the drag threshold, and the press point.
        private bool pressed, dragging;
        private Point pressAt;

        private bool loading, building, busy, saved, failed, closing, hiddenWhileBusy, autoRun;

        // 1 until something is saved (Esc, an empty side, open faces and failures all leave nothing).
        public int ExitCode { get; private set; } = 1;

        public CutWindow(string path) : this(path, false)
        {
        }

        // Preview (dev): RenderPreview loads synchronously and fakes the states; nothing is saved or shown.
        internal CutWindow(string path, bool preview)
        {
            this.path = path;
            previewMode = preview;
            try
            {
                before = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                // The read reports the real error.
                Log.Write($"Cut size read failed: {path}: {ex.Message}");
            }

            Title = "Typeless Cut";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name, triangles and size under it, file size right.
            var nameText = new TextBlock
            {
                Text = IOPath.GetFileName(path),
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = MinStageW - 110,
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
            var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
            DockPanel.SetDock(sizeText, Dock.Right);
            header.Children.Add(sizeText);
            header.Children.Add(nameStack);

            // View pills above the stage.
            viewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            for (int i = 0; i < ViewAxes.Length; i++)
            {
                char axis = ViewAxes[i];
                viewRow.Children.Add(MakePill(ViewLabels[i], () => viewAxis == axis, () => PickView(axis), out viewFills[i]));
            }

            // Stage: the view bitmap, the box overlay (dimmed outside, outline, size), spinner and status share one cell.
            image = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            // One even-odd path (stage minus box) so the dimming has no seams.
            dim = new System.Windows.Shapes.Path
            {
                Fill = DimBrush,
                Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { dimStage, dimHole } },
            };
            outline = new Rectangle
            {
                Stroke = AccentBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            sizeLabel = new TextBlock
            {
                Text = " ",
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = Brushes.White,
                Background = Frozen(Color.FromArgb(0x99, 0x14, 0x14, 0x18)),
                Padding = new Thickness(4, 0, 4, 1),
            };
            overlay = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            overlay.Children.Add(dim);
            overlay.Children.Add(outline);
            overlay.Children.Add(sizeLabel);
            spinnerRotate = new RotateTransform(0, 18, 18);
            spinner = MakeSpinner(36, 15, 3, AccentBrush, spinnerRotate);
            status = MakeStatus(out statusGlyph, out statusTitle, out statusSub);

            stage = new Grid
            {
                Width = MinStageW,
                Height = PlaceholderH,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
                ClipToBounds = true,
                Cursor = Cursors.Cross,
            };
            stage.Children.Add(image);
            stage.Children.Add(overlay);
            stage.Children.Add(spinner);
            stage.Children.Add(status);
            stage.MouseLeftButtonDown += OnStageDown;
            stage.MouseMove += OnStageMove;
            stage.MouseLeftButtonUp += OnStageUp;
            stage.LostMouseCapture += (_, _) => pressed = dragging = false;

            // Keep pills.
            keepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < Keeps.Length; i++)
            {
                string k = Keeps[i];
                keepRow.Children.Add(MakePill(KeepLabels[i], () => keep == k, () => PickKeep(k), out keepFills[i]));
            }

            // Readout: "Box 20 × 15 mm, seen from the top", the hint or why Save did not save under it.
            bigText = new TextBlock
            {
                Text = " ",
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            bigSub = new TextBlock
            {
                Text = " ",
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var readout = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2, 6, 0, 0) };
            readout.Children.Add(bigText);
            readout.Children.Add(bigSub);

            // Toolbar: readout left, Cancel and Save right.
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
            toolbar.Children.Add(readout);

            // Fixed width (the stage's once loaded), so long names and messages trim instead of resizing the window.
            content = new StackPanel { Orientation = Orientation.Vertical, Width = MinStageW };
            content.Children.Add(header);
            content.Children.Add(viewRow);
            content.Children.Add(stage);
            content.Children.Add(keepRow);
            content.Children.Add(toolbar);

            // The shadow sits on a sibling plate so the effect does not re-render the view.
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
            PreviewKeyUp += (_, e) =>
            {
                if (IsAltKey(e)) e.Handled = true;
            };
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
            Closed += (_, _) => closing = true;

            UpdateAll();
        }

        private bool CanEdit => view != null && !building && !busy && !saved && !failed && !closing;

        // Why Save is off, or null when it can save.
        private string? SaveBlocker()
        {
            if (view == null) return " ";
            if (!hasBox) return "Drag a box over the model first";
            if (boxA1 <= boxA0 || boxB1 <= boxB0) return "The box needs a width and a height";
            return null;
        }

        private bool CanSave => CanEdit && SaveBlocker() == null;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            if (scene == null && !loading) LoadModel();
        }

        // Read and rasterize the top view on a worker; spinner meanwhile.
        private async void LoadModel()
        {
            loading = true;
            StartSpinner(spinner, spinnerRotate);
            UpdateAll();
            LoadResult result;
            try
            {
                string src = path;
                double w = maxW, h = maxH, s = scale;
                result = await Task.Run(() => Load(src, w, h, s));
                loading = false;
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
            }
            catch (Exception ex)
            {
                loading = false;
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                failed = true;
                Log.Write($"Cut load failed: {path}: {ex}");
                UpdateAll();
                ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                AutoAbort();
                return;
            }
            SetModel(result);
            RunAutoCut();
        }

        // Runs on a worker.
        private static LoadResult Load(string src, double maxW, double maxH, double s)
        {
            Scene scene = ModelBackend.Read(src);
            if (scene.TriangleCount == 0) throw new InvalidOperationException("No triangles found");
            (double[] min, double[] max) = scene.Bounds();
            return new LoadResult { Scene = scene, Min = min, Max = max, View = BuildView(scene, min, max, 'z', maxW, maxH, s) };
        }

        // Header and the first (top) view.
        private void SetModel(LoadResult loaded)
        {
            scene = loaded.Scene;
            bmin = loaded.Min;
            bmax = loaded.Max;
            dimsText.Text = $"{scene.TriangleCount.ToString("N0", CultureInfo.InvariantCulture)} triangles · "
                + $"{Mm(bmax[0] - bmin[0])} × {Mm(bmax[1] - bmin[1])} × {Mm(bmax[2] - bmin[2])} mm";

            View v = loaded.View;
            views[v.Axis] = v;
            viewAxis = v.Axis;
            Display(v);
            UpdateAll();
        }

        private void Display(View v)
        {
            view = v;
            if (v.Bitmap == null)
            {
                var pixels = new uint[v.Pw * v.Ph];
                byte[] shade = v.Shade;
                for (int i = 0; i < pixels.Length; i++) pixels[i] = Lut[shade[i]];
                v.Bitmap = new WriteableBitmap(v.Pw, v.Ph, 96 * v.S, 96 * v.S, PixelFormats.Pbgra32, null);
                v.Bitmap.WritePixels(new Int32Rect(0, 0, v.Pw, v.Ph), pixels, v.Pw * 4, 0);
            }
            // The stage takes this view's size; the window re-centers on SizeChanged.
            stage.Width = v.W;
            stage.Height = v.H;
            content.Width = v.W;
            image.Width = v.W;
            image.Height = v.H;
            image.Source = v.Bitmap;
            image.Visibility = Visibility.Visible;
            LayoutBox();
        }

        // Switches the view (cached after its first build); the box is cleared.
        private async Task<bool> ShowView(char axis)
        {
            if (scene == null || view == null) return false;
            viewAxis = axis;
            ClearBox();
            if (views.TryGetValue(axis, out View? cached))
            {
                Display(cached);
                UpdateAll();
                return true;
            }

            building = true;
            image.Visibility = Visibility.Collapsed;
            StartSpinner(spinner, spinnerRotate);
            UpdateAll();
            try
            {
                Scene sc = scene;
                double[] min = bmin, max = bmax;
                double w = maxW, h = maxH, s = view.S;
                View v = await Task.Run(() => BuildView(sc, min, max, axis, w, h, s));
                building = false;
                if (closing) return false;
                StopSpinner(spinner, spinnerRotate);
                views[axis] = v;
                Display(v);
                UpdateAll();
                return true;
            }
            catch (Exception ex)
            {
                building = false;
                if (closing) return false;
                StopSpinner(spinner, spinnerRotate);
                failed = true;
                Log.Write($"Cut view failed: {path} ({axis}): {ex}");
                UpdateAll();
                ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                return false;
            }
        }

        private async void PickView(char axis)
        {
            if (!CanEdit || axis == viewAxis) return;
            await ShowView(axis);
        }

        private void PickKeep(string k)
        {
            if (!CanEdit || k == keep) return;
            keep = k;
            openPending = false;
            message = null;
            UpdateAll();
        }

        // Test hook: TYPELESS_AUTOCUT=<axis>:<a0,b0,a1,b1> selects that view and box once the model loads
        // and saves; TYPELESS_CUT_KEEP=inside|outside picks the keep pill, TYPELESS_CUT_OPEN=1 allows open
        // faces. Same call and output as --cut <file> <axis> <box> [keep] [open]. Invalid spec, an empty
        // side or open faces without TYPELESS_CUT_OPEN: rc 1, nothing saved.
        private async void RunAutoCut()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOCUT");
            if (string.IsNullOrWhiteSpace(auto) || scene == null) return;
            autoRun = true;
            char axis;
            double a0, b0, a1, b1;
            try
            {
                int colon = auto.IndexOf(':');
                if (colon < 0) throw new FormatException($"Invalid cut spec: {auto} (needs <axis>:<a0,b0,a1,b1>)");
                axis = ModelCut.ParseAxis(auto.Substring(0, colon));
                (a0, b0, a1, b1) = ModelCut.ParseBox(auto.Substring(colon + 1));
            }
            catch (FormatException ex)
            {
                Log.Write($"TYPELESS_AUTOCUT: {ex.Message}, nothing saved");
                CloseAnimated();
                return;
            }
            if (axis != viewAxis && !await ShowView(axis))
            {
                Log.Write($"TYPELESS_AUTOCUT: {auto}: the view could not be drawn, nothing saved");
                CloseAnimated();
                return;
            }
            string k = (Environment.GetEnvironmentVariable("TYPELESS_CUT_KEEP") ?? "").Trim().ToLowerInvariant();
            keep = k == "inside" || k == "outside" ? k : "both";
            SetBox(a0, b0, a1, b1);
            openPending = Environment.GetEnvironmentVariable("TYPELESS_CUT_OPEN") == "1";
            UpdateAll();
            string? blocker = SaveBlocker();
            if (blocker != null || !CanSave)
            {
                Log.Write($"TYPELESS_AUTOCUT: {auto}: {blocker ?? "cannot save"}, nothing saved");
                CloseAnimated();
                return;
            }
            Save();
        }

        // With the hook set, a load failure closes instead of waiting for a key.
        private void AutoAbort()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPELESS_AUTOCUT"))) return;
            Log.Write("TYPELESS_AUTOCUT: the model could not be read, nothing saved");
            CloseAnimated();
        }

        // Height share, capped by the width so a portrait monitor still fits the panel.
        private void SetLimits(double workW, double workH)
        {
            maxH = Math.Max(MinStageH, workH * StageShare);
            maxW = Math.Max(MinStageW, workW * 0.85 - 2 * (Pad + ShadowMargin));
        }

        // Scale (DIPs per mm) that fits a w by d rectangle into the stage less the margin.
        private static double FitK(double w, double d, double maxW, double maxH)
        {
            double availW = maxW - 2 * ViewMargin, availH = maxH - 2 * ViewMargin;
            double kx = w > 1e-9 ? availW / w : double.PositiveInfinity;
            double ky = d > 1e-9 ? availH / d : double.PositiveInfinity;
            double k = Math.Min(kx, ky);
            return double.IsInfinity(k) ? 1 : k;
        }

        // Orthographic fit of the model's (a, b) extent with a margin, b up, then the rasterizer at s
        // device pixels per DIP. Each view has its own fitted stage size.
        private static View BuildView(Scene scene, double[] min, double[] max, char axis, double maxW, double maxH, double s)
        {
            if (!double.IsFinite(min[0] + min[1] + min[2] + max[0] + max[1] + max[2]))
                throw new InvalidOperationException("The model has invalid coordinates.");

            (int ia, int ib) = ModelCut.Axes(axis);
            double w = max[ia] - min[ia], d = max[ib] - min[ib];
            double k = FitK(w, d, maxW, maxH);
            // Stage size rounded to whole device pixels so the bitmap maps 1:1.
            int pw = (int)Math.Ceiling(Math.Max(MinStageW, w * k + 2 * ViewMargin) * s);
            int ph = (int)Math.Ceiling(Math.Max(MinStageH, d * k + 2 * ViewMargin) * s);
            var v = new View { Axis = axis, K = k, A0 = min[ia], B1 = max[ib], S = s, Pw = pw, Ph = ph, W = pw / s, H = ph / s };
            v.Ox = (v.W - w * k) / 2;
            v.Oy = (v.H - d * k) / 2;
            Rasterize(scene, v);
            return v;
        }

        // Scan-converts every triangle's (a, b) projection with edge functions at pixel centers, keeping
        // the surface nearest the viewer (Top: max z, Front: min y, Side: max x) and its facing shade.
        // Triangles that cover no pixel center (sub-pixel, slivers, walls seen edge on) plot their longest
        // edge instead, so thin features still show.
        private static void Rasterize(Scene scene, View v)
        {
            int pw = v.Pw, ph = v.Ph;
            var shade = new byte[pw * ph];
            var zb = new float[pw * ph];
            Array.Fill(zb, float.NegativeInfinity);

            (int ka, int kb) = ModelCut.Axes(v.Axis);
            int kc = 3 - ka - kb;
            double sign = v.Axis == 'y' ? -1 : 1;   // depth: larger is nearer the viewer

            // Light from the viewer, tilted up (+b) and to the left (-a).
            var light = new double[3];
            light[kc] = sign;
            light[kb] += 0.5;
            light[ka] -= 0.3;
            double ll = Math.Sqrt(light[0] * light[0] + light[1] * light[1] + light[2] * light[2]);
            double lx = light[0] / ll, ly = light[1] / ll, lz = light[2] / ll;

            // Pixel = (ax * a + bx, ay * b + by).
            double ax = v.K * v.S, bxo = (v.Ox - v.A0 * v.K) * v.S;
            double ay = -v.K * v.S, byo = (v.Oy + v.B1 * v.K) * v.S;

            foreach (Part part in scene.Parts)
            {
                float[] pos = part.Positions;
                int[] tris = part.Triangles;
                int vc = part.VertexCount;
                var sx = new float[vc];
                var sy = new float[vc];
                var sd = new float[vc];
                for (int i = 0; i < vc; i++)
                {
                    sx[i] = (float)(ax * pos[i * 3 + ka] + bxo);
                    sy[i] = (float)(ay * pos[i * 3 + kb] + byo);
                    sd[i] = (float)(sign * pos[i * 3 + kc]);
                }

                int tc = part.TriangleCount;
                for (int t = 0; t < tc; t++)
                {
                    int ia = tris[t * 3], ib = tris[t * 3 + 1], ic = tris[t * 3 + 2];
                    double pax = sx[ia], pay = sy[ia], paz = sd[ia];
                    double pbx = sx[ib], pby = sy[ib], pbz = sd[ib];
                    double pcx = sx[ic], pcy = sy[ic], pcz = sd[ic];
                    if (!double.IsFinite(pax + pay + paz + pbx + pby + pbz + pcx + pcy + pcz)) continue;

                    // Facing: |n . L| on the 3D face normal, so either winding lights the same.
                    double ux = pos[ib * 3] - pos[ia * 3], uy = pos[ib * 3 + 1] - pos[ia * 3 + 1], uz = pos[ib * 3 + 2] - pos[ia * 3 + 2];
                    double wx = pos[ic * 3] - pos[ia * 3], wy = pos[ic * 3 + 1] - pos[ia * 3 + 1], wz = pos[ic * 3 + 2] - pos[ia * 3 + 2];
                    double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    double f = nl > 0 ? 0.25 + 0.75 * Math.Abs(nx * lx + ny * ly + nz * lz) / nl : 0.25;
                    byte sh = (byte)Math.Max(1, Math.Round(Clamp(f, 0, 1) * 255));

                    double area = (pbx - pax) * (pcy - pay) - (pby - pay) * (pcx - pax);
                    if (area < 0)
                    {
                        (pbx, pcx) = (pcx, pbx);
                        (pby, pcy) = (pcy, pby);
                        (pbz, pcz) = (pcz, pbz);
                        area = -area;
                    }

                    bool hit = false;
                    if (area > 1e-9)
                    {
                        // Pixel i has its center at i + 0.5.
                        int minX = Math.Max(0, (int)Math.Ceiling(Math.Min(pax, Math.Min(pbx, pcx)) - 0.5));
                        int maxX = Math.Min(pw - 1, (int)Math.Floor(Math.Max(pax, Math.Max(pbx, pcx)) - 0.5));
                        int minY = Math.Max(0, (int)Math.Ceiling(Math.Min(pay, Math.Min(pby, pcy)) - 0.5));
                        int maxY = Math.Min(ph - 1, (int)Math.Floor(Math.Max(pay, Math.Max(pby, pcy)) - 0.5));
                        if (minX <= maxX && minY <= maxY)
                        {
                            double inv = 1.0 / area;
                            // e0 = edge a->b (weight of c), e1 = b->c (weight of a), e2 = c->a (weight of b).
                            double e0x = -(pby - pay), e0y = pbx - pax;
                            double e1x = -(pcy - pby), e1y = pcx - pbx;
                            double e2x = -(pay - pcy), e2y = pax - pcx;
                            double cx0 = minX + 0.5, cy0 = minY + 0.5;
                            double r0 = (pbx - pax) * (cy0 - pay) - (pby - pay) * (cx0 - pax);
                            double r1 = (pcx - pbx) * (cy0 - pby) - (pcy - pby) * (cx0 - pbx);
                            double r2 = (pax - pcx) * (cy0 - pcy) - (pay - pcy) * (cx0 - pcx);
                            for (int y = minY; y <= maxY; y++)
                            {
                                double w0 = r0, w1 = r1, w2 = r2;
                                int row = y * pw;
                                for (int x = minX; x <= maxX; x++)
                                {
                                    if (w0 >= 0 && w1 >= 0 && w2 >= 0)
                                    {
                                        hit = true;
                                        float z = (float)((w1 * paz + w2 * pbz + w0 * pcz) * inv);
                                        int idx = row + x;
                                        if (z > zb[idx])
                                        {
                                            zb[idx] = z;
                                            shade[idx] = sh;
                                        }
                                    }
                                    w0 += e0x;
                                    w1 += e1x;
                                    w2 += e2x;
                                }
                                r0 += e0y;
                                r1 += e1y;
                                r2 += e2y;
                            }
                        }
                    }
                    if (!hit)
                    {
                        float zTop = (float)Math.Max(paz, Math.Max(pbz, pcz));
                        double lab = Dist2(pax, pay, pbx, pby), lbc = Dist2(pbx, pby, pcx, pcy), lca = Dist2(pcx, pcy, pax, pay);
                        if (lab >= lbc && lab >= lca) PlotLine(shade, zb, pw, ph, pax, pay, pbx, pby, zTop, sh);
                        else if (lbc >= lca) PlotLine(shade, zb, pw, ph, pbx, pby, pcx, pcy, zTop, sh);
                        else PlotLine(shade, zb, pw, ph, pcx, pcy, pax, pay, zTop, sh);
                    }
                }
            }
            v.Shade = shade;
        }

        private static double Dist2(double ax, double ay, double bx, double by) => (bx - ax) * (bx - ax) + (by - ay) * (by - ay);

        // DDA from a to b; pixel i covers [i, i + 1).
        private static void PlotLine(byte[] shade, float[] zb, int pw, int ph, double ax, double ay, double bx, double by, float z, byte sh)
        {
            double dx = bx - ax, dy = by - ay;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            for (int i = 0; i <= steps; i++)
            {
                double t = steps == 0 ? 0 : (double)i / steps;
                int x = (int)Math.Floor(ax + dx * t), y = (int)Math.Floor(ay + dy * t);
                if (x < 0 || y < 0 || x >= pw || y >= ph) continue;
                int idx = y * pw + x;
                if (z > zb[idx])
                {
                    zb[idx] = z;
                    shade[idx] = sh;
                }
            }
        }

        private static uint[] BuildLut()
        {
            static uint Pack(double r, double g, double b, double a) =>
                ((uint)Math.Round(a * 255) << 24) | ((uint)Math.Round(r * a * 255) << 16)
                | ((uint)Math.Round(g * a * 255) << 8) | (uint)Math.Round(b * a * 255);

            double ar = AccentColor.R / 255.0, ag = AccentColor.G / 255.0, ab = AccentColor.B / 255.0;
            var lut = new uint[256];
            for (int l = 1; l < 256; l++)
            {
                double f = l / 255.0;
                lut[l] = Pack(ar * f, ag * f, ab * f, 1);
            }
            return lut;   // lut[0] = 0: empty
        }

        // The box from two stage points, converted to model mm with the view's projection.
        private void SetBoxFromRect(Rect r)
        {
            View? v = view;
            if (v == null) return;
            double a0 = v.A0 + (r.Left - v.Ox) / v.K, a1 = v.A0 + (r.Right - v.Ox) / v.K;
            double b1 = v.B1 - (r.Top - v.Oy) / v.K, b0 = v.B1 - (r.Bottom - v.Oy) / v.K;
            SetBox(a0, b0, a1, b1);
        }

        private void SetBox(double a0, double b0, double a1, double b1)
        {
            boxA0 = Math.Min(a0, a1);
            boxA1 = Math.Max(a0, a1);
            boxB0 = Math.Min(b0, b1);
            boxB1 = Math.Max(b0, b1);
            hasBox = true;
            openPending = false;
            message = null;
            LayoutBox();
        }

        private void ClearBox()
        {
            hasBox = false;
            openPending = false;
            message = null;
            LayoutBox();
        }

        // Dims the stage outside the box, then the outline and the size label at its top-left inside.
        private void LayoutBox()
        {
            View? v = view;
            if (v == null || !hasBox)
            {
                overlay.Visibility = Visibility.Collapsed;
                return;
            }
            double x0 = Clamp(v.Ox + (boxA0 - v.A0) * v.K, 0, v.W), x1 = Clamp(v.Ox + (boxA1 - v.A0) * v.K, 0, v.W);
            double y0 = Clamp(v.Oy + (v.B1 - boxB1) * v.K, 0, v.H), y1 = Clamp(v.Oy + (v.B1 - boxB0) * v.K, 0, v.H);
            dimStage.Rect = new Rect(0, 0, v.W, v.H);
            dimHole.Rect = new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
            Place(outline, x0, y0, x1 - x0, y1 - y0);
            sizeLabel.Text = $"{Mm(boxA1 - boxA0)} × {Mm(boxB1 - boxB0)} mm";
            Canvas.SetLeft(sizeLabel, x0 + 4);
            Canvas.SetTop(sizeLabel, y0 + 3);
            overlay.Visibility = Visibility.Visible;
        }

        private static void Place(FrameworkElement e, double x, double y, double w, double h)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            e.Width = Math.Max(0, w);
            e.Height = Math.Max(0, h);
        }

        private void OnStageDown(object sender, MouseButtonEventArgs e)
        {
            if (!CanEdit) return;
            e.Handled = true;
            pressed = true;
            dragging = false;
            pressAt = e.GetPosition(stage);
            stage.CaptureMouse();
        }

        private void OnStageMove(object sender, MouseEventArgs e)
        {
            if (!CanEdit || !pressed) return;
            Point p = e.GetPosition(stage);
            if (!dragging && (Math.Abs(p.X - pressAt.X) > DragThreshold || Math.Abs(p.Y - pressAt.Y) > DragThreshold))
                dragging = true;
            if (!dragging) return;
            // Dragging again replaces the box.
            SetBoxFromRect(DragRect(p));
            UpdateAll();
        }

        private void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool wasDrag = dragging;
            Point p = e.GetPosition(stage);
            pressed = dragging = false;
            stage.ReleaseMouseCapture();
            if (!CanEdit || !wasDrag) return;   // a click without a drag does nothing
            SetBoxFromRect(DragRect(p));
            UpdateAll();
        }

        private Rect DragRect(Point p)
        {
            double x = Clamp(p.X, 0, stage.Width), y = Clamp(p.Y, 0, stage.Height);
            return new Rect(new Point(pressAt.X, pressAt.Y), new Point(x, y));
        }

        // Pill like the parts window's: accent when selected.
        private Border MakePill(string label, Func<bool> selected, Action pick, out SolidColorBrush fill)
        {
            var brush = new SolidColorBrush(selected() ? AccentColor : PillIdle);
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
                Margin = new Thickness(0, 0, 6, 0),
                Background = brush,
                Child = text,
                Cursor = Cursors.Hand,
            };
            pill.MouseEnter += (_, _) =>
            {
                if (CanEdit && !selected()) AnimateColor(brush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(brush, selected() ? AccentColor : PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                pick();
            };
            fill = brush;
            return pill;
        }

        private static string Count(int n, string one, string many) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

        // "12.5" style, no trailing zeros.
        private static string Mm(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        private static string ViewName(char axis) => axis switch
        {
            'y' => "front",
            'x' => "side",
            _ => "top",
        };

        private void UpdateAll()
        {
            for (int i = 0; i < viewFills.Length; i++)
            {
                SolidColorBrush b = viewFills[i];
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = ViewAxes[i] == viewAxis ? AccentColor : PillIdle;
            }
            for (int i = 0; i < keepFills.Length; i++)
            {
                SolidColorBrush b = keepFills[i];
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = Keeps[i] == keep ? AccentColor : PillIdle;
            }
            double rowOpacity = CanEdit || saved ? 1 : 0.5;
            viewRow.Opacity = rowOpacity;
            keepRow.Opacity = rowOpacity;
            UpdateReadout();
            UpdateSave();
        }

        private void UpdateReadout()
        {
            if (scene == null || view == null)
            {
                bigText.Text = loading ? "Reading model" : failed ? "Failed" : " ";
                bigText.Foreground = failed ? FailBrush : SubBrush;
                bigSub.Text = " ";
                return;
            }
            bigText.Text = hasBox
                ? $"Box {Mm(boxA1 - boxA0)} × {Mm(boxB1 - boxB0)} mm, seen from the {ViewName(viewAxis)}"
                : building ? $"Drawing the {ViewName(viewAxis)} view" : "Drag a box over the model";
            bigText.Foreground = Brushes.White;
            string? note = busy || saved ? null : message;
            bigSub.Text = note ?? Hint;
            bigSub.Foreground = note != null ? Brushes.White : SubBrush;
        }

        private void UpdateSave()
        {
            bool can = CanSave;
            save.Opacity = busy || can ? 1 : 0.4;
            save.Cursor = can ? Cursors.Hand : null;
            save.ToolTip = busy || view == null ? null : SaveBlocker();
            saveGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            saveText.Text = busy ? "Saving..." : openPending ? "Save anyway" : "Save";
            if (busy && saveSpinner.Visibility != Visibility.Visible) StartSpinner(saveSpinner, saveSpinnerRotate);
            else if (!busy && saveSpinner.Visibility == Visibility.Visible) StopSpinner(saveSpinner, saveSpinnerRotate);
        }

        // Alt alone must not put the window into system menu mode, which would eat the next key.
        private static bool IsAltKey(KeyEventArgs e) =>
            e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt);

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (IsAltKey(e))
            {
                e.Handled = true;
                return;
            }
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
                    // Esc clears a box first, then closes.
                    if (hasBox && CanEdit)
                    {
                        ClearBox();
                        UpdateAll();
                    }
                    else CloseAnimated();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    Save();
                    break;
            }
        }

        private async void Save()
        {
            if (!CanSave || scene == null) return;
            busy = true;
            message = null;
            UpdateAll();

            string file = path;
            Scene s = scene;
            char axis = viewAxis;
            double a0 = boxA0, b0 = boxB0, a1 = boxA1, b1 = boxB1;
            string k = keep;
            bool allowOpen = openPending;
            try
            {
                var (outputs, _) = await Task.Run(() => ModelCut.Save(file, s, axis, a0, b0, a1, b1, k, allowOpen));
                busy = false;
                saved = true;
                ExitCode = 0;
                Log.Write($"Cut saved: {string.Join(", ", outputs)}");
                UpdateAll();
                if (hiddenWhileBusy || !IsVisible)
                {
                    Close();
                    return;
                }
                // An obj keeps its mtl next to it; name the models only.
                List<string> models = outputs.Where(o => !o.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase)).ToList();
                ShowStatus(RadialWindow.OkGlyph, OkColor, "Saved",
                    models.Count == 1 ? IOPath.GetFileName(models[0]) : Count(models.Count, "file", "files"));
                await Task.Delay(TimeSpan.FromSeconds(CloseDelaySeconds));
                CloseAnimated();
            }
            catch (Exception ex) when ((ex is CutOpenException && !allowOpen) || ex is ArgumentException)
            {
                // Not a failure: the box stays editable. Open faces turn the button into "Save anyway".
                busy = false;
                if (ex is CutOpenException) openPending = true;
                message = ex.Message;
                Log.Write($"Cut: {path} ({axis} {a0},{b0},{a1},{b1}, keep={k}): {ex.Message}");
                UpdateAll();
                if (autoRun)
                {
                    Log.Write($"TYPELESS_AUTOCUT: {ex.Message}, nothing saved");
                    CloseAnimated();
                    return;
                }
                if (hiddenWhileBusy || !IsVisible) Close();
            }
            catch (Exception ex)
            {
                busy = false;
                failed = true;
                Log.Write($"Cut failed: {path} ({axis} {a0},{b0},{a1},{b1}, keep={k}, open={allowOpen}): {ex}");
                UpdateAll();
                if (hiddenWhileBusy || !IsVisible || autoRun)
                {
                    CloseAnimated();
                    return;
                }
                ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
            }
        }

        private void ShowStatus(string glyph, Color color, string titleText, string subText)
        {
            statusGlyph.Text = glyph;
            statusGlyph.Foreground = Frozen(color);
            statusTitle.Text = titleText;
            statusSub.Text = subText;
            status.Visibility = Visibility.Visible;
            // The view stays as a dim backdrop behind the status pill.
            image.Opacity = 0.35;
            overlay.Visibility = Visibility.Hidden;
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

        // Dev only (--cut-preview): never shown and nothing is saved. Loads the real file synchronously,
        // sized for a 1920x1080 work area and rasterized at 2x (the PNG is 192 dpi). "select" draws a box
        // over the middle half of the top view, "done" and "error" fake the end states on top of it.
        internal void RenderPreview(string outPath, string state, char axis = 'z')
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            if (state is not ("idle" or "select" or "done" or "error"))
                throw new ArgumentException("Unknown state: " + state);
            SetLimits(1920, 1080);
            SetModel(Load(path, maxW, maxH, 2));
            if (axis != 'z')
            {
                View sv = BuildView(scene!, bmin, bmax, axis, maxW, maxH, view!.S);
                views[axis] = sv;
                viewAxis = axis;
                Display(sv);
                UpdateAll();
            }

            root.BeginAnimation(OpacityProperty, null);
            root.Opacity = 1;
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rootScale.ScaleX = 1;
            rootScale.ScaleY = 1;

            if (state != "idle")
            {
                (int ia, int ib) = ModelCut.Axes(axis);
                double wa = bmax[ia] - bmin[ia], wb = bmax[ib] - bmin[ib];
                SetBox(bmin[ia] + 0.25 * wa, bmin[ib] + 0.25 * wb, bmin[ia] + 0.75 * wa, bmin[ib] + 0.75 * wb);
                UpdateAll();
                if (state == "done")
                {
                    saved = true;
                    UpdateAll();
                    ShowStatus(RadialWindow.OkGlyph, OkColor, "Saved", Count(2, "file", "files"));
                }
                else if (state == "error")
                {
                    failed = true;
                    UpdateAll();
                    ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage("Sample failure message for preview."));
                }
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
            using var fs = File.Create(outPath);
            encoder.Save(fs);
        }
    }
}
