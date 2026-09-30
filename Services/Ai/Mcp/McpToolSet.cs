using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The MCP face of the nine read-only data tools, shared by the stdio bridge and the local
/// HTTP host. <c>suggest_action</c> is in-app only and never listed here: MCP stays read-only.
///
/// <para>
/// Each tool is a typed method, so MCP clients see a proper input schema, and each forwards to
/// a <see cref="ToolInvoker"/> with arguments keyed by the <c>MicaTools</c> parameter names.
/// Results go back as JSON text; non-ASCII (a Thai process name) stays readable rather than
/// becoming <c>\u</c> escapes.
/// </para>
/// </summary>
public static class McpToolSet
{
    /// <summary>The server name MCP clients see, and the key the config snippets use.</summary>
    public const string ServerName = "micastats";

    /// <summary>What the server tells the client about itself at connection time.</summary>
    public const string Instructions =
        "MicaStats is a system monitor running on this Windows PC. Every tool is read-only and returns compact JSON. " +
        "A reading the PC does not provide is reported as unavailable with a reason, never as 0. Times are UTC. " +
        "Paths under the user's profile folder are shown as %USERPROFILE%.";

    /// <summary>The MicaStats version, for <c>serverInfo</c>.</summary>
    public static string CurrentVersion => typeof(McpToolSet).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Fresh server options listing the nine read-only tools, all annotated read-only,
    /// non-destructive and closed-world. Fresh each call because the SDK may adjust the options
    /// object of the server it creates, and the HTTP host creates one server per request.
    /// </summary>
    public static McpServerOptions CreateOptions(ToolInvoker invoke, string serverVersion)
    {
        var handlers = new Handlers(invoke);
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = serverVersion },
            ServerInstructions = Instructions,
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
            {
                Tool(handlers.GetLiveStatus, ToolNames.GetLiveStatus,
                    "Current readings: CPU total and per core, temperatures, RAM, GPU, disks, network, battery and sensors, plus min/avg/max over the last two minutes."),
                Tool(handlers.GetHistory, ToolNames.GetHistory,
                    "Stored per-minute history (last 24 h per minute, up to 7 days in 5-minute rows) for one metric over a time range. Empty when history is turned off in MicaStats."),
                Tool(handlers.GetTopProcesses, ToolNames.GetTopProcesses,
                    "The busiest processes right now by CPU, memory or disk, with name, path, PID, start time, CPU %, working set MB and disk KB/s. Takes about 2 seconds."),
                Tool(handlers.ListSlowdownReports, ToolNames.ListSlowdownReports,
                    "Saved slowdown reports, newest first: id, time, what triggered it and a one-line summary."),
                Tool(handlers.GetSlowdownReport, ToolNames.GetSlowdownReport,
                    "The full text of one slowdown report: a per-second timeline and the worst offending processes."),
                Tool(handlers.ListAlerts, ToolNames.ListAlerts,
                    "The alert rules (enabled, threshold, how long it must last) and the alerts raised recently."),
                Tool(handlers.GetHardware, ToolNames.GetHardware,
                    "Hardware inventory: CPU, GPU, RAM, mainboard and disk models and sizes."),
                Tool(handlers.GetBattery, ToolNames.GetBattery,
                    "Battery charge, health, wear, design and full-charge capacity and cycle count. Unavailable on a desktop."),
                Tool(handlers.GetBootSummary, ToolNames.GetBootSummary,
                    "Recent Windows boot durations, their trend, and the apps, drivers and services that slowed startup."),
            },
        };
    }

    private static McpServerTool Tool(Delegate method, string name, string description) =>
        McpServerTool.Create(method, new McpServerToolCreateOptions
        {
            Name = name,
            Description = description,
            ReadOnly = true,
            Destructive = false,
            OpenWorld = false,
        });

    /// <summary>
    /// The tool methods. Parameter names and defaults are the MCP input schema; the
    /// <see cref="CancellationToken"/> is bound by the SDK and not shown to clients.
    /// </summary>
    private sealed class Handlers
    {
        private readonly ToolInvoker _invoke;

        public Handlers(ToolInvoker invoke) => _invoke = invoke;

        public Task<string> GetLiveStatus(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetLiveStatus, null, cancellationToken);

        public Task<string> GetHistory(
            [Description("One of: cpu, cpuTemp, ram, gpu, gpuTemp, netUp, netDown, diskFree, diskActivity, battery, topProcesses, all.")] string metric,
            [Description("Start of the range: ISO-8601 UTC such as 2026-09-30T08:00:00Z, or relative such as -90s, -30m, -6h, -2d.")] string from,
            [Description("End of the range, in the same forms, or now.")] string to = "now",
            [Description("Most rows to return, 1 to 500; a longer range is downsampled.")] int maxPoints = 200,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.GetHistory, new JsonObject
            {
                ["metric"] = metric,
                ["from"] = from,
                ["to"] = to,
                ["maxPoints"] = maxPoints,
            }, cancellationToken);

        public Task<string> GetTopProcesses(
            [Description("Sort by cpu, memory or disk.")] string by = "cpu",
            [Description("How many processes, 1 to 15.")] int count = 10,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.GetTopProcesses, new JsonObject { ["by"] = by, ["count"] = count }, cancellationToken);

        public Task<string> ListSlowdownReports(
            [Description("How many reports, 1 to 30.")] int limit = 10,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.ListSlowdownReports, new JsonObject { ["limit"] = limit }, cancellationToken);

        public Task<string> GetSlowdownReport(
            [Description("The report id from list_slowdown_reports, for example slowdown-20260930-140200.")] string id,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.GetSlowdownReport, new JsonObject { ["id"] = id }, cancellationToken);

        public Task<string> ListAlerts(
            [Description("Only alerts at or after this time: ISO-8601 UTC or relative such as -24h. Omit for all recent alerts.")] string? since = null,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.ListAlerts, since == null ? new JsonObject() : new JsonObject { ["since"] = since }, cancellationToken);

        public Task<string> GetHardware(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetHardware, null, cancellationToken);

        public Task<string> GetBattery(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetBattery, null, cancellationToken);

        public Task<string> GetBootSummary(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetBootSummary, null, cancellationToken);

        private async Task<string> CallAsync(string tool, JsonObject? args, CancellationToken ct)
        {
            JsonNode result;
            try
            {
                result = await _invoke(tool, args, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A tool never throws at the model: the failure becomes data it can read.
                result = ToolJson.Error(ex.Message);
            }
            return result.ToJsonString(TextOptions);
        }
    }
}
