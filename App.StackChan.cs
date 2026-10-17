using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.StackChan;
using Kil0bitSystemMonitor.Services.StackChan.Presence;

namespace Kil0bitSystemMonitor;

public partial class App
{
    public static StackChanService? StackChan { get; private set; }
    public static PresenceService? StackChanPresence { get; private set; }
    private AppConfig? _stackChanConfig;

    private void StartStackChan(ConfigService config)
    {
        var transport = new StackChanTransport(new StackChanBleTransport(Dispatcher),
            new StackChanUsbTransport(), () => config.Config.StackChanUseUsb
                ? StackChanConnectionKind.Usb : StackChanConnectionKind.Bluetooth);
        StackChan = new StackChanService(config.Config, transport);
        string model = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "yunet-2023mar.onnx");
        StackChanPresence = new PresenceService(config.Config, StackChan, transport,
            forceCpu => FaceDetector.Create(model, forceCpu), TimeProvider.System, new PresenceActions(Dispatcher));
        _stackChanConfig = config.Config;
        _stackChanConfig.PropertyChanged += OnStackChanSettingsChanged;
        if (m_history != null) m_history.Updated += OnStackChanMetrics;
        if (s_alerts != null) s_alerts.Raised += OnStackChanAlert;
    }

    private void OnStackChanSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.StartsWith("StackChan", StringComparison.Ordinal) == true)
            StackChan?.RefreshSettings();
    }

    private void OnStackChanMetrics()
    {
        if (m_history?.Latest is not { } metrics) return;
        StackChan?.ObserveMetrics(metrics);
        StackChanPresence?.NoteMetrics(metrics.CpuUsage, metrics.RamPercent, metrics.GpuUsage);
    }

    private void OnStackChanAlert(AlertEvent alert)
    {
        StackChan?.NotifyAlert(alert);
        StackChanPresence?.NoteAlert();
    }

    private void StopStackChan()
    {
        if (_stackChanConfig != null) _stackChanConfig.PropertyChanged -= OnStackChanSettingsChanged;
        if (m_history != null) m_history.Updated -= OnStackChanMetrics;
        if (s_alerts != null) s_alerts.Raised -= OnStackChanAlert;
        StackChanPresence?.Dispose();
        StackChanPresence = null;
        StackChan?.Dispose();
        StackChan = null;
    }

    /// <summary>The PC side of presence: everything here runs on the UI thread.</summary>
    private sealed class PresenceActions(Dispatcher dispatcher) : IPresenceActions
    {
        // Only a vault that was opened can hold an unlock; never create one just to lock it.
        public void LockVault() => dispatcher.BeginInvoke(() => s_padVault?.Lock());

        public void ShowGuard(bool on) => dispatcher.BeginInvoke(() =>
        {
            Kil0bitSystemMonitor.Pad.MicaPadWindow.GuardActive = on;
            foreach (var window in Current.Windows.OfType<Kil0bitSystemMonitor.Pad.MicaPadWindow>())
                window.SetPrivacyShield(on);
        });

        public void LockWindows() => dispatcher.BeginInvoke(() => LockWorkStation());

        public TimeSpan UserIdleFor()
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero; // Unknown: treat as in use, never lock.
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        public void ShowBreakToast() => dispatcher.BeginInvoke(BreakToastWindow.ShowNow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();
    }
}
