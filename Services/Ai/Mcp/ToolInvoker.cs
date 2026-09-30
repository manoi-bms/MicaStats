using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Runs one read-only tool by name. The running app passes <c>MicaTools.InvokeAsync</c>; the
/// <c>--mcp</c> bridge passes a forwarder to the running app. Both the tool pipe server and the
/// MCP tool set take this shape, so neither knows which one is behind it.
/// </summary>
public delegate Task<JsonNode> ToolInvoker(string tool, JsonObject? args, CancellationToken ct);
