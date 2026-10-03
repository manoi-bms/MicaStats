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
            //
            // The note tools read MicaPad's workspace, search and feeder. No MicaPad window is needed,
            // and MicaPad need not have been opened: a note tool call that passed its switch starts
            // them itself (PadHost, on the UI thread), the same ones a MicaPad window would get.
            // Each surface has its own switch, read at every call: off by default, and either one
            // can be turned off while a question or an MCP client is in the middle of its work.
            AppConfig settings = config.Config;
            AiTools = new Services.Ai.Tools.MicaTools(
                new Services.Ai.Tools.LiveMicaData(history, ui, SharedProcessSampler, History,
                    () => AlertMonitorForAi, () => Battery, () => DateTime.UtcNow),
                Services.Ai.Tools.Redactor.ForCurrentUser())
            {
                Notes = new Services.Ai.Tools.NoteAccess(
                    new Services.Pad.Ai.NoteTools(
                        new Kil0bitSystemMonitor.Pad.LiveNoteReader(
                            () => PadHostIfStarted?.Workspace, () => PadHostIfStarted?.Search, () => PadHostIfStarted?.Feeder,
                            ui, start: () => PadHost.StartForNoteTools())),
                    () => settings.AiNotesInAsk,
                    () => settings.AiNotesInMcp),
                // The tool, the surface and counts: never a query, a title or note text.
                NoteLog = line => DiagnosticsLog.Log("ai", line),
            };
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Starting the data tools failed", ex);
        }
        // After AiTools exists (Task 7's code above this line).
        ApplyToolPipe();
        ApplyMcpHttp();
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
        ApplyMcpHttp();
        // Switching the assistant off closes Ask MicaStats, which also stops an answer in
        // progress. The hotkey (App.xaml.cs) and the overlay menu item follow the same switch.
        if (ConfigService?.Config.AiAssistantEnabled != true)
            Current?.Dispatcher.BeginInvoke(new Action(Kil0bitSystemMonitor.Ai.AskWindow.CloseIfOpen));
        // AI anchor: apply
    }

    /// <summary>
    /// Stops the AI side on exit, before the shared process sampler goes: the history recorder
    /// may hold a sampler lease for its once-a-minute top process. Never throws.
    /// </summary>
    internal static void StopAi()
    {
        StopMcpServers();
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

    /// <summary>
    /// Stops the two ways in from outside, the tool pipe and local HTTP. On exit this comes first,
    /// before MicaPad's search and workspace are disposed, so no note call from an MCP client runs
    /// against what is being taken down; <see cref="StopAi"/> calls it again, which does nothing
    /// more. Never throws: a throw here would skip the rest of App.OnExit's teardown, including
    /// the config flush.
    /// </summary>
    internal static void StopMcpServers()
    {
        try
        {
            s_toolPipe?.Dispose();
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping the tool pipe failed", ex);
        }
        s_toolPipe = null;
        try
        {
            s_mcpHttp?.Dispose();
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping local HTTP MCP failed", ex);
        }
        s_mcpHttp = null;
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
                // Guarded like StopAi: this runs from the queued ApplyAiSettings, and a throw there
                // reaches the dispatcher handler, which only logs, so the app would end.
                try
                {
                    s_toolPipe.Dispose();
                    Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Tool pipe for the stdio bridge stopped");
                }
                catch (Exception ex)
                {
                    Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping the tool pipe failed", ex);
                }
                s_toolPipe = null;
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

    /// <summary>The local HTTP MCP host; runs only while MCP is set to Local HTTP.</summary>
    private static Kil0bitSystemMonitor.Services.Ai.Mcp.McpHttpHost? s_mcpHttp;

    /// <summary>The port <see cref="s_mcpHttp"/> listens on, so a port change restarts it.</summary>
    private static int s_mcpHttpPort;

    /// <summary>
    /// Why local HTTP mode is not serving, for example "Port 47831 is in use.", or null while it
    /// serves or is not chosen. Settings shows it under the MCP choice.
    /// </summary>
    public static string? AiMcpHttpProblem { get; private set; }

    /// <summary>
    /// Starts, restarts (the port changed) or stops the local HTTP host to match the AI
    /// settings. Idempotent: called at the end of <see cref="StartAi"/> and on every AI setting
    /// change, so a port that was busy is tried again at the next change.
    /// </summary>
    private static void ApplyMcpHttp()
    {
        var config = ConfigService?.Config;
        Kil0bitSystemMonitor.Services.Ai.Tools.MicaTools? tools = AiTools;
        bool wanted = config != null && tools != null &&
                      config.AiMcpMode == Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Http;
        int port = config?.AiMcpHttpPort ?? 0;

        if (s_mcpHttp != null && (!wanted || port != s_mcpHttpPort))
        {
            // Guarded like StopAi, for the same reason as the tool pipe in ApplyToolPipe.
            try
            {
                s_mcpHttp.Dispose();
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Local HTTP MCP stopped");
            }
            catch (Exception ex)
            {
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping local HTTP MCP failed", ex);
            }
            s_mcpHttp = null;
        }
        if (!wanted || tools == null)
        {
            AiMcpHttpProblem = null;
            return;
        }
        if (s_mcpHttp != null) return;

        try
        {
            EnsureMcpHttpToken();
            string version = Kil0bitSystemMonitor.Services.Ai.Mcp.McpToolSet.CurrentVersion;
            Kil0bitSystemMonitor.Services.Ai.Mcp.ToolInvoker invoke = tools.InvokeAsync;
            // The note tools are listed while the user lets MCP clients read notes; asked at each
            // list request. A call is checked again by the tools themselves (tools.Notes.ForMcp).
            AppConfig settings = config!;
            Func<bool> notesListed = () => settings.AiNotesInMcp;
            var host = new Kil0bitSystemMonitor.Services.Ai.Mcp.McpHttpHost(
                port,
                ReadMcpHttpToken,
                () => Kil0bitSystemMonitor.Services.Ai.Mcp.McpToolSet.CreateOptions(invoke, version, notesListed),
                message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("mcp", message));
            if (host.TryStart(out string? problem))
            {
                s_mcpHttp = host;
                s_mcpHttpPort = port;
                AiMcpHttpProblem = null;
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp",
                    "Local HTTP MCP listening on 127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                host.Dispose();
                // Logged once per new problem, not again at every settings change while it lasts.
                if (problem != AiMcpHttpProblem)
                    Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("mcp", "Local HTTP MCP did not start: " + problem);
                AiMcpHttpProblem = problem;
            }
        }
        catch (Exception ex)
        {
            AiMcpHttpProblem = "Local HTTP could not start. The diagnostics log has the details.";
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Local HTTP MCP could not start", ex);
        }
    }

    /// <summary>
    /// The local HTTP bearer token, read afresh for every request so a token regenerated in
    /// Settings applies at once. Null when it cannot be read, which refuses the request.
    /// </summary>
    private static string? ReadMcpHttpToken()
    {
        try
        {
            return new Kil0bitSystemMonitor.Services.Ai.SecretStore(Kil0bitSystemMonitor.Services.Ai.SecretStore.DefaultPath)
                .Get(Kil0bitSystemMonitor.Services.Ai.SecretNames.McpToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creates the local HTTP bearer token the first time local HTTP mode starts.</summary>
    private static void EnsureMcpHttpToken()
    {
        var secrets = new Kil0bitSystemMonitor.Services.Ai.SecretStore(
            Kil0bitSystemMonitor.Services.Ai.SecretStore.DefaultPath,
            message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", message));
        if (!secrets.Has(Kil0bitSystemMonitor.Services.Ai.SecretNames.McpToken))
            secrets.Set(Kil0bitSystemMonitor.Services.Ai.SecretNames.McpToken,
                Kil0bitSystemMonitor.Services.Ai.SecretStore.NewToken());
    }

    // ---- Ask MicaStats -------------------------------------------------------------------

    private static Kil0bitSystemMonitor.Services.Ai.SecretStore? s_aiSecrets;
    private static Kil0bitSystemMonitor.Services.Ai.UsageMeter? s_aiUsage;

    /// <summary>
    /// The one DPAPI secret store (API keys and the MCP token), created on first use. One
    /// instance for the whole app, so a key saved in Settings is the one the next question uses.
    /// </summary>
    internal static Kil0bitSystemMonitor.Services.Ai.SecretStore AiSecrets =>
        System.Threading.LazyInitializer.EnsureInitialized(ref s_aiSecrets, () =>
            new Kil0bitSystemMonitor.Services.Ai.SecretStore(
                Kil0bitSystemMonitor.Services.Ai.SecretStore.DefaultPath,
                message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", message)));

    /// <summary>Today's question count, shared by every Send and Explain; created on first use.</summary>
    internal static Kil0bitSystemMonitor.Services.Ai.UsageMeter AiUsage =>
        System.Threading.LazyInitializer.EnsureInitialized(ref s_aiUsage, () =>
            new Kil0bitSystemMonitor.Services.Ai.UsageMeter(
                Kil0bitSystemMonitor.Services.Ai.UsageMeter.DefaultPath, () => DateTime.Now));

    /// <summary>
    /// Shows Ask MicaStats and, with a <paramref name="question"/>, asks it at once (the Explain
    /// buttons). From the overlay menu, the hotkey and Explain.
    /// </summary>
    public static void OpenAsk(string? question = null)
    {
        try
        {
            Kil0bitSystemMonitor.Ai.AskWindow.ShowOrActivate(question);
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("ai", "Opening Ask MicaStats failed", ex);
        }
    }

    /// <summary>
    /// The model line under the Ask window's title, read from the config each time the window
    /// opens or a question is sent: "Claude · model", or "model · host" for an OpenAI-compatible
    /// server (the host only, never the path or a key). Null while the config is not loaded.
    /// </summary>
    internal static string? AskModelLabel()
    {
        var config = ConfigService?.Config;
        return config == null
            ? null
            : Kil0bitSystemMonitor.Services.Ai.AiModelLabel.For(
                config.AiProvider, config.AiClaudeModel, config.AiCompatibleModel, config.AiCompatibleBaseUrl);
    }

    /// <summary>
    /// What one Send or Explain needs, built fresh each time so a provider, model or key change
    /// applies from the next question. Never throws: a problem comes back as the sentence to show.
    /// </summary>
    internal static Kil0bitSystemMonitor.Ai.AskSetup CreateAskSetup()
    {
        var config = ConfigService?.Config;
        if (config == null)
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, "MicaStats is still starting. Try again in a moment.");
        if (!config.AiAssistantEnabled)
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, "The assistant is off. Turn it on in Settings > AI.");
        var tools = AiTools;
        if (tools == null)
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, "The data tools are not ready yet. Try again in a moment.");

        Kil0bitSystemMonitor.Services.Ai.AiClientResult? result = null;
        try
        {
            // Where this question goes, from the same settings the client is built from right after
            // (both on the UI thread, with nothing in between). A conversation that read notes
            // remembers it, and a question that goes somewhere else first takes back what was read.
            string destination = Kil0bitSystemMonitor.Services.Pad.Ai.PadAiPrivacy.Destination(config.AiProvider, config.AiCompatibleBaseUrl);
            result = Kil0bitSystemMonitor.Services.Ai.AiProviderFactory.Create(config, AiSecrets);
            if (result.Client == null)
                return new Kil0bitSystemMonitor.Ai.AskSetup(null,
                    result.Problem ?? "The AI provider could not be set up. Check Settings > AI.");

            var assistant = new Kil0bitSystemMonitor.Services.Ai.AiAssistant(
                result.Client, result.IsClaude, tools, AiUsage,
                new Kil0bitSystemMonitor.Services.Ai.AiAssistantOptions
                {
                    DailyLimit = () => config.AiDailyLimit,
                    Destination = destination,
                });
            return new Kil0bitSystemMonitor.Ai.AskSetup(assistant.AskAsync, null, result.Client);
        }
        catch (Exception ex)
        {
            // The client was built but the assistant was not: nothing else owns it.
            try { result?.Client?.Dispose(); } catch { }
            // The type only: a message could quote a URL or a server reply.
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", "Setting up the provider failed (" + ex.GetType().Name + ")");
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, Kil0bitSystemMonitor.Services.Ai.AiErrorText.Describe(ex));
        }
    }

    /// <summary>
    /// The runner for MicaPad's AI actions: the provider of Settings > AI, the shared key store
    /// and the shared daily count. Built per request, so a settings change applies at once.
    /// </summary>
    internal static Kil0bitSystemMonitor.Services.Pad.Ai.PadAiRunner? CreatePadAiRunner()
    {
        AppConfig? config = ConfigService?.Config;
        if (config == null) return null;
        return new Kil0bitSystemMonitor.Services.Pad.Ai.PadAiRunner(
            () => Kil0bitSystemMonitor.Services.Ai.AiProviderFactory.Create(config, AiSecrets), AiUsage, () => config.AiDailyLimit);
    }

    // AI anchor: members
}
