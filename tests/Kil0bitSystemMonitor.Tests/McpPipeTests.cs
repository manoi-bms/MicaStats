using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// The tool pipe end to end, in-process: a server on a unique test pipe name and the real
/// client. Nothing here touches the user's real pipe name.
/// </summary>
public class McpPipeTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static string TestPipeName() => "MicaStats.Tools.Test." + Guid.NewGuid().ToString("N");

    private static ToolPipeServer StartServer(string name, ToolInvoker invoke)
    {
        var server = new ToolPipeServer(name, invoke);
        server.Start();
        return server;
    }

    private static ToolInvoker Constant(JsonObject result) =>
        (tool, args, ct) => Task.FromResult<JsonNode>(result.DeepClone());

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public async Task A_call_reaches_the_tool_with_its_arguments_and_returns_the_result()
    {
        string name = TestPipeName();
        string? seenTool = null;
        string? seenArgs = null;
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
        {
            seenTool = tool;
            seenArgs = args?.ToJsonString();
            return Task.FromResult<JsonNode>(new JsonObject { ["cpu"] = 12.5 });
        });

        JsonNode result = await ToolPipeClient.CallAsync(name, "get_top_processes", new JsonObject { ["by"] = "cpu" }, TenSeconds, CancellationToken.None);

        Assert.Equal("{\"cpu\":12.5}", result.ToJsonString());
        Assert.Null(result.Parent);
        Assert.Equal("get_top_processes", seenTool);
        Assert.Equal("{\"by\":\"cpu\"}", seenArgs);
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task One_connection_can_carry_several_requests()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["tool"] = tool }));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        foreach (string tool in new[] { "get_battery", "get_hardware" })
        {
            await ToolPipeProtocol.WriteAsync(pipe, new JsonObject { ["v"] = 1, ["tool"] = tool }, CancellationToken.None);
            JsonObject? reply = await ToolPipeProtocol.ReadAsync(pipe, CancellationToken.None);

            Assert.Equal(1, (int?)reply!["v"]);
            Assert.Equal(true, (bool?)reply["ok"]);
            Assert.Equal(tool, (string?)reply["result"]!["tool"]);
        }
    }

    [Fact]
    public async Task A_request_from_another_protocol_version_is_refused_with_a_clear_error()
    {
        string name = TestPipeName();
        int calls = 0;
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<JsonNode>(new JsonObject());
        });
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        await ToolPipeProtocol.WriteAsync(pipe, new JsonObject { ["v"] = 2, ["tool"] = "get_battery" }, CancellationToken.None);
        JsonObject? reply = await ToolPipeProtocol.ReadAsync(pipe, CancellationToken.None);

        Assert.Equal(false, (bool?)reply!["ok"]);
        Assert.Contains("version mismatch", (string?)reply["error"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task The_client_refuses_a_reply_from_another_protocol_version()
    {
        string name = TestPipeName();
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serve = Task.Run(async () =>
        {
            await fake.WaitForConnectionAsync();
            await ToolPipeProtocol.ReadAsync(fake, CancellationToken.None);
            await ToolPipeProtocol.WriteAsync(fake, new JsonObject { ["v"] = 2, ["ok"] = true, ["result"] = new JsonObject() }, CancellationToken.None);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None));

        Assert.Contains("version mismatch", ex.Message);
        await serve;
    }

    [Fact]
    public async Task A_failing_tool_reaches_the_caller_as_its_message()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => throw new InvalidOperationException("The sensor could not be read."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToolPipeClient.CallAsync(name, "get_live_status", null, TenSeconds, CancellationToken.None));

        Assert.Equal("The sensor could not be read.", ex.Message);
    }

    [Fact]
    public async Task A_call_that_takes_too_long_times_out()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, async (tool, args, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new JsonObject();
        });
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => ToolPipeClient.CallAsync(name, "get_live_status", null, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal("MicaStats did not answer within 0.3 s.", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task No_server_means_unavailable_within_about_a_second()
    {
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<ToolPipeUnavailableException>(
            () => ToolPipeClient.CallAsync(TestPipeName(), "get_live_status", null, TenSeconds, CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cancelled_caller_gets_a_cancellation_not_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToolPipeClient.CallAsync(TestPipeName(), "get_live_status", null, TenSeconds, cts.Token));
    }

    [Fact]
    public void The_descriptor_admits_only_the_current_user_and_carries_a_medium_label()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier me = identity.User!;

        Assert.Equal("D:P(A;;GA;;;" + me.Value + ")S:(ML;;NW;;;ME)", ToolPipeNative.SddlFor(me));
    }

    [Fact]
    public async Task The_live_pipe_has_a_user_only_dacl_and_the_medium_label()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, Constant(new JsonObject()));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        string dacl = ToolPipeNative.ReadSddl(pipe.SafePipeHandle, ToolPipeNative.DACL_SECURITY_INFORMATION);
        string label = ToolPipeNative.ReadSddl(pipe.SafePipeHandle, ToolPipeNative.LABEL_SECURITY_INFORMATION);

        Assert.StartsWith("D:P(", dacl);
        Assert.Equal(1, CountOf(dacl, "(A;"));
        Assert.Contains(";;;" + identity.User!.Value + ")", dacl);
        Assert.Contains("(ML;;NW;;;ME)", label);
    }

    [Fact]
    public void A_name_another_process_already_serves_is_refused()
    {
        string name = TestPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances);
        using var server = new ToolPipeServer(name, Constant(new JsonObject()));

        var ex = Assert.Throws<IOException>(() => server.Start());

        Assert.Contains("already in use", ex.Message);
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void A_second_server_on_the_same_name_is_refused()
    {
        string name = TestPipeName();
        using ToolPipeServer first = StartServer(name, Constant(new JsonObject()));
        using var second = new ToolPipeServer(name, Constant(new JsonObject()));

        Assert.Throws<IOException>(() => second.Start());
        Assert.True(first.IsRunning);
    }

    [Fact]
    public async Task Stop_closes_the_pipe_and_frees_the_name()
    {
        string name = TestPipeName();
        ToolPipeServer first = StartServer(name, Constant(new JsonObject { ["n"] = 1 }));

        first.Stop();

        Assert.False(first.IsRunning);
        await Assert.ThrowsAsync<ToolPipeUnavailableException>(
            () => ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None));
        using ToolPipeServer second = StartServer(name, Constant(new JsonObject { ["n"] = 2 }));
        JsonNode result = await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);
        Assert.Equal(2, (int?)result["n"]);
        first.Dispose();
    }

    [Fact]
    public async Task At_most_four_calls_run_at_once_and_the_rest_wait_their_turn()
    {
        string name = TestPipeName();
        int running = 0;
        int peak = 0;
        using ToolPipeServer server = StartServer(name, async (tool, args, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
            await Task.Delay(200, ct);
            Interlocked.Decrement(ref running);
            return new JsonObject { ["tool"] = tool };
        });

        JsonNode[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(
            i => ToolPipeClient.CallAsync(name, "t" + i, null, TenSeconds, CancellationToken.None)));

        Assert.Equal(Enumerable.Range(0, 6).Select(i => "t" + i), results.Select(r => (string?)r["tool"]));
        Assert.InRange(Volatile.Read(ref peak), 2, ToolPipeServer.MaxClients);
    }
}
