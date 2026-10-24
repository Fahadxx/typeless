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
    // Parts panel on the cursor's monitor: the model's objects (ModelParts.Find) seen from the top. Click
    // toggles an object, a dragged box keeps only the objects fully inside it (Shift adds, Alt removes).
    // Enter saves <stem>_parts.<ext> or <stem>_part_N.<ext> via ModelParts.Keep; Esc closes.
    public sealed class PartsWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, StageShare = 0.5, ViewMargin = 16;
        private const double MinStageW = 520, MinStageH = 240, PlaceholderH = 340;
        private const double DragThreshold = 4, CloseDelaySeconds = 2.5;
        private const int MaxLabels = 60;

        private static readonly Brush AccentBrush = Frozen(AccentColor);
        private static readonly Brush FailBrush = Frozen(FailColor);
        private const string Hint ="Click to toggle, drag a box to keep only those (Shift adds, Alt removes)";

        // Premultiplied BGRA per (state, shade): state 0 kept, 1 kept hovered, 2 dropped, 3 dropped hovered.
        private static readonly uint[] Lut = BuildLut();

        // The top view, built once on the worker: per device pixel the topmost piece (-1 empty) and its
        // shade (height plus edge darkening). Screen (DIP) = (Ox + (x - X0) * K, Oy + (Y1 - y) * K).
        private sealed class View
        {
            public double K, Ox, Oy, X0, Y1, W, H, S;
            public int Pw, Ph;
            public int[] Ids = Array.Empty<int>();
            public byte[] Shade = Array.Empty<byte>();
        }

        private sealed class LoadResult
        {
            public Scene Scene = null!;
            public List<Piece> Pieces = null!;
            public View? View;
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
        private readonly Canvas labels;
        private readonly Canvas overlay;
        private readonly Rectangle box;
        private readonly Canvas spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly TextBlock hoverText;
        private readonly Border status;
        private readonly TextBlock statusGlyph;
        private readonly TextBlock statusTitle;
        private readonly TextBlock statusSub;
        private readonly SolidColorBrush[] modeFills = new SolidColorBrush[2];
        private readonly StackPanel modeRow;
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
        private List<Piece>? pieces;
        private View? view;
        private WriteableBitmap? bitmap;
        private uint[] pixels = Array.Empty<uint>();
        private bool[] kept = Array.Empty<bool>();
        private TextBlock[] labelTexts = Array.Empty<TextBlock>();

        // Projected XY bbox per piece, in stage DIPs (box select).
        private double[] bx0 = Array.Empty<double>(), by0 = Array.Empty<double>();
        private double[] bx1 = Array.Empty<double>(), by1 = Array.Empty<double>();

        private bool separate;
        private int hovered = -1;

        // Mouse: pressed on the stage, past the drag threshold, and the press point.
        private bool pressed, dragging;
        private Point pressAt;

        private bool loading, busy, saved, failed, closing, hiddenWhileBusy;

        // 1 until something is saved (Esc, one object, nothing to change and failures all leave nothing).
        public int ExitCode { get; private set; } = 1;

        public PartsWindow(string path) : this(path, false)
        {
        }

        // Preview (dev): RenderPreview loads synchronously and fakes the states; nothing is saved or shown.
        internal PartsWindow(string path, bool preview)
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
                Log.Write($"Parts size read failed: {path}: {ex.Message}");
            }

            Title = "Typeless Parts";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name, object and triangle counts under it, file size right.
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

            // Stage: the top view bitmap, piece numbers, the drag box, spinner and status share one cell.
            image = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            labels = new Canvas { IsHitTestVisible = false };
            box = new Rectangle
            {
                Stroke = AccentBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Frozen(Color.FromArgb(0x22, 0x6C, 0x8C, 0xFF)),
                Visibility = Visibility.Collapsed,
            };
            overlay = new Canvas { IsHitTestVisible = false };
            overlay.Children.Add(box);
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
            };
            stage.Children.Add(image);
            stage.Children.Add(labels);
            stage.Children.Add(overlay);
            stage.Children.Add(spinner);
            stage.Children.Add(status);
            stage.MouseLeftButtonDown += OnStageDown;
            stage.MouseMove += OnStageMove;
            stage.MouseLeftButtonUp += OnStageUp;
            stage.MouseLeave += (_, _) =>
            {
                if (!pressed) SetHovered(-1);
            };
            stage.LostMouseCapture += (_, _) =>
            {
                pressed = dragging = false;
                box.Visibility = Visibility.Collapsed;
            };

            // Hovered piece: name, triangles, size.
            hoverText = new TextBlock
            {
                Text = " ",
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 8, 2, 0),
            };

            // Save mode pills.
            modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            modeRow.Children.Add(MakeModePill(0, "One file"));
            modeRow.Children.Add(MakeModePill(1, "Separate files"));

            // Readout: "Keeping 12 of 40 objects · 210,400 triangles", the hint or why Save is off under it.
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

            // Fixed width (the stage's once loaded), so long names and hints trim instead of resizing the window.
            content = new StackPanel { Orientation = Orientation.Vertical, Width = MinStageW };
            content.Children.Add(header);
            content.Children.Add(stage);
            content.Children.Add(hoverText);
            content.Children.Add(modeRow);
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

        private int KeptCount
        {
            get
            {
                int n = 0;
                foreach (bool k in kept) if (k) n++;
                return n;
            }
        }

        private bool CanEdit => view != null && !busy && !saved && !failed && !closing;

        // Why Save is off, or null when it can save.
        private string? SaveBlocker()
        {
            if (view == null) return " ";
            int n = KeptCount;
            if (n == 0) return "Nothing selected: click objects or drag a box";
            if (!separate && n == kept.Length) return "Everything is kept: drop some objects, or pick Separate files";
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

            if (pieces == null && !loading) LoadModel();
        }

        // Read, find the pieces and rasterize the top view on a worker; spinner meanwhile.
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
                Log.Write($"Parts load failed: {path}: {ex}");
                UpdateAll();
                ShowStatus(RadialWindow.FailGlyph, FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                AutoAbort();
                return;
            }
            if (!SetModel(result))
            {
                AutoAbort();
                return;
            }
            RunAutoParts();
        }

        // Runs on a worker. The view is only built when there are two or more pieces to choose from.
        private static LoadResult Load(string src, double maxW, double maxH, double s)
        {
            Scene scene = ModelBackend.Read(src);
            List<Piece> pieces = ModelParts.Find(scene);
            return new LoadResult
            {
                Scene = scene,
                Pieces = pieces,
                View = pieces.Count >= 2 ? BuildView(scene, pieces, maxW, maxH, s) : null,
            };
        }

        // Header, buffers and labels. False (status shown, rc 1) when there is only one piece.
        private bool SetModel(LoadResult loaded)
        {
            scene = loaded.Scene;
            pieces = loaded.Pieces;
            long tris = 0;
            foreach (Piece p in pieces) tris += p.TriangleCount;
            dimsText.Text = $"{Count(pieces.Count, "object", "objects")} · {tris.ToString("N0", CultureInfo.InvariantCulture)} triangles";
            try
            {
                ModelParts.CheckSeveral(pieces);
            }
            catch (InvalidOperationException ex)
            {
                failed = true;
                Log.Write($"Parts: {path}: {ex.Message}");
                UpdateAll();
                ShowStatus(RadialWindow.FailGlyph, Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF), ex.Message, "Nothing to pick from");
                return false;
            }

            View v = loaded.View!;
            view = v;
            kept = new bool[pieces.Count];
            Array.Fill(kept, true);

            stage.Width = v.W;
            stage.Height = v.H;
            content.Width = v.W;
            bitmap = new WriteableBitmap(v.Pw, v.Ph, 96 * v.S, 96 * v.S, PixelFormats.Pbgra32, null);
            pixels = new uint[v.Pw * v.Ph];
            image.Width = v.W;
            image.Height = v.H;
            image.Source = bitmap;
            image.Visibility = Visibility.Visible;

            int n = pieces.Count;
            bx0 = new double[n];
            by0 = new double[n];
            bx1 = new double[n];
            by1 = new double[n];
            for (int i = 0; i < n; i++)
            {
                Piece p = pieces[i];
                bx0[i] = v.Ox + (p.Min[0] - v.X0) * v.K;
                bx1[i] = v.Ox + (p.Max[0] - v.X0) * v.K;
                by0[i] = v.Oy + (v.Y1 - p.Max[1]) * v.K;
                by1[i] = v.Oy + (v.Y1 - p.Min[1]) * v.K;
            }
            BuildLabels();
            Recolor();
            UpdateAll();
            return true;
        }

        // Piece numbers (1-based, as --model-parts prints them) at each projected bbox center.
        private void BuildLabels()
        {
            labels.Children.Clear();
            if (pieces == null || pieces.Count > MaxLabels)
            {
                labelTexts = Array.Empty<TextBlock>();
                return;
            }
            labelTexts = new TextBlock[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                var text = new TextBlock
                {
                    Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                    FontFamily = TextFont,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = Brushes.White,
                    Background = Frozen(Color.FromArgb(0x99, 0x14, 0x14, 0x18)),
                    Padding = new Thickness(4, 0, 4, 1),
                };
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(text, (bx0[i] + bx1[i]) / 2 - text.DesiredSize.Width / 2);
                Canvas.SetTop(text, (by0[i] + by1[i]) / 2 - text.DesiredSize.Height / 2);
                labels.Children.Add(text);
                labelTexts[i] = text;
            }
        }

        // Test hook: TYPELESS_AUTOPARTS=<spec> (the --keep-parts grammar) selects those pieces once they
        // load and saves; TYPELESS_PARTS_SEPARATE=1 picks Separate files. Same call and output as
        // --keep-parts <file> <spec> [separate]. Invalid spec, nothing selected or no change: rc 1, nothing saved.
        private void RunAutoParts()
        {
            string? auto = Environment.GetEnvironmentVariable("TYPELESS_AUTOPARTS");
            if (string.IsNullOrWhiteSpace(auto) || pieces == null) return;
            List<int> pick;
            try
            {
                pick = ModelParts.ParseSpec(auto, pieces);
            }
            catch (FormatException ex)
            {
                Log.Write($"TYPELESS_AUTOPARTS: {ex.Message}, nothing saved");
                CloseAnimated();
                return;
            }
            Array.Fill(kept, false);
            foreach (int i in pick) kept[i] = true;
            separate = Environment.GetEnvironmentVariable("TYPELESS_PARTS_SEPARATE") == "1";
            Recolor();
            UpdateAll();
            string? blocker = SaveBlocker();
            if (blocker != null)
            {
                Log.Write($"TYPELESS_AUTOPARTS: {auto}: {blocker}, nothing saved");
                CloseAnimated();
                return;
            }
            Save();
        }

        // With the hook set, a load failure or a single piece closes instead of waiting for a key.
        private void AutoAbort()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPELESS_AUTOPARTS"))) return;
            Log.Write("TYPELESS_AUTOPARTS: nothing to pick from, nothing saved");
            CloseAnimated();
        }

        // Height share, capped by the width so a portrait monitor still fits the panel.
        private void SetLimits(double workW, double workH)
        {
            maxH = Math.Max(MinStageH, workH * StageShare);
            maxW = Math.Max(MinStageW, workW * 0.85 - 2 * (Pad + ShadowMargin));
        }

        // Orthographic XY fit with a margin, Y up, then the rasterizer at s device pixels per DIP.
        private static View BuildView(Scene scene, List<Piece> pieces, double maxW, double maxH, double s)
        {
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, z0 = double.PositiveInfinity;
            double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity, z1 = double.NegativeInfinity;
            foreach (Piece p in pieces)
            {
                x0 = Math.Min(x0, p.Min[0]);
                y0 = Math.Min(y0, p.Min[1]);
                z0 = Math.Min(z0, p.Min[2]);
                x1 = Math.Max(x1, p.Max[0]);
                y1 = Math.Max(y1, p.Max[1]);
                z1 = Math.Max(z1, p.Max[2]);
            }
            if (!double.IsFinite(x0 + x1 + y0 + y1 + z0 + z1)) throw new InvalidOperationException("The model has invalid coordinates.");

            double w = x1 - x0, d = y1 - y0;
            double availW = maxW - 2 * ViewMargin, availH = maxH - 2 * ViewMargin;
            double kx = w > 1e-9 ? availW / w : double.PositiveInfinity;
            double ky = d > 1e-9 ? availH / d : double.PositiveInfinity;
            double k = Math.Min(kx, ky);
            if (double.IsInfinity(k)) k = 1;

            // Stage size rounded to whole device pixels so the bitmap maps 1:1.
            int pw = (int)Math.Ceiling(Math.Max(MinStageW, w * k + 2 * ViewMargin) * s);
            int ph = (int)Math.Ceiling(Math.Max(MinStageH, d * k + 2 * ViewMargin) * s);
            var v = new View { K = k, X0 = x0, Y1 = y1, S = s, Pw = pw, Ph = ph, W = pw / s, H = ph / s };
            v.Ox = (v.W - w * k) / 2;
            v.Oy = (v.H - d * k) / 2;
            Rasterize(scene, pieces, v, z0, z1);
            return v;
        }

        // Scan-converts every triangle's XY projection with edge functions at pixel centers, keeping the
        // highest interpolated Z per pixel. Triangles that cover no pixel center (sub-pixel, slivers,
        // vertical walls) plot their longest edge instead, so thin and tiny pieces still show and can be clicked.
        private static void Rasterize(Scene scene, List<Piece> pieces, View v, double zMin, double zMax)
        {
            int pw = v.Pw, ph = v.Ph;
            var ids = new int[pw * ph];
            var zb = new float[pw * ph];
            Array.Fill(ids, -1);
            Array.Fill(zb, float.NegativeInfinity);

            // Pixel = (ax * x + bx, ay * y + by).
            double ax = v.K * v.S, bxo = (v.Ox - v.X0 * v.K) * v.S;
            double ay = -v.K * v.S, byo = (v.Oy + v.Y1 * v.K) * v.S;

            float[] sx = Array.Empty<float>(), sy = Array.Empty<float>();
            int projected = -1;
            for (int pi = 0; pi < pieces.Count; pi++)
            {
                Piece piece = pieces[pi];
                Part part = scene.Parts[piece.PartIndex];
                float[] pos = part.Positions;
                int[] tris = part.Triangles;
                if (projected != piece.PartIndex)
                {
                    // Pieces come ordered by part, so each part is projected once.
                    int vc = part.VertexCount;
                    sx = new float[vc];
                    sy = new float[vc];
                    for (int i = 0; i < vc; i++)
                    {
                        sx[i] = (float)(ax * pos[i * 3] + bxo);
                        sy[i] = (float)(ay * pos[i * 3 + 1] + byo);
                    }
                    projected = piece.PartIndex;
                }

                foreach (int t in piece.Triangles)
                {
                    int ia = tris[t * 3], ib = tris[t * 3 + 1], ic = tris[t * 3 + 2];
                    double pax = sx[ia], pay = sy[ia], paz = pos[ia * 3 + 2];
                    double pbx = sx[ib], pby = sy[ib], pbz = pos[ib * 3 + 2];
                    double pcx = sx[ic], pcy = sy[ic], pcz = pos[ic * 3 + 2];
                    if (!double.IsFinite(pax + pay + paz + pbx + pby + pbz + pcx + pcy + pcz)) continue;

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
                                            ids[idx] = pi;
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
                        if (lab >= lbc && lab >= lca) PlotLine(ids, zb, pw, ph, pax, pay, pbx, pby, zTop, pi);
                        else if (lbc >= lca) PlotLine(ids, zb, pw, ph, pbx, pby, pcx, pcy, zTop, pi);
                        else PlotLine(ids, zb, pw, ph, pcx, pcy, pax, pay, zTop, pi);
                    }
                }
            }

            // Shade: brighter = higher, darker where the neighbor is another piece or empty.
            var shade = new byte[pw * ph];
            double span = zMax - zMin;
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int i = y * pw + x;
                    int id = ids[i];
                    if (id < 0) continue;
                    double f = span > 1e-9 ? 0.4 + 0.6 * Clamp((zb[i] - zMin) / span, 0, 1) : 0.85;
                    bool edge = (x > 0 && ids[i - 1] != id) || (x < pw - 1 && ids[i + 1] != id)
                        || (y > 0 && ids[i - pw] != id) || (y < ph - 1 && ids[i + pw] != id);
                    if (edge) f *= 0.55;
                    shade[i] = (byte)Math.Max(1, Math.Round(f * 255));
                }
            }
            v.Ids = ids;
            v.Shade = shade;
        }

        private static double Dist2(double ax, double ay, double bx, double by) => (bx - ax) * (bx - ax) + (by - ay) * (by - ay);

        // DDA from a to b; pixel i covers [i, i + 1).
        private static void PlotLine(int[] ids, float[] zb, int pw, int ph, double ax, double ay, double bx, double by, float z, int id)
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
                    ids[idx] = id;
                }
            }
        }

        private static uint[] BuildLut()
        {
            static uint Pack(double r, double g, double b, double a) =>
                ((uint)Math.Round(a * 255) << 24) | ((uint)Math.Round(r * a * 255) << 16)
                | ((uint)Math.Round(g * a * 255) << 8) | (uint)Math.Round(b * a * 255);

            double ar = AccentColor.R / 255.0, ag = AccentColor.G / 255.0, ab = AccentColor.B / 255.0;
            const double Lift = 0.45, Grey = 0.62;
            var lut = new uint[4 * 256];
            for (int l = 0; l < 256; l++)
            {
                double f = l / 255.0;
                lut[l] = Pack(ar * f, ag * f, ab * f, 1);
                lut[256 + l] = Pack((ar + (1 - ar) * Lift) * f, (ag + (1 - ag) * Lift) * f, (ab + (1 - ab) * Lift) * f, 1);
                lut[512 + l] = Pack(Grey * f, Grey * f, Grey * f, 0.3);
                lut[768 + l] = Pack(Grey * f, Grey * f, Grey * f, 0.55);
            }
            lut[0] = lut[256] = lut[512] = lut[768] = 0;   // shade 0 = empty
            return lut;
        }

        // Selection and hover only recolor from the buffers; nothing is rasterized again.
        private void Recolor()
        {
            View? v = view;
            if (v == null || bitmap == null) return;
            var state = new int[kept.Length];
            for (int i = 0; i < state.Length; i++) state[i] = ((kept[i] ? 0 : 2) + (i == hovered ? 1 : 0)) * 256;
            int[] ids = v.Ids;
            byte[] shade = v.Shade;
            for (int i = 0; i < pixels.Length; i++)
            {
                int id = ids[i];
                pixels[i] = id < 0 ? 0u : Lut[state[id] + shade[i]];
            }
            bitmap.WritePixels(new Int32Rect(0, 0, v.Pw, v.Ph), pixels, v.Pw * 4, 0);
            for (int i = 0; i < labelTexts.Length; i++) labelTexts[i].Opacity = kept[i] ? 1 : 0.45;
        }

        // Topmost piece under a stage point, from the id buffer (-1 for none).
        private int PieceAt(Point p)
        {
            View? v = view;
            if (v == null) return -1;
            int x = (int)Math.Floor(p.X * v.S), y = (int)Math.Floor(p.Y * v.S);
            if (x < 0 || y < 0 || x >= v.Pw || y >= v.Ph) return -1;
            return v.Ids[y * v.Pw + x];
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
            if (!CanEdit) return;
            Point p = e.GetPosition(stage);
            if (!pressed)
            {
                SetHovered(PieceAt(p));
                return;
            }
            if (!dragging && (Math.Abs(p.X - pressAt.X) > DragThreshold || Math.Abs(p.Y - pressAt.Y) > DragThreshold))
            {
                dragging = true;
                box.Visibility = Visibility.Visible;
                SetHovered(-1);
            }
            if (!dragging) return;
            Rect r = DragRect(p);
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            box.Width = r.Width;
            box.Height = r.Height;
        }

        private void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool wasDrag = dragging;
            Point p = e.GetPosition(stage);
            pressed = dragging = false;
            stage.ReleaseMouseCapture();
            box.Visibility = Visibility.Collapsed;
            if (!CanEdit) return;

            if (wasDrag)
            {
                // Replace by default; Shift adds, Alt removes.
                Rect r = DragRect(p);
                ModifierKeys mods = Keyboard.Modifiers;
                bool add = mods.HasFlag(ModifierKeys.Shift), remove = !add && mods.HasFlag(ModifierKeys.Alt);
                for (int i = 0; i < kept.Length; i++)
                {
                    bool inside = bx0[i] >= r.Left && bx1[i] <= r.Right && by0[i] >= r.Top && by1[i] <= r.Bottom;
                    if (add) kept[i] |= inside;
                    else if (remove) kept[i] &= !inside;
                    else kept[i] = inside;
                }
            }
            else
            {
                int id = PieceAt(p);
                if (id < 0) return;
                kept[id] = !kept[id];
            }
            hovered = PieceAt(p);
            Recolor();
            UpdateAll();
        }

        private Rect DragRect(Point p)
        {
            double x = Clamp(p.X, 0, stage.Width), y = Clamp(p.Y, 0, stage.Height);
            return new Rect(new Point(pressAt.X, pressAt.Y), new Point(x, y));
        }

        private void SetHovered(int id)
        {
            if (id == hovered) return;
            hovered = id;
            Recolor();
            UpdateHover();
        }

        private void SetAll(Func<bool, bool> change)
        {
            if (!CanEdit) return;
            for (int i = 0; i < kept.Length; i++) kept[i] = change(kept[i]);
            Recolor();
            UpdateAll();
        }

        private void SetSeparate(bool on)
        {
            if (!CanEdit) return;
            separate = on;
            UpdateAll();
        }

        // Pill like the resize window's: accent when selected.
        private Border MakeModePill(int index, string label)
        {
            var brush = new SolidColorBrush(index == 0 ? AccentColor : PillIdle);
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
            bool Selected() => (index == 1) == separate;
            pill.MouseEnter += (_, _) =>
            {
                if (CanEdit && !Selected()) AnimateColor(brush, PillHover);
            };
            pill.MouseLeave += (_, _) => AnimateColor(brush, Selected() ? AccentColor : PillIdle);
            pill.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetSeparate(index == 1);
            };
            modeFills[index] = brush;
            return pill;
        }

        private static string Count(int n, string one, string many) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

        // "12.5" style, no trailing zeros.
        private static string Mm(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        private void UpdateAll()
        {
            for (int i = 0; i < modeFills.Length; i++)
            {
                SolidColorBrush b = modeFills[i];
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = (i == 1) == separate ? AccentColor : PillIdle;
            }
            modeRow.Opacity = CanEdit || saved ? 1 : 0.5;
            UpdateReadout();
            UpdateHover();
            UpdateSave();
        }

        private void UpdateReadout()
        {
            if (pieces == null || view == null)
            {
                // A single piece is not a failure of the read: the status pill says why.
                bool readFailed = failed && pieces == null;
                bigText.Text = loading ? "Reading model" : readFailed ? "Failed" : " ";
                bigText.Foreground = readFailed ? FailBrush : SubBrush;
                bigSub.Text = " ";
                return;
            }
            int n = 0;
            long tris = 0;
            for (int i = 0; i < kept.Length; i++)
            {
                if (!kept[i]) continue;
                n++;
                tris += pieces[i].TriangleCount;
            }
            bigText.Text = $"Keeping {n.ToString("N0", CultureInfo.InvariantCulture)} of {Count(pieces.Count, "object", "objects")} · "
                + $"{tris.ToString("N0", CultureInfo.InvariantCulture)} triangles";
            bigText.Foreground = Brushes.White;
            string? blocker = busy || saved ? null : SaveBlocker();
            bigSub.Text = blocker ?? Hint;
            bigSub.Foreground = blocker != null ? Brushes.White : SubBrush;
        }

        // "name · 12,345 triangles · 40 × 30.5 × 12 mm" for the hovered piece.
        private void UpdateHover()
        {
            if (pieces == null || hovered < 0 || hovered >= pieces.Count)
            {
                hoverText.Text = view != null ? "Ctrl+A keeps all, I inverts" : " ";
                return;
            }
            Piece p = pieces[hovered];
            hoverText.Text = $"{(hovered + 1).ToString(CultureInfo.InvariantCulture)}. {p.Name} · "
                + $"{p.TriangleCount.ToString("N0", CultureInfo.InvariantCulture)} triangles · "
                + $"{Mm(p.Max[0] - p.Min[0])} × {Mm(p.Max[1] - p.Min[1])} × {Mm(p.Max[2] - p.Min[2])} mm"
                + (kept[hovered] ? "" : " · dropped");
        }

        private void UpdateSave()
        {
            bool can = CanSave;
            save.Opacity = busy || can ? 1 : 0.4;
            save.Cursor = can ? Cursors.Hand : null;
            save.ToolTip = busy || view == null ? null : SaveBlocker();
            saveGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            saveText.Text = busy ? "Saving..." : "Save";
            if (busy && saveSpinner.Visibility != Visibility.Visible) StartSpinner(saveSpinner, saveSpinnerRotate);
            else if (!busy && saveSpinner.Visibility == Visibility.Visible) StopSpinner(saveSpinner, saveSpinnerRotate);
        }

        // Alt alone (Alt-drag) must not put the window into system menu mode, which would eat the next key.
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
                    CloseAnimated();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    Save();
                    break;
                case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                    e.Handled = true;
                    SetAll(_ => true);
                    break;
                case Key.I:
                    e.Handled = true;
                    SetAll(k => !k);
                    break;
            }
        }

        // Kept indices in piece order, the same list ModelParts.ParseSpec gives the CLI.
        private List<int> KeptIndices()
        {
            var list = new List<int>();
            for (int i = 0; i < kept.Length; i++)
                if (kept[i]) list.Add(i);
            return list;
        }

        private async void Save()
        {
            if (!CanSave || scene == null || pieces == null) return;
            busy = true;
            SetHovered(-1);
            UpdateAll();

            string file = path;
            Scene s = scene;
            List<Piece> all = pieces;
            List<int> keep = KeptIndices();
            bool sep = separate;
            try
            {
                List<string> outputs = await Task.Run(() => ModelParts.Keep(file, s, all, keep, sep));
                busy = false;
                saved = true;
                ExitCode = 0;
                Log.Write($"Parts saved: {string.Join(", ", outputs)}");
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
            catch (Exception ex)
            {
                busy = false;
                failed = true;
                Log.Write($"Parts failed: {path} (kept {keep.Count} of {all.Count}, {(sep ? "separate" : "one file")}): {ex}");
                UpdateAll();
                if (hiddenWhileBusy || !IsVisible)
                {
                    Close();
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
            labels.Visibility = Visibility.Hidden;
            hoverText.Text = " ";
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

        // Dev only (--parts-preview): never shown and nothing is saved. Loads the real file synchronously,
        // sized for a 1920x1080 work area and rasterized at 2x (the PNG is 192 dpi). "select" keeps pieces
        // 1-2, "separate" adds the Separate pill, "done" and "error" fake the end states.
        internal void RenderPreview(string outPath, string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            if (state is not ("idle" or "select" or "separate" or "done" or "error"))
                throw new ArgumentException("Unknown state: " + state);
            SetLimits(1920, 1080);
            bool many = SetModel(Load(path, maxW, maxH, 2));

            root.BeginAnimation(OpacityProperty, null);
            root.Opacity = 1;
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rootScale.ScaleX = 1;
            rootScale.ScaleY = 1;

            if (many && state != "idle")
            {
                for (int i = 0; i < kept.Length; i++) kept[i] = i < 2;
                separate = state == "separate";
                Recolor();
                UpdateAll();
                string stem = IOPath.GetFileNameWithoutExtension(path), ext = IOPath.GetExtension(path);
                if (state == "done")
                {
                    saved = true;
                    UpdateAll();
                    ShowStatus(RadialWindow.OkGlyph, OkColor, "Saved", stem + "_parts" + ext);
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
