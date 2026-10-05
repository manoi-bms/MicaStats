using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Conference;

/// <summary>A bounded amplitude envelope. It draws only received samples, without animation.</summary>
public sealed class MeetingWaveform : Control
{
    private float[] _peaks = Array.Empty<float>();

    public MeetingWaveform()
    {
        Focusable = false;
        IsTabStop = false;
    }

    public static readonly DependencyProperty ClipBrushProperty = DependencyProperty.Register(
        nameof(ClipBrush), typeof(Brush), typeof(MeetingWaveform),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush ClipBrush
    {
        get => (Brush)GetValue(ClipBrushProperty);
        set => SetValue(ClipBrushProperty, value);
    }

    internal bool HasSignal => Array.Exists(_peaks, peak => peak > 0);

    internal void SetPeaks(float[] peaks)
    {
        // Keep display memory bounded even when a synthetic/optional monitor supplies a larger history.
        int length = Math.Min(peaks.Length, 60);
        _peaks = new float[length];
        for (int i = 0; i < length; i++)
        {
            float value = peaks[peaks.Length - length + i];
            _peaks[i] = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double center = ActualHeight / 2;
        drawingContext.DrawLine(new Pen(Background, 1), new Point(0, center), new Point(ActualWidth, center));
        if (_peaks.Length == 0 || ActualWidth <= 0 || ActualHeight <= 4) return;

        double step = ActualWidth / _peaks.Length;
        double width = Math.Max(1, step * 0.55);
        for (int i = 0; i < _peaks.Length; i++)
        {
            float peak = _peaks[i];
            if (peak <= 0) continue;
            // Square-root scaling keeps quiet input visible; the numeric dBFS label remains exact.
            double height = Math.Max(1, Math.Sqrt(peak) * (ActualHeight - 4));
            drawingContext.DrawRoundedRectangle(peak >= 0.98f ? ClipBrush : Foreground, null,
                new Rect(i * step + (step - width) / 2, center - height / 2, width, height),
                width / 2, width / 2);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WaveformPeer(this);

    private sealed class WaveformPeer(MeetingWaveform owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MeetingWaveform);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
