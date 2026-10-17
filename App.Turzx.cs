using System;
using System.ComponentModel;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.Turzx;

namespace Kil0bitSystemMonitor;

public partial class App
{
    public static TurzxDisplayService? Turzx { get; private set; }
    private AppConfig? _turzxConfig;

    private void StartTurzx(ConfigService config)
    {
        Turzx = new TurzxDisplayService(config.Config, new TurzxUsbTransport());
        _turzxConfig = config.Config;
        _turzxConfig.PropertyChanged += OnTurzxSettingsChanged;
        if (m_history != null) m_history.Updated += OnTurzxMetrics;
        if (s_alerts != null) s_alerts.Raised += OnTurzxAlert;
    }

    private void OnTurzxSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.StartsWith("Turzx", StringComparison.Ordinal) == true)
            Turzx?.RefreshSettings();
    }

    private void OnTurzxMetrics()
    {
        if (m_history?.Latest is { } metrics) Turzx?.ObserveMetrics(metrics);
    }

    private void OnTurzxAlert(AlertEvent alert) => Turzx?.NotifyAlert(alert);

    private void StopTurzx()
    {
        if (_turzxConfig != null) _turzxConfig.PropertyChanged -= OnTurzxSettingsChanged;
        if (m_history != null) m_history.Updated -= OnTurzxMetrics;
        if (s_alerts != null) s_alerts.Raised -= OnTurzxAlert;
        Turzx?.Dispose();
        Turzx = null;
    }
}
