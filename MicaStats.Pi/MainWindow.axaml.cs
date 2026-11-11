using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MicaStats.Pi.Telemetry;

namespace MicaStats.Pi;

public partial class MainWindow : Window
{
    private readonly LinuxTelemetryService _telemetry = new();
    private readonly DispatcherTimer _timer;

    private static readonly IBrush BrightText = new SolidColorBrush(Color.Parse("#E9EDEDF2"));
    private static readonly IBrush DimText = new SolidColorBrush(Color.Parse("#6EE9EDF2"));
    private static readonly IBrush Cyan = new SolidColorBrush(Color.Parse("#3FD2E4"));

    public MainWindow()
    {
        InitializeComponent();

        CpuGraph.Series = _telemetry.CpuHistory;
        TempGraph.Series = _telemetry.TempHistory;
        NetGraph.UpSeries = _telemetry.NetUpHistory;
        NetGraph.DownSeries = _telemetry.NetDownHistory;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var s = _telemetry.Sample();

        CpuValue.Text = $"{s.CpuPercent:0}%";
        CpuSub.Text = $"Load {s.LoadAvg1:0.00}  ·  {s.CoreCount} cores";
        CpuFreq.Text = s.CpuFreqMhz is double f ? $"{f / 1000.0:0.00} GHz" : "—";

        MemValue.Text = $"{s.MemPercent:0}%";
        MemRing.Value = s.MemPercent;
        MemRingText.Text = $"{s.MemPercent:0}%";
        MemUsed.Text = $"{s.MemUsedGb:0.0} GB";
        MemFree.Text = $"{s.MemTotalGb - s.MemUsedGb:0.0} GB";
        MemSwap.Text = s.SwapTotalGb > 0 ? $"{s.SwapUsedGb:0.0} / {s.SwapTotalGb:0.0} GB" : "—";

        TempValue.Text = s.TempC is float t ? $"{t:0.0}°" : "—";

        NetHeader.Text = $"NETWORK · {s.NetInterface.ToUpperInvariant()}";
        NetUp.Text = "▲ " + Rate(s.NetUpBytesPerSec);
        NetDown.Text = "▼ " + Rate(s.NetDownBytesPerSec);

        DiskValue.Text = $"{s.DiskUsedPercent:0}%";
        DiskSpace.Text = $"{s.DiskFreeGb:0.0} GB free of {s.DiskTotalGb:0.0} GB";
        DiskRead.Text = "R " + (s.DiskReadBytesPerSec is double r ? Rate(r) : "—");
        DiskWrite.Text = "W " + (s.DiskWriteBytesPerSec is double w ? Rate(w) : "—");
        double trackWidth = DiskBarTrack.Bounds.Width;
        if (trackWidth > 0)
            DiskBarFill.Width = Math.Clamp(trackWidth * s.DiskUsedPercent / 100.0, 0, trackWidth);

        if (s.TopProcesses is { } procs)
        {
            ProcList.Children.Clear();
            foreach (var p in procs)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,52") };
                var name = new TextBlock
                {
                    Text = p.Name, FontSize = 10.5, Foreground = BrightText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var mem = new TextBlock
                {
                    Text = $"{p.MemMb:0} MB", FontSize = 10, Foreground = DimText,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                var cpu = new TextBlock
                {
                    Text = $"{p.CpuPercent:0.0}%", FontSize = 10.5, Foreground = Cyan,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                Grid.SetColumn(mem, 1);
                Grid.SetColumn(cpu, 2);
                row.Children.Add(name);
                row.Children.Add(mem);
                row.Children.Add(cpu);
                ProcList.Children.Add(row);
            }
        }

        Footer.Text = $"Uptime {FormatUptime(s.Uptime)}  ·  {DateTime.Now:ddd d MMM HH:mm}";

        CpuGraph.InvalidateVisual();
        TempGraph.InvalidateVisual();
        NetGraph.InvalidateVisual();
    }

    private static string Rate(double bytesPerSec)
    {
        if (bytesPerSec < 1000) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1000_000) return $"{bytesPerSec / 1000:0.0} kB/s";
        if (bytesPerSec < 1000_000_000) return $"{bytesPerSec / 1000_000:0.0} MB/s";
        return $"{bytesPerSec / 1000_000_000:0.00} GB/s";
    }

    private static string FormatUptime(TimeSpan u) =>
        u.TotalDays >= 1 ? $"{(int)u.TotalDays}d {u.Hours}h" :
        u.TotalHours >= 1 ? $"{(int)u.TotalHours}h {u.Minutes}m" :
        $"{u.Minutes}m";
}
