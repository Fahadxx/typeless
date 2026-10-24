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
using System.Windows.Threading;
using IOPath = System.IO.Path;
using WinPdf = Windows.Data.Pdf;
using static Typeless.Theme;

namespace Typeless
{
    // Borderless pages panel on the cursor's monitor: a thumbnail grid to pick, reorder and rotate
    // pages; Enter saves <stem>_pages.pdf. Pages are copied by PdfEdit.Pages, never rasterized.
    public sealed class PagesWindow : Window
    {
        private const double ShadowMargin = 24, Pad = 14, WorkShare = 0.70, Chrome = 150, MinContentW = 640;
        private const double ThumbSide = 180, TilePad = 8, LabelH = 22, Gap = 10, ThumbBarRoom = 12;
        private const double TileW = ThumbSide + 2 * TilePad, TileH = ThumbSide + LabelH + 2 * TilePad;
        private const double SlotW = TileW + Gap, SlotH = TileH + Gap;
        private const double DragSlop = 4, EdgeScroll = 36;

        private static readonly Color TileIdle = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF);
        private static readonly Color TileHover = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        private static readonly Brush AccentBrush = Frozen(AccentColor);

        // One grid cell. Page is the 1-based source page; Angle accumulates (clockwise) so the
        // animation always turns the short way, and is normalized only for the plan.
        private sealed class Tile
        {
            public int Page;
            public bool Kept = true, Requested;
            public int Angle;
            public Border Root = null!;
            public SolidColorBrush Fill = null!;
            public Grid Body = null!;
            public Image Thumb = null!;
            public RotateTransform Turn = null!;
            public Canvas Spinner = null!;
            public Grid OffMark = null!;
            public Border RotateButton = null!;
        }

        private readonly string path;
        private readonly bool previewMode;
        private readonly List<Tile> tiles = new List<Tile>();

        private readonly Grid frame;
        private readonly Grid root;
        private readonly ScaleTransform rootScale;
        private readonly TextBlock nameText;
        private readonly TextBlock countText;
        private readonly Grid stage;
        private readonly ScrollViewer scroller;
        private readonly Grid host;
        private readonly WrapPanel wrap;
        private readonly Rectangle dropBar;
        private readonly Rectangle scrollThumb;
        private readonly Canvas spinner;
        private readonly RotateTransform spinnerRotate;
        private readonly RotateTransform tileSpin;
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
        private readonly DispatcherTimer dragTimer;

        private IntPtr hwnd;
        private bool haveMonitor;
        private NativeMethods.RECT work;
        private double scale = 1.0;

        // Grid geometry: fixed slots, so the visible range and drop position are plain index math.
        private int maxCols = 6, cols = 4;
        private double maxViewH = 700, viewH = SlotH;
        private double renderScale = 1;

        // The WinRT document stays open for the window lifetime; one render in flight at a time.
        private WinPdf.PdfDocument? doc;
        private bool pumping, autoChecked;

        private int focus = -1, keptCount;
        private Tile? ringTile;
        private Tile? hovered;

        // A press only becomes a drag past DragSlop; a press that never does is a click (toggle).
        private Tile? pressTile;
        private Point pressPt;
        private bool dragging;
        private int dropIndex;

        private bool loaded, busy, saved, failed, closing, hiddenWhileBusy;

        public int ExitCode { get; private set; }

        public PagesWindow(string path) : this(path, false)
        {
        }

