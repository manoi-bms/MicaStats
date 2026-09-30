using System.IO;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// <c>MicaStats.exe --mcp</c>: an MCP server on stdin/stdout for Claude Desktop and Claude Code,
/// which forwards every tool call to the running MicaStats over the tool pipe.
///
/// <para>
/// It runs in its own short-lived process that the MCP client starts and stops: no window, no
/// single-instance mutex, no monitoring. When MicaStats is not running (or is not serving the
/// pipe) the bridge answers from files on disk, so history and slowdown reports still work and
/// live tools say "MicaStats is not running". The MCP setting is read from config.json on
/// every call, so turning MCP off in Settings takes effect for a bridge already running.
/// </para>
/// </summary>
public static class McpBridge
{
    /// <summary>The error every call returns while MCP is Off in Settings.</summary>
    public const string OffMessage = "MCP is turned off in MicaStats Settings";

    /// <summary>How long the bridge waits for the running app to answer one call.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where ConfigService keeps the settings: <c>%APPDATA%\MicaStats\config.json</c>.</summary>
    internal static string DefaultConfigPath => Path.Combine(DiagnosticsLog.DataDir, "config.json");

    /// <summary>
    /// Serves MCP on stdio until the client closes stdin, then returns the process exit code
    /// (0 after a normal end, 1 after a failure, which goes to the diagnostics log). Blocking.
    /// Writes nothing but MCP to stdout.
    /// </summary>
    public static int RunStdio()
    {
        // Before the first log line: the bridge runs beside the app, and two processes appending
        // to micastats.log would refuse each other's writes, so the bridge keeps its own file.
        DiagnosticsLog.UseFileName(DiagnosticsLog.BridgeFileName);

        // The SDK writes the raw stdout handle, not Console.Out, so this cannot silence MCP; it
        // only makes sure a stray Console.Write anywhere can never corrupt the stream.
        Console.SetOut(TextWriter.Null);
        try
        {
            // Called on the WPF thread from OnStartup. Blocking that thread on async work that
            // resumes on its SynchronizationContext would deadlock, so the work runs on the pool.
            Task.Run(RunStdioAsync).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("mcp", "The MCP bridge stopped", ex);
            return 1;
        }
    }

    private static async Task RunStdioAsync()
    {
        Func<DateTime> clock = () => DateTime.UtcNow;
        Action<string> warn = message => DiagnosticsLog.Warn("mcp", message);
        var offline = new MicaTools(
            new OfflineMicaData(new HistoryStore(HistoryStore.DefaultFolder, clock, warn), SlowdownRecorder.ReportDir, clock),
            Redactor.ForCurrentUser());
        McpServerOptions options = CreateBridgeOptions(DefaultConfigPath, ToolPipeProtocol.DefaultPipeName(), offline, CallTimeout);

        await using var transport = new StdioServerTransport(McpToolSet.ServerName);
        await using McpServer server = McpServer.Create(transport, options);
        DiagnosticsLog.Log("mcp", "MCP bridge started");
        await server.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The bridge's server options: the nine read-only tools over a forwarder that reads the mode
    /// from <paramref name="configPath"/> on every call. Separate from <see cref="RunStdio"/> so
    /// tests can drive the same server over in-memory streams.
    /// </summary>
    internal static McpServerOptions CreateBridgeOptions(string configPath, string pipeName, MicaTools offline, TimeSpan callTimeout) =>
        McpToolSet.CreateOptions(
            CreateForwarder(pipeName, () => ReadMcpMode(configPath), offline, callTimeout),
            McpToolSet.CurrentVersion);

    /// <summary>
    /// <c>AiMcpMode</c> from config.json: <see cref="AiMcpModes.Stdio"/> or
    /// <see cref="AiMcpModes.Http"/>, and <see cref="AiMcpModes.Off"/> for anything else,
    /// including a missing, locked or unreadable file. Opened with full sharing so the app's
    /// own save is never blocked.
    /// </summary>
    public static string ReadMcpMode(string configPath)
    {
        try
        {
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(nameof(Kil0bitSystemMonitor.Models.AppConfig.AiMcpMode), out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                string? mode = value.GetString();
                if (string.Equals(mode, AiMcpModes.Stdio, StringComparison.Ordinal)) return AiMcpModes.Stdio;
                if (string.Equals(mode, AiMcpModes.Http, StringComparison.Ordinal)) return AiMcpModes.Http;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // Unreadable counts as Off: the bridge never serves data the user may have turned off.
        }
        return AiMcpModes.Off;
    }

    /// <summary>
    /// The bridge's tool invoker. Off (per <paramref name="mode"/>, asked on every call): every
    /// call returns <see cref="OffMessage"/> as an error. Otherwise the call goes to the running
    /// app over <paramref name="pipeName"/>; when nothing answers there, to
    /// <paramref name="offline"/> (the file-backed tools); a timeout, a version mismatch or a
    /// failure inside the app comes back as an error object. Never throws except for
    /// cancellation.
    /// </summary>
    public static ToolInvoker CreateForwarder(string pipeName, Func<string> mode, MicaTools offline, TimeSpan callTimeout)
    {
        return async (tool, args, ct) =>
        {
            string current = mode();
            if (current != AiMcpModes.Stdio && current != AiMcpModes.Http) return ToolJson.Error(OffMessage);

            try
            {
                return await ToolPipeClient.CallAsync(pipeName, tool, args, callTimeout, ct).ConfigureAwait(false);
            }
            catch (ToolPipeUnavailableException)
            {
                return await offline.InvokeAsync(tool, args, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                return ToolJson.Error(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ToolJson.Error(ex.Message);
            }
        };
    }
}
