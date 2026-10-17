using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Kil0bitSystemMonitor.Services.StackChan;

internal interface IStackChanUsbEndpoint : IDisposable
{
    Task<int> ReadByteAsync(CancellationToken token);
    Task WriteAsync(byte[] bytes, CancellationToken token);
    Task DisposeAndSettleAsync();
}

/// <summary>USB-serial transport for the explicitly installed MicaStats companion script.</summary>
public sealed class StackChanUsbTransport : IStackChanTransport, IStackChanUsbCompanionStarter, IStackChanFrameSource
{
    public event Action<StackChanFrame>? FrameReceived;

    private const int MaximumWireBytes = 2048;
    // A QVGA JPEG is about 4.3 KB of base64; frames may use more room than JSON lines.
    private const int MaximumFrameLineBytes = 16384;
    private const int MaximumScriptBytes = 64 * 1024;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<IReadOnlyList<StackChanDevice>> _discover;
    private readonly Func<string, CancellationToken, Task<IStackChanUsbEndpoint>> _open;
    private readonly TimeSpan _connectTimeout;
    private LinkSession? _connection;
    private CancellationTokenSource? _operation;
    private bool _disposed;

    public StackChanUsbTransport()
        : this(QueryUsbPorts, OpenConnectionAsync)
    {
    }

    internal StackChanUsbTransport(
        Func<IReadOnlyList<StackChanDevice>> discover,
        Func<string, CancellationToken, Task<IStackChanUsbEndpoint>> open,
        TimeSpan? connectTimeout = null)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    public event Action? Disconnected;

    public async Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Discovery duration must be positive.");

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(duration < TimeSpan.FromSeconds(4) ? duration : TimeSpan.FromSeconds(4));
            var query = Task.Run(_discover, CancellationToken.None);
            try
            {
                return await query.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObserveLate(query);
                throw;
            }
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    public Task<StackChanCapabilities> ConnectAsync(StackChanDevice device, CancellationToken cancellationToken) =>
        ConnectCoreAsync(device, script: null, cancellationToken);

