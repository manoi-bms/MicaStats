using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MicaStats.Pi.Telemetry;

namespace MicaStats.Pi;

public partial class MainWindow : Window
{
    /// <summary>Height of the desktop's top panel (waybar), which the dropdown hangs beneath.</summary>
    private const int MenuBarHeight = 28;
    private const int EdgeGap = 6;

    /// <summary>
    /// A click on the menu bar both deactivates the dropdown (hiding it) and sends the
    /// toggle signal; a toggle this soon after a focus-loss hide is that same click.
    /// </summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly LinuxTelemetryService _telemetry = new();
    private readonly DispatcherTimer _timer;
    private TelemetrySnapshot? _last;
    private IReadOnlyList<ProcessSample>? _lastProcesses;
    private DateTime _hiddenByFocusLossAt = DateTime.MinValue;

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

        // Sampling continues while hidden so the history graphs are already full when
        // the dropdown opens, as in iStat Menus; only the visible window is redrawn.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };

        Deactivated += (_, _) =>
        {
            if (!IsVisible) return;
            _hiddenByFocusLossAt = DateTime.UtcNow;
            Hide();
        };

        // Closing (e.g. a window-manager close) hides instead of destroying the window,
        // so the background sampler and its history survive.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public void ToggleDropdown()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (DateTime.UtcNow - _hiddenByFocusLossAt < ReopenGuard) return;
        ShowDropdown();
    }

    public void ShowDropdown()
    {
        if (_last != null) Apply(_last);
        Show();
        PlaceUnderMenuBar();
        Activate();
    }

    /// <summary>Right-aligned beneath the menu bar, where the stats modules sit.</summary>
    private void PlaceUnderMenuBar()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        var area = screen.Bounds;
        double scale = screen.Scaling;
        int widthPx = (int)Math.Ceiling(Bounds.Width * scale);
        int x = area.Right - widthPx - (int)(EdgeGap * scale);
        int y = area.Y + (int)((MenuBarHeight + EdgeGap) * scale);
        Position = new PixelPoint(Math.Max(area.X, x), y);
    }

    private void Tick()
    {
        var s = _telemetry.Sample();
        _last = s;
        if (s.TopProcesses != null) _lastProcesses = s.TopProcesses;
        if (IsVisible) Apply(s);
    }

    private void Apply(TelemetrySnapshot s)
    {
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

        if (_lastProcesses is { } procs) RenderProcesses(procs);

        Footer.Text = $"Uptime {FormatUptime(s.Uptime)}  ·  {DateTime.Now:ddd d MMM HH:mm}";

        CpuGraph.InvalidateVisual();
        TempGraph.InvalidateVisual();
        NetGraph.InvalidateVisual();
    }

    private void RenderProcesses(IReadOnlyList<ProcessSample> procs)
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
