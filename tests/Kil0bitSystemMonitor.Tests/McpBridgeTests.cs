using System.IO;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.History;
using ModelContextProtocol.Client;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// The <c>--mcp</c> bridge without a process: argument parsing, the mode read from config.json,
/// and the forwarder against a test pipe, the offline tools and Off.
/// </summary>
public class McpBridgeTests : IDisposable
{
    private const string SlowdownReport =
        "==============================================================\n" +
        " MicaStats Slowdown Report\n" +
        " Version   : MicaStats 1.11.0\n" +
        " Trigger   : Recorded by hand\n" +
        " Window    : 2026-09-30 10:00:00  to  2026-09-30 10:05:00\n" +
        " Samples   : 0\n" +
        "==============================================================\n" +
        "No samples were held when this report was written.\n";

    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private readonly AiTestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string TestPipeName() => "MicaStats.Tools.Test." + Guid.NewGuid().ToString("N");

    private MicaTools OfflineTools()
    {
        string reports = _env.PathOf("reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "slowdown-20260930-100000.txt"), SlowdownReport);
        var store = new HistoryStore(_env.PathOf("history"), () => _env.Clock.UtcNow);
        var data = new OfflineMicaData(store, reports, () => _env.Clock.UtcNow);
        return new MicaTools(data, new Redactor(_env.Root, "tester", "TESTPC"));
    }

    private static ToolPipeServer StartServer(string name, ToolInvoker invoke)
    {
        var server = new ToolPipeServer(name, invoke);
        server.Start();
        return server;
    }

    [Theory]
    [InlineData(new[] { "--mcp" }, true)]
    [InlineData(new[] { "--MCP" }, true)]
    [InlineData(new[] { "--startup", "--mcp" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--pad" }, false)]
    [InlineData(new[] { "--mcpx" }, false)]
    [InlineData(new[] { "mcp" }, false)]
    public void The_mcp_flag_is_recognised_anywhere_on_the_command_line(string[] args, bool expected)
    {
        Assert.Equal(expected, McpArguments.TryParse(args));
    }

    [Theory]
    [InlineData("{\"AiMcpMode\":\"Stdio\"}", AiMcpModes.Stdio)]
    [InlineData("{\"Theme\":\"Dark\",\"AiMcpMode\":\"Http\"}", AiMcpModes.Http)]
    [InlineData("{\"AiMcpMode\":\"Off\"}", AiMcpModes.Off)]
    [InlineData("{\"AiMcpMode\":\"stdio\"}", AiMcpModes.Off)]
    [InlineData("{\"AiMcpMode\":3}", AiMcpModes.Off)]
    [InlineData("{}", AiMcpModes.Off)]
    [InlineData("[]", AiMcpModes.Off)]
    [InlineData("not json", AiMcpModes.Off)]
    [InlineData("", AiMcpModes.Off)]
    public void The_mode_comes_from_config_json_and_anything_unclear_is_off(string configText, string expected)
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, configText);

        Assert.Equal(expected, McpBridge.ReadMcpMode(config));
    }

    [Fact]
    public void A_missing_config_is_off()
    {
        Assert.Equal(AiMcpModes.Off, McpBridge.ReadMcpMode(_env.PathOf("missing.json")));
    }

    [Fact]
    public void The_config_is_read_while_the_app_holds_it_open_for_writing()
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\"}");
        using var writer = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(AiMcpModes.Stdio, McpBridge.ReadMcpMode(config));
    }

