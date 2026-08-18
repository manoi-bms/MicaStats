using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>One logical processor's current load, rendered as a small ring gauge.</summary>
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
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One drive's row in the Disks card.</summary>
    public sealed class DiskRow : INotifyPropertyChanged
    {
        private string _activity = "—";
        private string _space = "";

        public DiskRow(string name) => Name = name;

        /// <summary>Drive letter(s), e.g. "C:".</summary>
        public string Name { get; }

        public string Activity
        {
            get => _activity;
            set { if (_activity != value) { _activity = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Activity))); } }
        }

        public string Space
        {
            get => _space;
            set { if (_space != value) { _space = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Space))); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Shapes <see cref="MetricsHistory"/> into the iStat-style card layout: fixed cards
    /// (CPU, Memory, GPU, Network, Disks, Processes) with explicit properties per readout.
    ///
    /// <para>
    /// Graphs bind to the history's <see cref="Series"/> instances directly and re-render off
    /// <see cref="Tick"/>; this class only produces text, percentages and the tick. All of it
    /// is gated on <see cref="IsLive"/>, so a closed panel does no work at all.
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

        public StatsPanelViewModel(MetricsHistory history, AppConfig config)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            Cores = new ObservableCollection<CoreLoad>();
            DiskRows = new ObservableCollection<DiskRow>();
            TopProcesses = new ObservableCollection<ProcessUsage>();

            _onHistoryUpdated = () => { if (IsLive) Refresh(); };
            _history.Updated += _onHistoryUpdated;
            _processes.Updated += OnProcessesUpdated;
        }

        // ---- graph sources (stable instances; the controls re-render off Tick) ----

        public Series CpuSeries => _history.Cpu;
        public Series CpuSystemSeries => _history.CpuSystem;
        public Series GpuSeries => _history.Gpu;
        public Series NetUpSeries => _history.NetUp;
        public Series NetDownSeries => _history.NetDown;

        private Series? _diskSeries;
        /// <summary>History of the busiest selected drive; instance changes when the busiest drive does.</summary>
        public Series? DiskSeries { get => _diskSeries; private set => Set(ref _diskSeries, value); }

        private int _tick;
        /// <summary>Monotonic sample counter; every graph invalidates when it changes.</summary>
        public int Tick { get => _tick; private set => Set(ref _tick, value); }

        // ---- CPU card ----

        private string _cpuValueText = "—";
        public string CpuValueText { get => _cpuValueText; private set => Set(ref _cpuValueText, value); }

        private bool _hasCpuSplit;
        /// <summary>True when the kernel/user split is readable; the legend then shows User/System.</summary>
        public bool HasCpuSplit { get => _hasCpuSplit; private set => Set(ref _hasCpuSplit, value); }

        private string _cpuLegend1Label = "Usage";
        public string CpuLegend1Label { get => _cpuLegend1Label; private set => Set(ref _cpuLegend1Label, value); }

        private string _cpuLegend1Value = "—";
        public string CpuLegend1Value { get => _cpuLegend1Value; private set => Set(ref _cpuLegend1Value, value); }

        private string _cpuLegend2Label = "Peak";
        public string CpuLegend2Label { get => _cpuLegend2Label; private set => Set(ref _cpuLegend2Label, value); }

        private string _cpuLegend2Value = "—";
        public string CpuLegend2Value { get => _cpuLegend2Value; private set => Set(ref _cpuLegend2Value, value); }

        private string _uptimeText = "";
        public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }

        // ---- Memory card ----

        private double _memoryPercent;
        public double MemoryPercent { get => _memoryPercent; private set => Set(ref _memoryPercent, value); }

        private string _memoryRingText = "—";
        public string MemoryRingText { get => _memoryRingText; private set => Set(ref _memoryRingText, value); }

        private double _commitPercent;
        public double CommitPercent { get => _commitPercent; private set => Set(ref _commitPercent, value); }

        private string _commitRingText = "—";
        public string CommitRingText { get => _commitRingText; private set => Set(ref _commitRingText, value); }

        private string _memUsedText = "—";
        public string MemUsedText { get => _memUsedText; private set => Set(ref _memUsedText, value); }

        private string _memFreeText = "—";
        public string MemFreeText { get => _memFreeText; private set => Set(ref _memFreeText, value); }

        private string _memCommitText = "—";
        public string MemCommitText { get => _memCommitText; private set => Set(ref _memCommitText, value); }

        // ---- GPU card ----

        private string _gpuValueText = "—";
        public string GpuValueText { get => _gpuValueText; private set => Set(ref _gpuValueText, value); }

        private double _gpuPercent;
        public double GpuPercent { get => _gpuPercent; private set => Set(ref _gpuPercent, value); }

        private double _gpuTempPercent;
        public double GpuTempPercent { get => _gpuTempPercent; private set => Set(ref _gpuTempPercent, value); }

        private string _gpuTempText = "—";
        public string GpuTempText { get => _gpuTempText; private set => Set(ref _gpuTempText, value); }

        private bool _gpuGraphAvailable = true;
        public bool GpuGraphAvailable { get => _gpuGraphAvailable; private set => Set(ref _gpuGraphAvailable, value); }

        // ---- Network card ----

        private string _netUpBigText = "0 KB/s";
        public string NetUpBigText { get => _netUpBigText; private set => Set(ref _netUpBigText, value); }

        private string _netDownBigText = "0 KB/s";
        public string NetDownBigText { get => _netDownBigText; private set => Set(ref _netDownBigText, value); }

        private string _netPeakUpText = "—";
        public string NetPeakUpText { get => _netPeakUpText; private set => Set(ref _netPeakUpText, value); }

        private string _netPeakDownText = "—";
        public string NetPeakDownText { get => _netPeakDownText; private set => Set(ref _netPeakDownText, value); }

        private double _netGraphMax = 1;
        /// <summary>Shared full-scale for both directions, so upload and download stay comparable.</summary>
        public double NetGraphMax { get => _netGraphMax; private set => Set(ref _netGraphMax, value); }

        // ---- Disks card ----

        private string _diskValueText = "—";
        public string DiskValueText { get => _diskValueText; private set => Set(ref _diskValueText, value); }

        private bool _hasDisks;
        public bool HasDisks { get => _hasDisks; private set => Set(ref _hasDisks, value); }

        public ObservableCollection<DiskRow> DiskRows { get; }

        // ---- shared collections ----

        public ObservableCollection<CoreLoad> Cores { get; }

        /// <summary>Highest CPU consumers, refreshed only while the panel is open.</summary>
        public ObservableCollection<ProcessUsage> TopProcesses { get; }

        public bool HasProcesses => TopProcesses.Count > 0;
        public bool HasCores => Cores.Count > 0;

        // ---- identity header ----

        public string CpuName => SystemInfoProvider.Current.CpuName;
        public string GpuName => SystemInfoProvider.Current.GpuName;
        public string TotalRamText => SystemInfoProvider.Current.TotalRamText;
        public string OsVersion => SystemInfoProvider.Current.OsVersion;

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
        /// Whether to keep the cards current. Set false when the panel closes so a hidden panel
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

        /// <summary>Re-derives every card from the current history. UI thread only.</summary>
        public void Refresh()
        {
            if (_disposed) return;

            var m = _history.Latest;

            // CPU
            CpuValueText = $"{(int)m.CpuUsage}%";
            bool split = _history.CpuSystem.Availability == Availability.Value;
            HasCpuSplit = split;
            if (split)
            {
                float sys = Math.Min(_history.CpuSystem.Latest, m.CpuUsage);
                CpuLegend1Label = "User";
                CpuLegend1Value = $"{(int)Math.Max(0f, m.CpuUsage - sys)}%";
                CpuLegend2Label = "System";
                CpuLegend2Value = $"{(int)sys}%";
            }
            else
            {
                CpuLegend1Label = "Usage";
                CpuLegend1Value = $"{(int)m.CpuUsage}%";
                CpuLegend2Label = "Peak";
                CpuLegend2Value = $"{_history.Cpu.Max:F0}%";
            }
            UptimeText = SystemInfoProvider.FormatUptime(SystemInfoProvider.Uptime);

            // Memory
            MemoryPercent = Math.Clamp(m.RamPercent, 0, 100);
            MemoryRingText = $"{(int)m.RamPercent}%";
            CommitPercent = Math.Clamp(m.CommitPercent, 0, 100);
            CommitRingText = $"{(int)m.CommitPercent}%";
            if (m.RamTotalBytes > 0)
            {
                MemUsedText = $"{m.RamUsedBytes / 1024d / 1024d / 1024d:F1} GB";
                MemFreeText = $"{(m.RamTotalBytes - m.RamUsedBytes) / 1024d / 1024d / 1024d:F1} GB";
            }
            MemCommitText = $"{(int)m.CommitPercent}%";

            // GPU
            GpuValueText = $"{(int)m.GpuUsage}%";
            GpuPercent = Math.Clamp(m.GpuUsage, 0, 100);
            bool hasTemp = m.GpuTemperature > 0;
            GpuTempPercent = hasTemp ? Math.Clamp(m.GpuTemperature, 0, 100) : 0;
            GpuTempText = hasTemp ? $"{(int)m.GpuTemperature}°" : "—";
            GpuGraphAvailable = _history.Gpu.Availability == Availability.Value;

            // Network
            NetUpBigText = m.NetUpText;
            NetDownBigText = m.NetDownText;
            NetPeakUpText = FormatKbps(_history.NetUp.Max);
            NetPeakDownText = FormatKbps(_history.NetDown.Max);
            NetGraphMax = Math.Max(1f, _history.SharedNetPeak);

            UpdateDisks(m);
            UpdateCores(m);

            OnPropertyChanged(nameof(CpuName));
            OnPropertyChanged(nameof(GpuName));
            OnPropertyChanged(nameof(TotalRamText));
            OnPropertyChanged(nameof(OsVersion));

            // Bump last: every HistoryBarGraph repaints once per refresh, after all text settled.
            Tick = unchecked(_tick + 1);
        }

        private void UpdateDisks(SystemMetrics m)
        {
            if (m.Disks == null || m.Disks.Count == 0)
            {
                HasDisks = false;
                DiskValueText = "—";
                DiskSeries = null;
                if (DiskRows.Count > 0) DiskRows.Clear();
                return;
            }

            HasDisks = true;

            // Headline and graph follow the busiest drive; the rows list every selected one.
            DiskMetric busiest = m.Disks[0];
            foreach (var d in m.Disks)
            {
                if (d.ActivityPercent > busiest.ActivityPercent) busiest = d;
            }
            DiskValueText = $"{(int)busiest.ActivityPercent}%";

            var series = _history.Disk(busiest.Name);
            if (!ReferenceEquals(series, DiskSeries)) DiskSeries = series;

            // Rebuild rows only when the drive set changes; otherwise update text in place.
            bool sameSet = DiskRows.Count == m.Disks.Count;
            if (sameSet)
            {
                for (int i = 0; i < m.Disks.Count; i++)
                {
                    if (!string.Equals(DiskRows[i].Name, Trim(m.Disks[i].Name), StringComparison.OrdinalIgnoreCase))
                    { sameSet = false; break; }
                }
            }
            if (!sameSet)
            {
                DiskRows.Clear();
                foreach (var d in m.Disks) DiskRows.Add(new DiskRow(Trim(d.Name)));
            }
            for (int i = 0; i < m.Disks.Count; i++)
            {
                DiskRows[i].Activity = $"{(int)m.Disks[i].ActivityPercent}%";
                DiskRows[i].Space = $"{(int)m.Disks[i].SpacePercent}% used";
            }
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

        /// <summary>Renders a KB/s figure with the same unit thresholds the overlay uses.</summary>
        private static string FormatKbps(float kbps)
        {
            if (kbps >= 1024f * 1024f) return $"{kbps / 1024f / 1024f:F1} GB/s";
            if (kbps >= 1024f) return $"{kbps / 1024f:F1} MB/s";
            return $"{kbps:F0} KB/s";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

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
