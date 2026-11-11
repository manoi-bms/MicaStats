using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MicaStats.Pi.Telemetry;

namespace MicaStats.Pi.Controls;

/// <summary>
/// iStat-style discrete-bar history graph: one thin bar per received sample,
/// oldest at the left, newest hugging the right edge. Draws only real samples —
/// a series with no data renders empty, never a fabricated flat line.
/// </summary>
public class HistoryGraph : Control
{
    public Series? Series { get; set; }
    public IBrush BarBrush { get; set; } = new SolidColorBrush(Color.Parse("#3FD2E4"));

    /// <summary>True for percent metrics (fixed 0–100); false autoscales to the series' decayed peak.</summary>
    public bool FixedScale100 { get; set; } = true;

    private static readonly IBrush Baseline = new SolidColorBrush(Color.Parse("#26FFFFFF"));

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        context.FillRectangle(Baseline, new Rect(0, h - 1, w, 1));

        var s = Series;
        if (s == null || s.Count == 0) return;

        int cap = s.Capacity;
        double slot = w / cap;
        double bw = Math.Max(1.0, slot - 1.0);
        float scale = FixedScale100 ? 100f : s.Peak;
        if (scale <= 0) scale = 1;

        for (int i = 0; i < s.Count; i++)
        {
            double v = Math.Clamp(s[i] / scale, 0, 1);
            if (v <= 0) continue;
            double bh = Math.Max(1.0, v * (h - 2));
            double x = (cap - s.Count + i) * slot;
            context.FillRectangle(BarBrush, new Rect(x, h - 1 - bh, bw, bh));
        }
    }
}