        // Preview (dev): loads and renders synchronously inside RenderPreview, never shown.
        internal PagesWindow(string path, bool preview)
        {
            this.path = path;
            previewMode = preview;

            Title = "Typeless Pages";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = true;

            // Header: file name left, page count right.
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
            countText = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
            DockPanel.SetDock(countText, Dock.Right);
            header.Children.Add(countText);
            header.Children.Add(nameText);

            // Grid: fixed-size slots in a WrapPanel; the insertion bar sits on an overlay in the
            // same scroll content, so it scrolls with the tiles.
            wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = SlotW, ItemHeight = SlotH };
            dropBar = new Rectangle
            {
                Width = 3,
                Height = TileH,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = AccentBrush,
                Visibility = Visibility.Collapsed,
            };
            var overlay = new Canvas { IsHitTestVisible = false };
            overlay.Children.Add(dropBar);
            host = new Grid { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(wrap);
            host.Children.Add(overlay);
            host.MouseMove += OnHostMove;
            host.MouseLeftButtonUp += OnHostUp;
            host.LostMouseCapture += (_, _) => EndDrag();

            // Hidden scrollbar (the wheel still scrolls); a thin thumb on the right shows the position.
            scroller = new ScrollViewer
            {
                Content = host,
                Focusable = false,
                CanContentScroll = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            scroller.ScrollChanged += OnScrollChanged;
            scrollThumb = new Rectangle
            {
                Width = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };

            spinnerRotate = new RotateTransform(0, 18, 18);
            spinner = MakeSpinner(36, 15, 3, AccentBrush, spinnerRotate);
            // All tile spinners share one rotation, so hundreds of tiles cost one animation.
            tileSpin = new RotateTransform(0, 12, 12);

            stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            stage.Children.Add(scroller);
            stage.Children.Add(scrollThumb);
            stage.Children.Add(spinner);

            // Toolbar: Keep all / Drop all / Invert left; readout, Cancel, Save right.
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pillRow.Children.Add(MakeActionPill("Keep all", () => SetKept(_ => true)));
            pillRow.Children.Add(MakeActionPill("Drop all", () => SetKept(_ => false)));
            pillRow.Children.Add(MakeActionPill("Invert", () => SetKept(t => !t.Kept)));

            readout = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 12, 0),
                Text = " ",
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
            // Disabled (dim, no hover) while no page is kept.
            save = MakeButton(saveContent, AccentColor, AccentHover, null, Save, () => busy || keptCount == 0);
            save.Margin = new Thickness(8, 0, 0, 0);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(readout);
            right.Children.Add(cancel);
            right.Children.Add(save);
            var toolbar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            toolbar.Children.Add(right);
            toolbar.Children.Add(pillRow);

            var stack = new StackPanel { Orientation = Orientation.Vertical, MinWidth = MinContentW };
            stack.Children.Add(header);
            stack.Children.Add(stage);
            stack.Children.Add(toolbar);

            // The status pill overlays the whole content, like the trim window.
            status = MakeStatus(out statusGlyph, out statusTitle, out statusSub);
            var content = new Grid();
            content.Children.Add(stack);
            content.Children.Add(status);

            // The shadow sits on a sibling plate so the effect does not re-render the thumbnails.
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

            // Edge auto-scroll while dragging a tile near the top or bottom of the grid.
            dragTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            dragTimer.Tick += (_, _) => OnDragTick();

            SetLayout(1920, 1080);

            SourceInitialized += (_, _) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                haveMonitor = NativeMethods.TryGetCursorMonitor(out _, out work, out scale);
                renderScale = Math.Max(1, scale);
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
                // WinRT PdfDocument has no Close; dropping the reference releases it.
                closing = true;
                doc = null;
                dragTimer.Stop();
                tileSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            };

