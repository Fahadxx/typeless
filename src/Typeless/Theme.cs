using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Typeless
{
    // Shared look for the borderless panels (crop, trim, pages): colors, fonts, buttons, spinner, status pill.
    internal static class Theme
    {
        // Colors first: the brushes below are built from them (static initializers run in order).
        public static readonly Color AccentColor = Color.FromArgb(0xF0, 0x6C, 0x8C, 0xFF);
        public static readonly Color AccentHover = Color.FromArgb(0xFF, 0x86, 0xA1, 0xFF);
        public static readonly Color OkColor = Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80);
        public static readonly Color FailColor = Color.FromArgb(0xFF, 0xF8, 0x71, 0x71);
        public static readonly Color PillIdle = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        public static readonly Color PillHover = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF);
        public static readonly Color GhostIdle = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        public static readonly Color GhostHover = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        public static readonly Brush DiscBrush = Frozen(Color.FromArgb(0xF2, 0x14, 0x14, 0x18));
        public static readonly Brush SubBrush = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Display, Segoe UI");

        // Rounded button; hover color is skipped while isBusy() is true.
        public static Border MakeButton(UIElement child, Color idle, Color hover, Brush? border, Action click, Func<bool> isBusy)
        {
            var brush = new SolidColorBrush(idle);
            var button = new Border
            {
                CornerRadius = new CornerRadius(8),
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                Background = brush,
                BorderBrush = border,
                BorderThickness = new Thickness(border == null ? 0 : 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = child,
                Cursor = Cursors.Hand,
            };
            button.MouseEnter += (_, _) =>
            {
                if (!isBusy()) AnimateColor(brush, hover);
            };
            button.MouseLeave += (_, _) => AnimateColor(brush, idle);
            button.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        // Centered status pill (glyph, title, sub), collapsed until shown.
        public static Border MakeStatus(out TextBlock glyph, out TextBlock title, out TextBlock sub)
        {
            glyph = new TextBlock
            {
                FontFamily = IconFont,
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            };
            title = new TextBlock
            {
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            sub = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 11,
                Foreground = SubBrush,
                MaxWidth = 260,
                MaxHeight = 46,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(glyph);
            stack.Children.Add(title);
            stack.Children.Add(sub);
            return new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = Frozen(Color.FromArgb(0xE6, 0x14, 0x14, 0x18)),
                Padding = new Thickness(22, 14, 22, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = stack,
            };
        }

        public static Canvas MakeSpinner(double side, double radius, double thickness, Brush stroke, RotateTransform rotate)
        {
            var arc = new Path
            {
                Data = ArcGeometry(new Point(side / 2, side / 2), radius, -90, 10),
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = rotate,
            };
            var box = new Canvas
            {
                Width = side,
                Height = side,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            box.Children.Add(arc);
            return box;
        }

        public static void StartSpinner(Canvas box, RotateTransform rotate)
        {
            box.Visibility = Visibility.Visible;
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        public static void StopSpinner(Canvas box, RotateTransform rotate)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            box.Visibility = Visibility.Collapsed;
        }

        public static Geometry ArcGeometry(Point c, double r, double a0, double a1)
        {
            Point At(double degrees)
            {
                double a = degrees * Math.PI / 180;
                return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            }
            var figure = new PathFigure { StartPoint = At(a0), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment(At(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        // Lower bound wins when the range is empty (Math.Clamp would throw).
        public static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(v, hi));

        public static void AnimateColor(SolidColorBrush brush, Color to) =>
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(120)));

        public static DoubleAnimation Anim(double to, int ms, IEasingFunction? ease = null) =>
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

        public static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
