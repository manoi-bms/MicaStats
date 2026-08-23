using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;

using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
// System.Windows.Forms is in scope via UseWindowsForms and defines its own KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The diagnostics window: what slowed the machine down, how the last boot went, how worn
    /// the battery is, and which readings should speak up.
    ///
    /// <para>
    /// A singleton, like the hardware inspector. Reading the boot log and spawning powercfg are
    /// both slow enough to background, so the window opens immediately with a status line and
    /// fills in as each answer lands. Only the small live strip ticks, and it stops on close —
    /// the zero-idle-cost rule the rest of the app follows.
    /// </para>
    /// </summary>
    public partial class DiagnosticsWindow : Window
    {
        private static DiagnosticsWindow? _open;

        private BootAnalysis? _boot;
        private BatteryHealth? _health;
        private BatteryReading? _battery;
        private List<SavedReport> _reports = new();
        private List<AlertRule> _rules = new();
        private DispatcherTimer? _timer;

        /// <summary>
        /// Completes when the current gather has been applied. Public because the render
        /// harness lives in a separate assembly and must await a populated window.
        /// </summary>
        public Task? GatherApplied { get; private set; }

        /// <summary>Opens the window, or focuses the one already open.</summary>
        public static void ShowDiagnostics()
        {
            if (_open != null)
            {
                try { _open.Activate(); return; } catch { _open = null; }
            }
            var w = new DiagnosticsWindow();
            w.Show();
        }

        /// <summary>Opens the window on a particular tab, for the "Show me" button on an alert.</summary>
        public static void ShowDiagnostics(int tabIndex)
        {
            ShowDiagnostics();
            if (_open != null && tabIndex >= 0 && tabIndex < _open.Tabs.Items.Count)
                _open.Tabs.SelectedIndex = tabIndex;
        }

        public DiagnosticsWindow()
        {
            InitializeComponent();
            _open = this;

            SourceInitialized += (s, e) => ApplyDarkTitleBar();
            Loaded += (s, e) =>
            {
                ApplySettingsToControls();
                GatherApplied = BeginGatherAsync();
                StartLiveStrip();
            };
            PreviewKeyDown += OnPreviewKeyDown;
            Closed += (s, e) =>
            {
                if (ReferenceEquals(_open, this)) _open = null;
                _timer?.Stop();
                _ownBattery?.Dispose();
            };
        }

        private static AppConfig? Config => App.ConfigService?.Config;

        /// <summary>
        /// Settings to display. Falls back to the shipped defaults so the window still renders
        /// truthfully if the application-wide config is unavailable.
        /// </summary>
        private static AppConfig DisplayConfig => Config ?? new AppConfig();

        /// <summary>
        /// Used when the application did not create one — diagnostics startup failed, or this
        /// window is being driven outside the running app. Disposed with the window.
        /// </summary>
        private BatteryMonitor? _ownBattery;

        private BatteryMonitor BatteryReader => App.Battery ?? (_ownBattery ??= new BatteryMonitor());

        private void ApplyDarkTitleBar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int dark = 1;
                Win32Helper.DwmSetWindowAttribute(hwnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }

        // ---- gathering -------------------------------------------------------------------

        private async Task BeginGatherAsync()
        {
            StatusText.Text = "Reading the boot log…";

            // Boot first: it is the slowest of the three, and the tab most likely to be opened.
            try
            {
                _boot = await Task.Run(BootAnalyzer.Gather);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("diagnostics", "Boot gather crashed", ex);
                _boot = new BootAnalysis { Problem = "The boot log could not be read — see the diagnostics log." };
            }
            ApplyBoot();

            StatusText.Text = "Reading the battery…";
            try
            {
                if (BatteryMonitor.HasBattery())
                {
                    _health = await BatteryReader.GetHealthAsync();
                    _battery = BatteryMonitor.Read();
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("diagnostics", "Battery gather crashed", ex);
            }
            ApplyBattery();

            ApplyReports();
            ApplyAlerts();

            StatusText.Text = Summarise();
        }

        private string Summarise()
        {
            var parts = new List<string>();

            if (_boot?.Latest is { } latest)
                parts.Add("Last boot " + latest.SecondsText);
            else if (!string.IsNullOrWhiteSpace(_boot?.Problem))
                parts.Add(_boot!.Problem!);

            if (_boot != null && _boot.Entries.Count > 0)
                parts.Add(StartupEntries.Summarise(_boot.Entries) + " at sign-in");

            if (_health is { Any: true } && _health.HealthPercent >= 0)
                parts.Add("Battery " + _health.HealthPercent.ToString("F0", CultureInfo.InvariantCulture) + "%");

            return parts.Count == 0 ? "Ready." : string.Join("  ·  ", parts);
        }

        private void ApplyBoot()
        {
            BootGroups.ItemsSource = DiagnosticsService.BuildBootSummary(_boot);

            IReadOnlyList<StartupDelay> delays = _boot?.Delays ?? Array.Empty<StartupDelay>();
            DelayList.ItemsSource = delays;
            NoDelaysText.Visibility = delays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            IReadOnlyList<StartupEntry> entries = _boot?.Entries ?? Array.Empty<StartupEntry>();
            StartupList.ItemsSource = entries;
        }

        private void ApplyBattery()
        {
            bool present = _battery is { Present: true } || _health is { Any: true };

            // A desktop has no battery, and a tab of dashes is worse than no tab.
            BatteryTab.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
            if (!present) return;

            BatteryGroups.ItemsSource =
                DiagnosticsService.BuildBattery(_health, _battery, BatteryMonitor.ReadOsEstimate());
        }

        private void ApplyReports()
        {
            _reports = DiagnosticsService.ListReports();
            ReportList.ItemsSource = _reports;
            NoReportsText.Visibility = _reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyAlerts()
        {
            _rules = AlertRuleSettings.Parse(Config?.AlertRules);
            AlertList.ItemsSource = null;
            AlertList.ItemsSource = _rules;
        }

        private void ApplySettingsToControls()
        {
            var config = DisplayConfig;

            RecordingCheck.IsChecked = config.SlowdownRecording;
            AutoCaptureCheck.IsChecked = config.SlowdownAutoCapture;
            AlertsEnabledCheck.IsChecked = config.AlertsEnabled;
            UpdateThresholdText();
        }

        private void UpdateThresholdText()
        {
            var config = DisplayConfig;

            ThresholdText.Text = string.Format(CultureInfo.InvariantCulture,
                "Records automatically when the CPU holds {0}%, the disk holds {1} MB/s, or memory holds {2}% for {3} seconds. Keeping the last {4} minutes.",
                config.SlowdownCpuPercent, config.SlowdownDiskMbPerSec, config.SlowdownMemoryPercent,
                config.SlowdownSustainSeconds, config.SlowdownWindowSeconds / 60);
        }

        // ---- live strip ------------------------------------------------------------------

        private void StartLiveStrip()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdateLiveStrip();
            _timer.Start();
            UpdateLiveStrip();
        }

        private void UpdateLiveStrip()
        {
            var recorder = App.Recorder;
            if (recorder == null || !recorder.IsRunning)
            {
                LiveRecorderText.Text = "Off";
                LiveBusiestText.Text = "—";
                LiveProcessList.ItemsSource = null;
                NoLiveText.Visibility = Visibility.Visible;
                return;
            }

            var frames = recorder.Frames;
            if (frames.Count == 0)
            {
                LiveRecorderText.Text = "Starting…";
                LiveBusiestText.Text = "—";
                LiveProcessList.ItemsSource = null;
                NoLiveText.Visibility = Visibility.Visible;
                return;
            }

            LiveRecorderText.Text = DescribeSpan(frames[^1].At - frames[0].At);

            var last = frames[^1];
            var lead = last.BusiestDisk is { DiskBytesPerSec: > 0 } d ? d : last.BusiestCpu;
            LiveBusiestText.Text = lead == null
                ? "—"
                : lead.Name + "  " + (lead.DiskBytesPerSec > 0 ? lead.DiskText : lead.CpuText);

            LiveProcessList.ItemsSource = last.TopCpu;
            NoLiveText.Visibility = last.TopCpu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string DescribeSpan(TimeSpan span)
        {
            if (span.TotalSeconds < 60)
                return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s held";
            return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min held";
        }

        // ---- commands --------------------------------------------------------------------

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            BootGroups.ItemsSource = null;
            GatherApplied = BeginGatherAsync();
        }

        private void OnRecordNow(object sender, RoutedEventArgs e)
        {
            var recorder = App.Recorder;
            if (recorder == null || !recorder.IsRunning)
            {
                StatusText.Text = "Recording is off, so there is no window to save. Switch it on above.";
                return;
            }

            string? path = recorder.Capture(SlowdownCause.Manual);
            if (path == null)
            {
                StatusText.Text = "Nothing has been sampled yet — give it a few seconds.";
                return;
            }

            ApplyReports();
            StatusText.Text = "Saved " + Path.GetFileName(path);
        }

        private void OnToggleRecording(object sender, RoutedEventArgs e)
        {
            var config = Config;
            if (config == null) return;

            config.SlowdownRecording = RecordingCheck.IsChecked == true;
            App.ConfigService?.SaveConfig();
            App.ApplyDiagnosticsSettings();
            UpdateLiveStrip();
        }

        private void OnToggleAutoCapture(object sender, RoutedEventArgs e)
        {
            var config = Config;
            if (config == null) return;

            config.SlowdownAutoCapture = AutoCaptureCheck.IsChecked == true;
            App.ConfigService?.SaveConfig();
            App.ApplyDiagnosticsSettings();
        }

        private void OnToggleAlerts(object sender, RoutedEventArgs e)
        {
            var config = Config;
            if (config == null) return;

            config.AlertsEnabled = AlertsEnabledCheck.IsChecked == true;
            App.ConfigService?.SaveConfig();
            App.ApplyDiagnosticsSettings();
        }

        private void OnToggleAlertRule(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.Tag is not AlertRule rule) return;

            var config = Config;
            if (config == null) return;

            int index = _rules.FindIndex(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;

            _rules[index] = _rules[index] with { Enabled = box.IsChecked == true };
            config.AlertRules = AlertRuleSettings.Serialize(_rules);
            App.ConfigService?.SaveConfig();
            App.ApplyDiagnosticsSettings();

            StatusText.Text = _rules[index].Describe() + (box.IsChecked == true ? " — on" : " — off");
        }

        /// <summary>
        /// Switches one startup entry on or off. This changes what launches on the machine, so
        /// the outcome is always reported rather than assumed: a refused change puts the box
        /// back where it was and says why.
        /// </summary>
        private void OnToggleStartupEntry(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.Tag is not StartupEntry entry) return;

            bool wanted = box.IsChecked == true;
            string? problem = StartupEntries.SetEnabled(entry, wanted);

            if (problem != null)
            {
                box.IsChecked = !wanted;      // put it back; nothing was changed
                StatusText.Text = problem;
                return;
            }

            StatusText.Text = entry.Name + (wanted
                ? " will start with Windows."
                : " will no longer start with Windows.");
        }

        private void OnOpenReport(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string path) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("diagnostics", "Could not open " + path, ex);
                StatusText.Text = "That report could not be opened — see the diagnostics log.";
            }
        }

        private void OnSaveReport(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = DiagnosticsService.SaveReport(_boot, _health, _battery, _rules, _reports);
                ApplyReports();
                StatusText.Text = "Saved " + path;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("diagnostics", "Report save failed", ex);
                StatusText.Text = "The report could not be saved — see the diagnostics log.";
            }
        }

        private void OnOpenDataFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(DiagnosticsLog.DataDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    DiagnosticsLog.DataDir) { UseShellExecute = true });
            }
            catch { }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                try { Close(); } catch { }
            }
        }
    }
}