            UpdateReadout();
        }

        private bool CanEdit => loaded && !busy && !saved && !failed && !closing;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
            Focus();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 160, ease));
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 160, ease));

            if (!loaded && doc == null) LoadDocument();
        }

        // Opens the PDF on a worker, builds the empty tiles, then the pump fills them in.
        private async void LoadDocument()
        {
            StartSpinner(spinner, spinnerRotate);
            try
            {
                string src = path;
                WinPdf.PdfDocument opened = await Task.Run(() => OpenDoc(src));
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                doc = opened;
                BuildTiles((int)opened.PageCount);
            }
            catch (Exception ex)
            {
                if (closing) return;
                StopSpinner(spinner, spinnerRotate);
                failed = true;
                ExitCode = 1;
                Log.Write($"Pages load failed: {path}: {ex}");
                ShowStatus("", FailColor, "Failed", RadialWindow.ShortMessage(ex.Message));
                return;
            }
            Pump();
        }

        // Runs on a worker. Same open and password rules as PdfBackend.RenderPages.
        private static WinPdf.PdfDocument OpenDoc(string pdfPath)
        {
            WinPdf.PdfDocument opened;
            try
            {
                Windows.Storage.StorageFile file = Windows.Storage.StorageFile
                    .GetFileFromPathAsync(IOPath.GetFullPath(pdfPath)).AsTask().GetAwaiter().GetResult();
                opened = WinPdf.PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Write($"PDF load failed for {pdfPath}: {ex}");
                throw new InvalidOperationException(
                    $"Cannot open {IOPath.GetFileName(pdfPath)}. It may be password protected or damaged.", ex);
            }
            if (opened.IsPasswordProtected)
                throw new InvalidOperationException($"{IOPath.GetFileName(pdfPath)} is password protected.");
            if (opened.PageCount == 0)
                throw new InvalidOperationException($"{IOPath.GetFileName(pdfPath)} has no pages.");
            return opened;
        }

        // Runs on a worker: one page as PNG bytes, its long side fitting side pixels.
        private static byte[] RenderThumb(WinPdf.PdfDocument pdf, uint index, double side)
        {
            using WinPdf.PdfPage page = pdf.GetPage(index);
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var options = new WinPdf.PdfPageRenderOptions();
            uint px = (uint)Math.Max(1, Math.Round(side));
            if (page.Size.Height > page.Size.Width) options.DestinationHeight = px;
            else options.DestinationWidth = px;
            page.RenderToStreamAsync(ras, options).AsTask().GetAwaiter().GetResult();
            using Stream s = ras.AsStream();
            s.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        // Decodes on the UI thread into a frozen bitmap that no longer needs the bytes.
        private static BitmapSource? Decode(byte[]? bytes)
        {
            if (bytes == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Write($"Pages thumbnail decode failed: {ex.Message}");
                return null;
            }
        }

        // Lazy fill: one render in flight, the next tile picked on the UI thread each time
        // (visible first, then a couple of viewports around). Scrolling restarts an idle pump.
        // Renders are keyed by tile, so a reorder meanwhile never puts a thumbnail on the wrong tile.
        private async void Pump()
        {
            if (pumping || doc == null || previewMode) return;
            pumping = true;
            try
            {
                while (!closing && doc != null)
                {
                    Tile? next = NextToRender(false);
                    if (next == null) break;
                    next.Requested = true;
                    WinPdf.PdfDocument pdf = doc;
                    uint index = (uint)(next.Page - 1);
                    double side = ThumbSide * renderScale;
                    byte[]? bytes;
                    try
                    {
                        bytes = await Task.Run(() => RenderThumb(pdf, index, side));
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Pages thumbnail failed: {path} page {next.Page}: {ex.Message}");
                        bytes = null;
                    }
                    if (closing) break;
                    SetThumb(next, Decode(bytes), true);
                }
            }
            finally
            {
                pumping = false;
            }
            // Every tile rendered: stop the shared spinner so the window stops ticking frames.
            if (tiles.All(t => t.Requested)) tileSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            if (closing || autoChecked) return;
            autoChecked = true;
            RunAutoPages();
        }

        // Visible slots first (in order), then ahead, then behind. Null when nothing near is left.
        private Tile? NextToRender(bool visibleOnly)
        {
            int n = tiles.Count;
            if (n == 0 || cols <= 0) return null;
            double offset = scroller.VerticalOffset;
            int firstRow = (int)Math.Floor(offset / SlotH);
            int lastRow = (int)Math.Floor((offset + viewH - 1) / SlotH);
            int lo = Math.Clamp(firstRow * cols, 0, n), hi = Math.Clamp((lastRow + 1) * cols, 0, n);
            for (int i = lo; i < hi; i++)
            {
                if (!tiles[i].Requested) return tiles[i];
            }
            if (visibleOnly) return null;
            int ahead = Math.Max(2, 2 * (lastRow - firstRow + 1)) * cols;
            for (int i = hi; i < Math.Min(n, hi + ahead); i++)
            {
                if (!tiles[i].Requested) return tiles[i];
            }
            for (int i = lo - 1; i >= Math.Max(0, lo - ahead); i--)
            {
                if (!tiles[i].Requested) return tiles[i];
            }
            return null;
        }

        private void SetThumb(Tile t, BitmapSource? img, bool animate)
        {
            t.Spinner.Visibility = Visibility.Collapsed;
            if (img == null) return;
            t.Thumb.Source = img;
            t.Thumb.Visibility = Visibility.Visible;
            if (!animate) return;
            t.Thumb.Opacity = 0;
            t.Thumb.BeginAnimation(OpacityProperty, Anim(1, 120));
        }

        // Test hook: TYPELESS_AUTOPAGES=<spec> (same grammar as --pages) applies the plan to the
        // tiles and saves, once the first thumbnails are in.
        private void RunAutoPages()
        {
            string? spec = Environment.GetEnvironmentVariable("TYPELESS_AUTOPAGES");
            if (string.IsNullOrWhiteSpace(spec)) return;
            if (!Program.TryParsePageSpec(spec, out List<(int Page, int Rotate)> plan))
            {
                Log.Write($"TYPELESS_AUTOPAGES: invalid spec {spec}");
                return;
            }
            ApplyPlan(plan);
            Save();
        }

        // Spec pages first, in spec order and kept, the rest dropped after them. A tile is one
        // page, so a repeated page keeps only its first occurrence (logged).
        private void ApplyPlan(List<(int Page, int Rotate)> plan)
        {
            var byPage = tiles.ToDictionary(t => t.Page);
            var order = new List<Tile>();
            var seen = new HashSet<Tile>();
            foreach ((int page, int rotate) in plan)
            {
                if (!byPage.TryGetValue(page, out Tile? t))
                {
                    Log.Write($"TYPELESS_AUTOPAGES: page {page} is out of range (1-{tiles.Count}), skipped");
                    continue;
                }
                if (!seen.Add(t))
                {
                    Log.Write($"TYPELESS_AUTOPAGES: page {page} repeats, only the first is used");
                    continue;
                }
                order.Add(t);
                t.Kept = true;
                t.Angle = 0;
                RotateTile(t, rotate, false);
            }
            foreach (Tile t in tiles)
            {
                if (seen.Contains(t)) continue;
                t.Kept = false;
                order.Add(t);
            }
            ClearFocus();
            tiles.Clear();
            tiles.AddRange(order);
            wrap.Children.Clear();
            foreach (Tile t in tiles)
            {
                wrap.Children.Add(t.Root);
                UpdateTileLook(t, false);
            }
            UpdateReadout();
        }

        private void BuildTiles(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                Tile t = MakeTile(i);
                tiles.Add(t);
                wrap.Children.Add(t.Root);
            }
            countText.Text = count == 1 ? "1 page" : $"{count} pages";
            loaded = true;
            ApplyGrid();
            UpdateReadout();
            if (!previewMode)
            {
                tileSpin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }

        // Rounded tile: square thumbnail box (a 90 degree turn stays inside it), page number below,
        // an "off" mark when dropped, and a rotate button on hover.
        private Tile MakeTile(int page)
        {
            var t = new Tile { Page = page };
            t.Turn = new RotateTransform(0);
            t.Thumb = new Image
            {
                Stretch = Stretch.Uniform,
                RenderTransform = t.Turn,
                RenderTransformOrigin = new Point(0.5, 0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            RenderOptions.SetBitmapScalingMode(t.Thumb, BitmapScalingMode.HighQuality);
            t.Spinner = MakeSpinner(24, 9, 2.5, AccentBrush, tileSpin);
            t.Spinner.Visibility = Visibility.Visible;

            var thumbBox = new Grid { Width = ThumbSide, Height = ThumbSide };
            thumbBox.Children.Add(t.Thumb);
            thumbBox.Children.Add(t.Spinner);
            var label = new TextBlock
            {
                Text = page.ToString(CultureInfo.InvariantCulture),
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = SubBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            t.Body = new Grid();
            t.Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ThumbSide) });
            t.Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LabelH) });
            Grid.SetRow(label, 1);
            t.Body.Children.Add(thumbBox);
            t.Body.Children.Add(label);

            t.OffMark = MakeOffMark();

            var rotateGlyph = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 13,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var rotateBrush = new SolidColorBrush(Color.FromArgb(0xD9, 0x14, 0x14, 0x18));
            t.RotateButton = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = rotateBrush,
                Child = rotateGlyph,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Hidden,
                ToolTip = "Rotate (R, Shift+R counter-clockwise)",
            };
            t.RotateButton.MouseEnter += (_, _) => AnimateColor(rotateBrush, AccentColor);
            t.RotateButton.MouseLeave += (_, _) => AnimateColor(rotateBrush, Color.FromArgb(0xD9, 0x14, 0x14, 0x18));
            // Bubbling handler marks it handled, so the tile below does not toggle.
            t.RotateButton.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!CanEdit) return;
                SetFocus(tiles.IndexOf(t));
                RotateTile(t, 90, true);
            };

            var cell = new Grid();
            cell.Children.Add(t.Body);
            cell.Children.Add(t.OffMark);
            cell.Children.Add(t.RotateButton);

            t.Fill = new SolidColorBrush(TileIdle);
            t.Root = new Border
            {
                Width = TileW,
                Height = TileH,
                CornerRadius = new CornerRadius(10),
                Background = t.Fill,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(TilePad - 2),
                Child = cell,
                Cursor = Cursors.Hand,
            };
            t.Root.MouseEnter += (_, _) =>
            {
                hovered = t;
                if (!CanEdit || pressTile != null) return;
                AnimateColor(t.Fill, TileHover);
                t.RotateButton.Visibility = Visibility.Visible;
            };
            t.Root.MouseLeave += (_, _) =>
            {
                if (hovered == t) hovered = null;
                AnimateColor(t.Fill, TileIdle);
                t.RotateButton.Visibility = Visibility.Hidden;
            };
            t.Root.MouseLeftButtonDown += (_, e) => OnTileDown(t, e);
            return t;
        }

        // Accent outline circle with a slash, centered on the thumbnail, kept at full opacity.
        private static Grid MakeOffMark()
        {
            var mark = new Grid
            {
                Width = 46,
                Height = 46,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, (ThumbSide - 46) / 2, 0, 0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            mark.Children.Add(new Ellipse
            {
                Stroke = AccentBrush,
                StrokeThickness = 3,
                Fill = Frozen(Color.FromArgb(0xB3, 0x14, 0x14, 0x18)),
            });
            mark.Children.Add(new Line
            {
                X1 = 11,
                Y1 = 11,
                X2 = 35,
                Y2 = 35,
                Stroke = AccentBrush,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
            return mark;
        }

        // Same look as the crop ratio pills, but a one-shot action instead of a toggle.
        private Border MakeActionPill(string label, Action click)
        {
            var brush = new SolidColorBrush(PillIdle);
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

        // About 70% of the work area: as many columns as fit, height capped (the grid scrolls).
        private void SetLayout(double workW, double workH)
        {
            maxCols = Math.Max(2, (int)((workW * WorkShare - 2 * Pad - 2 * ShadowMargin - ThumbBarRoom) / SlotW));
            maxViewH = Math.Max(SlotH, workH * WorkShare - Chrome);
            ApplyGrid();
        }

        // Fewer pages than columns narrows the grid; the toolbar keeps a minimum width.
        private void ApplyGrid()
        {
            int n = tiles.Count;
            cols = n == 0 ? Math.Min(maxCols, 4) : Math.Clamp(n, 1, maxCols);
            int rows = Math.Max(1, (n + cols - 1) / cols);
            double gridW = cols * SlotW;
            viewH = Math.Min(maxViewH, rows * SlotH);
            wrap.Width = gridW;
            host.Width = gridW;
            scroller.Width = gridW;
            scroller.Height = viewH;
            stage.Width = gridW + ThumbBarRoom;
            stage.Height = viewH;
            nameText.MaxWidth = Math.Max(200, Math.Max(gridW, MinContentW) - 160);
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeight <= e.ViewportHeight + 0.5 || e.ViewportHeight <= 0)
            {
                scrollThumb.Visibility = Visibility.Collapsed;
            }
            else
            {
                double h = Math.Max(24, e.ViewportHeight * e.ViewportHeight / e.ExtentHeight);
                double top = e.VerticalOffset / (e.ExtentHeight - e.ViewportHeight) * (e.ViewportHeight - h);
                scrollThumb.Height = h;
                scrollThumb.Margin = new Thickness(0, top, 2, 0);
                scrollThumb.Visibility = Visibility.Visible;
            }
            Pump();
        }

        private void OnTileDown(Tile t, MouseButtonEventArgs e)
        {
            if (!CanEdit) return;
            e.Handled = true;
            pressTile = t;
            dragging = false;
            pressPt = e.GetPosition(host);
            host.CaptureMouse();
        }

        private void OnHostMove(object sender, MouseEventArgs e)
        {
            if (pressTile == null) return;
            Point p = e.GetPosition(host);
            if (!dragging)
            {
                if ((p - pressPt).Length < DragSlop) return;
                dragging = true;
                pressTile.Root.Opacity = 0.45;
                pressTile.RotateButton.Visibility = Visibility.Hidden;
                dragTimer.Start();
            }
            UpdateDrop(p);
        }

        // Release: a drag drops the tile at the bar; a plain click toggles kept or dropped.
        private void OnHostUp(object sender, MouseButtonEventArgs e)
        {
            if (pressTile == null) return;
            Tile t = pressTile;
            bool wasDrag = dragging;
            int to = dropIndex;
            EndDrag();
            if (wasDrag)
            {
                int from = tiles.IndexOf(t);
                MoveTo(t, to > from ? to - 1 : to);
            }
            else Toggle(t);
            SetFocus(tiles.IndexOf(t));
        }

        private void EndDrag()
        {
            Tile? t = pressTile;
            pressTile = null;
            dragging = false;
            dragTimer.Stop();
            dropBar.Visibility = Visibility.Collapsed;
            if (t != null) t.Root.Opacity = 1;
            if (host.IsMouseCaptured) host.ReleaseMouseCapture();
        }

        // Insertion slot from the pointer: the nearest gap in the row under it, never past the
        // last tile. dropIndex is the index before which the tile goes.
        private void UpdateDrop(Point p)
        {
            int n = tiles.Count;
            if (n == 0) return;
            int rows = (n + cols - 1) / cols;
            int row = (int)Clamp(Math.Floor(p.Y / SlotH), 0, rows - 1);
            int col = (int)Clamp(Math.Round(p.X / SlotW), 0, Math.Min(cols, n - row * cols));
            dropIndex = row * cols + col;
            Canvas.SetLeft(dropBar, Clamp(col * SlotW - 1.5, 0, cols * SlotW - 3));
            Canvas.SetTop(dropBar, row * SlotH + Gap / 2);
            dropBar.Visibility = Visibility.Visible;
        }

        private void OnDragTick()
        {
            if (!dragging)
            {
                dragTimer.Stop();
                return;
            }
            double y = Mouse.GetPosition(scroller).Y;
            double dy = y < EdgeScroll ? y - EdgeScroll : y > viewH - EdgeScroll ? y - (viewH - EdgeScroll) : 0;
            if (dy == 0) return;
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + Clamp(dy, -EdgeScroll, EdgeScroll) * 0.5);
            UpdateDrop(Mouse.GetPosition(host));
        }

        private void Toggle(Tile t)
        {
            if (!CanEdit) return;
            t.Kept = !t.Kept;
            UpdateTileLook(t, true);
            UpdateReadout();
        }

        private void SetKept(Func<Tile, bool> kept)
        {
            if (!CanEdit) return;
            foreach (Tile t in tiles)
            {
                bool k = kept(t);
                if (k == t.Kept) continue;
                t.Kept = k;
                UpdateTileLook(t, true);
            }
            UpdateReadout();
        }

        // Clockwise for positive delta, like PDF /Rotate.
        private void RotateTile(Tile t, int delta, bool animate)
        {
            t.Angle += delta;
            if (animate)
            {
                t.Turn.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(t.Angle, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
            else
            {
                t.Turn.BeginAnimation(RotateTransform.AngleProperty, null);
                t.Turn.Angle = t.Angle;
            }
        }

        private static int Normalize(int angle) => (angle % 360 + 360) % 360;

        // Moves the tile to its final index in the order (and in the grid).
        private void MoveTo(Tile t, int index)
        {
            int from = tiles.IndexOf(t);
            index = Math.Clamp(index, 0, tiles.Count - 1);
            if (from < 0 || index == from) return;
            tiles.RemoveAt(from);
            tiles.Insert(index, t);
            wrap.Children.RemoveAt(from);
            wrap.Children.Insert(index, t.Root);
        }

        private void UpdateTileLook(Tile t, bool animate)
        {
            double opacity = t.Kept ? 1 : 0.3;
            if (animate) t.Body.BeginAnimation(OpacityProperty, Anim(opacity, 120));
            else
            {
                t.Body.BeginAnimation(OpacityProperty, null);
                t.Body.Opacity = opacity;
            }
            t.OffMark.Visibility = t.Kept ? Visibility.Collapsed : Visibility.Visible;
        }

        // Keyboard focus is our own index with an accent ring, not WPF focus. The ring is tracked
        // by tile, since a reorder can move the ringed tile away from the old index.
        private void SetFocus(int index)
        {
            if (tiles.Count == 0) return;
            index = Math.Clamp(index, 0, tiles.Count - 1);
            if (ringTile != null) ringTile.Root.BorderBrush = Brushes.Transparent;
            focus = index;
            ringTile = tiles[focus];
            ringTile.Root.BorderBrush = AccentBrush;
            ScrollToTile(focus);
        }

        private void ClearFocus()
        {
            if (ringTile != null) ringTile.Root.BorderBrush = Brushes.Transparent;
            ringTile = null;
            focus = -1;
        }

        private void ScrollToTile(int index)
        {
            double top = index / cols * SlotH, offset = scroller.VerticalOffset;
            if (top < offset) scroller.ScrollToVerticalOffset(top);
            else if (top + SlotH > offset + viewH) scroller.ScrollToVerticalOffset(top + SlotH - viewH);
        }

        private void UpdateReadout()
        {
            keptCount = tiles.Count(t => t.Kept);
            int n = tiles.Count;
            readout.Text = n == 0 ? " " : $"{keptCount} of {n} {(n == 1 ? "page" : "pages")}";
            save.Opacity = keptCount == 0 ? 0.4 : 1;
            save.Cursor = keptCount == 0 ? null : Cursors.Hand;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (failed && !busy)
            {
                e.Handled = true;
                CloseAnimated();
                return;
            }
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
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
                case Key.A:
                    if (!ctrl) break;
                    e.Handled = true;
                    SetKept(_ => true);
                    break;
                case Key.Space:
                    e.Handled = true;
                    if (!CanEdit) break;
                    if (focus < 0) SetFocus(0);
                    Toggle(tiles[focus]);
                    break;
                case Key.R:
                    e.Handled = true;
                    if (!CanEdit) break;
                    Tile? target = focus >= 0 ? tiles[focus] : hovered;
                    if (target != null) RotateTile(target, shift ? -90 : 90, true);
                    break;
                case Key.Left:
                case Key.Right:
                    e.Handled = true;
                    int step = e.Key == Key.Left ? -1 : 1;
                    if (ctrl) MoveFocused(step);
                    else MoveFocus(step);
                    break;
                case Key.Up:
                    e.Handled = true;
                    MoveFocus(-cols);
                    break;
                case Key.Down:
                    e.Handled = true;
                    MoveFocus(cols);
                    break;
            }
        }

        private void MoveFocus(int delta)
        {
            if (!CanEdit || pressTile != null) return;
            SetFocus(focus < 0 ? 0 : focus + delta);
        }

        // Ctrl+Left/Right: the focused tile swaps places with its neighbour and keeps the focus.
        private void MoveFocused(int delta)
        {
            if (!CanEdit || pressTile != null) return;
            if (focus < 0)
            {
                SetFocus(0);
                return;
            }
            int to = focus + delta;
            if (to < 0 || to >= tiles.Count) return;
            MoveTo(tiles[focus], to);
            SetFocus(to);
        }

        // Kept tiles in grid order with their rotation; PdfEdit.Pages runs on a worker.
        private async void Save()
        {
            if (!CanEdit || keptCount == 0) return;
            EndDrag();
            List<(int Page, int Rotate)> plan = tiles.Where(t => t.Kept).Select(t => (t.Page, Normalize(t.Angle))).ToList();

            busy = true;
            if (hovered != null) hovered.RotateButton.Visibility = Visibility.Hidden;
            SetSaving(true);

            string file = path;
            try
            {
                string outPath = await Task.Run(() => PdfEdit.Pages(file, plan));
                busy = false;
                saved = true;
                SetSaving(false);
                Log.Write($"Pages saved: {outPath} ({plan.Count} pages)");
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
                Log.Write($"Pages failed: {path}: {ex}");
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

        // Dev only (--pages-preview): never shown; loads and renders the visible thumbnails
        // synchronously at 2x, sized for a 1920x1080 work area at 100%, and renders to PNG.
        internal void RenderPreview(string outPath, string state)
        {
            if (!previewMode) throw new InvalidOperationException("Preview needs preview mode.");
            renderScale = 2;
            SetLayout(1920, 1080);
            string src = path;
            WinPdf.PdfDocument pdf = Task.Run(() => OpenDoc(src)).GetAwaiter().GetResult();
            doc = pdf;
            BuildTiles((int)pdf.PageCount);
            for (Tile? t = NextToRender(true); t != null; t = NextToRender(true))
            {
                t.Requested = true;
                uint index = (uint)(t.Page - 1);
                double side = ThumbSide * renderScale;
                SetThumb(t, Decode(Task.Run(() => RenderThumb(pdf, index, side)).GetAwaiter().GetResult()), false);
            }

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
                    // Some dropped, the first rotated (with its hover button), one focused.
                    for (int i = 1; i < tiles.Count; i += 3)
                    {
                        tiles[i].Kept = false;
                        UpdateTileLook(tiles[i], false);
                    }
                    RotateTile(tiles[0], 90, false);
                    tiles[0].Fill.Color = TileHover;
                    tiles[0].RotateButton.Visibility = Visibility.Visible;
                    SetFocus(Math.Min(2, tiles.Count - 1));
                    UpdateReadout();
                    break;
                case "done":
                    saved = true;
                    ShowStatus("", OkColor, "Saved",
                        IOPath.GetFileNameWithoutExtension(path) + "_pages" + IOPath.GetExtension(path));
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
            using var fs = File.Create(outPath);
            encoder.Save(fs);
        }
    }
}
