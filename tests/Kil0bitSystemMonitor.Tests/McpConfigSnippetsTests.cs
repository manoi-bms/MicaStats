using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>The exact text Settings copies for Claude Desktop and Claude Code.</summary>
public class McpConfigSnippetsTests
{
    private const string Exe = @"C:\Program Files\MicaStats\MicaStats.exe";

    [Fact]
    public void Claude_desktop_json_starts_the_exe_with_the_mcp_flag()
    {
        string json = McpConfigSnippets.ClaudeDesktopJson(Exe);

        JsonNode server = JsonNode.Parse(json)!["mcpServers"]!["micastats"]!;
        Assert.Equal(Exe, (string?)server["command"]);
        Assert.Equal("[\"--mcp\"]", server["args"]!.ToJsonString());
        Assert.Contains("\"C:\\\\Program Files\\\\MicaStats\\\\MicaStats.exe\"", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Claude_desktop_json_keeps_a_thai_folder_name_readable()
    {
        string exe = "C:\\\u0E42\u0E1B\u0E23\u0E41\u0E01\u0E23\u0E21\\MicaStats.exe";

        string json = McpConfigSnippets.ClaudeDesktopJson(exe);

        Assert.Contains("\u0E42\u0E1B\u0E23\u0E41\u0E01\u0E23\u0E21", json);
        Assert.Equal(exe, (string?)JsonNode.Parse(json)!["mcpServers"]!["micastats"]!["command"]);
    }

    [Fact]
    public void Claude_code_stdio_command_quotes_the_path_and_passes_the_flag_after_the_separator()
    {
        Assert.Equal(
            "claude mcp add --scope user micastats -- \"C:\\Program Files\\MicaStats\\MicaStats.exe\" --mcp",
            McpConfigSnippets.ClaudeCodeStdioCommand(Exe));
    }

    [Fact]
    public void Claude_code_http_command_names_the_loopback_url_and_the_bearer_token()
    {
        Assert.Equal(
            "claude mcp add --transport http --scope user micastats http://127.0.0.1:47831/mcp --header \"Authorization: Bearer abc_DEF-123\"",
            McpConfigSnippets.ClaudeCodeHttpCommand(47831, "abc_DEF-123"));
    }
}
