using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MicaStats.Pi.Telemetry;

namespace MicaStats.Pi.Controls;

/// <summary>
/// The mirrored up/down network graph from the Windows app: upload bars rise
/// above a dim centre line (cyan), download bars hang below it (red). Both
/// halves share one autoscale ceiling so their magnitudes are comparable.
/// </summary>
public class DualHistoryGraph : Control
{
    public Series? UpSeries { get; set; }
    public Series? DownSeries { get; set; }

    public IBrush UpBrush { get; set; } = new SolidColorBrush(Color.Parse("#3FD2E4"));
    public IBrush DownBrush { get; set; } = new SolidColorBrush(Color.Parse("#FF5147"));

    private static readonly IBrush Centre = new SolidColorBrush(Color.Parse("#33FFFFFF"));

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double mid = Math.Round(h / 2);
        context.FillRectangle(Centre, new Rect(0, mid, w, 1));

        var up = UpSeries;
        var down = DownSeries;
        if (up == null || down == null) return;

        float scale = Math.Max(up.Peak, down.Peak);
        if (scale <= 0) return;

        int cap = up.Capacity;
        double slot = w / cap;
        double bw = Math.Max(1.0, slot - 1.0);
        double half = mid - 1;

        for (int i = 0; i < up.Count; i++)
        {
            double v = Math.Clamp(up[i] / scale, 0, 1);
            if (v <= 0) continue;
            double bh = Math.Max(1.0, v * half);
            double x = (cap - up.Count + i) * slot;
            context.FillRectangle(UpBrush, new Rect(x, mid - bh, bw, bh));
        }

        for (int i = 0; i < down.Count; i++)
        {
            double v = Math.Clamp(down[i] / scale, 0, 1);
            if (v <= 0) continue;
            double bh = Math.Max(1.0, v * half);
            double x = (cap - down.Count + i) * slot;
            context.FillRectangle(DownBrush, new Rect(x, mid + 1, bw, bh));
        }
    }
}
