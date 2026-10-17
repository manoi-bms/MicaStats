using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.StackChan;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanUsbTransportTests
{
    private static readonly StackChanDevice UsbRobot = new(0x112233445566, "StackChan USB",
        StackChanFirmware.UiFlow2, StackChanConnectionKind.Usb, "COM8");
    private const StackChanCapabilities AllCapabilities = StackChanCapabilities.Avatar |
        StackChanCapabilities.Rgb | StackChanCapabilities.Motion | StackChanCapabilities.Dashboard;

    [Fact]
    public async Task Missing_calibration_removes_motion_capability_but_preserves_screen_and_leds()
    {
        var endpoint = new FakeEndpoint { MotionCalibrated = false };
        using var transport = Create(endpoint);
        var capabilities = await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        Assert.Equal(AllCapabilities & ~StackChanCapabilities.Motion, capabilities);
    }

    [Fact]
    public async Task Legacy_hello_without_calibration_proof_cannot_enable_motion()
    {
        var endpoint = new FakeEndpoint { MotionCalibrated = null };
        using var transport = Create(endpoint);
        var capabilities = await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        Assert.Equal(AllCapabilities & ~StackChanCapabilities.Motion, capabilities);
    }

    [Fact]
    public void Port_path_is_normalized_and_rejects_non_com_names()
    {
        Assert.Equal(@"\\.\COM8", StackChanUsbTransport.FormatPortPath("com8"));
        Assert.Throws<ArgumentException>(() => StackChanUsbTransport.FormatPortPath("COM0"));
        Assert.Throws<ArgumentException>(() => StackChanUsbTransport.FormatPortPath("LPT1"));
    }

    [Theory]
    [InlineData(@"USB\VID_303A&PID_1001\7C:4F:AD:AE:3D:14", 0x7C4FADAE3D14)]
    [InlineData(@"USB\VID_303A&PID_1001\7C-4F-AD-AE-3D-14", 0x7C4FADAE3D14)]
    [InlineData(@"USB\VID_303A&PID_811A\7C4FADAE3D140000", 0x7C4FADAE3D14)]
    public void Usb_device_id_parser_extracts_a_stable_mac(string deviceId, ulong expected)
    {
        Assert.True(StackChanUsbTransport.TryParseMacFromDeviceId(deviceId, out ulong actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"USB\VID_303A&PID_1001\COM8")]
    [InlineData(@"USB\VID_303A&PID_1001\7C:4F:AD:AE:3D")]
    [InlineData(@"USB\VID_303A&PID_1001\7C4FADAE3D140000")]
    [InlineData("")]
    public void Usb_device_id_parser_rejects_values_without_a_complete_mac(string deviceId)
    {
        Assert.False(StackChanUsbTransport.TryParseMacFromDeviceId(deviceId, out _));
    }

    [Fact]
    public async Task Connect_accepts_a_matching_hello_after_other_lines_in_the_same_stream()
    {
        var endpoint = new FakeEndpoint { PrefixHelloWithNoise = true };
        string? openedPort = null;
        using var transport = Create(endpoint, port => openedPort = port);

        StackChanCapabilities capabilities = await transport.ConnectAsync(UsbRobot, CancellationToken.None);

        Assert.Equal("COM8", openedPort);
        Assert.Equal(AllCapabilities, capabilities);
        Assert.Single(endpoint.JsonWrites);
        using JsonDocument hello = JsonDocument.Parse(endpoint.JsonWrites[0]);
        Assert.Equal(1, hello.RootElement.GetProperty("companionHello").GetInt32());
        Assert.EndsWith("\n", Encoding.UTF8.GetString(endpoint.Writes[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_is_newline_framed_and_ignores_an_applied_ack_for_another_request()
    {
        var endpoint = new FakeEndpoint { PrefixAppliedWithWrongRequest = true };
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.Writes.Clear();
        endpoint.JsonWrites.Clear();

        await transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), CancellationToken.None);

        byte[] wire = Assert.Single(endpoint.Writes);
        Assert.Equal((byte)'\n', wire[^1]);
        using JsonDocument envelope = JsonDocument.Parse(wire.AsMemory(0, wire.Length - 1));
        Assert.Equal(8, envelope.RootElement.GetProperty("command")
            .GetProperty("mouth").GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task Command_rejection_surfaces_the_device_error_and_retires_the_connection()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.RejectCommandsWith = "unsupported command";
        int disconnected = 0;
        transport.Disconnected += () => disconnected++;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.WriteAsync(StackChanChannel.Avatar,
                Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), CancellationToken.None));

        Assert.Contains("unsupported command", error.Message, StringComparison.Ordinal);
        Assert.True(endpoint.Settled);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task Correlated_firmware_rejection_type_is_reported_immediately()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.RejectCommandsWithType = "rejected-motion";

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.WriteAsync(StackChanChannel.Motion,
                Encoding.UTF8.GetBytes("{\"yawServo\":{\"angle\":451}}"), CancellationToken.None));

        Assert.Contains("rejected-motion", error.Message, StringComparison.Ordinal);
        Assert.True(endpoint.Settled);
    }

    [Fact]
    public async Task Cancelling_a_command_read_retires_the_serial_connection()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.RespondToCommands = false;
        using var cancellation = new CancellationTokenSource();
        Task write = transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), cancellation.Token);
        await endpoint.CommandWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(endpoint.Settled);
    }

    [Fact]
    public async Task Wire_limit_counts_the_json_envelope_but_excludes_the_line_feed()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.Writes.Clear();
        endpoint.JsonWrites.Clear();
        int fixedEnvelopeBytes = Encoding.UTF8.GetByteCount(
            "{\"requestId\":\"" + new string('0', 32) + "\",\"command\":}");
        const string commandShell = "{\"data\":\"\"}";
        int dataLength = 2048 - fixedEnvelopeBytes - Encoding.UTF8.GetByteCount(commandShell);
        byte[] maximum = Encoding.UTF8.GetBytes("{\"data\":\"" + new string('x', dataLength) + "\"}");

        await transport.WriteAsync(StackChanChannel.Dashboard, maximum, CancellationToken.None);

        byte[] wire = Assert.Single(endpoint.Writes);
        Assert.Equal(2049, wire.Length);
        Assert.Equal((byte)'\n', wire[^1]);
    }

    [Fact]
    public async Task Oversized_wire_envelope_is_rejected_before_a_command_is_written()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.Writes.Clear();
        endpoint.JsonWrites.Clear();
        byte[] oversized = Encoding.UTF8.GetBytes("{\"data\":\"" + new string('x', 2048) + "\"}");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.WriteAsync(StackChanChannel.Dashboard, oversized, CancellationToken.None));

        Assert.Empty(endpoint.Writes);
    }

    [Fact]
    public async Task Start_companion_follows_raw_paste_flow_control_then_verifies_hello()
    {
        var endpoint = new FakeEndpoint();
        endpoint.Enqueue(Encoding.ASCII.GetBytes("raw REPL; CTRL-B to exit\r\n>"));
        endpoint.Enqueue((byte)'R', 0x01, 0x04, 0x00, 0x01, 0x01, 0x04);
        endpoint.Enqueue(Encoding.ASCII.GetBytes("MicaStatsStackChan USB ready\r\n"));
        using var transport = Create(endpoint);
        byte[] script = Encoding.UTF8.GetBytes("0123456789");

        StackChanCapabilities capabilities = await transport.StartCompanionAsync(
            UsbRobot, script, CancellationToken.None);

        Assert.Equal(AllCapabilities, capabilities);
        Assert.Equal(new byte[] { 0x03, 0x03 }, endpoint.Writes[0]);
        Assert.Equal(new byte[] { 0x01 }, endpoint.Writes[1]);
        Assert.Equal(new byte[] { 0x05, (byte)'A', 0x01 }, endpoint.Writes[2]);
        Assert.Equal("0123", Encoding.UTF8.GetString(endpoint.Writes[3]));
        Assert.Equal("4567", Encoding.UTF8.GetString(endpoint.Writes[4]));
        Assert.Equal("89", Encoding.UTF8.GetString(endpoint.Writes[5]));
        Assert.Equal(new byte[] { 0x04 }, endpoint.Writes[6]);
        Assert.Contains(endpoint.JsonWrites, json =>
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("companionHello", out _);
        });
    }

    [Fact]
    public async Task Startup_traceback_is_reported_and_the_endpoint_is_settled()
    {
        var endpoint = new FakeEndpoint();
        endpoint.Enqueue(Encoding.ASCII.GetBytes("raw REPL; CTRL-B to exit\r\n>"));
        endpoint.Enqueue((byte)'R', 0x00);
        endpoint.Enqueue(Encoding.ASCII.GetBytes("OK\r\nTraceback: bad import\r\n"));
        using var transport = Create(endpoint);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.StartCompanionAsync(UsbRobot, Encoding.UTF8.GetBytes("print('x')"),
                CancellationToken.None));

        Assert.Contains("Traceback", error.Message, StringComparison.Ordinal);
        Assert.True(endpoint.Settled);
    }

    [Fact]
    public async Task Connect_finds_a_hello_glued_to_a_cut_off_picture_line()
    {
        // MicaStats restarted while the robot was mid-picture: the rest of that line was lost
        // and the hello reply follows the cut-off picture text with no line break.
        var endpoint = new FakeEndpoint { PrefixHello = Encoding.ASCII.GetBytes("F12:" + new string('A', 3000)) };
        using var transport = Create(endpoint);

        Assert.Equal(AllCapabilities, await transport.ConnectAsync(UsbRobot, CancellationToken.None));
    }

    [Fact]
    public async Task Command_reply_glued_to_a_cut_off_picture_line_still_correlates()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.BeforeCommandResponse = () => endpoint.Enqueue(Encoding.ASCII.GetBytes("F3:" + new string('B', 3000)));

        await transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Frame_lines_raise_FrameReceived_and_commands_still_correlate()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        var frames = new List<StackChanFrame>();
        transport.FrameReceived += frame => { lock (frames) frames.Add(frame); };
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        endpoint.BeforeCommandResponse = () =>
            endpoint.Enqueue(Encoding.ASCII.GetBytes("F7:" + Convert.ToBase64String(jpeg) + "\n"));

        await transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), CancellationToken.None);

        StackChanFrame frame = Assert.Single(frames);
        Assert.Equal(7, frame.Sequence);
        Assert.Equal(jpeg, frame.Jpeg);
    }

    [Fact]
    public async Task Malformed_frame_line_is_dropped_without_retiring_the_connection()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        int frames = 0, disconnected = 0;
        transport.FrameReceived += _ => Interlocked.Increment(ref frames);
        transport.Disconnected += () => disconnected++;
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.BeforeCommandResponse = () => endpoint.Enqueue(Encoding.ASCII.GetBytes("Fx:abc\nF1:!!!\n"));

        await transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), CancellationToken.None);
        await transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":9}}"), CancellationToken.None);

        Assert.Equal(0, frames);
        Assert.Equal(0, disconnected);
    }

    [Fact]
    public async Task Overlong_frame_line_is_dropped()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        int frames = 0;
        transport.FrameReceived += _ => Interlocked.Increment(ref frames);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);
        endpoint.BeforeCommandResponse = () =>
            endpoint.Enqueue(Encoding.ASCII.GetBytes("F1:" + new string('A', 20000) + "\n"));

        await transport.WriteAsync(StackChanChannel.Avatar,
            Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}"), CancellationToken.None);

        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task Disconnect_stops_frame_delivery()
    {
        var endpoint = new FakeEndpoint();
        using var transport = Create(endpoint);
        int frames = 0;
        transport.FrameReceived += _ => Interlocked.Increment(ref frames);
        await transport.ConnectAsync(UsbRobot, CancellationToken.None);

        await transport.DisconnectAsync();
        endpoint.Enqueue(Encoding.ASCII.GetBytes("F1:" + Convert.ToBase64String(new byte[] { 1, 2 }) + "\n"));
        await Task.Delay(100);

        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task Router_forwards_frames_only_from_the_active_usb_link()
    {
        var bluetooth = new FrameSourceTransport();
        var usb = new FrameSourceTransport();
        var mode = StackChanConnectionKind.Usb;
        using var router = new StackChanTransport(bluetooth, usb, () => mode);
        var frames = new List<StackChanFrame>();
        router.FrameReceived += frames.Add;
        var frame = new StackChanFrame(1, [1], DateTimeOffset.UnixEpoch);

        usb.Raise(frame);
        await router.ConnectAsync(UsbRobot, CancellationToken.None);
        usb.Raise(frame);
        bluetooth.Raise(frame);
        await router.DisconnectAsync();
        usb.Raise(frame);

        Assert.Single(frames);
    }

    private sealed class FrameSourceTransport : IStackChanTransport, IStackChanFrameSource
    {
        public event Action? Disconnected { add { } remove { } }
        public event Action<StackChanFrame>? FrameReceived;
        public void Raise(StackChanFrame frame) => FrameReceived?.Invoke(frame);
        public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StackChanDevice>>([]);
        public Task<StackChanCapabilities> ConnectAsync(StackChanDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(AllCapabilities);
        public Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task Connect_without_a_companion_reply_reports_that_the_companion_is_not_running()
    {
        var endpoint = new FakeEndpoint { AnswerHello = false };
        using var transport = Create(endpoint, connectTimeout: TimeSpan.FromMilliseconds(200));

        StackChanUsbException error = await Assert.ThrowsAsync<StackChanUsbException>(() =>
            transport.ConnectAsync(UsbRobot, CancellationToken.None));

        Assert.Equal(StackChanUsbFailure.NoCompanion, error.Failure);
        Assert.True(endpoint.Settled);
    }

    [Fact]
    public async Task Connect_write_failure_keeps_its_usb_cause()
    {
        var endpoint = new FakeEndpoint
        {
            WriteFailure = new StackChanUsbException(StackChanUsbFailure.NotAccepting, "synthetic")
        };
        using var transport = Create(endpoint);

        StackChanUsbException error = await Assert.ThrowsAsync<StackChanUsbException>(() =>
            transport.ConnectAsync(UsbRobot, CancellationToken.None));

        Assert.Equal(StackChanUsbFailure.NotAccepting, error.Failure);
        Assert.True(endpoint.Settled);
    }

    private static StackChanUsbTransport Create(FakeEndpoint endpoint, Action<string>? opened = null,
        TimeSpan? connectTimeout = null) =>
        new(() => [UsbRobot], (port, token) =>
        {
            token.ThrowIfCancellationRequested();
            opened?.Invoke(port);
            return Task.FromResult<IStackChanUsbEndpoint>(endpoint);
        }, connectTimeout);

    private sealed class FakeEndpoint : IStackChanUsbEndpoint
    {
        private readonly object _gate = new();
        private readonly Queue<byte> _incoming = new();
        private readonly SemaphoreSlim _available = new(0);
        public List<byte[]> Writes { get; } = [];
        public List<byte[]> JsonWrites { get; } = [];
        public bool PrefixHelloWithNoise { get; set; }
        public bool? MotionCalibrated { get; set; } = true;
        public bool PrefixAppliedWithWrongRequest { get; set; }
        public bool RespondToCommands { get; set; } = true;
        public bool AnswerHello { get; set; } = true;
        public Action? BeforeCommandResponse { get; set; }
        public byte[]? PrefixHello { get; set; }
        public Exception? WriteFailure { get; set; }
        public string? RejectCommandsWith { get; set; }
        public string? RejectCommandsWithType { get; set; }
        public bool Settled { get; private set; }
        public TaskCompletionSource CommandWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> ReadByteAsync(CancellationToken token)
        {
            await _available.WaitAsync(token);
            lock (_gate) return _incoming.Dequeue();
        }

        public Task WriteAsync(byte[] bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (WriteFailure != null) return Task.FromException(WriteFailure);
            byte[] copy = (byte[])bytes.Clone();
            Writes.Add(copy);
            if (copy.Length > 1 && copy[^1] == (byte)'\n' && copy[0] == (byte)'{')
                RespondToJson(copy);
            return Task.CompletedTask;
        }

        private void RespondToJson(byte[] wire)
        {
            byte[] json = wire[..^1];
            JsonWrites.Add(json);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string requestId = root.GetProperty("requestId").GetString()!;
            if (root.TryGetProperty("companionHello", out _))
            {
                if (!AnswerHello) return;
                if (PrefixHello != null) Enqueue(PrefixHello);
                if (PrefixHelloWithNoise)
                    Enqueue(Encoding.UTF8.GetBytes("boot message\r\n{\"requestId\":\"other\",\"ok\":true,\"type\":\"hello\"}\n"));
                if (MotionCalibrated.HasValue)
                    EnqueueJson(new { requestId, ok = true, type = "hello", device = "MicaStatsStackChan", protocol = 1, transport = "usb", motionCalibrated = MotionCalibrated.Value });
                else
                    EnqueueJson(new { requestId, ok = true, type = "hello", device = "MicaStatsStackChan", protocol = 1, transport = "usb" });
                return;
            }

            CommandWritten.TrySetResult();
            if (!RespondToCommands) return;
            BeforeCommandResponse?.Invoke();
            if (PrefixAppliedWithWrongRequest)
                EnqueueJson(new { requestId = "wrong", ok = true, type = "applied" });
            if (RejectCommandsWith is { } error)
                EnqueueJson(new { requestId, ok = false, error });
            else if (RejectCommandsWithType is { } rejectionType)
                EnqueueJson(new { requestId, ok = true, type = rejectionType });
            else
                EnqueueJson(new { requestId, ok = true, type = "applied" });
        }

        private void EnqueueJson(object value)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
            Enqueue(json);
            Enqueue((byte)'\n');
        }

        public void Enqueue(params byte[] bytes)
        {
            lock (_gate)
            {
                foreach (byte value in bytes) _incoming.Enqueue(value);
            }
            _available.Release(bytes.Length);
        }

        public Task DisposeAndSettleAsync()
        {
            Settled = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Settled = true;
    }
}
