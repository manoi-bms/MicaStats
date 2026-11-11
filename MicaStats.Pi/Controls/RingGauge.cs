using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MicaStats.Pi.Controls;

/// <summary>iStat-style ring gauge: dim full-circle track, cyan arc for the value.</summary>
public class RingGauge : Control
{
    private double _value;

    /// <summary>0–100.</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); InvalidateVisual(); }
    }

    public IBrush ArcBrush { get; set; } = new SolidColorBrush(Color.Parse("#3FD2E4"));
    public double Thickness { get; set; } = 5;

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#22FFFFFF"));

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double r = (Math.Min(w, h) - Thickness) / 2;
        var centre = new Point(w / 2, h / 2);

        context.DrawEllipse(null, new Pen(Track, Thickness), centre, r, r);

        double sweep = 360.0 * _value / 100.0;
        if (sweep <= 0.5) return;
        sweep = Math.Min(sweep, 359.5); // a full 360° arc has coincident endpoints and vanishes

        const double startDeg = -90;
        double endDeg = startDeg + sweep;
        Point PointAt(double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(centre.X + r * Math.Cos(rad), centre.Y + r * Math.Sin(rad));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointAt(startDeg), isFilled: false);
            ctx.ArcTo(PointAt(endDeg), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        var pen = new Pen(ArcBrush, Thickness) { LineCap = PenLineCap.Round };
        context.DrawGeometry(null, pen, geometry);
    }
}