    public Task<StackChanCapabilities> StartCompanionAsync(
        StackChanDevice device,
        byte[] script,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Length == 0 || script.Length > MaximumScriptBytes)
            throw new ArgumentOutOfRangeException(nameof(script), "Companion source must contain 1 to 65536 bytes.");
        if (Array.IndexOf(script, (byte)0x04) >= 0)
            throw new ArgumentException("Companion source cannot contain the raw-REPL execute byte.", nameof(script));
        return ConnectCoreAsync(device, (byte[])script.Clone(), cancellationToken);
    }

    public async Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        ThrowIfDisposed();
        var requestId = NewRequestId();
        var envelope = BuildCommandEnvelope(requestId, json);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LinkSession connection;
            CancellationTokenSource operation;
            lock (_stateGate)
            {
                ThrowIfDisposedLocked();
                connection = _connection ?? throw new InvalidOperationException("StackChan USB is not connected.");
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                _operation = operation;
            }

            using (operation)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
            {
                timeout.CancelAfter(CommandTimeout);
                var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Pending[requestId] = response;
                try
                {
                    await connection.Endpoint.WriteAsync(envelope, timeout.Token).ConfigureAwait(false);
                    await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    await RetireConnectionAsync(connection, raiseEvent: true).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    connection.Pending.TryRemove(requestId, out _);
                    lock (_stateGate)
                    {
                        if (ReferenceEquals(_operation, operation))
                            _operation = null;
                    }
                }
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (IsDisposed())
            return;
        CancelActiveOperation();
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            LinkSession? connection;
            lock (_stateGate)
            {
                connection = _connection;
                _connection = null;
            }
            if (connection is not null)
            {
                await connection.CloseAsync().ConfigureAwait(false);
                RaiseDisconnected();
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public void Dispose()
    {
        LinkSession? connection;
        CancellationTokenSource? operation;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            connection = _connection;
            operation = _operation;
            _connection = null;
            _operation = null;
        }
        operation?.Cancel();
        connection?.Stop();
        _lifetime.Dispose();
    }

    private async Task<StackChanCapabilities> ConnectCoreAsync(
        StackChanDevice device,
        byte[]? script,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ValidateUsbDevice(device);
        ThrowIfDisposed();
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LinkSession? previous;
            lock (_stateGate)
            {
                previous = _connection;
                _connection = null;
            }
            if (previous is not null)
            {
                await previous.CloseAsync().ConfigureAwait(false);
                RaiseDisconnected();
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(script is null ? _connectTimeout : StartTimeout);
            lock (_stateGate)
            {
                ThrowIfDisposedLocked();
                _operation = operation;
            }

            IStackChanUsbEndpoint? opened = null;
            try
            {
                opened = await _open(device.PortName, timeout.Token).ConfigureAwait(false);
                if (script is not null)
                    await StartScriptAsync(opened, script, timeout.Token).ConfigureAwait(false);
                var capabilities = await VerifyHelloAsync(opened, timeout.Token, script is not null).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                var session = new LinkSession(opened);
                lock (_stateGate)
                {
                    ThrowIfDisposedLocked();
                    _connection = session;
                }
                opened = null;
                // From here one reader owns every byte: correlated replies and camera frames share the stream.
                session.Reader = Task.Run(() => ReadLoopAsync(session), CancellationToken.None);
                return capabilities;
            }
            catch (OperationCanceledException) when (!operation.IsCancellationRequested)
            {
                if (script is null)
                    throw new StackChanUsbException(StackChanUsbFailure.NoCompanion,
                        "StackChan accepted the hello but no companion answered.");
                throw new TimeoutException("Timed out while starting the StackChan USB companion.");
            }
            finally
            {
                try
                {
                    if (opened is not null)
                        await opened.DisposeAndSettleAsync().ConfigureAwait(false);
                }
                finally
                {
                    lock (_stateGate)
                    {
                        if (ReferenceEquals(_operation, operation))
                            _operation = null;
                    }
                }
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private static async Task StartScriptAsync(IStackChanUsbEndpoint connection, byte[] script, CancellationToken token)
    {
        await connection.WriteAsync(new byte[] { 0x03, 0x03 }, token).ConfigureAwait(false);
        await Task.Delay(100, token).ConfigureAwait(false);
        await connection.WriteAsync(new byte[] { 0x01 }, token).ConfigureAwait(false);
        await WaitForTextAsync(connection, "raw REPL; CTRL-B to exit\r\n>", token).ConfigureAwait(false);
        await connection.WriteAsync(new byte[] { 0x05, (byte)'A', 0x01 }, token).ConfigureAwait(false);
        var response0 = await connection.ReadByteAsync(token).ConfigureAwait(false);
        var response1 = await connection.ReadByteAsync(token).ConfigureAwait(false);
        if (response0 != (byte)'R')
            throw new InvalidOperationException("StackChan returned an invalid raw-paste response.");

        if (response1 == 0x01)
        {
            var windowLow = await connection.ReadByteAsync(token).ConfigureAwait(false);
            var windowHigh = await connection.ReadByteAsync(token).ConfigureAwait(false);
            var window = windowLow | (windowHigh << 8);
            if (window <= 0)
                throw new InvalidOperationException("StackChan returned an invalid raw-paste window.");
            await WriteRawPasteAsync(connection, script, window, token).ConfigureAwait(false);
            await connection.WriteAsync(new byte[] { 0x04 }, token).ConfigureAwait(false);
            await WaitForByteAsync(connection, 0x04, token).ConfigureAwait(false);
        }
        else if (response1 == 0x00)
        {
            await WriteNormalRawAsync(connection, script, token).ConfigureAwait(false);
            await connection.WriteAsync(new byte[] { 0x04 }, token).ConfigureAwait(false);
            await WaitForTextAsync(connection, "OK", token).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("StackChan rejected raw-paste negotiation.");
        }

        await WaitForReadyAsync(connection, token).ConfigureAwait(false);
    }

    private static async Task WriteNormalRawAsync(IStackChanUsbEndpoint connection, byte[] script, CancellationToken token)
    {
        for (var offset = 0; offset < script.Length; offset += 256)
        {
            var length = Math.Min(256, script.Length - offset);
            var chunk = new byte[length];
            System.Buffer.BlockCopy(script, offset, chunk, 0, length);
            await connection.WriteAsync(chunk, token).ConfigureAwait(false);
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private static async Task WriteRawPasteAsync(
        IStackChanUsbEndpoint connection,
        byte[] script,
        int window,
        CancellationToken token)
    {
        var offset = 0;
        var available = window;
        while (offset < script.Length)
        {
            if (available == 0)
            {
                var control = await connection.ReadByteAsync(token).ConfigureAwait(false);
                if (control == 0x04)
                    throw new InvalidOperationException("StackChan ended raw-paste transfer before receiving the script.");
                if (control != 0x01)
                    continue;
                available = window;
            }

            var length = Math.Min(Math.Min(available, 256), script.Length - offset);
            var chunk = new byte[length];
            System.Buffer.BlockCopy(script, offset, chunk, 0, length);
            await connection.WriteAsync(chunk, token).ConfigureAwait(false);
            offset += length;
            available -= length;
        }
    }

    private static async Task WaitForByteAsync(IStackChanUsbEndpoint connection, int expected, CancellationToken token)
    {
        while (true)
        {
            var value = await connection.ReadByteAsync(token).ConfigureAwait(false);
            if (value == expected)
                return;
        }
    }

    private static async Task WaitForReadyAsync(IStackChanUsbEndpoint connection, CancellationToken token)
    {
        const string ready = "MicaStatsStackChan USB ready";
        var line = new List<byte>(128);
        while (true)
        {
            var value = await connection.ReadByteAsync(token).ConfigureAwait(false);
            if (value == 0x04)
                throw new InvalidOperationException("StackChan companion exited before becoming ready.");
            if (value != (byte)'\n')
            {
                if (line.Count < MaximumWireBytes)
                    line.Add((byte)value);
                continue;
            }

            var text = Encoding.UTF8.GetString(line.ToArray()).Trim('\r', '\0', ' ');
            line.Clear();
            if (string.Equals(text, ready, StringComparison.Ordinal) ||
                string.Equals(text, "OK" + ready, StringComparison.Ordinal))
                return;
            if (LooksLikeStartupError(text))
                throw new InvalidOperationException($"StackChan companion startup failed: {Limit(text, 240)}");
        }
    }

    private static async Task<StackChanCapabilities> VerifyHelloAsync(
        IStackChanUsbEndpoint connection,
        CancellationToken token,
        bool failOnStartupErrors)
    {
        var requestId = NewRequestId();
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            companionHello = 1,
            requestId
        });
        await connection.WriteAsync(AppendLineFeed(request), token).ConfigureAwait(false);
        var response = await WaitForResponseAsync(connection, requestId, ResponseKind.Hello, failOnStartupErrors, token)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        // A companion must explicitly attest that both stored zeros exist.
        // Legacy/malformed responses retain screen, face and LED controls.
        return document.RootElement.TryGetProperty("motionCalibrated", out var calibrated) &&
               calibrated.ValueKind == JsonValueKind.True
            ? AllCapabilities : AllCapabilities & ~StackChanCapabilities.Motion;
    }

    private static async Task<string> WaitForResponseAsync(
        IStackChanUsbEndpoint connection,
        string requestId,
        ResponseKind expected,
        bool failOnStartupErrors,
        CancellationToken token)
    {
        var line = new List<byte>(256);
        var dropping = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var value = await connection.ReadByteAsync(token).ConfigureAwait(false);
            if (value == (byte)'\n')
            {
                if (!dropping && line.Count > 0)
                {
                    var text = ReplyPart(Encoding.UTF8.GetString(line.ToArray()).Trim('\r', '\0', '\x04', ' '));
                    if (TryMatchResponse(text, requestId, expected, out var rejection))
                        return text;
                    if (rejection is not null)
                        throw new InvalidOperationException(rejection);
                    if (failOnStartupErrors && LooksLikeStartupError(text))
                        throw new InvalidOperationException($"StackChan companion startup failed: {Limit(text, 240)}");
                }
                line.Clear();
                dropping = false;
                continue;
            }
            if (dropping)
                continue;
            if (line.Count >= LineLimit(line))
            {
                line.Clear();
                dropping = true;
                continue;
            }
            line.Add((byte)value);
        }
    }

    /// <summary>Picture lines may be long, and may carry a reply glued after a cut-off picture.</summary>
    private static int LineLimit(List<byte> line) =>
        line.Count > 0 && line[0] == (byte)'F' ? MaximumFrameLineBytes + MaximumWireBytes : MaximumWireBytes;

    /// <summary>
    /// A port closed mid-picture leaves a picture line without its end; the next reply then
    /// follows it on the same line. Base64 never contains '{', so the reply starts at the first one.
    /// </summary>
    private static string ReplyPart(string text)
    {
        if (text.Length == 0 || text[0] == '{') return text;
        int start = text.IndexOf("{\"", StringComparison.Ordinal);
        return start > 0 ? text[start..] : text;
    }

    private static bool TryMatchResponse(string line, string requestId, ResponseKind expected, out string? rejection)
    {
        rejection = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("requestId", out var id) ||
                id.ValueKind != JsonValueKind.String || id.GetString() != requestId)
                return false;

            var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : null;
            if (!ok)
            {
                var error = root.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String
                    ? errorValue.GetString()
                    : "command rejected";
                rejection = $"StackChan USB rejected the request: {Limit(error ?? "command rejected", 160)}";
                return false;
            }

            if (expected == ResponseKind.Applied)
            {
                if (string.Equals(type, "applied", StringComparison.Ordinal))
                    return true;
                if (!string.IsNullOrEmpty(type))
                    rejection = $"StackChan USB rejected the request: {Limit(type, 160)}";
                return false;
            }

            return string.Equals(type, "hello", StringComparison.Ordinal) &&
                root.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.String &&
                device.GetString() == "MicaStatsStackChan" &&
                root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number &&
                protocol.TryGetInt32(out var version) && version == 1 &&
                root.TryGetProperty("transport", out var transport) && transport.ValueKind == JsonValueKind.String &&
                transport.GetString() == "usb";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task WaitForTextAsync(IStackChanUsbEndpoint connection, string expected, CancellationToken token)
    {
        var wanted = Encoding.ASCII.GetBytes(expected);
        var matched = 0;
        while (matched < wanted.Length)
        {
            token.ThrowIfCancellationRequested();
            var value = await connection.ReadByteAsync(token).ConfigureAwait(false);
            matched = value == wanted[matched] ? matched + 1 : value == wanted[0] ? 1 : 0;
        }
    }

    private static byte[] BuildCommandEnvelope(string requestId, byte[] json)
    {
        JsonElement command;
        try
        {
            using var source = JsonDocument.Parse(json);
            if (source.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("StackChan command JSON must be an object.", nameof(json));
            command = source.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("StackChan command JSON is invalid.", nameof(json), error);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", requestId);
            writer.WritePropertyName("command");
            command.WriteTo(writer);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumWireBytes)
            throw new ArgumentException($"StackChan USB command envelope cannot exceed {MaximumWireBytes} bytes.", nameof(json));
        return AppendLineFeed(payload);
    }

    private static byte[] AppendLineFeed(byte[] value)
    {
        var result = new byte[value.Length + 1];
        System.Buffer.BlockCopy(value, 0, result, 0, value.Length);
        result[^1] = (byte)'\n';
        return result;
    }

    private static async Task<IStackChanUsbEndpoint> OpenConnectionAsync(string portName, CancellationToken token)
    {
        var task = Task.Run(() => PortConnection.Open(portName), CancellationToken.None);
        try
        {
            return await task.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            _ = task.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                    completed.Result.Dispose();
                else
                    _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private async Task RetireConnectionAsync(LinkSession connection, bool raiseEvent)
    {
        var owned = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                owned = true;
            }
        }
        await connection.CloseAsync().ConfigureAwait(false);
        if (owned && raiseEvent)
            RaiseDisconnected();
    }

    private async Task ReadLoopAsync(LinkSession session)
    {
        var line = new List<byte>(256);
        var dropping = false;
        try
        {
            while (true)
            {
                var value = await session.Endpoint.ReadByteAsync(session.Stopping).ConfigureAwait(false);
                if (value == (byte)'\n')
                {
                    if (!dropping && line.Count > 0)
                        DispatchLine(session, line.ToArray());
                    line.Clear();
                    dropping = false;
                    continue;
                }
                if (dropping)
                    continue;
                if (line.Count >= LineLimit(line))
                {
                    line.Clear();
                    dropping = true;
                    continue;
                }
                line.Add((byte)value);
            }
        }
        catch (Exception) when (session.Stopping.IsCancellationRequested)
        {
            // Closed by Disconnect, a retired command or disposal.
        }
        catch (Exception)
        {
            await RetireConnectionAsync(session, raiseEvent: true).ConfigureAwait(false);
        }
    }

    private void DispatchLine(LinkSession session, byte[] bytes)
    {
        var text = ReplyPart(Encoding.UTF8.GetString(bytes).Trim('\r', '\0', '\x04', ' '));
        if (text.Length > 0 && text[0] == 'F')
        {
            if (TryParseFrame(text, out var frame) && !session.Stopping.IsCancellationRequested)
                RaiseFrame(frame);
            return;
        }

        foreach (var pending in session.Pending)
        {
            if (TryMatchResponse(text, pending.Key, ResponseKind.Applied, out var rejection))
                pending.Value.TrySetResult(text);
            else if (rejection is not null)
                pending.Value.TrySetException(new InvalidOperationException(rejection));
        }
    }

    internal static bool TryParseFrame(string line, out StackChanFrame frame)
    {
        frame = null!;
        var colon = line.IndexOf(':');
        if (colon < 2 ||
            !int.TryParse(line.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence > 65535)
            return false;
        var payload = line.AsSpan(colon + 1);
        var jpeg = new byte[payload.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64Chars(payload, jpeg, out var written) || written == 0)
            return false;
        frame = new StackChanFrame(sequence, jpeg[..written], DateTimeOffset.UtcNow);
        return true;
    }

    private void RaiseFrame(StackChanFrame frame)
    {
        var handlers = FrameReceived;
        if (handlers is null)
            return;
        foreach (Action<StackChanFrame> handler in handlers.GetInvocationList())
        {
            try { handler(frame); }
            catch { }
        }
    }

    private void CancelActiveOperation()
    {
        lock (_stateGate)
            _operation?.Cancel();
    }

    private static IReadOnlyList<StackChanDevice> QueryUsbPorts()
    {
        var portsDeviceClass = PortsDeviceClass;
        var deviceInfoSet = SetupDiGetClassDevsW(
            ref portsDeviceClass,
            null,
            IntPtr.Zero,
            DigcfPresent);
        if (deviceInfoSet == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate USB serial ports.");

        try
        {
            var devices = new List<StackChanDevice>();
            for (uint index = 0; index < MaximumPortDevices; index++)
            {
                var deviceInfo = new SpDevinfoData
                {
                    Size = (uint)Marshal.SizeOf<SpDevinfoData>()
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        break;
                    throw new Win32Exception(error, "Cannot enumerate a USB serial port.");
                }

                var instanceId = new StringBuilder(MaximumDeviceInstanceIdCharacters);
                if (!SetupDiGetDeviceInstanceIdW(
                        deviceInfoSet,
                        ref deviceInfo,
                        instanceId,
                        (uint)instanceId.Capacity,
                        out _))
                    continue;

                var pnpId = instanceId.ToString();
                if (!IsEspressifCompanionUsb(pnpId))
                    continue;

                var registryHandle = SetupDiOpenDevRegKey(
                    deviceInfoSet,
                    ref deviceInfo,
                    DicsFlagGlobal,
                    0,
                    DiregDev,
                    KeyRead);
                if (registryHandle == InvalidHandleValue)
                    continue;

                string port;
                using (var safeRegistryHandle = new SafeRegistryHandle(registryHandle, ownsHandle: true))
                using (var registryKey = RegistryKey.FromHandle(safeRegistryHandle))
                    port = registryKey.GetValue("PortName") as string ?? "";
                if (!IsComPortName(port))
                    continue;

                var address = TryGetParentUsbAddress(pnpId);
                devices.Add(new StackChanDevice(
                    address,
                    "MicaStats StackChan USB",
                    StackChanFirmware.UiFlow2,
                    StackChanConnectionKind.Usb,
                    port.ToUpperInvariant()));
            }

            return devices.OrderBy(device => device.PortName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static bool IsEspressifCompanionUsb(string pnpId) =>
        pnpId.Contains("VID_303A&PID_1001", StringComparison.OrdinalIgnoreCase) ||
        pnpId.Contains("VID_303A&PID_4001", StringComparison.OrdinalIgnoreCase) ||
        pnpId.Contains("VID_303A&PID_811A", StringComparison.OrdinalIgnoreCase);

    private static ulong TryGetParentUsbAddress(string pnpId)
    {
        uint devInst = 0;
        if (CM_Locate_DevNodeW(ref devInst, pnpId, 0) != 0)
            return 0;
        for (var depth = 0; depth < 8; depth++)
        {
            var id = new StringBuilder(512);
            if (CM_Get_Device_IDW(devInst, id, id.Capacity, 0) == 0 &&
                TryParseMacFromDeviceId(id.ToString(), out var address))
                return address;
            if (CM_Get_Parent(out devInst, devInst, 0) != 0)
                break;
        }
        return 0;
    }

    internal static bool TryParseMacFromDeviceId(string deviceId, out ulong address)
    {
        address = 0;
        var separator = deviceId.LastIndexOf('\\');
        var value = separator >= 0 ? deviceId[(separator + 1)..] : deviceId;
        value = value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (deviceId.Contains("VID_303A&PID_811A", StringComparison.OrdinalIgnoreCase) &&
            value.Length == 16 && value.EndsWith("0000", StringComparison.Ordinal))
            value = value[..12];
        return value.Length == 12 && ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static void ValidateUsbDevice(StackChanDevice device)
    {
        if (device.Connection != StackChanConnectionKind.Usb)
            throw new ArgumentException("The selected device is not a USB StackChan companion.", nameof(device));
        if (!IsComPortName(device.PortName))
            throw new ArgumentException("StackChan USB requires a valid COM port name.", nameof(device));
    }

    private static bool IsComPortName(string value)
    {
        if (value.Length is < 4 or > 6 || !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(value.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    }

    internal static string FormatPortPath(string portName)
    {
        if (!IsComPortName(portName))
            throw new ArgumentException("A valid COM port name is required.", nameof(portName));
        return string.Concat((char)92, (char)92, '.', (char)92, portName.ToUpperInvariant());
    }

    private static bool LooksLikeStartupError(string value) =>
        value.Contains("Traceback", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("MemoryError", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Exception", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string NewRequestId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private static StackChanCapabilities AllCapabilities => StackChanCapabilities.Avatar |
        StackChanCapabilities.Rgb | StackChanCapabilities.Motion | StackChanCapabilities.Dashboard;

    private void RaiseDisconnected()
    {
        var handlers = Disconnected;
        if (handlers is null)
            return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { }
        }
    }

    private bool IsDisposed()
    {
        lock (_stateGate) return _disposed;
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate) ThrowIfDisposedLocked();
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ObserveLate(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private enum ResponseKind { Hello, Applied }

    /// <summary>A verified link: the endpoint plus the one reader that owns its incoming bytes.</summary>
    private sealed class LinkSession(IStackChanUsbEndpoint endpoint)
    {
        private readonly CancellationTokenSource _stop = new();

        public IStackChanUsbEndpoint Endpoint { get; } = endpoint;
        public ConcurrentDictionary<string, TaskCompletionSource<string>> Pending { get; } = new();
        public Task? Reader { get; set; }
        public CancellationToken Stopping => _stop.Token;

        public void Stop()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var pending in Pending.Values)
                pending.TrySetCanceled();
            Endpoint.Dispose();
        }

        public async Task CloseAsync()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var pending in Pending.Values)
                pending.TrySetCanceled();
            await Endpoint.DisposeAndSettleAsync().ConfigureAwait(false);
        }
    }

    private sealed class PortConnection : IStackChanUsbEndpoint
    {
        private readonly object _gate = new();
        private readonly FileStream _stream;
        private readonly Queue<byte> _pending = new();
        private readonly byte[] _readBuffer = new byte[256];
        // The reader loop and command writes run concurrently; each direction has one worker.
        private Task? _activeRead;
        private Task? _activeWrite;
        private bool _disposed;

        private PortConnection(FileStream stream) => _stream = stream;

        public static PortConnection Open(string portName)
        {
            var path = FormatPortPath(portName);
            var handle = CreateFileW(path, GenericRead | GenericWrite, 0, IntPtr.Zero,
                OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                var inner = new Win32Exception(error);
                throw error switch
                {
                    ErrorFileNotFound or ErrorPathNotFound => new StackChanUsbException(
                        StackChanUsbFailure.PortMissing, $"{portName} is not present.", inner),
                    ErrorAccessDenied or ErrorSharingViolation => new StackChanUsbException(
                        StackChanUsbFailure.PortBusy, $"{portName} is open in another program.", inner),
                    _ => new IOException($"Cannot open {portName}.", inner)
                };
            }
            try
            {
                ConfigurePort(handle);
                return new PortConnection(new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public async Task<int> ReadByteAsync(CancellationToken token)
        {
            if (_pending.Count > 0)
                return _pending.Dequeue();
            while (true)
            {
                var read = await RunWorkerAsync(
                    () => _stream.Read(_readBuffer, 0, _readBuffer.Length), token, reading: true).ConfigureAwait(false);
                for (var index = 0; index < read; index++)
                    _pending.Enqueue(_readBuffer[index]);
                if (_pending.Count > 0)
                    return _pending.Dequeue();
                token.ThrowIfCancellationRequested();
            }
        }

        public Task WriteAsync(byte[] bytes, CancellationToken token) => RunWorkerAsync(() =>
        {
            try { _stream.Write(bytes, 0, bytes.Length); }
            catch (IOException error) when (error.HResult == SemaphoreTimeoutHResult)
            {
                // The robot stopped draining USB input, so even Ctrl-C cannot reach it.
                throw new StackChanUsbException(StackChanUsbFailure.NotAccepting,
                    "StackChan is not accepting USB data.", error);
            }
            return 0;
        }, token, reading: false);

        private async Task<T> RunWorkerAsync<T>(Func<T> work, CancellationToken token, bool reading)
        {
            Task<T> worker;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var active = reading ? _activeRead : _activeWrite;
                if (active is { IsCompleted: false })
                    throw new InvalidOperationException("A serial operation is already active.");
                worker = Task.Run(work, CancellationToken.None);
                if (reading) _activeRead = worker; else _activeWrite = worker;
            }
            try
            {
                return await worker.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (worker.IsCompleted)
                    {
                        if (ReferenceEquals(_activeRead, worker)) _activeRead = null;
                        if (ReferenceEquals(_activeWrite, worker)) _activeWrite = null;
                    }
                }
            }
        }

        public async Task DisposeAndSettleAsync()
        {
            Task?[] workers;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    CloseStream();
                }
                workers = [_activeRead, _activeWrite];
            }
            foreach (var worker in workers)
            {
                if (worker is null) continue;
                try { await worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { _ = worker.Exception; }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CloseStream();
            }
        }

        private void CloseStream()
        {
            try { _stream.Dispose(); }
            catch (IOException) { }
        }

        private static void ConfigurePort(SafeFileHandle handle)
        {
            var dcb = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
            // MicroPython's TinyUSB console drops output while the host has not raised DTR
            // (2026-10-17 spike: merged, truncated lines). DTR on with RTS off does not reset it.
            if (!BuildCommDCBW("baud=115200 parity=N data=8 stop=1 dtr=on rts=off", ref dcb))
                ThrowWin32("Cannot build StackChan serial settings.");
            dcb.Flags |= 1;
            dcb.Flags &= ~0x3030u;
            dcb.Flags |= DtrControlEnable;
            if (!SetCommState(handle, ref dcb))
                ThrowWin32("Cannot configure StackChan serial settings.");
            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = 50,
                ReadTotalTimeoutConstant = 50,
                ReadTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 2000,
                WriteTotalTimeoutMultiplier = 0
            };
            if (!SetCommTimeouts(handle, ref timeouts))
                ThrowWin32("Cannot configure StackChan serial timeouts.");
            if (!EscapeCommFunction(handle, SetDtr) || !EscapeCommFunction(handle, ClearRts))
                ThrowWin32("Cannot set StackChan DTR/RTS lines.");
        }

        private static void ThrowWin32(string message) =>
            throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint ClearRts = 4;
    private const uint SetDtr = 5;
    private const uint DtrControlEnable = 0x10;
    private const uint DigcfPresent = 0x00000002;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const uint KeyRead = 0x00020019;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int SemaphoreTimeoutHResult = unchecked((int)0x80070079);
    private const uint MaximumPortDevices = 4096;
    private const int MaximumDeviceInstanceIdCharacters = 512;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Guid PortsDeviceClass = new("4d36e978-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLimit;
        public ushort XoffLimit;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EventChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "BuildCommDCBW", ExactSpelling = true)]
    private static extern bool BuildCommDCBW(string definition, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle handle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle handle, uint function);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint scope,
        uint hardwareProfile,
        uint keyType,
        uint desiredAccess);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(ref uint deviceInstance, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parentDeviceInstance, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint deviceInstance, StringBuilder buffer, int bufferLength, uint flags);
}
