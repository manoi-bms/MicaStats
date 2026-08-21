using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.HardwareInfo;

// System.Windows.Forms is in scope via UseWindowsForms and defines its own KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The CPU-Z-style hardware inspector opened from the stats panel's Hardware button.
    ///
    /// <para>
    /// A singleton by design: hardware identity does not change while the app runs, so a second
    /// click focuses the existing window instead of re-querying WMI. Gathering happens on a
    /// background task — the window opens instantly with a status line and fills in when the
    /// snapshot lands. Only the small clock/memory strip is live (1 s timer, stopped on close),
    /// keeping the zero-idle-cost rule the rest of the app follows.
    /// </para>
    /// </summary>
    public partial class HardwareInfoWindow : Window
    {
        private static HardwareInfoWindow? _open;

        private HardwareSnapshot? _snapshot;
        private CpuClockMonitor? _clock;
        private DispatcherTimer? _timer;

        /// <summary>
        /// Completes when the current gather has been applied to the tabs. Public because the
        /// render harness lives in a separate assembly and must await a populated window
        /// before capturing.
        /// </summary>
        public Task? GatherApplied { get; private set; }

        /// <summary>Opens the inspector, or focuses the one already open.</summary>
        public static void ShowHardware()
        {
            if (_open != null)
            {
                try { _open.Activate(); return; } catch { _open = null; }
            }
            var w = new HardwareInfoWindow();
            w.Show();
        }

        public HardwareInfoWindow()
        {
            InitializeComponent();
            _open = this;

            SourceInitialized += (s, e) => ApplyDarkTitleBar();
            Loaded += (s, e) => GatherApplied = BeginGatherAsync();
            PreviewKeyDown += OnPreviewKeyDown;
            Closed += (s, e) =>
            {
                if (ReferenceEquals(_open, this)) _open = null;
                _timer?.Stop();
                _clock?.Dispose();
            };
        }

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

        private async Task BeginGatherAsync()
        {
            StatusText.Text = "Reading hardware…";
            HardwareSnapshot snap;
            try
            {
                snap = await Task.Run(HardwareInfoService.Gather);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("hardware", "Gather crashed", ex);
                StatusText.Text = "Hardware could not be read — see the diagnostics log.";
                return;
            }

            _snapshot = snap;
            Tabs.ItemsSource = snap.Tabs;
            if (Tabs.Items.Count > 0 && Tabs.SelectedIndex < 0) Tabs.SelectedIndex = 0;
            StatusText.Text = "Gathered in " + snap.GatherDuration.TotalMilliseconds.ToString("0") + " ms";
            StartLiveStrip();
        }

        private void StartLiveStrip()
        {
            _clock ??= new CpuClockMonitor();
            if (_timer != null) return;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdateLiveStrip();
            _timer.Start();
            UpdateLiveStrip();
        }

        private void UpdateLiveStrip()
        {
            double? mhz = _clock?.ReadMhz();
            LiveClockText.Text = mhz is double v ? v.ToString("N0") + " MHz" : "—";

            int? load = HardwareInfoService.TryReadMemoryLoad();
            LiveMemText.Text = load is int l ? l + "%" : "—";
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            Tabs.ItemsSource = null;
            GatherApplied = BeginGatherAsync();
        }

        private void OnSaveReport(object sender, RoutedEventArgs e)
        {
            if (_snapshot == null) return;
            try
            {
                string path = HardwareInfoService.SaveReport(_snapshot);
                StatusText.Text = "Saved " + path;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("hardware", "Report save failed", ex);
                StatusText.Text = "Report could not be saved — see the diagnostics log.";
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
