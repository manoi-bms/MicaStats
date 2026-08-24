using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Kil0bitSystemMonitor.Services.Sensors;
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
        private string _detail = "";

        public DiskRow(string name) => Name = name;

        /// <summary>Drive letter(s), e.g. "C:".</summary>
        public string Name { get; }

        public string Activity
        {
            get => _activity;
            set { if (_activity != value) { _activity = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Activity))); } }
        }

        /// <summary>"62% used · 234 GB free".</summary>
        public string Detail
        {
            get => _detail;
            set { if (_detail != value) { _detail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail))); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// One line on the SENSORS card.
    ///
    /// <para>
    /// Mirrors <see cref="DiskRow"/> rather than using the view model's own <c>Set</c> helper,
    /// which is a private member of <see cref="StatsPanelViewModel"/> and returns void.
    /// </para>
    /// </summary>
    public sealed class SensorRow : INotifyPropertyChanged
    {
        private string _value = "—";
        private string _detail = "";

        public SensorRow(string label) => Label = label;

        /// <summary>What the reading is, e.g. "System (TZ01)".</summary>
        public string Label { get; }

        /// <summary>The formatted reading, or an em dash when unavailable.</summary>
        public string Value
        {
            get => _value;
            set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        }

        /// <summary>Tooltip: provenance, or what would populate an absent reading.</summary>
        public string Detail
        {
            get => _detail;
            set { if (_detail != value) { _detail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail))); } }
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
        /// <summary>
        /// The process sampler, shared with the slowdown recorder rather than owned here.
        /// One kernel snapshot serves both; a second instance would double the syscall for
        /// identical data. Leased with Retain/Release so neither switches the other off.
        /// </summary>
        private readonly ProcessSampler _processes = App.SharedProcessSampler;
        private bool _samplerLeased;
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
        public Series RamSeries => _history.Ram;
        public Series NetUpSeries => _history.NetUp;
        public Series NetDownSeries => _history.NetDown;

        private Series? _diskSeries;
        /// <summary>History of the busiest selected drive; instance changes when the busiest drive does.</summary>
        public Series? DiskSeries { get => _diskSeries; private set => Set(ref _diskSeries, value); }

        private int _tick;
        /// <summary>Monotonic sample counter; every graph invalidates when it changes.</summary>
        public int Tick { get => _tick; private set => Set(ref _tick, value); }

        private PanelSection _filter = PanelSection.All;

        /// <summary>
        /// Which cards render. A hover dropdown shows a single section; the click-opened
        /// panel shows everything.
        /// </summary>
        public PanelSection Filter
        {
            get => _filter;
            set
            {
                if (_filter == value) return;
                _filter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowHeaderCard));
                OnPropertyChanged(nameof(ShowCpuCard));
                OnPropertyChanged(nameof(ShowMemoryCard));
                OnPropertyChanged(nameof(ShowGpuCard));
                OnPropertyChanged(nameof(ShowNetworkCard));
                OnPropertyChanged(nameof(ShowDisksCard));
                OnPropertyChanged(nameof(ShowProcessesCard));
                OnPropertyChanged(nameof(ShowBatteryCard));
            }
        }

        public bool ShowHeaderCard => _filter == PanelSection.All;
        public bool ShowCpuCard => _filter is PanelSection.All or PanelSection.Cpu;
        public bool ShowMemoryCard => _filter is PanelSection.All or PanelSection.Memory;
        public bool ShowGpuCard => _filter is PanelSection.All or PanelSection.Gpu;
        public bool ShowNetworkCard => _filter is PanelSection.All or PanelSection.Network;
        public bool ShowDisksCard => _filter is PanelSection.All or PanelSection.Disks;

        /// <summary>The process list is CPU-ranked, so it accompanies the CPU view and the full panel.</summary>
        public bool ShowProcessesCard => _filter is PanelSection.All or PanelSection.Cpu;

        private bool _hasGpuSensors;

        /// <summary>
        /// Whether any adapter reported at all. The CPU sensor block is always shown, because
        /// an absent die reading is the thing worth explaining; the GPU block is not, because
        /// a machine whose driver answers nothing has nothing to explain there.
        /// </summary>
        public bool HasGpuSensors { get => _hasGpuSensors; private set => Set(ref _hasGpuSensors, value); }

        /// <summary>
        /// The battery card, shown only on a machine that has one. A desktop must not be told
        /// its battery is at 0%.
        /// </summary>
        public bool ShowBatteryCard =>
            (_filter is PanelSection.All or PanelSection.Battery) && HasBattery;

        // ---- Battery card ----

        private bool _hasBattery;
        public bool HasBattery
        {
            get => _hasBattery;
            private set
            {
                if (_hasBattery == value) return;
                _hasBattery = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowBatteryCard));
            }
        }

        private string _batteryValueText = "—";
        /// <summary>Charge as a percentage, e.g. "72%".</summary>
        public string BatteryValueText { get => _batteryValueText; private set => Set(ref _batteryValueText, value); }

        private double _batteryPercent;
        /// <summary>Charge for the ring gauge, 0-100.</summary>
        public double BatteryPercent { get => _batteryPercent; private set => Set(ref _batteryPercent, value); }

        private string _batteryStateText = "—";
        /// <summary>"Charging", "On battery", "Plugged in".</summary>
        public string BatteryStateText { get => _batteryStateText; private set => Set(ref _batteryStateText, value); }

        private string _batteryRemainingText = "—";
        /// <summary>Our own estimate from the measured draw, never the OS sentinel.</summary>
        public string BatteryRemainingText { get => _batteryRemainingText; private set => Set(ref _batteryRemainingText, value); }

        private string _batteryPowerText = "—";
        /// <summary>Charge or discharge power, e.g. "24.0 W".</summary>
        public string BatteryPowerText { get => _batteryPowerText; private set => Set(ref _batteryPowerText, value); }

        private string _batteryHealthText = "—";
        /// <summary>Wear against design capacity — the figure Windows never shows.</summary>
        public string BatteryHealthText { get => _batteryHealthText; private set => Set(ref _batteryHealthText, value); }

        /// <summary>Fills the battery card from one sample.</summary>
        private void UpdateBattery(SystemMetrics m)
        {
            HasBattery = m.HasBattery;
            if (!m.HasBattery) return;

            BatteryPercent = m.BatteryPercent;
            BatteryValueText = m.BatteryPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

            BatteryStateText = m.BatteryCharging ? "Charging"
                : m.BatteryOnAc ? "Plugged in"
                : "On battery";

            BatteryPowerText = m.BatteryWatts > 0
                ? m.BatteryWatts.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " W"
                : "—";

            BatteryRemainingText = m.BatteryMinutesLeft >= 0
                ? Services.Diagnostics.BatteryEstimate.Format(TimeSpan.FromMinutes(m.BatteryMinutesLeft))
                : m.BatteryOnAc ? "—" : "Measuring…";

            BatteryHealthText = m.BatteryHealthPercent >= 0
                ? m.BatteryHealthPercent.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "%  " +
                  Services.Diagnostics.BatteryEstimate.HealthVerdict(m.BatteryHealthPercent)
                : "—";
        }

        // ---- CPU card ----

        private string _cpuValueText = "—";
        public string CpuValueText { get => _cpuValueText; private set => Set(ref _cpuValueText, value); }

        private string _cpuHeaderText = "—";
        /// <summary>"8% · 3.87 GHz · 72°" — usage plus whatever else is readable, iStat style.</summary>
        public string CpuHeaderText { get => _cpuHeaderText; private set => Set(ref _cpuHeaderText, value); }

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

        private string _memCommitGbText = "—";
        /// <summary>Commit charge as "34.2 / 68.0 GB".</summary>
        public string MemCommitGbText { get => _memCommitGbText; private set => Set(ref _memCommitGbText, value); }

        private string _memCachedText = "—";
        public string MemCachedText { get => _memCachedText; private set => Set(ref _memCachedText, value); }

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

        private string _gpuVramText = "—";
        public string GpuVramText { get => _gpuVramText; private set => Set(ref _gpuVramText, value); }

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

        private string _netAdapterText = "—";
        public string NetAdapterText { get => _netAdapterText; private set => Set(ref _netAdapterText, value); }

        private string _netIpText = "—";
        public string NetIpText { get => _netIpText; private set => Set(ref _netIpText, value); }

        private string _netTotalsText = "—";
        /// <summary>Session transfer totals, "↓ 4.2 GB · ↑ 310 MB".</summary>
        public string NetTotalsText { get => _netTotalsText; private set => Set(ref _netTotalsText, value); }

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

        /// <summary>Highest memory consumers, for the Memory card's mini list.</summary>
        public ObservableCollection<ProcessUsage> MemoryTopProcesses { get; } = new();

        /// <summary>
        /// Sensor rows belonging to the processor — the die temperature, the ACPI zone, and
        /// any firmware limiting. Shown inside the CPU card, next to its usage summary, rather
        /// than in a card of their own: a temperature is only meaningful beside the load that
        /// produced it.
        /// </summary>
        public ObservableCollection<SensorRow> CpuSensorRows { get; } = new();

        /// <summary>Sensor rows belonging to the graphics adapters. Shown inside the GPU card.</summary>
        public ObservableCollection<SensorRow> GpuSensorRows { get; } = new();

        /// <summary>
        /// Splits the tick's readings between the CPU card and the GPU card, so each sits next
        /// to the load that produced it.
        ///
        /// <para>
        /// A reading belongs to the GPU card when its id names an adapter, and to the CPU card
        /// otherwise — which puts the die temperature, the ACPI zone and any firmware limiting
        /// of the processor together in one place. The CPU die row is always present even when
        /// nothing can supply it: that absence is the most common state and the thing users ask
        /// about, so it gets a row and an explanation rather than being omitted.
        /// </para>
        /// </summary>
        private void UpdateSensors(SystemMetrics m)
        {
            var cpu = new List<(string Label, string Value, string Detail)> { BuildDieRow(m) };
            var gpu = new List<(string Label, string Value, string Detail)>();

            foreach (var r in m.Sensors)
            {
                if (r.IsCpuDie) continue;                              // already the row above
                if (r.Category == SensorCategory.Throttle) continue;   // summarised per card

                var row = (r.Label, FormatSensorValue(r.Value, r.Unit), "Reported by " + r.Source);
                if (IsAdapterReading(r)) gpu.Add(row); else cpu.Add(row);
            }

            // One summary line per card rather than a row per flag: "what is being limited" is
            // a single question, and firmware often raises several flags for one cause.
            AddThrottleSummary(cpu, m, adapters: false);
            AddThrottleSummary(gpu, m, adapters: true);

            Sync(CpuSensorRows, cpu);
            Sync(GpuSensorRows, gpu);
            HasGpuSensors = gpu.Count > 0;
        }

        /// <summary>
        /// Which card a reading belongs to. Adapter readings are identified by their id prefix
        /// rather than by their label, because labels are vendor strings and change between
        /// driver versions while the prefix is a contract every source honours.
        /// </summary>
        public static bool IsAdapterReading(SensorReading r) =>
            r.Id.StartsWith("gpu.", StringComparison.Ordinal);

        private static void AddThrottleSummary(
            List<(string Label, string Value, string Detail)> rows, SystemMetrics m, bool adapters)
        {
            var labels = m.Sensors
                .Where(r => r.Category == SensorCategory.Throttle && IsAdapterReading(r) == adapters)
                .Select(r => r.Label)
                .Distinct()
                .ToList();

            // Nothing to say about adapters that reported nothing at all.
            if (labels.Count == 0 && adapters && rows.Count == 0) return;

            rows.Add(("Throttling",
                      labels.Count == 0 ? "none" : string.Join(", ", labels),
                      labels.Count == 0
                          ? "Nothing is limiting this right now"
                          : "The firmware is currently limiting performance"));
        }

        /// <summary>
        /// Rebuilds a collection only when its shape changes; otherwise updates in place, so
        /// WPF is not asked to re-create every row four times a second.
        /// </summary>
        private static void Sync(
            ObservableCollection<SensorRow> target, List<(string Label, string Value, string Detail)> rows)
        {
            bool sameShape = target.Count == rows.Count;
            if (sameShape)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (target[i].Label == rows[i].Label) continue;
                    sameShape = false;
                    break;
                }
            }

            if (!sameShape)
            {
                target.Clear();
                foreach (var row in rows) target.Add(new SensorRow(row.Label));
            }

            for (int i = 0; i < rows.Count; i++)
            {
                target[i].Value = rows[i].Value;
                target[i].Detail = rows[i].Detail;
            }
        }

        private static (string, string, string) BuildDieRow(SystemMetrics m)
        {
            if (m.CpuTemperature > 0)
            {
                var die = m.Sensors.FirstOrDefault(r => r.IsCpuDie);
                return ("CPU die",
                        FormatSensorValue(m.CpuTemperature, "°C"),
                        die != null ? "Reported by " + die.Source : "");
            }

            return ("CPU die", "—",
                    "The CPU die sensor needs a kernel driver, which MicaStats does not install. "
                    + "Run Core Temp, HWiNFO, MSI Afterburner, AIDA64 or LibreHardwareMonitor "
                    + "and this fills in automatically.");
        }

        /// <summary>
        /// Formats a reading for the card. A missing value is an em dash, never 0 — the whole
        /// point of the card is that "no source" and "cold" must not look the same.
        /// </summary>
        public static string FormatSensorValue(double value, string unit)
        {
            if (value < 0) return "—";
            string number = value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            return unit == "RPM" ? number + " RPM" : number + unit;
        }

        /// <summary>
        /// The CPU card header. An absent temperature shows an em dash rather than vanishing:
        /// the old behaviour silently dropped the suffix, so the panel could not distinguish
        /// "no source installed" from "this app does not report temperatures".
        /// </summary>
        public static string BuildCpuHeader(int usagePercent, float ghz, double dieCelsius)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            string header = usagePercent.ToString(invariant) + "%";
            if (ghz > 0) header += " · " + ghz.ToString("F2", invariant) + " GHz";
            header += " · " + (dieCelsius > 0
                ? ((int)dieCelsius).ToString(invariant) + "°"
                : "—");
            return header;
        }

        public bool HasProcesses => TopProcesses.Count > 0;
        public bool HasMemoryProcesses => MemoryTopProcesses.Count > 0;
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

                bool hadMem = MemoryTopProcesses.Count > 0;
                MemoryTopProcesses.Clear();
                int taken = 0;
                foreach (var p in _processes.TopByRam)
                {
                    MemoryTopProcesses.Add(p);
                    if (++taken >= 3) break;
                }
                if (hadMem != (MemoryTopProcesses.Count > 0)) OnPropertyChanged(nameof(HasMemoryProcesses));
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
                // Leases rather than a raw Enabled flag: the recorder may be holding the
                // sampler open, and a closing panel must not stop it sampling.
                if (_isLive && !_samplerLeased) { _processes.Retain(); _samplerLeased = true; }
                else if (!_isLive && _samplerLeased) { _processes.Release(); _samplerLeased = false; }

                if (_isLive) Refresh();
                else
                {
                    TopProcesses.Clear();
                    MemoryTopProcesses.Clear();
                    OnPropertyChanged(nameof(HasProcesses));
                    OnPropertyChanged(nameof(HasMemoryProcesses));
                }
            }
        }

        /// <summary>Re-derives every card from the current history. UI thread only.</summary>
        public void Refresh()
        {
            if (_disposed) return;

            var m = _history.Latest;

            UpdateBattery(m);

            // CPU
            CpuValueText = $"{(int)m.CpuUsage}%";
            CpuHeaderText = BuildCpuHeader((int)m.CpuUsage, m.CpuFrequencyGhz, m.CpuTemperature);
            UpdateSensors(m);
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
            MemCommitGbText = m.CommitLimitBytes > 0
                ? $"{m.CommitUsedBytes / 1024d / 1024d / 1024d:F1} / {m.CommitLimitBytes / 1024d / 1024d / 1024d:F0} GB"
                : $"{(int)m.CommitPercent}%";
            MemCachedText = m.CachedBytes > 0 ? FormatBytes(m.CachedBytes) : "—";

            // GPU
            GpuValueText = $"{(int)m.GpuUsage}%";
            GpuPercent = Math.Clamp(m.GpuUsage, 0, 100);
            bool hasTemp = m.GpuTemperature > 0;
            GpuTempPercent = hasTemp ? Math.Clamp(m.GpuTemperature, 0, 100) : 0;
            GpuTempText = hasTemp ? $"{(int)m.GpuTemperature}°" : "—";
            GpuGraphAvailable = _history.Gpu.Availability == Availability.Value;
            GpuVramText = m.GpuVramUsedBytes > 0 ? FormatBytes(m.GpuVramUsedBytes) : "—";

            // Network
            NetUpBigText = m.NetUpText;
            NetDownBigText = m.NetDownText;
            NetPeakUpText = FormatKbps(_history.NetUp.Max);
            NetPeakDownText = FormatKbps(_history.NetDown.Max);
            NetGraphMax = Math.Max(1f, _history.SharedNetPeak);
            NetAdapterText = string.IsNullOrEmpty(m.NetAdapterName) ? "—" : m.NetAdapterName;
            NetIpText = string.IsNullOrEmpty(m.NetIpAddress) ? "—" : m.NetIpAddress;
            NetTotalsText = $"↓ {FormatBytes(m.NetSessionDownBytes)} · ↑ {FormatBytes(m.NetSessionUpBytes)}";

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
                var d = m.Disks[i];
                DiskRows[i].Activity = $"{(int)d.ActivityPercent}%";
                DiskRows[i].Detail = d.FreeBytes > 0
                    ? $"{(int)d.SpacePercent}% used · {FormatBytes(d.FreeBytes)} free"
                    : $"{(int)d.SpacePercent}% used";
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

        /// <summary>Bytes as a compact human figure ("3.2 GB", "412 MB").</summary>
        private static string FormatBytes(ulong bytes)
        {
            if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / 1024d / 1024d / 1024d:F1} GB";
            if (bytes >= 1024UL * 1024) return $"{bytes / 1024d / 1024d:F0} MB";
            return $"{bytes / 1024d:F0} KB";
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
            // Not disposed: the sampler outlives this panel and belongs to the application.
            if (_samplerLeased) { _processes.Release(); _samplerLeased = false; }
        }
    }
}
