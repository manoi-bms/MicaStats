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

        /// <summary>Free capacity in bytes, or 0 if the drive was not ready.</summary>
        public ulong FreeBytes { get; set; }

        /// <summary>Total capacity in bytes, or 0 if the drive was not ready.</summary>
        public ulong TotalBytes { get; set; }
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
        Disks,
        Battery
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

        /// <summary>Commit charge in use, bytes. 0 if unknown.</summary>
        public ulong CommitUsedBytes { get; set; }

        /// <summary>Commit limit, bytes. 0 if unknown.</summary>
        public ulong CommitLimitBytes { get; set; }

        /// <summary>System file cache, bytes. 0 if unknown.</summary>
        public ulong CachedBytes { get; set; }

        /// <summary>
        /// Hottest CPU core in Celsius, published by Core Temp or a hardware-monitor app; -1
        /// when no publisher is running (the die sensors need ring-0, which this app avoids).
        /// </summary>
        public float CpuTemperature { get; set; } = -1f;

        /// <summary>Effective CPU clock in GHz (base clock x performance ratio), or 0 if unknown.</summary>
        public float CpuFrequencyGhz { get; set; }

        /// <summary>Dedicated GPU memory in use, bytes. 0 if unknown.</summary>
        public ulong GpuVramUsedBytes { get; set; }

        /// <summary>The monitored adapter's name, or empty when none matched.</summary>
        public string NetAdapterName { get; set; } = "";

        /// <summary>The monitored adapter's IPv4 address, or empty when unknown.</summary>
        public string NetIpAddress { get; set; } = "";

        /// <summary>Bytes received since this app started sampling.</summary>
        public ulong NetSessionDownBytes { get; set; }

        /// <summary>Bytes sent since this app started sampling.</summary>
        public ulong NetSessionUpBytes { get; set; }

        // ----- Battery ----------------------------------------------------------------------
        // Sampled on a slower cadence than the rest (WMI is not free) and cached between ticks.

        /// <summary>Charge percentage, or -1 when this machine has no battery.</summary>
        public int BatteryPercent { get; set; } = -1;

        /// <summary>True when a battery is present and the machine is on mains power.</summary>
        public bool BatteryOnAc { get; set; }

        public bool BatteryCharging { get; set; }

        /// <summary>Charge or discharge power in watts, or 0 when idle or unreported.</summary>
        public float BatteryWatts { get; set; }

        /// <summary>
        /// Minutes of charge left at the measured draw, or -1 when that cannot be computed.
        /// Deliberately not Windows' own estimate, which reports a 136-year sentinel when it
        /// does not know.
        /// </summary>
        public int BatteryMinutesLeft { get; set; } = -1;

        /// <summary>Wear as a percentage of design capacity, or -1 until it has been read.</summary>
        public float BatteryHealthPercent { get; set; } = -1f;

        /// <summary>True when a battery was found at all.</summary>
        public bool HasBattery => BatteryPercent >= 0;
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
        private bool _avoidStartMenu = true;

        // Screen capture
        private string _captureFolder = "";
        private string _captureNameTemplate = "MicaStats_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";
        private string _captureFormat = "Png";
        private int _captureJpegQuality = 92;
        private bool _captureIncludeCursor;
        private bool _captureCopyToClipboard = true;
        private bool _captureAutoSave = true;
        private bool _captureOpenEditor = true;
        private int _captureDelaySeconds;
        private string _captureRedactStyle = "Pixelate";
        private bool _captureHotkeysEnabled = true;

        // Updates
        private bool _autoCheckUpdates = true;
        private string _lastUpdateCheckUtc = "";
        private string _skippedUpdateVersion = "";

        // Diagnostics
        private bool _showBattery = true;
        private bool _slowdownRecording = true;
        private bool _slowdownAutoCapture = true;
        private int _slowdownWindowSeconds = 300;
        private int _slowdownCpuPercent = 90;
        private int _slowdownDiskMbPerSec = 150;
        private int _slowdownMemoryPercent = 92;
        private int _slowdownSustainSeconds = 8;
        private bool _alertsEnabled = true;
        private string _alertRules = "";
        private string _captureHotkeyRegion = "Ctrl+Shift+1";
        private string _captureHotkeyWindow = "Ctrl+Shift+2";
        private string _captureHotkeyFullScreen = "Ctrl+Shift+3";

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

        /// <summary>
        /// Cap the stacked overlay's width so it never overlaps the taskbar's own buttons.
        /// A centred Windows 11 taskbar moves its Start button LEFT as icons are added, so a
        /// fixed-position overlay eventually collides; with this on, sparklines hide first,
        /// then trailing modules, and everything returns once the space is back.
        /// </summary>
        public bool AvoidStartMenu { get => _avoidStartMenu; set { Set(ref _avoidStartMenu, value); } }

        // ----- Screen capture ---------------------------------------------------------------

        /// <summary>Where captures are saved. Empty means Pictures\MicaStats.</summary>
        public string CaptureFolder { get => _captureFolder; set { Set(ref _captureFolder, value); } }

        /// <summary>File-name template; see CaptureFileNamer for the tokens.</summary>
        public string CaptureNameTemplate { get => _captureNameTemplate; set { Set(ref _captureNameTemplate, value); } }

        /// <summary>"Png" or "Jpeg". Stored as text so the config file stays readable.</summary>
        public string CaptureFormat { get => _captureFormat; set { Set(ref _captureFormat, value); } }

        public int CaptureJpegQuality { get => _captureJpegQuality; set { Set(ref _captureJpegQuality, value); } }

        /// <summary>Composite the mouse pointer into the capture.</summary>
        public bool CaptureIncludeCursor { get => _captureIncludeCursor; set { Set(ref _captureIncludeCursor, value); } }

        public bool CaptureCopyToClipboard { get => _captureCopyToClipboard; set { Set(ref _captureCopyToClipboard, value); } }

        public bool CaptureAutoSave { get => _captureAutoSave; set { Set(ref _captureAutoSave, value); } }

        /// <summary>Open the annotation editor after a capture instead of finishing silently.</summary>
        public bool CaptureOpenEditor { get => _captureOpenEditor; set { Set(ref _captureOpenEditor, value); } }

        /// <summary>Seconds to wait before capturing, for catching menus and hover states.</summary>
        public int CaptureDelaySeconds { get => _captureDelaySeconds; set { Set(ref _captureDelaySeconds, value); } }

        /// <summary>"Pixelate", "Blur" or "Solid".</summary>
        public string CaptureRedactStyle { get => _captureRedactStyle; set { Set(ref _captureRedactStyle, value); } }

        public bool CaptureHotkeysEnabled { get => _captureHotkeysEnabled; set { Set(ref _captureHotkeysEnabled, value); } }

        // ----- Updates ----------------------------------------------------------------------

        /// <summary>Check GitHub for a newer release in the background, at most once a day.</summary>
        public bool AutoCheckUpdates { get => _autoCheckUpdates; set { Set(ref _autoCheckUpdates, value); } }

        /// <summary>When the last check ran, as a round-trip UTC timestamp. Empty means never.</summary>
        public string LastUpdateCheckUtc { get => _lastUpdateCheckUtc; set { Set(ref _lastUpdateCheckUtc, value); } }

        /// <summary>A version the user chose not to be reminded about again.</summary>
        public string SkippedUpdateVersion { get => _skippedUpdateVersion; set { Set(ref _skippedUpdateVersion, value); } }

        // ----- Diagnostics ------------------------------------------------------------------

        /// <summary>Show the battery module in the taskbar overlay. Ignored on a desktop.</summary>
        public bool ShowBattery { get => _showBattery; set { Set(ref _showBattery, value); } }

        /// <summary>
        /// Keep a rolling window of per-process activity so a stall can be explained after it
        /// has passed. Off means the app cannot answer "what was that?" — nothing else in
        /// Windows keeps that history either.
        /// </summary>
        public bool SlowdownRecording { get => _slowdownRecording; set { Set(ref _slowdownRecording, value); } }

        /// <summary>Write a report by itself when a threshold is crossed.</summary>
        public bool SlowdownAutoCapture { get => _slowdownAutoCapture; set { Set(ref _slowdownAutoCapture, value); } }

        /// <summary>Seconds of history retained for the report.</summary>
        public int SlowdownWindowSeconds
        {
            get => _slowdownWindowSeconds;
            set { Set(ref _slowdownWindowSeconds, Math.Clamp(value, 60, 900)); }
        }

        public int SlowdownCpuPercent
        {
            get => _slowdownCpuPercent;
            set { Set(ref _slowdownCpuPercent, Math.Clamp(value, 50, 100)); }
        }

        public int SlowdownDiskMbPerSec
        {
            get => _slowdownDiskMbPerSec;
            set { Set(ref _slowdownDiskMbPerSec, Math.Clamp(value, 10, 5000)); }
        }

        public int SlowdownMemoryPercent
        {
            get => _slowdownMemoryPercent;
            set { Set(ref _slowdownMemoryPercent, Math.Clamp(value, 50, 100)); }
        }

        public int SlowdownSustainSeconds
        {
            get => _slowdownSustainSeconds;
            set { Set(ref _slowdownSustainSeconds, Math.Clamp(value, 2, 120)); }
        }

        /// <summary>Say something when a threshold rule is breached.</summary>
        public bool AlertsEnabled { get => _alertsEnabled; set { Set(ref _alertsEnabled, value); } }

        /// <summary>
        /// The alert rules, as <c>id:enabled:threshold:sustain</c> separated by semicolons.
        /// See AlertRuleSettings for why this is a flat string rather than nested JSON.
        /// </summary>
        public string AlertRules { get => _alertRules; set { Set(ref _alertRules, value); } }
        public string CaptureHotkeyRegion { get => _captureHotkeyRegion; set { Set(ref _captureHotkeyRegion, value); } }
        public string CaptureHotkeyWindow { get => _captureHotkeyWindow; set { Set(ref _captureHotkeyWindow, value); } }
        public string CaptureHotkeyFullScreen { get => _captureHotkeyFullScreen; set { Set(ref _captureHotkeyFullScreen, value); } }

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
