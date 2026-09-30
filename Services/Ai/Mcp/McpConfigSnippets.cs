using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The text behind Settings' Copy buttons: how Claude Desktop and Claude Code reach MicaStats.
/// Built here rather than in the window so the exact strings are tested.
/// </summary>
public static class McpConfigSnippets
{
    /// <summary>
    /// A <c>claude_desktop_config.json</c> fragment that starts <paramref name="exePath"/> with
    /// <c>--mcp</c>. Real JSON (backslashes escaped), indented, with non-ASCII folder names kept
    /// readable.
    /// </summary>
    public static string ClaudeDesktopJson(string exePath)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpToolSet.ServerName] = new JsonObject
                {
                    ["command"] = exePath,
                    ["args"] = new JsonArray(McpArguments.Flag),
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// The Claude Code command that adds the stdio bridge for every project of this user. The
    /// <c>--</c> keeps Claude Code from reading <c>--mcp</c> as one of its own options.
    /// </summary>
    public static string ClaudeCodeStdioCommand(string exePath) =>
        "claude mcp add --scope user " + McpToolSet.ServerName + " -- \"" + exePath + "\" " + McpArguments.Flag;

    /// <summary>The Claude Code command that adds local HTTP mode, bearer token included.</summary>
    public static string ClaudeCodeHttpCommand(int port, string token) =>
        "claude mcp add --transport http --scope user " + McpToolSet.ServerName +
        " http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/mcp" +
        " --header \"Authorization: Bearer " + token + "\"";
}
