using System;
using System.Windows.Media;
using System.Linq;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Models
{
    /// <summary>
    /// Whether a metric series has usable data. A graph has no "N/A" glyph, so a series that
    /// cannot be read must be distinguishable from one reading zero — otherwise a flat line at
    /// the baseline asserts "idle" when the truth is "unreadable".
    /// </summary>
    public enum Availability
    {
        /// <summary>The sensor could not be read at all (no counter, no driver, access denied).</summary>
        Unavailable,
        /// <summary>The sensor works but no sample has arrived yet.</summary>
        NoDataYet,
        /// <summary>At least one real sample is present.</summary>
        Value
    }

    public class DiskMetric
    {
        public string Name { get; set; } = "";
        public float SpacePercent { get; set; }
        public float ActivityPercent { get; set; }
    }

    /// <summary>
    /// Which slice of the stats panel is shown. <see cref="All"/> is the full click-opened
    /// panel; the rest are the per-module hover dropdowns, one per taskbar section — the
    /// iStat Menus model where every menu bar item owns its own dropdown.
    /// </summary>
    public enum PanelSection
    {
        All,
        Cpu,
        Memory,
        Gpu,
        Network,
        Disks
    }

    public class SystemMetrics
    {
        public float CpuUsage { get; set; }
        public float RamPercent { get; set; }
        public float GpuUsage { get; set; }

        /// <summary>GPU temperature in Celsius, or -1 when no source could supply it.</summary>
        public float GpuTemperature { get; set; }

        public float NetUpKbps { get; set; }
        public float NetDownKbps { get; set; }
        public string NetUpText { get; set; } = "0 KB/s";
        public string NetDownText { get; set; } = "0 KB/s";
        public float DiskUsage { get; set; }
        public float DiskPercent { get; set; }
        public System.Collections.Generic.List<DiskMetric> Disks { get; set; } = new();

        /// <summary>
        /// Per-logical-processor usage, ordered by (NUMA node, index-within-node). Empty when
        /// per-core sampling is unavailable. The array index is a display ordinal, not a
        /// processor number — the counter's second field restarts per NUMA node.
        /// </summary>
        public float[] CoreUsage { get; set; } = System.Array.Empty<float>();

        /// <summary>Total physical memory in bytes, or 0 if unknown.</summary>
        public ulong RamTotalBytes { get; set; }

        /// <summary>Physical memory currently in use in bytes, or 0 if unknown.</summary>
        public ulong RamUsedBytes { get; set; }

        /// <summary>
        /// Share of total CPU time spent in kernel mode ("% Privileged Time" of the totals
        /// instance), or -1 when the counter could not be read. Drives the iStat-style
        /// User/System split; 0 is a legitimate reading, so the sentinel must be negative.
        /// </summary>
        public float CpuSystem { get; set; } = -1f;

        /// <summary>
        /// Commit charge in use as a percentage of the commit limit, or 0 if unknown. The
        /// closest Windows analogue to macOS "memory pressure" for the panel's second ring.
        /// </summary>
        public float CommitPercent { get; set; }
    }

    public class AppConfig : System.ComponentModel.INotifyPropertyChanged
    {
        /// <summary>
        /// Schema version of the persisted file. Bumped when a field's meaning changes so a
        /// future build can migrate rather than guess.
        /// </summary>
        public const int CurrentVersion = 1;

        private int _configVersion = CurrentVersion;

        private bool _showOverlay = true;
        private bool _lockPosition = false;
        private bool _launchOnStartup = false;
        private bool _showCpu = true;
        private bool _showRam = true;
        private bool _showGpu = true;
        private bool _showTemp = true;
        private bool _showDisk = true;
        private bool _showDiskSpeed = true;
        private bool _showNetUp = true;
        private bool _showNetDown = true;
        private string _networkAdapter = "Default";
        private string _gpuAdapter = "Default";
        private string _selectedDisks = "All";
        private string _displayStyle = "Text";
        private string _fontFamily = "Segoe UI";
        private string _accentColorHex = "#FFFFFF";
        private string _labelColorHex = "#00CCFF";
        private double _x = 100;
        private double _y = 100;
        private bool _hideOnFullscreen = true;
        private bool _stickToTaskbar = true;
        private bool _showBackground = false;
        private string _backgroundColorHex = "#B4141414";
        private double _scaleFactor = 1.0;
        private bool _isTextBold = true;
        private int _columnSpacing = 6;

        private string _theme = "Default";
        private int _updateInterval = 1000;
        private int _gpuIndex = 0;
        private bool _showPods = true;
        private string _podColorHex = "#0FFFFFFF";
        private bool _alwaysOnTop = true;

        // Sparkline rendering. Deliberately orthogonal to DisplayStyle, which is really a
        // label-width axis ("CPU" vs "C") — a third DisplayStyle value would make
        // compact-labels-plus-graphs unexpressible.
        private bool _showGraphs = false;
        private int _graphHistorySeconds = 60;
        private bool _showPanelOnClick = true;
        private bool _stackedTaskbar = true;
        private bool _hoverPanels = true;

        // Per-section label colors (null = use global LabelColorHex)
        private string? _netLabelColorHex = null;
        private string? _cpuRamLabelColorHex = null;
        private string? _gpuLabelColorHex = null;
        private string? _diskLabelColorHex = null;

        // Per-section metric/accent colors (null = use global AccentColorHex)
        private string? _netAccentColorHex = null;
        private string? _cpuRamAccentColorHex = null;
        private string? _gpuAccentColorHex = null;
        private string? _diskAccentColorHex = null;

        public int ConfigVersion { get => _configVersion; set { Set(ref _configVersion, value); } }

        public bool ShowOverlay { get => _showOverlay; set { Set(ref _showOverlay, value); } }
        public bool LockPosition { get => _lockPosition; set { Set(ref _lockPosition, value); } }
        public bool LaunchOnStartup { get => _launchOnStartup; set { Set(ref _launchOnStartup, value); } }

        public bool ShowCpu { get => _showCpu; set { Set(ref _showCpu, value); } }
        public bool ShowRam { get => _showRam; set { Set(ref _showRam, value); } }
        public bool ShowGpu { get => _showGpu; set { Set(ref _showGpu, value); } }
        public bool ShowTemp { get => _showTemp; set { Set(ref _showTemp, value); } }
        public bool ShowDisk { get => _showDisk; set { Set(ref _showDisk, value); } }
        public bool ShowDiskSpeed { get => _showDiskSpeed; set { Set(ref _showDiskSpeed, value); } }
        public bool ShowNetUp { get => _showNetUp; set { Set(ref _showNetUp, value); } }
        public bool ShowNetDown { get => _showNetDown; set { Set(ref _showNetDown, value); } }

        public string NetworkAdapter { get => _networkAdapter; set { Set(ref _networkAdapter, value); } }
        public string GpuAdapter { get => _gpuAdapter; set { Set(ref _gpuAdapter, value); } }
        public string SelectedDisks { get => _selectedDisks; set { Set(ref _selectedDisks, value); } }
        public string DisplayStyle { get => _displayStyle; set { Set(ref _displayStyle, value); } }
        public string FontFamily { get => _fontFamily; set { Set(ref _fontFamily, value); } }

        public string AccentColorHex { get => _accentColorHex; set { if (Set(ref _accentColorHex, value)) OnPropertyChanged(nameof(AccentColor)); } }
        public string LabelColorHex { get => _labelColorHex; set { if (Set(ref _labelColorHex, value)) OnPropertyChanged(nameof(LabelColor)); } }
        public string BackgroundColorHex { get => _backgroundColorHex; set { if (Set(ref _backgroundColorHex, value)) OnPropertyChanged(nameof(BackgroundColor)); } }

        public double ScaleFactor { get => _scaleFactor; set { Set(ref _scaleFactor, value); } }
        public bool IsTextBold { get => _isTextBold; set { Set(ref _isTextBold, value); } }
        public int ColumnSpacing { get => _columnSpacing; set { Set(ref _columnSpacing, Math.Clamp(value, 0, 20)); } }

        public string Theme { get => _theme; set { Set(ref _theme, value); } }
        public int UpdateInterval { get => _updateInterval; set { Set(ref _updateInterval, value); } }
        public int GpuIndex { get => _gpuIndex; set { Set(ref _gpuIndex, value); } }
        public bool ShowPods { get => _showPods; set { Set(ref _showPods, value); } }
        public string PodColorHex { get => _podColorHex; set { if (Set(ref _podColorHex, value)) OnPropertyChanged(nameof(PodColor)); } }
        public bool AlwaysOnTop { get => _alwaysOnTop; set { Set(ref _alwaysOnTop, value); } }

        /// <summary>Render a live sparkline beside each metric value in the overlay.</summary>
        public bool ShowGraphs { get => _showGraphs; set { Set(ref _showGraphs, value); } }

        /// <summary>Seconds of history the detail panel graphs span.</summary>
        public int GraphHistorySeconds { get => _graphHistorySeconds; set { Set(ref _graphHistorySeconds, Math.Clamp(value, 10, 300)); } }

        /// <summary>Whether tapping the overlay opens the detail panel.</summary>
        public bool ShowPanelOnClick { get => _showPanelOnClick; set { Set(ref _showPanelOnClick, value); } }

        /// <summary>
        /// iStat-style taskbar layout: each metric is its own module with a small dim label
        /// stacked above a bold value (network as paired ↑/↓ lines). Orthogonal to
        /// DisplayStyle and ShowGraphs for the same reason ShowGraphs is — combining either
        /// with stacking must stay expressible. Off = the classic two-row inline layout.
        /// </summary>
        public bool StackedTaskbar { get => _stackedTaskbar; set { Set(ref _stackedTaskbar, value); } }

        /// <summary>
        /// Hovering a stacked-taskbar module opens that section's own detail dropdown after a
        /// short dwell, iStat-style. Only active while <see cref="StackedTaskbar"/> is on,
        /// because the classic layout fuses two metrics per column.
        /// </summary>
        public bool HoverPanels { get => _hoverPanels; set { Set(ref _hoverPanels, value); } }

        // Per-section label colors (null/empty = inherit global LabelColorHex)
        public string? NetLabelColorHex { get => _netLabelColorHex; set { Set(ref _netLabelColorHex, value); } }
        public string? CpuRamLabelColorHex { get => _cpuRamLabelColorHex; set { Set(ref _cpuRamLabelColorHex, value); } }
        public string? GpuLabelColorHex { get => _gpuLabelColorHex; set { Set(ref _gpuLabelColorHex, value); } }
        public string? DiskLabelColorHex { get => _diskLabelColorHex; set { Set(ref _diskLabelColorHex, value); } }

        // Per-section metric/accent colors (null/empty = inherit global AccentColorHex)
        public string? NetAccentColorHex { get => _netAccentColorHex; set { Set(ref _netAccentColorHex, value); } }
        public string? CpuRamAccentColorHex { get => _cpuRamAccentColorHex; set { Set(ref _cpuRamAccentColorHex, value); } }
        public string? GpuAccentColorHex { get => _gpuAccentColorHex; set { Set(ref _gpuAccentColorHex, value); } }
        public string? DiskAccentColorHex { get => _diskAccentColorHex; set { Set(ref _diskAccentColorHex, value); } }

        public double X { get => _x; set { Set(ref _x, value); } }
        public double Y { get => _y; set { Set(ref _y, value); } }
        public bool HideOnFullscreen { get => _hideOnFullscreen; set { Set(ref _hideOnFullscreen, value); } }
        public bool StickToTaskbar { get => _stickToTaskbar; set { Set(ref _stickToTaskbar, value); } }
        public bool ShowBackground { get => _showBackground; set { Set(ref _showBackground, value); } }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color AccentColor { get => HexToColor(AccentColorHex); set => AccentColorHex = ColorToHex(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color LabelColor { get => HexToColor(LabelColorHex); set => LabelColorHex = ColorToHex(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color BackgroundColor { get => HexToColor(BackgroundColorHex); set => BackgroundColorHex = ColorToHex(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color PodColor { get => HexToColor(PodColorHex); set => PodColorHex = ColorToHex(value); }

        private System.Windows.Media.Color HexToColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 8) // ARGB
                {
                    return System.Windows.Media.Color.FromArgb(
                        byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
                }
                if (hex.Length == 6) // RGB
                {
                    return System.Windows.Media.Color.FromRgb(
                        byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
                }
            }
            catch { }
            return Colors.White;
        }

        private string ColorToHex(System.Windows.Media.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>
        /// Assigns a backing field and notifies only when the value actually changed.
        /// Every notification triggers a full config file rewrite and an overlay repaint, so an
        /// unguarded setter turns an idempotent assignment into disk I/O plus a re-render.
        /// </summary>
        /// <returns>True if the value changed and a notification was raised.</returns>
        private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}
