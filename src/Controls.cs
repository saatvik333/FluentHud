using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentHud
{
    static class Theme
    {
        public static readonly Brush Track = Solid(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        public static readonly Brush TextPrimary = Solid(Colors.White);
        public static readonly Brush Stroke = Solid(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        public static readonly Brush StrokeStrong = Solid(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)); // unlocked: brighter, still neutral

        // Fluent dark-theme status colors (SystemFillColorCaution / Critical).
        public static readonly Color Caution = Color.FromRgb(0xFC, 0xE1, 0x00);
        public static readonly Color Critical = Color.FromRgb(0xFF, 0x99, 0xA4);

        static readonly IEasingFunction Decelerate = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };

        public static SolidColorBrush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color WithAlpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        public static Color LevelColor(Level level, Color normal)
        {
            return level == Level.Critical ? Critical : level == Level.Caution ? Caution : normal;
        }

        // Fluent "decelerate" motion, capped at 30 fps so the overlay never competes with the game.
        // With Settings > Accessibility > Visual effects > Animation effects off, values just jump.
        public static void Animate(UIElement target, DependencyProperty property, double to)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                target.BeginAnimation(property, null);
                target.SetValue(property, to);
                return;
            }
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(600)) { EasingFunction = Decelerate };
            Timeline.SetDesiredFrameRate(a, 30);
            target.BeginAnimation(property, a, HandoffBehavior.SnapshotAndReplace);
        }
    }

    // Fluent progress ring: a track with a round-capped arc, like WinUI's determinate ProgressRing.
    public class RingGauge : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
            "Brush", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public Brush Brush { get { return (Brush)GetValue(BrushProperty); } set { SetValue(BrushProperty, value); } }
        public double StrokeThickness { get; set; }

        public RingGauge() { StrokeThickness = 4; }

        protected override void OnRender(DrawingContext dc)
        {
            double t = StrokeThickness;
            double r = (Math.Min(ActualWidth, ActualHeight) - t) / 2;
            if (r <= 0) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, new Pen(Theme.Track, t), c, r, r);

            double v = Math.Max(0, Math.Min(1, Value));
            if (v < 0.005) return;
            var pen = new Pen(Brush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (v > 0.995) { dc.DrawEllipse(null, pen, c, r, r); return; }

            double a0 = -Math.PI / 2, a1 = a0 + v * 2 * Math.PI;
            var fig = new PathFigure { StartPoint = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)) };
            fig.Segments.Add(new ArcSegment(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)),
                new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            dc.DrawGeometry(null, pen, geo);
        }
    }

    // Linear meter with fully rounded ends, like the Fluent ProgressBar.
    public class LinearBar : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(LinearBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
            "Brush", typeof(Brush), typeof(LinearBar), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public Brush Brush { get { return (Brush)GetValue(BrushProperty); } set { SetValue(BrushProperty, value); } }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, r = h / 2;
            dc.DrawRoundedRectangle(Theme.Track, null, new Rect(0, 0, w, h), r, r);
            double v = Math.Max(0, Math.Min(1, Value));
            // Never narrower than it is tall, so a small value still reads as a rounded pill.
            if (v > 0.001) dc.DrawRoundedRectangle(Brush, null, new Rect(0, 0, Math.Max(h, w * v), h), r, r);
        }
    }

    // Task Manager–style history graph: accent line over a fading area fill.
    public class Sparkline : FrameworkElement
    {
        double[] values;
        Color color = Colors.White;

        public void Update(double[] history, Color c)
        {
            values = history;
            color = c;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            var bounds = new Rect(0, 0, w, h);
            dc.PushClip(new RectangleGeometry(bounds, 4, 4));
            dc.DrawRectangle(Theme.Solid(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), null, bounds);
            var grid = new Pen(Theme.Solid(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)), 1);
            dc.DrawLine(grid, new Point(0, Math.Round(h / 2) + 0.5), new Point(w, Math.Round(h / 2) + 0.5));

            if (values != null && values.Length > 1)
            {
                double step = w / (values.Length - 1);
                Func<int, Point> pt = i => new Point(i * step, 1.5 + (h - 3) * (1 - values[i] / 100.0));

                var line = new StreamGeometry();
                var area = new StreamGeometry();
                using (StreamGeometryContext l = line.Open(), a = area.Open())
                {
                    l.BeginFigure(pt(0), false, false);
                    a.BeginFigure(new Point(0, h), true, true);
                    a.LineTo(pt(0), false, false);
                    for (int i = 1; i < values.Length; i++)
                    {
                        l.LineTo(pt(i), true, true);
                        a.LineTo(pt(i), false, false);
                    }
                    a.LineTo(new Point(w, h), false, false);
                }
                var fill = new LinearGradientBrush(Theme.WithAlpha(color, 0x59), Theme.WithAlpha(color, 0x00), 90);
                dc.DrawGeometry(fill, null, area);
                // Mitered, but capped so a one-sample spike doesn't grow a long needle.
                dc.DrawGeometry(null, new Pen(Theme.Solid(color), 1.5) { LineJoin = PenLineJoin.Miter, MiterLimit = 2 }, line);
            }
            dc.Pop();
        }
    }

    // The last minute of one value, for the sparklines.
    sealed class History
    {
        readonly double[] buffer;
        int count, next;

        public History(int size) { buffer = new double[size]; }

        public void Push(double v)
        {
            buffer[next] = v;
            next = (next + 1) % buffer.Length;
            if (count < buffer.Length) count++;
        }

        // Always returns a full window (oldest first) so the graph scrolls in from the right.
        public double[] ToArray()
        {
            var a = new double[buffer.Length];
            for (int i = 0; i < count; i++)
                a[buffer.Length - count + i] = buffer[(next - count + i + buffer.Length) % buffer.Length];
            return a;
        }
    }

    // One bar per logical processor; reveals a single pegged thread that the average hides.
    // A thread past the CpuThread thresholds turns yellow or red on its own.
    public class CoreStrip : FrameworkElement
    {
        double[] values;
        Color color = Colors.White;

        public void Update(double[] loads, Color c)
        {
            values = loads;
            color = c;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (values == null || values.Length == 0) return;
            double w = ActualWidth, h = ActualHeight;
            // Tighter gaps on many-core chips (32+ threads) so the bars keep most of the width.
            double gap = values.Length > 64 ? 0.5 : values.Length > 32 ? 1 : 2;
            double bw = (w - gap * (values.Length - 1)) / values.Length;
            if (bw <= 0) return;
            for (int i = 0; i < values.Length; i++)
            {
                double x = i * (bw + gap), r = Math.Min(2, bw / 2);
                double v = values[i] / 100.0;
                dc.DrawRoundedRectangle(Theme.Track, null, new Rect(x, 0, bw, h), r, r);
                double fh = Math.Max(2 * r, h * v);
                byte alpha = (byte)(0x60 + 0x9F * v);
                Color bar = Theme.LevelColor(Thresholds.Classify(Metric.CpuThread, values[i]), color);
                dc.DrawRoundedRectangle(Theme.Solid(Theme.WithAlpha(bar, alpha)), null, new Rect(x, h - fh, bw, fh), r, r);
            }
        }
    }
}