    [Fact]
    public async Task Off_refuses_every_tool_without_touching_the_pipe()
    {
        string name = TestPipeName();
        int calls = 0;
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<JsonNode>(new JsonObject());
        });
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Off, OfflineTools(), TenSeconds);

        foreach (string tool in ToolNames.ReadOnly.Concat(ToolNames.Notes))
        {
            JsonNode result = await forward(tool, null, CancellationToken.None);
            Assert.Equal("MCP is turned off in MicaStats Settings", (string?)result["error"]);
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task With_the_app_running_a_call_is_answered_by_the_app()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
            Task.FromResult<JsonNode>(new JsonObject { ["tool"] = tool, ["args"] = args?.DeepClone() }));
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode result = await forward(ToolNames.GetTopProcesses, new JsonObject { ["by"] = "disk" }, CancellationToken.None);

        Assert.Equal("{\"tool\":\"get_top_processes\",\"args\":{\"by\":\"disk\"}}", result.ToJsonString());
    }

    [Fact]
    public async Task Without_the_app_history_and_reports_come_from_disk_and_live_tools_say_so()
    {
        ToolInvoker forward = McpBridge.CreateForwarder(TestPipeName(), () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode live = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);
        JsonNode list = await forward(ToolNames.ListSlowdownReports, new JsonObject { ["limit"] = 10 }, CancellationToken.None);
        JsonNode report = await forward(ToolNames.GetSlowdownReport, new JsonObject { ["id"] = "slowdown-20260930-100000" }, CancellationToken.None);

        Assert.Equal("MicaStats is not running", (string?)live["error"]);
        Assert.Contains("slowdown-20260930-100000", list.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Recorded by hand", report.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_inside_the_app_comes_back_as_an_error_object()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => throw new InvalidOperationException("The battery could not be read."));
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode result = await forward(ToolNames.GetBattery, null, CancellationToken.None);

        Assert.Equal("The battery could not be read.", (string?)result["error"]);
    }

    [Fact]
    public async Task A_slow_app_comes_back_as_a_timeout_error_object()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, async (tool, args, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new JsonObject();
        });
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TimeSpan.FromMilliseconds(300));

        JsonNode result = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);

        Assert.Equal("MicaStats did not answer within 0.3 s.", (string?)result["error"]);
    }

    [Fact]
    public async Task The_mode_is_asked_on_every_call()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["cpu"] = 7 }));
        string mode = AiMcpModes.Off;
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => mode, OfflineTools(), TenSeconds);

        JsonNode before = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);
        mode = AiMcpModes.Stdio;
        JsonNode after = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);

        Assert.Equal(McpBridge.OffMessage, (string?)before["error"]);
        Assert.Equal("{\"cpu\":7}", after.ToJsonString());
    }

    [Fact]
    public async Task The_bridge_server_serves_the_nine_tools_and_follows_config_json_between_calls()
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, "{\"AiMcpMode\":\"Off\"}");
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["cpu"] = 7 }));
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpBridge.CreateBridgeOptions(config, name, OfflineTools(), TenSeconds));

        IList<McpClientTool> tools = await mcp.Client.ListToolsAsync();
        string off = await mcp.CallTextAsync(ToolNames.GetLiveStatus);
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\"}");
        string on = await mcp.CallTextAsync(ToolNames.GetLiveStatus);

        Assert.Equal(ToolNames.ReadOnly.Count, tools.Count);
        Assert.Equal("MCP is turned off in MicaStats Settings", (string?)JsonNode.Parse(off)!["error"]);
        Assert.Equal("{\"cpu\":7}", on);
    }

    // ---- the note tools over the bridge -------------------------------------------------------

    [Theory]
    [InlineData("{\"AiNotesInMcp\":true}", true)]
    [InlineData("{\"AiMcpMode\":\"Stdio\",\"AiNotesInMcp\":true,\"AiNotesInAsk\":false}", true)]
    [InlineData("{\"AiNotesInMcp\":false}", false)]
    [InlineData("{\"AiNotesInAsk\":true}", false)]
    [InlineData("{\"AiNotesInMcp\":\"true\"}", false)]
    [InlineData("{\"AiNotesInMcp\":1}", false)]
    [InlineData("{\"AiNotesInMcp\":null}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void Notes_over_mcp_come_from_config_json_and_anything_unclear_is_off(string configText, bool expected)
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, configText);

        Assert.Equal(expected, McpBridge.ReadNotesInMcp(config));
    }

    [Fact]
    public void Notes_over_mcp_are_off_for_a_missing_config_and_read_while_the_app_holds_it_open()
    {
        string config = _env.PathOf("config.json");
        Assert.False(McpBridge.ReadNotesInMcp(config));

        File.WriteAllText(config, "{\"AiNotesInMcp\":true}");
        using var writer = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(McpBridge.ReadNotesInMcp(config));
    }

    [Fact]
    public async Task The_bridge_server_lists_the_note_tools_while_config_json_allows_them_and_follows_it_between_lists()
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\"}");
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(
            McpBridge.CreateBridgeOptions(config, TestPipeName(), OfflineTools(), TenSeconds));

        IList<McpClientTool> before = await mcp.Client.ListToolsAsync();
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\",\"AiNotesInMcp\":true}");
        IList<McpClientTool> on = await mcp.Client.ListToolsAsync();
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\",\"AiNotesInMcp\":false}");
        IList<McpClientTool> off = await mcp.Client.ListToolsAsync();

        Assert.Equal(ToolNames.ReadOnly.Count, before.Count);
        Assert.Equal(ToolNames.ReadOnly.Count + ToolNames.Notes.Count, on.Count);
        Assert.Equal(ToolNames.Notes, on.Select(t => t.Name).Skip(ToolNames.ReadOnly.Count));
        Assert.Equal(ToolNames.ReadOnly.Count, off.Count);
        Assert.DoesNotContain(off, t => ToolNames.Notes.Contains(t.Name));
    }

    [Fact]
    public async Task Without_the_app_a_note_tool_says_MicaStats_is_not_running()
    {
        ToolInvoker forward = McpBridge.CreateForwarder(TestPipeName(), () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode search = await forward(ToolNames.SearchNotes, new JsonObject { ["query"] = "vpn" }, CancellationToken.None);
        JsonNode get = await forward(ToolNames.GetNote, new JsonObject { ["noteId"] = "a1" }, CancellationToken.None);

        Assert.Equal("{\"error\":\"MicaStats is not running\"}", search.ToJsonString());
        Assert.Equal("{\"error\":\"MicaStats is not running\"}", get.ToJsonString());
    }

    [Fact]
    public async Task With_the_app_running_a_note_tool_is_answered_by_the_app_which_follows_its_own_switch()
    {
        var reader = new FakeNoteReader();
        bool allowed = true;
        var app = new MicaTools(new FakeMicaData(), new Redactor(_env.Root, "tester", "TESTPC"))
        {
            Notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(reader), () => false, () => allowed),
        };
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, app.InvokeAsync);
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode search = await forward(ToolNames.SearchNotes, new JsonObject { ["query"] = "vpn", ["limit"] = 3 }, CancellationToken.None);
        JsonNode get = await forward(ToolNames.GetNote, new JsonObject { ["noteId"] = "a1", ["firstLine"] = 2, ["lineCount"] = 1 }, CancellationToken.None);
        allowed = false;
        JsonNode refused = await forward(ToolNames.SearchNotes, new JsonObject { ["query"] = "vpn" }, CancellationToken.None);

        Assert.Equal("a1", (string?)search["results"]![0]!["noteId"]);
        Assert.Equal("vpn", reader.Query);
        Assert.Equal("line two", (string?)get["text"]);   // numbers survive the pipe as numbers
        Assert.Equal(2, (int?)get["firstLine"]);
        Assert.Equal("Notes access is off in Settings → MicaPad → AI", (string?)refused["error"]);
        Assert.Equal(1, reader.Searches);
    }
}
