using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
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

    /// <summary>A hand-driven single-instance server with the real descriptor, so the client trusts it.</summary>
    private static NamedPipeServerStream FakeServer(string name)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var handle = ToolPipeNative.Create(name, ToolPipeNative.SddlFor(identity.User!), firstInstance: true, maxInstances: 1);
        return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
    }

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
        Assert.Contains("version mismatch", (string?)reply["error"], StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task The_client_refuses_a_reply_from_another_protocol_version()
    {
        string name = TestPipeName();
        using NamedPipeServerStream fake = FakeServer(name);
        Task serve = Task.Run(async () =>
        {
            await fake.WaitForConnectionAsync();
            await ToolPipeProtocol.ReadAsync(fake, CancellationToken.None);
            await ToolPipeProtocol.WriteAsync(fake, new JsonObject { ["v"] = 2, ["ok"] = true, ["result"] = new JsonObject() }, CancellationToken.None);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None));

        Assert.Contains("version mismatch", ex.Message, StringComparison.Ordinal);
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

        Assert.Equal("O:" + me.Value + "D:P(A;;GA;;;" + me.Value + ")S:(ML;;NWNR;;;ME)", ToolPipeNative.SddlFor(me));
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

        Assert.StartsWith("D:P(", dacl, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(dacl, "(A;"));
        Assert.Equal("O:" + identity.User!.Value, ToolPipeNative.ReadSddl(pipe.SafePipeHandle, 0x00000001));
        Assert.Contains(";;;" + identity.User!.Value + ")", dacl, StringComparison.Ordinal);
        Assert.Contains("(ML;;NWNR;;;ME)", label, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_another_process_already_serves_is_refused()
    {
        string name = TestPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances);
        using var server = new ToolPipeServer(name, Constant(new JsonObject()));

        var ex = Assert.Throws<IOException>(() => server.Start());

        Assert.Contains("already in use", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task A_squatter_with_a_default_descriptor_is_refused_before_any_request_is_sent()
    {
        string name = TestPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<JsonObject?> received = Task.Run(async () =>
        {
            try
            {
                await squatter.WaitForConnectionAsync();
                return await ToolPipeProtocol.ReadAsync(squatter, CancellationToken.None);
            }
            catch (IOException)
            {
                return null;   // the client came and went before the squatter looked: nothing was sent
            }
        });

        var ex = await Assert.ThrowsAsync<ToolPipeUnavailableException>(
            () => ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None));

        Assert.Contains("not a MicaStats pipe", ex.Message, StringComparison.Ordinal);
        Assert.Null(await received);   // the client hung up without writing a frame
    }

    [Fact]
    public async Task The_client_connects_at_the_identification_level_so_the_server_cannot_act_as_it()
    {
        string name = TestPipeName();
        using NamedPipeServerStream fake = FakeServer(name);
        Task<TokenImpersonationLevel?> serve = Task.Run(async () =>
        {
            await fake.WaitForConnectionAsync();
            await ToolPipeProtocol.ReadAsync(fake, CancellationToken.None);
            TokenImpersonationLevel? level = null;
            fake.RunAsClient(() =>
            {
                using WindowsIdentity? impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
                level = impersonated?.ImpersonationLevel;
            });
            await ToolPipeProtocol.WriteAsync(fake, new JsonObject { ["v"] = 1, ["ok"] = true, ["result"] = new JsonObject() }, CancellationToken.None);
            return level;
        });

        await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);

        Assert.Equal(TokenImpersonationLevel.Identification, await serve);
    }

    [Theory]
    [InlineData("S:AI(ML;;NWNR;;;ME)", true)]
    [InlineData("S:(ML;;NW;;;HI)", true)]
    [InlineData("S:(ML;;NW;;;S-1-16-8192)", true)]
    [InlineData("S:(ML;;NW;;;LW)", false)]
    [InlineData("S:(ML;;NW;;;S-1-16-4096)", false)]
    [InlineData("", false)]
    public void Only_a_medium_or_higher_label_counts(string sddl, bool expected)
    {
        Assert.Equal(expected, ToolPipeNative.LabelIsMediumOrHigher(sddl));
    }

    [Fact]
    public async Task Callers_dropping_connections_while_all_slots_are_busy_do_not_kill_the_server()
    {
        string name = TestPipeName();
        using var server = new ToolPipeServer(name, async (tool, args, ct) =>
        {
            await Task.Delay(100, ct);
            return new JsonObject();
        }) { RequestWaitLimit = TimeSpan.FromMilliseconds(300) };
        server.Start();
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        Task[] callers = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            while (DateTime.UtcNow < stopAt)
            {
                try
                {
                    await ToolPipeClient.CallAsync(name, "t", null, TenSeconds, CancellationToken.None);
                }
                catch (Exception ex) when (ex is ToolPipeUnavailableException or TimeoutException or InvalidOperationException)
                {
                }
            }
        })).ToArray();
        Task[] hitters = Enumerable.Range(0, 3).Select(i => Task.Run(() =>
        {
            while (DateTime.UtcNow < stopAt)
            {
                try
                {
                    using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
                    c.Connect(0);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                }
            }
        })).ToArray();
        await Task.WhenAll(callers.Concat(hitters));

        Assert.True(server.IsRunning);
        JsonNode result = await ToolPipeClient.CallAsync(name, "t", null, TenSeconds, CancellationToken.None);
        Assert.NotNull(result);
    }

    /// <summary>A server whose next instances fail to be created <paramref name="failures"/> times, with short pauses.</summary>
    private static ToolPipeServer FlakyServer(string name, List<string> warnings, Func<Exception> failure, StrongBox<int> failures) =>
        new(name, Constant(new JsonObject { ["n"] = 1 }), message =>
        {
            lock (warnings) warnings.Add(message);
        })
        {
            RetryDelayFirst = TimeSpan.FromMilliseconds(10),
            RetryDelayMax = TimeSpan.FromMilliseconds(40),
            BeforeCreateInstance = () =>
            {
                if (Interlocked.Decrement(ref failures.Value) >= 0) throw failure();
            },
        };

    [Fact]
    public async Task An_unexpected_failure_creating_the_next_instance_is_logged_with_its_cause_and_serving_goes_on()
    {
        string name = TestPipeName();
        var warnings = new List<string>();
        var failures = new StrongBox<int>(3);
        using ToolPipeServer server = FlakyServer(name, warnings, () => new UnauthorizedAccessException("Access to the pipe is denied."), failures);
        server.Start();

        JsonNode first = await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);
        JsonNode second = await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);

        Assert.Equal(1, (int?)first["n"]);
        Assert.Equal(1, (int?)second["n"]);
        Assert.True(server.IsRunning);
        string warning;
        lock (warnings) warning = Assert.Single(warnings);
        Assert.Contains("UnauthorizedAccessException: Access to the pipe is denied.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_create_failure_that_repeats_is_logged_once_per_run_of_failures()
    {
        string name = TestPipeName();
        var warnings = new List<string>();
        var failures = new StrongBox<int>(6);
        using ToolPipeServer server = FlakyServer(name, warnings, () => new IOException("All pipe instances are busy."), failures);
        server.Start();

        await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);
        lock (warnings) Assert.Single(warnings);

        // A success ends the run: the next run of failures is logged again, once.
        Volatile.Write(ref failures.Value, 2);
        await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);
        await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);

        Assert.True(server.IsRunning);
        lock (warnings)
        {
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("IOException: All pipe instances are busy.", w, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 400)]
    [InlineData(2, 800)]
    [InlineData(4, 3200)]
    [InlineData(5, 5000)]
    [InlineData(1000, 5000)]
    public void Retries_back_off_from_200_ms_doubling_up_to_5_s(int failures, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs),
            ToolPipeServer.RetryDelay(failures, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Idle_connections_are_closed_and_do_not_hold_the_slots()
    {
        string name = TestPipeName();
        using var server = new ToolPipeServer(name, Constant(new JsonObject { ["n"] = 1 })) { RequestWaitLimit = TimeSpan.FromMilliseconds(300) };
        server.Start();
        var idle = new List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < ToolPipeServer.MaxClients; i++)
            {
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                idle.Add(pipe);
                await pipe.ConnectAsync(5000);
            }

            // Every slot is taken by a silent connection; the call gets through once they time out.
            JsonNode result = await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);

            Assert.Equal(1, (int?)result["n"]);
            foreach (NamedPipeClientStream pipe in idle)
                Assert.Null(await ToolPipeProtocol.ReadAsync(pipe, CancellationToken.None));   // closed by the server
        }
        finally
        {
            foreach (NamedPipeClientStream pipe in idle) pipe.Dispose();
        }
    }
}
