using System;
using System.Windows;
using System.Windows.Media;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;

// UseWindowsForms and ImplicitUsings together put System.Drawing in scope, which collides with
// the WPF types on these names. These controls are WPF-facing, so bind them all there.
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Controls
{
    /// <summary>
    /// Shared frozen brushes for the panel's two-hue data palette. iStat Menus' dark theme
    /// tells sections apart by position, not colour: cyan is always the primary series and
    /// red always the counterpart (System time, Upload, Write).
    /// </summary>
    internal static class PanelPalette
    {
        public static readonly SolidColorBrush Cyan = Frozen(0xFF, 0x3F, 0xD2, 0xE4);
        public static readonly SolidColorBrush Red = Frozen(0xFF, 0xFF, 0x51, 0x47);
        public static readonly SolidColorBrush Track = Frozen(0x2E, 0xFF, 0xFF, 0xFF);

        private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// A circular progress ring, the iStat Menus gauge. Draws a full track circle and an arc
    /// from twelve o'clock clockwise proportional to <see cref="Value"/> (0-100).
    /// </summary>
    public sealed class RingGauge : FrameworkElement
    {
        /// <summary>
        /// Kills every ring animation globally. The render harness flips this on: a capture
        /// taken mid-sweep would show half-empty rings and fail visual comparison.
        /// </summary>
        public static bool DisableAnimations;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

        // What OnRender actually draws. Value is the bound truth; VisualValue chases it with
        // an ease so readings sweep instead of jumping — all motion runs on WPF's GPU-composited
        // animation clock and only while a panel exists, so the idle app animates nothing.
        private static readonly DependencyProperty VisualValueProperty = DependencyProperty.Register(
            "VisualValue", typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// False for the tiny per-core rings: two dozen 18px elements easing on every tick
        /// would invalidate continuously for imperceptible motion. The open sweep still runs.
        /// </summary>
        public bool AnimateChanges { get; set; } = true;

        public RingGauge()
        {
            // The iStat signature: rings sweep from zero to their reading when a panel opens.
            Loaded += (s, e) =>
            {
                SetValue(VisualValueProperty, 0d);
                DriveVisual(Value, TimeSpan.FromMilliseconds(550));
            };
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((RingGauge)d).DriveVisual((double)e.NewValue, TimeSpan.FromMilliseconds(280));

        private void DriveVisual(double to, TimeSpan duration)
        {
            double from = (double)GetValue(VisualValueProperty);
            bool openSweep = duration.TotalMilliseconds >= 500;
            if (DisableAnimations || !IsLoaded || (!AnimateChanges && !openSweep) || Math.Abs(to - from) < 1.0)
            {
                BeginAnimation(VisualValueProperty, null);
                SetValue(VisualValueProperty, to);
                return;
            }
            BeginAnimation(VisualValueProperty, new System.Windows.Media.Animation.DoubleAnimation(from, to, duration)
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
            });
        }

        public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
            nameof(RingThickness), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
            nameof(ProgressBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(PanelPalette.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(PanelPalette.Track, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
        public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
        public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

        private Pen? _cachedTrackPen;
        private Brush? _cachedTrackBrush;
        private double _cachedTrackThickness;

        private Pen? _cachedProgressPen;
        private Brush? _cachedProgressBrush;
        private double _cachedProgressThickness;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double thickness = Math.Max(1.0, RingThickness);
            double radius = (Math.Min(w, h) - thickness) / 2.0;
            if (radius <= 0) return;

            var trackBrush = TrackBrush;
            if (_cachedTrackPen == null || !ReferenceEquals(_cachedTrackBrush, trackBrush) || _cachedTrackThickness != thickness)
            {
                _cachedTrackBrush = trackBrush;
                _cachedTrackThickness = thickness;
                _cachedTrackPen = new Pen(trackBrush, thickness);
                if (trackBrush.IsFrozen) _cachedTrackPen.Freeze();
            }

            var center = new Point(w / 2.0, h / 2.0);
            dc.DrawEllipse(null, _cachedTrackPen, center, radius, radius);

            double pct = Math.Clamp((double)GetValue(VisualValueProperty), 0.0, 100.0);
            if (pct <= 0.05) return;

            var progressBrush = ProgressBrush;
            if (_cachedProgressPen == null || !ReferenceEquals(_cachedProgressBrush, progressBrush) || _cachedProgressThickness != thickness)
            {
                _cachedProgressBrush = progressBrush;
                _cachedProgressThickness = thickness;
                _cachedProgressPen = new Pen(progressBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (progressBrush.IsFrozen) _cachedProgressPen.Freeze();
            }

            if (pct >= 99.95)
            {
                dc.DrawEllipse(null, _cachedProgressPen, center, radius, radius);
                return;
            }

            double sweep = pct / 100.0 * 2.0 * Math.PI;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + radius * Math.Sin(sweep), center.Y - radius * Math.Cos(sweep));

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, isFilled: false, isClosed: false);
                ctx.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, _cachedProgressPen, geometry);
        }
    }

    /// <summary>How a <see cref="HistoryBarGraph"/> composes its series.</summary>
    public enum GraphMode
    {
        /// <summary>One series as plain bars.</summary>
        Bars,
        /// <summary>Primary series as full bars with the secondary drawn as a tip segment at the top (CPU User/System).</summary>
        Stacked,
        /// <summary>Secondary above and primary below a dashed centre axis, sharing one scale (Network, Disks).</summary>
        Mirrored
    }

    /// <summary>
    /// The iStat-style history graph: discrete vertical bars, newest at the right edge, one
    /// sample per bar. Reads <see cref="Series"/> directly — safe because MetricsHistory is
    /// UI-thread-only — and re-renders when <see cref="Tick"/> changes.
    /// </summary>
    public sealed class HistoryBarGraph : FrameworkElement
    {
        private const double BarWidth = 2.0;
        private const double BarStep = 3.0; // bar plus 1px gap

        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
            nameof(Series), typeof(Series), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SecondarySeriesProperty = DependencyProperty.Register(
            nameof(SecondarySeries), typeof(Series), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(GraphMode), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(GraphMode.Bars, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
            nameof(Max), typeof(double), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
            nameof(Tick), typeof(int), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
            nameof(BarBrush), typeof(Brush), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(PanelPalette.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SecondaryBrushProperty = DependencyProperty.Register(
            nameof(SecondaryBrush), typeof(Brush), typeof(HistoryBarGraph),
            new FrameworkPropertyMetadata(PanelPalette.Red, FrameworkPropertyMetadataOptions.AffectsRender));

        public Series? Series { get => (Series?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
        public Series? SecondarySeries { get => (Series?)GetValue(SecondarySeriesProperty); set => SetValue(SecondarySeriesProperty, value); }
        public GraphMode Mode { get => (GraphMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

        /// <summary>Full-scale value. 0 autoscales to the series peak (shared across both in Mirrored mode).</summary>
        public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }

        /// <summary>Monotonic refresh counter; the view model increments it per sample.</summary>
        public int Tick { get => (int)GetValue(TickProperty); set => SetValue(TickProperty, value); }

        public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
        public Brush SecondaryBrush { get => (Brush)GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }

        // Depth treatment shared by every mode: faint quarter guides plus a floor hairline
        // give the reading a scale, and bars fill with a vertical gradient anchored to the
        // graph (not the bar), so taller bars reach into brighter colour — the iStat look.
        private static readonly Pen GridPen = FrozenHairline(0x12);
        private static readonly Pen FloorPen = FrozenHairline(0x28);
        private static readonly Pen MirroredAxisPen = FrozenDashed(0x46, 2, 2);
        private static readonly Pen BaselinePen = FrozenDashed(0x3C, 3, 3);

        private Brush? _cachedBarSource;
        private Brush? _cachedSecSource;
        private double _cachedHeight;
        private Brush? _cachedFill;
        private Brush? _cachedUpFill;
        private Brush? _cachedDownFill;

        private static Pen FrozenHairline(byte alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
            brush.Freeze();
            var pen = new Pen(brush, 1.0);
            pen.Freeze();
            return pen;
        }

        private static Pen FrozenDashed(byte alpha, double dash, double gap)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
            brush.Freeze();
            var dashStyle = new DashStyle(new double[] { dash, gap }, 0);
            dashStyle.Freeze();
            var pen = new Pen(brush, 1.0) { DashStyle = dashStyle };
            pen.Freeze();
            return pen;
        }

        private static Brush GradientFor(Brush source, double yBright, double yDim)
        {
            if (source is not SolidColorBrush solid) return source;
            Color c = solid.Color;
            var dim = Color.FromArgb((byte)(c.A * 0.45), c.R, c.G, c.B);
            var g = new LinearGradientBrush(c, dim,
                new Point(0, yBright), new Point(0, yDim))
            { MappingMode = BrushMappingMode.Absolute };
            g.Freeze();
            return g;
        }

        /// <summary>
        /// Formats the hover readout for the bar at horizontal position <paramref name="x"/>
        /// across a graph of <paramref name="width"/> pixels, or returns <c>null</c> if no
        /// sample occupies that slot.
        /// </summary>
        public static string? FormatHoverTooltip(Series? primary, double x, double width, string unit = "%")
        {
            if (primary == null || primary.Availability != Availability.Value || primary.Count == 0)
                return null;
            if (width <= BarStep || x < 0 || x > width)
                return null;

            int k = (int)Math.Floor((width - x) / BarStep);
            if (k < 0) k = 0;

            int idx = primary.Count - 1 - k;
            if (idx < 0 || idx >= primary.Count)
                return null;

            string age = k == 0
                ? "now"
                : "-" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
            string val = primary[idx].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + unit;
            return val + " (" + age + ")";
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pt = e.GetPosition(this);
            string unit = Max == 100d ? "%" : "";
            ToolTip = FormatHoverTooltip(Series, pt.X, ActualWidth, unit);
        }

        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            ToolTip = null;
        }

        private void EnsureGradients(double h, double axisY)
        {
            var barSource = BarBrush;
            var secSource = SecondaryBrush;
            if (ReferenceEquals(_cachedBarSource, barSource) &&
                ReferenceEquals(_cachedSecSource, secSource) &&
                _cachedHeight == h &&
                _cachedFill != null &&
                _cachedUpFill != null &&
                _cachedDownFill != null)
            {
                return;
            }

            _cachedBarSource = barSource;
            _cachedSecSource = secSource;
            _cachedHeight = h;
            _cachedFill = GradientFor(barSource, 0, h);
            _cachedUpFill = GradientFor(secSource, axisY, 0);
            _cachedDownFill = GradientFor(barSource, axisY, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= BarStep || h <= 2) return;

            var primary = Series;
            if (primary == null || primary.Availability != Availability.Value || primary.Count == 0)
            {
                DrawBaseline(dc, w, h);
                return;
            }

            var secondary = SecondarySeries;
            bool hasSecondary = secondary != null && secondary.Availability == Availability.Value && secondary.Count > 0;

            double max = Max;
            if (max <= 0)
            {
                max = primary.Peak;
                if (Mode == GraphMode.Mirrored && hasSecondary) max = Math.Max(max, secondary!.Peak);
                if (max <= 0) max = 1;
            }

            int slots = (int)((w + (BarStep - BarWidth)) / BarStep);
            double axisY = Math.Round(h / 2.0);
            EnsureGradients(h, axisY);

            if (Mode == GraphMode.Mirrored)
            {
                double halfH = axisY - 1;

                // Half-scale guides either side of the axis, then bars rooted bright at the
                // axis fading toward the edges.
                double guide = Math.Round(halfH / 2.0);
                dc.DrawLine(GridPen, new Point(0, axisY - guide + 0.5), new Point(w, axisY - guide + 0.5));
                dc.DrawLine(GridPen, new Point(0, axisY + guide - 0.5), new Point(w, axisY + guide - 0.5));

                Brush upFill = _cachedUpFill!;
                Brush downFill = _cachedDownFill!;

                for (int k = 0; k < slots; k++)
                {
                    double x = w - BarWidth - k * BarStep;
                    if (x < 0) break;

                    if (hasSecondary)
                    {
                        int upIdx = secondary!.Count - 1 - k;
                        if (upIdx >= 0)
                        {
                            double uh = BarHeight(secondary[upIdx], max, halfH);
                            if (uh > 0) dc.DrawRectangle(upFill, null, new Rect(x, axisY - uh, BarWidth, uh));
                        }
                    }

                    int downIdx = primary.Count - 1 - k;
                    if (downIdx >= 0)
                    {
                        double dh = BarHeight(primary[downIdx], max, halfH);
                        if (dh > 0) dc.DrawRectangle(downFill, null, new Rect(x, axisY, BarWidth, dh));
                    }
                }

                dc.DrawLine(MirroredAxisPen, new Point(0, axisY - 0.5), new Point(w, axisY - 0.5));
                return;
            }

            for (int q = 1; q <= 3; q++)
            {
                double gy = Math.Round(h * q / 4.0) + 0.5;
                dc.DrawLine(GridPen, new Point(0, gy), new Point(w, gy));
            }
            dc.DrawLine(FloorPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

            Brush fill = _cachedFill!;
            for (int k = 0; k < slots; k++)
            {
                double x = w - BarWidth - k * BarStep;
                if (x < 0) break;

                int idx = primary.Count - 1 - k;
                if (idx < 0) break;

                double total = BarHeight(primary[idx], max, h);
                if (total <= 0) continue;
                dc.DrawRectangle(fill, null, new Rect(x, h - total, BarWidth, total));

                if (Mode == GraphMode.Stacked && hasSecondary)
                {
                    // Tail-aligned lookup: both series gain one sample per tick once warmed up,
                    // so index-from-the-end pairs the same instants even though the secondary
                    // starts one tick later and holds one fewer sample.
                    int sIdx = secondary!.Count - 1 - k;
                    if (sIdx >= 0)
                    {
                        double tip = Math.Min(BarHeight(secondary[sIdx], max, h), total);
                        if (tip > 0) dc.DrawRectangle(SecondaryBrush, null, new Rect(x, h - total, BarWidth, tip));
                    }
                }
            }
        }

        private static double BarHeight(float value, double max, double available)
        {
            if (value <= 0 || max <= 0) return 0;
            double frac = Math.Clamp(value / max, 0.0, 1.0);
            // A non-zero sample must stay visible; rounding a sliver to zero would read as idle.
            return Math.Max(1.0, frac * available);
        }

        /// <summary>An unreadable sensor gets a dashed baseline, never empty bars.</summary>
        private static void DrawBaseline(DrawingContext dc, double w, double h)
        {
            dc.DrawLine(BaselinePen, new Point(0, h - 0.5), new Point(w, h - 0.5));
        }
    }
}
