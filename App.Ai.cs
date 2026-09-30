using System;
using System.Threading;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor;

// All of MicaStats AI wiring lives in this file: the 7-day history here, and the assistant, the
// tool pipe and the MCP servers as later work adds them at the "AI anchor" lines. App.xaml.cs only
// calls StartAi after the diagnostics have started and StopAi on exit.
public partial class App
{
    private static HistoryStore? s_historyStore;
    private static HistoryRecorder? s_historyRecorder;
    private static TelemetryService? s_aiTelemetry;
    private static Action<SystemMetrics>? s_aiMetricsHandler;

    /// <summary>
    /// The 7-day history files. Always there (creating it touches no disk), so Settings can show
    /// the size and delete the folder while recording is off, and the tools can read old days.
    /// </summary>
    public static HistoryStore History => LazyInitializer.EnsureInitialized(ref s_historyStore, CreateHistoryStore);

    /// <summary>The alert monitor, for the <c>list_alerts</c> tool; null before the diagnostics start.</summary>
    internal static AlertMonitor? AlertMonitorForAi => s_alerts;

    /// <summary>
    /// Brings up the AI side once the diagnostics exist: the history recorder, fed by every
    /// telemetry tick, and a re-apply whenever a setting whose name starts with "Ai" changes.
    /// Nothing here blocks startup or reaches the network.
    /// </summary>
    internal static void StartAi(ConfigService config, TelemetryService telemetry, MetricsHistory history, Dispatcher ui)
    {
        try
        {
            s_historyRecorder = new HistoryRecorder(History, new SamplerTopProcessSource(SharedProcessSampler),
                                                    () => DateTime.UtcNow, AiWarn("history"));

            // The telemetry timer thread, once a second; the recorder ignores ticks while history is off.
            s_aiTelemetry = telemetry;
            s_aiMetricsHandler = metrics => s_historyRecorder?.OnMetrics(metrics);
            telemetry.MetricsUpdated += s_aiMetricsHandler;

            config.Config.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null && e.PropertyName.StartsWith("Ai", StringComparison.Ordinal))
                    ui.BeginInvoke(new Action(ApplyAiSettings));
            };

            // Queued, not called: everything StartAi builds, including what later work adds at the
            // anchor below, exists before the settings are applied for the first time.
            ui.BeginInvoke(new Action(ApplyAiSettings));
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Starting the history failed", ex);
        }
        try
        {
            // Live readings go through the UI dispatcher, process rankings take a short lease on the
            // shared sampler, and the alert and battery monitors are looked up on each call.
            AiTools = new Services.Ai.Tools.MicaTools(
                new Services.Ai.Tools.LiveMicaData(history, ui, SharedProcessSampler, History,
                    () => AlertMonitorForAi, () => Battery, () => DateTime.UtcNow),
                Services.Ai.Tools.Redactor.ForCurrentUser());
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Starting the data tools failed", ex);
        }
        // After AiTools exists (Task 7's code above this line).
        ApplyToolPipe();
        // AI anchor: start
    }

    /// <summary>
    /// Pushes the AI settings into the running services. Runs on the UI thread, at startup and
    /// after any change to a setting whose name starts with "Ai"; every start and stop is idempotent.
    /// </summary>
    internal static void ApplyAiSettings()
    {
        AppConfig? config = ConfigService?.Config;
        if (config == null) return;

        try
        {
            if (config.AiHistoryEnabled) s_historyRecorder?.Start();
            else s_historyRecorder?.Stop();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Applying the history setting failed", ex);
        }
        ApplyToolPipe();
        // AI anchor: apply
    }

    /// <summary>
    /// Stops the AI side on exit, before the shared process sampler goes: the history recorder
    /// may hold a sampler lease for its once-a-minute top process. Never throws.
    /// </summary>
    internal static void StopAi()
    {
        // Guarded here because this runs before StopAi's own try: a throw would skip the rest
        // of App.OnExit's teardown, including the config flush.
        try
        {
            s_toolPipe?.Dispose();
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping the tool pipe failed", ex);
        }
        s_toolPipe = null;
        // AI anchor: stop
        try
        {
            if (s_aiTelemetry != null && s_aiMetricsHandler != null) s_aiTelemetry.MetricsUpdated -= s_aiMetricsHandler;
            s_aiTelemetry = null;

            // Writes the minute in progress, so an exit loses at most the seconds since the last tick.
            s_historyRecorder?.Dispose();
            s_historyRecorder = null;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Stopping the history failed", ex);
        }
    }

    private static HistoryStore CreateHistoryStore() =>
        new(HistoryStore.DefaultFolder, () => DateTime.UtcNow, AiWarn("history"));

    /// <summary>
    /// A warn callback into the diagnostics log under <paramref name="area"/>. For failures only:
    /// questions, answers, tool data and secrets never go to the log.
    /// </summary>
    private static Action<string> AiWarn(string area) => message => DiagnosticsLog.Warn(area, message);

    /// <summary>
    /// The read-only data tools over the running app, shared by the assistant and the tool pipe;
    /// null until <see cref="StartAi"/> has built them, or if building them failed (logged).
    /// </summary>
    public static Services.Ai.Tools.MicaTools? AiTools { get; private set; }

    /// <summary>
    /// The tool pipe the <c>--mcp</c> bridge forwards to. Runs only while the AI settings have
    /// MCP set to the stdio bridge, so a user who never turns MCP on has no pipe at all.
    /// </summary>
    private static Kil0bitSystemMonitor.Services.Ai.Mcp.ToolPipeServer? s_toolPipe;

    /// <summary>
    /// Starts or stops the tool pipe to match <c>AiMcpMode</c>. Idempotent: called at the end of
    /// <see cref="StartAi"/> and on every AI setting change. A pipe that cannot start (another
    /// session of the same user already serves the name) is logged and tried again at the next
    /// change.
    /// </summary>
    private static void ApplyToolPipe()
    {
        var config = ConfigService?.Config;
        Kil0bitSystemMonitor.Services.Ai.Tools.MicaTools? tools = AiTools;
        if (config == null || tools == null ||
            config.AiMcpMode != Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Stdio)
        {
            if (s_toolPipe != null)
            {
                s_toolPipe.Dispose();
                s_toolPipe = null;
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Tool pipe for the stdio bridge stopped");
            }
            return;
        }
        if (s_toolPipe != null) return;

        try
        {
            var server = new Kil0bitSystemMonitor.Services.Ai.Mcp.ToolPipeServer(
                Kil0bitSystemMonitor.Services.Ai.Mcp.ToolPipeProtocol.DefaultPipeName(),
                tools.InvokeAsync,
                message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("mcp", message));
            server.Start();
            s_toolPipe = server;
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Tool pipe for the stdio bridge started");
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "The tool pipe for the stdio bridge could not start", ex);
        }
    }

    // AI anchor: members
}
