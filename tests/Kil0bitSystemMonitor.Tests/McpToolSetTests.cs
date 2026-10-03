using System.Text.Json;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using ModelContextProtocol.Client;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>The MCP tool list and argument mapping, through the SDK client over in-memory streams.</summary>
public class McpToolSetTests
{
    private sealed class RecordingInvoker
    {
        public readonly List<(string Tool, string? Args)> Calls = new();
        public JsonNode Reply { get; set; } = new JsonObject { ["ok"] = 1 };

        public Task<JsonNode> Invoke(string tool, JsonObject? args, CancellationToken ct)
        {
            lock (Calls) Calls.Add((tool, args?.ToJsonString()));
            return Task.FromResult(Reply.DeepClone());
        }
    }

    [Fact]
    public async Task The_server_lists_exactly_the_nine_read_only_tools()
    {
        var invoker = new RecordingInvoker();
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "9.8.7"));

        IList<McpClientTool> tools = await mcp.Client.ListToolsAsync();

        Assert.Equal(ToolNames.ReadOnly.OrderBy(n => n, StringComparer.Ordinal), tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(tools, t => t.Name == ToolNames.SuggestAction);
        Assert.All(tools, t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
            Assert.False(t.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });
        Assert.Equal(McpToolSet.ServerName, mcp.Client.ServerInfo.Name);
        Assert.Equal("9.8.7", mcp.Client.ServerInfo.Version);
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task With_notes_allowed_the_server_lists_the_nine_and_the_two_note_tools_all_read_only()
    {
        var invoker = new RecordingInvoker();
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "9.8.7", () => true));

        IList<McpClientTool> tools = await mcp.Client.ListToolsAsync();

        Assert.Equal(ToolNames.ReadOnly.Concat(ToolNames.Notes).OrderBy(n => n, StringComparer.Ordinal),
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(tools, t => t.Name == ToolNames.SuggestAction);
        Assert.All(tools, t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
            Assert.False(t.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });
        Assert.Empty(invoker.Calls);   // listing asks nothing of the notes
    }

    [Fact]
    public async Task The_history_schema_requires_metric_and_from_and_hides_the_cancellation_token()
    {
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(new RecordingInvoker().Invoke, "1.0.0"));

        McpClientTool history = (await mcp.Client.ListToolsAsync()).Single(t => t.Name == ToolNames.GetHistory);
        JsonElement schema = history.JsonSchema;

        string[] properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "from", "maxPoints", "metric", "to" }, properties);
        string[] required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "from", "metric" }, required);
    }

    [Theory]
    [InlineData(ToolNames.GetLiveStatus, "{}", null)]
    [InlineData(ToolNames.GetHistory, "{\"metric\":\"cpu\",\"from\":\"-1h\"}", "{\"metric\":\"cpu\",\"from\":\"-1h\",\"to\":\"now\",\"maxPoints\":200}")]
    [InlineData(ToolNames.GetHistory, "{\"metric\":\"all\",\"from\":\"-2d\",\"to\":\"-1d\",\"maxPoints\":50}", "{\"metric\":\"all\",\"from\":\"-2d\",\"to\":\"-1d\",\"maxPoints\":50}")]
    [InlineData(ToolNames.GetTopProcesses, "{}", "{\"by\":\"cpu\",\"count\":10}")]
    [InlineData(ToolNames.GetTopProcesses, "{\"by\":\"memory\",\"count\":3}", "{\"by\":\"memory\",\"count\":3}")]
    [InlineData(ToolNames.ListSlowdownReports, "{}", "{\"limit\":10}")]
    [InlineData(ToolNames.GetSlowdownReport, "{\"id\":\"slowdown-20260930-140200\"}", "{\"id\":\"slowdown-20260930-140200\"}")]
    [InlineData(ToolNames.ListAlerts, "{}", "{}")]
    [InlineData(ToolNames.ListAlerts, "{\"since\":\"-24h\"}", "{\"since\":\"-24h\"}")]
    [InlineData(ToolNames.GetHardware, "{}", null)]
    [InlineData(ToolNames.GetBattery, "{}", null)]
    [InlineData(ToolNames.GetBootSummary, "{}", null)]
    public async Task Each_tool_forwards_its_name_and_arguments_keyed_like_mica_tools(string tool, string argsJson, string? expectedArgs)
    {
        var invoker = new RecordingInvoker();
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "1.0.0"));
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)!;

        await mcp.CallTextAsync(tool, args);

        var call = Assert.Single(invoker.Calls);
        Assert.Equal(tool, call.Tool);
        Assert.Equal(expectedArgs, call.Args);
    }

    [Fact]
    public async Task The_result_comes_back_as_json_text_with_non_ascii_kept_readable()
    {
        var invoker = new RecordingInvoker { Reply = new JsonObject { ["name"] = "\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe", ["cpu"] = 7.5 } };
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "1.0.0"));

        string text = await mcp.CallTextAsync(ToolNames.GetLiveStatus);

        Assert.Equal("{\"name\":\"\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe\",\"cpu\":7.5}", text);
    }

    [Fact]
    public async Task An_invoker_failure_comes_back_as_an_error_object()
    {
        ToolInvoker failing = (tool, args, ct) => throw new InvalidOperationException("The pipe broke.");
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(failing, "1.0.0"));

        string text = await mcp.CallTextAsync(ToolNames.GetBattery);

        Assert.Equal("The pipe broke.", (string?)JsonNode.Parse(text)!["error"]);
    }
}
