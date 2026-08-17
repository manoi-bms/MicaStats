using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;

// UseWindowsForms and ImplicitUsings together put System.Drawing in scope, which collides with
// System.Windows.Media on Color and Brush. This view model is WPF-facing, so bind the names there.
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>One label/value cell of a section's statistics grid (e.g. "Peak" / "87%").</summary>
    public sealed class StatPair : INotifyPropertyChanged
    {
        private string _value = "—";

        public StatPair(string label) => Label = label;

        public string Label { get; }

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One row of the detail panel: a heading, readouts, a bar and a history graph.</summary>
    public sealed class MetricSection : INotifyPropertyChanged
    {
        /// <summary>
        /// Graph box in device-independent units. Fixed so the geometry helper can project into a
        /// known size without the view having to report its layout back to the view model.
        /// </summary>
        public const double GraphWidth = 320;
        public const double GraphHeight = 56;

        private string _primary = "";
        private string _secondary = "";
        private double _percent;
        private bool _isAvailable = true;
        private string _statusText = "";
        private PointCollection _area = new();

        public MetricSection(string title, Color accent)
        {
            Title = title;
            var stroke = new SolidColorBrush(accent);
            stroke.Freeze();
            Accent = stroke;
            var fill = new SolidColorBrush(Color.FromArgb(52, accent.R, accent.G, accent.B));
            fill.Freeze();
            AreaFill = fill;
        }

        public string Title { get; }
        public Brush Accent { get; }
        public Brush AreaFill { get; }

        /// <summary>Marks the processor section so the view can fuse the per-core strip under its graph.</summary>
        public bool IsCpu { get; init; }

        /// <summary>The section's statistics grid, fixed at construction; only values change.</summary>
        public System.Collections.ObjectModel.ObservableCollection<StatPair> Stats { get; } = new();

        /// <summary>Headline reading, e.g. "45%".</summary>
        public string Primary { get => _primary; set => Set(ref _primary, value); }

        /// <summary>Supporting detail, e.g. "9.8 / 16.0 GB". Empty when there is none.</summary>
        public string Secondary { get => _secondary; set => Set(ref _secondary, value); }

        /// <summary>0-100 for the horizontal bar.</summary>
        public double Percent { get => _percent; set => Set(ref _percent, value); }

        /// <summary>False when the sensor cannot be read; the view shows a notice instead of a graph.</summary>
        public bool IsAvailable { get => _isAvailable; set => Set(ref _isAvailable, value); }

        /// <summary>Why there is no graph, when <see cref="IsAvailable"/> is false.</summary>
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        /// <summary>Closed polygon for the filled history area, in graph-box coordinates.</summary>
        public PointCollection Area { get => _area; set => Set(ref _area, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>One logical processor's current load.</summary>
    public sealed class CoreLoad : INotifyPropertyChanged
    {
        private double _percent;

        public CoreLoad(int ordinal) => Label = ordinal.ToString();

        /// <summary>
        /// Display ordinal, not a processor number: the counter's instance names are
        /// (NUMA node, index-within-node) and the index restarts per node.
        /// </summary>
        public string Label { get; }

        public double Percent
        {
            get => _percent;
            set
            {
                if (Math.Abs(_percent - value) < 0.01) return;
                _percent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarHeight)));
            }
        }

        /// <summary>Bar height in the 22px-tall core strip.</summary>
        public double BarHeight => Math.Max(1.0, _percent * 0.22);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Shapes <see cref="MetricsHistory"/> into bindable sections for the detail panel.
    ///
    /// <para>
    /// Recomputation is gated on <see cref="IsLive"/>. A closed panel does no work at all: no
    /// geometry projection, no PointCollection rebuilds, no notifications.
    /// </para>
    /// </summary>
    public sealed class StatsPanelViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly MetricsHistory _history;
        private readonly AppConfig _config;
        private readonly Action _onHistoryUpdated;
        private readonly ProcessSampler _processes = new();
        private bool _isLive;
        private bool _disposed;

        private readonly MetricSection _cpu;
        private readonly MetricSection _memory;
        private readonly MetricSection _gpu;
        private readonly MetricSection _network;
        private readonly MetricSection _disk;

        public StatsPanelViewModel(MetricsHistory history, AppConfig config)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _cpu = new MetricSection("Processor", Color.FromRgb(0x4F, 0xC3, 0xF7)) { IsCpu = true };
            _memory = new MetricSection("Memory", Color.FromRgb(0x81, 0xC7, 0x84));
            _gpu = new MetricSection("Graphics", Color.FromRgb(0xBA, 0x68, 0xC8));
            _network = new MetricSection("Network", Color.FromRgb(0xFF, 0xB7, 0x4D));
            _disk = new MetricSection("Storage", Color.FromRgb(0xE5, 0x73, 0x73));

            // Fixed stat cells; Refresh only rewrites their values, so nothing rebinds per tick.
            foreach (var l in new[] { "Average", "Peak", "Cores" }) _cpu.Stats.Add(new StatPair(l));
            foreach (var l in new[] { "Used", "Free", "Peak" }) _memory.Stats.Add(new StatPair(l));
            foreach (var l in new[] { "Temp", "Average", "Peak" }) _gpu.Stats.Add(new StatPair(l));
            foreach (var l in new[] { "↓ Peak", "↑ Peak", "↓ Avg" }) _network.Stats.Add(new StatPair(l));
            foreach (var l in new[] { "Average", "Peak", "Used" }) _disk.Stats.Add(new StatPair(l));

            Sections = new ObservableCollection<MetricSection> { _cpu, _memory, _gpu, _network, _disk };
            Cores = new ObservableCollection<CoreLoad>();

            TopProcesses = new ObservableCollection<ProcessUsage>();

            _onHistoryUpdated = () => { if (IsLive) Refresh(); };
            _history.Updated += _onHistoryUpdated;
            _processes.Updated += OnProcessesUpdated;
        }

        public ObservableCollection<MetricSection> Sections { get; }
        public ObservableCollection<CoreLoad> Cores { get; }

        /// <summary>Highest CPU consumers, refreshed only while the panel is open.</summary>
        public ObservableCollection<ProcessUsage> TopProcesses { get; }

        public bool HasProcesses => TopProcesses.Count > 0;

        /// <summary>
        /// The sampler raises this on its own timer thread, so the collection update is marshalled
        /// to the UI thread before touching anything bound.
        /// </summary>
        private void OnProcessesUpdated()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !IsLive) return;

                bool had = TopProcesses.Count > 0;
                TopProcesses.Clear();
                foreach (var p in _processes.TopByCpu) TopProcesses.Add(p);
                if (had != (TopProcesses.Count > 0)) OnPropertyChanged(nameof(HasProcesses));
            });
        }

        /// <summary>
        /// Whether to keep the sections current. Set false when the panel closes so a hidden panel
        /// costs nothing.
        /// </summary>
        public bool IsLive
        {
            get => _isLive;
            set
            {
                if (_isLive == value) return;
                _isLive = value;
                OnPropertyChanged();

                // Process enumeration is the one genuinely costly sample, so it runs only while the
                // panel is actually on screen.
                _processes.Enabled = _isLive;

                if (_isLive) Refresh();
                else
                {
                    TopProcesses.Clear();
                    OnPropertyChanged(nameof(HasProcesses));
                }
            }
        }

        public string CpuName => SystemInfoProvider.Current.CpuName;
        public string GpuName => SystemInfoProvider.Current.GpuName;
        public string TotalRamText => SystemInfoProvider.Current.TotalRamText;
        public string OsVersion => SystemInfoProvider.Current.OsVersion;
        public string Uptime => SystemInfoProvider.FormatUptime(SystemInfoProvider.Uptime);
        public bool HasCores => Cores.Count > 0;

        /// <summary>Re-derives every section from the current history. UI thread only.</summary>
        public void Refresh()
        {
            if (_disposed) return;

            var m = _history.Latest;

            UpdatePercentSection(_cpu, _history.Cpu, m.CpuUsage, $"{(int)m.CpuUsage}%", CoreSummary());
            UpdatePercentSection(_memory, _history.Ram, m.RamPercent, $"{(int)m.RamPercent}%", MemorySummary(m));
            UpdatePercentSection(_gpu, _history.Gpu, m.GpuUsage, $"{(int)m.GpuUsage}%", TempSummary(m));

            UpdateNetworkSection(m);
            UpdateDiskSection(m);
            UpdateCores(m);
            UpdateStats(m);

            OnPropertyChanged(nameof(Uptime));
            OnPropertyChanged(nameof(CpuName));
            OnPropertyChanged(nameof(GpuName));
            OnPropertyChanged(nameof(TotalRamText));
            OnPropertyChanged(nameof(OsVersion));
        }

        private string CoreSummary() =>
            _history.Cores.Count > 0 ? $"{_history.Cores.Count} logical processors" : "";

        private static string MemorySummary(SystemMetrics m)
        {
            if (m.RamTotalBytes == 0) return "";
            double usedGb = m.RamUsedBytes / 1024d / 1024d / 1024d;
            double totalGb = m.RamTotalBytes / 1024d / 1024d / 1024d;
            return $"{usedGb:F1} / {totalGb:F1} GB";
        }

        private static string TempSummary(SystemMetrics m) =>
            m.GpuTemperature > 0 ? $"{(int)m.GpuTemperature} °C" : "temperature unavailable";

        private void UpdatePercentSection(MetricSection section, Series series, float value, string primary, string secondary)
        {
            section.Primary = primary;
            section.Secondary = secondary;
            section.Percent = Math.Clamp(value, 0, 100);
            ApplyGraph(section, series, max: 100f);
        }

        private void UpdateNetworkSection(SystemMetrics m)
        {
            _network.Primary = $"↓ {m.NetDownText}";
            _network.Secondary = $"↑ {m.NetUpText}";

            // Both directions share one scale, so the bar shows download against the shared peak
            // rather than against an absolute link speed we cannot know.
            float peak = _history.SharedNetPeak;
            _network.Percent = peak > 0 ? Math.Clamp(m.NetDownKbps / peak * 100f, 0, 100) : 0;
            ApplyGraph(_network, _history.NetDown, max: peak);
        }

        private void UpdateDiskSection(SystemMetrics m)
        {
            if (m.Disks == null || m.Disks.Count == 0)
            {
                _disk.Primary = "—";
                _disk.Secondary = "no drives selected";
                _disk.Percent = 0;
                _disk.IsAvailable = false;
                _disk.StatusText = "No drives are selected in Monitoring settings.";
                _disk.Area = new PointCollection();
                return;
            }

            // Show the busiest drive; the overlay already breaks out each one individually.
            DiskMetric busiest = m.Disks[0];
            foreach (var d in m.Disks)
            {
                if (d.ActivityPercent > busiest.ActivityPercent) busiest = d;
            }

            _disk.Primary = $"{(int)busiest.ActivityPercent}%";
            _disk.Secondary = $"{Trim(busiest.Name)} · {(int)busiest.SpacePercent}% used";
            _disk.Percent = Math.Clamp(busiest.ActivityPercent, 0, 100);
            ApplyGraph(_disk, _history.Disk(busiest.Name), max: 100f);
        }

        private static string Trim(string instanceName)
        {
            int space = instanceName.IndexOf(' ');
            return space > 0 && space + 1 < instanceName.Length ? instanceName.Substring(space + 1) : instanceName;
        }

        private void UpdateCores(SystemMetrics m)
        {
            var usage = m.CoreUsage;
            int had = Cores.Count;

            if (usage == null || usage.Length == 0)
            {
                if (had > 0) { Cores.Clear(); OnPropertyChanged(nameof(HasCores)); }
                return;
            }

            while (Cores.Count < usage.Length) Cores.Add(new CoreLoad(Cores.Count));
            while (Cores.Count > usage.Length) Cores.RemoveAt(Cores.Count - 1);

            for (int i = 0; i < usage.Length; i++) Cores[i].Percent = Math.Clamp(usage[i], 0, 100);

            if (had != Cores.Count) OnPropertyChanged(nameof(HasCores));
        }

        /// <summary>Rewrites every section's statistics grid from the retained window.</summary>
        private void UpdateStats(SystemMetrics m)
        {
            _cpu.Stats[0].Value = $"{_history.Cpu.Average:F0}%";
            _cpu.Stats[1].Value = $"{_history.Cpu.Max:F0}%";
            _cpu.Stats[2].Value = _history.Cores.Count > 0 ? _history.Cores.Count.ToString() : "—";

            if (m.RamTotalBytes > 0)
            {
                double usedGb = m.RamUsedBytes / 1024d / 1024d / 1024d;
                double freeGb = (m.RamTotalBytes - m.RamUsedBytes) / 1024d / 1024d / 1024d;
                _memory.Stats[0].Value = $"{usedGb:F1} GB";
                _memory.Stats[1].Value = $"{freeGb:F1} GB";
            }
            _memory.Stats[2].Value = $"{_history.Ram.Max:F0}%";

            _gpu.Stats[0].Value = m.GpuTemperature > 0 ? $"{(int)m.GpuTemperature}°C" : "—";
            _gpu.Stats[1].Value = $"{_history.Gpu.Average:F0}%";
            _gpu.Stats[2].Value = $"{_history.Gpu.Max:F0}%";

            _network.Stats[0].Value = FormatKbps(_history.NetDown.Max);
            _network.Stats[1].Value = FormatKbps(_history.NetUp.Max);
            _network.Stats[2].Value = FormatKbps(_history.NetDown.Average);

            Series? busiest = null;
            float space = 0;
            if (m.Disks != null && m.Disks.Count > 0)
            {
                DiskMetric top = m.Disks[0];
                foreach (var d in m.Disks) if (d.ActivityPercent > top.ActivityPercent) top = d;
                busiest = _history.Disk(top.Name);
                space = top.SpacePercent;
            }
            _disk.Stats[0].Value = busiest != null ? $"{busiest.Average:F0}%" : "—";
            _disk.Stats[1].Value = busiest != null ? $"{busiest.Max:F0}%" : "—";
            _disk.Stats[2].Value = busiest != null ? $"{space:F0}%" : "—";
        }

        /// <summary>Renders a KB/s figure with the same unit thresholds the overlay uses.</summary>
        private static string FormatKbps(float kbps)
        {
            if (kbps >= 1024f * 1024f) return $"{kbps / 1024f / 1024f:F1} GB/s";
            if (kbps >= 1024f) return $"{kbps / 1024f:F1} MB/s";
            return $"{kbps:F0} KB/s";
        }

        /// <summary>
        /// Projects a series into a closed polygon for the filled area chart. An unreadable sensor
        /// yields no geometry and flips the section to unavailable, because a flat line at the
        /// baseline would read as "idle" rather than "cannot be measured".
        /// </summary>
        private static void ApplyGraph(MetricSection section, Series? series, float max)
        {
            if (series == null || series.Availability != Availability.Value || series.Count == 0)
            {
                section.IsAvailable = false;
                section.StatusText = series?.Availability == Availability.Unavailable
                    ? "This sensor could not be read on this system."
                    : "Collecting data…";
                section.Area = new PointCollection();
                return;
            }

            section.IsAvailable = true;
            section.StatusText = "";

            float w = (float)MetricSection.GraphWidth;
            float h = (float)MetricSection.GraphHeight;

            Span<System.Drawing.PointF> pts = stackalloc System.Drawing.PointF[512];
            int n = SparklineGeometry.Project(series, w, h, max, pts);
            if (n <= 0)
            {
                section.Area = new PointCollection();
                return;
            }

            // Close the polygon along the baseline so it fills as an area rather than a stroke.
            var poly = new PointCollection(n + 2);
            poly.Add(new System.Windows.Point(pts[0].X, h));
            for (int i = 0; i < n; i++) poly.Add(new System.Windows.Point(pts[i].X, pts[i].Y));
            poly.Add(new System.Windows.Point(pts[n - 1].X, h));
            poly.Freeze();
            section.Area = poly;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _history.Updated -= _onHistoryUpdated;
            _processes.Updated -= OnProcessesUpdated;
            _processes.Dispose();
        }
    }
}
