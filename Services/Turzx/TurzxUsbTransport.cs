using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Kil0bitSystemMonitor.Services.Turzx;

public interface ITurzxTransport : IDisposable
{
    event Action? Disconnected;
    Task<IReadOnlyList<TurzxDevice>> DiscoverAsync(CancellationToken token);
    Task ConnectAsync(TurzxDevice device, CancellationToken token);
    Task WriteAsync(byte[] payload, CancellationToken token);
    Task WriteRectAsync(ushort[] frame, int frameWidth, TurzxRect rect, CancellationToken token);
    Task DisconnectAsync();
}

internal interface ITurzxSerialEndpoint : IDisposable
{
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token);
    Task DisposeAndSettleAsync();
}

public sealed class TurzxUsbTransport : ITurzxTransport
{
    private readonly Func<IReadOnlyList<TurzxDevice>> _enumeratePorts;
    private readonly Func<string, CancellationToken, Task<ITurzxSerialEndpoint>> _openPort;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _stateGate = new();
    private ITurzxSerialEndpoint? _endpoint;
    private bool _disposed;

    public TurzxUsbTransport()
        : this(QueryUsbPorts, OpenConnectionAsync)
    {
    }

    internal TurzxUsbTransport(
        Func<IReadOnlyList<TurzxDevice>> enumeratePorts,
        Func<string, CancellationToken, Task<ITurzxSerialEndpoint>> openPort)
    {
        _enumeratePorts = enumeratePorts ?? throw new ArgumentNullException(nameof(enumeratePorts));
        _openPort = openPort ?? throw new ArgumentNullException(nameof(openPort));
    }

    public event Action? Disconnected;

    public Task<IReadOnlyList<TurzxDevice>> DiscoverAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return _enumeratePorts();
        }, token);
    }

    public async Task ConnectAsync(TurzxDevice device, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsComPortName(device.PortName))
            throw new ArgumentException("Turzx USB requires a valid COM port name.", nameof(device));
        ThrowIfDisposed();

        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_endpoint != null)
                await RetireEndpointCoreAsync(_endpoint, raiseEvent: false).ConfigureAwait(false);

            var endpoint = await _openPort(device.PortName, token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (_disposed)
                {
                    endpoint.Dispose();
                    throw new ObjectDisposedException(nameof(TurzxUsbTransport));
                }
                _endpoint = endpoint;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task WriteAsync(byte[] payload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0) return;
        ThrowIfDisposed();

        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var endpoint = GetConnectedEndpoint();
            try
            {
                await endpoint.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or Win32Exception)
            {
                await RetireEndpointCoreAsync(endpoint, raiseEvent: true).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task WriteRectAsync(ushort[] frame, int frameWidth, TurzxRect rect, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        ThrowIfDisposed();

        byte[] header = TurzxProtocol.BuildBitmapHeader(rect);
        byte[] pixels = TurzxProtocol.SliceSubRectBytes(frame, frameWidth, rect);
        int chunkSize = Math.Max(1024, frameWidth * 8);

        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var endpoint = GetConnectedEndpoint();
            try
            {
                await endpoint.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                int offset = 0;
                while (offset < pixels.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(chunkSize, pixels.Length - offset);
                    await endpoint.WriteAsync(pixels, offset, count, token).ConfigureAwait(false);
                    offset += count;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or Win32Exception)
            {
                await RetireEndpointCoreAsync(endpoint, raiseEvent: true).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        ITurzxSerialEndpoint? current;
        lock (_stateGate)
        {
            current = _endpoint;
            _endpoint = null;
        }
        if (current != null)
            await current.DisposeAndSettleAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        ITurzxSerialEndpoint? current;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            current = _endpoint;
            _endpoint = null;
        }
        current?.Dispose();
    }

    internal static bool IsTurzxUsb(string pnpId)
    {
        if (string.IsNullOrWhiteSpace(pnpId)) return false;
        return pnpId.Contains("VID_1A86&PID_5722", StringComparison.OrdinalIgnoreCase) ||
               pnpId.Contains("USB35INCHIPSV2", StringComparison.OrdinalIgnoreCase) ||
               pnpId.Contains("VID_1A86&PID_7523", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsComPortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 4 or > 6 ||
            !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(value.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0;
    }

    internal static string FormatPortPath(string portName)
    {
        if (!IsComPortName(portName))
            throw new ArgumentException("A valid COM port name is required.", nameof(portName));
        return string.Concat((char)92, (char)92, '.', (char)92, portName.Trim().ToUpperInvariant());
    }

    private ITurzxSerialEndpoint GetConnectedEndpoint()
    {
        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            return _endpoint ?? throw new InvalidOperationException("Turzx USB display is not connected.");
        }
    }

    private async Task RetireEndpointCoreAsync(ITurzxSerialEndpoint endpoint, bool raiseEvent)
    {
        bool owned = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_endpoint, endpoint))
            {
                _endpoint = null;
                owned = true;
            }
        }
        await endpoint.DisposeAndSettleAsync().ConfigureAwait(false);
        if (owned && raiseEvent)
            RaiseDisconnected();
    }

    private void RaiseDisconnected()
    {
        Action? handlers = Disconnected;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate) ThrowIfDisposedLocked();
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static IReadOnlyList<TurzxDevice> QueryUsbPorts()
    {
        Guid portsClass = PortsDeviceClass;
        IntPtr deviceInfoSet = SetupDiGetClassDevsW(ref portsClass, null, IntPtr.Zero, DigcfPresent);
        if (deviceInfoSet == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate USB serial ports.");

        try
        {
            var devices = new List<TurzxDevice>();
            for (uint index = 0; index < MaximumPortDevices; index++)
            {
                var deviceInfo = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
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

                string pnpId = instanceId.ToString();
                if (!IsTurzxUsb(pnpId))
                    continue;

                IntPtr registryHandle = SetupDiOpenDevRegKey(
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
                    port = registryKey.GetValue("PortName") as string ?? string.Empty;
                if (!IsComPortName(port))
                    continue;

                string label = pnpId.Contains("USB35INCHIPSV2", StringComparison.OrdinalIgnoreCase) ||
                               pnpId.Contains("VID_1A86&PID_5722", StringComparison.OrdinalIgnoreCase)
                    ? "Turzx 3.5\" IPS Display"
                    : "Turzx USB Display";
                devices.Add(new TurzxDevice(port.ToUpperInvariant(), label, pnpId));
            }

            return devices.OrderBy(d => d.PortName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static async Task<ITurzxSerialEndpoint> OpenConnectionAsync(string portName, CancellationToken token)
    {
        var task = Task.Run<ITurzxSerialEndpoint>(() => PortConnection.Open(portName), CancellationToken.None);
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

    private sealed class PortConnection : ITurzxSerialEndpoint
    {
        private readonly object _gate = new();
        private readonly FileStream _stream;
        private Task? _activeWorker;
        private bool _disposed;

        private PortConnection(FileStream stream) => _stream = stream;

        public static PortConnection Open(string portName)
        {
            string path = FormatPortPath(portName);
            SafeFileHandle handle = CreateFileW(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException($"Cannot open {portName}.", new Win32Exception(Marshal.GetLastWin32Error()));
            try
            {
                ConfigurePort(handle);
                return new PortConnection(new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            RunWorkerAsync(() =>
            {
                _stream.Write(buffer, offset, count);
                _stream.Flush();
                // Ensure 6-byte/16-byte command packets and the end of bitmap streams never coalesce
                // into the same 64-byte USB Full-Speed bulk packet on the CH552T (1ms SOF interval).
                if (count <= 16 || count < 3840)
                    Thread.Sleep(2);
                return 0;
            }, token);

        private async Task<T> RunWorkerAsync<T>(Func<T> work, CancellationToken token)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_activeWorker is { IsCompleted: false })
                    throw new InvalidOperationException("A Turzx serial operation is already active.");
                _activeWorker = Task.Run(work, CancellationToken.None);
            }
            var worker = (Task<T>)_activeWorker;
            try
            {
                return await worker.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeWorker, worker) && worker.IsCompleted)
                        _activeWorker = null;
                }
            }
        }

        public async Task DisposeAndSettleAsync()
        {
            Task? worker;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    CloseStream();
                }
                worker = _activeWorker;
            }
            if (worker != null)
            {
                try { await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
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
            if (!BuildCommDCBW("baud=115200 parity=N data=8 stop=1 dtr=on rts=on", ref dcb))
                ThrowWin32("Cannot build Turzx serial settings.");
            dcb.Flags |= 1; // fBinary
            if (!SetCommState(handle, ref dcb))
                ThrowWin32("Cannot configure Turzx serial settings.");
            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = 50,
                ReadTotalTimeoutConstant = 100,
                ReadTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 2500,
                WriteTotalTimeoutMultiplier = 0,
            };
            if (!SetCommTimeouts(handle, ref timeouts))
                ThrowWin32("Cannot configure Turzx serial timeouts.");
            _ = EscapeCommFunction(handle, SetDtr);
            _ = EscapeCommFunction(handle, SetRts);
            _ = PurgeComm(handle, PurgeRxClear | PurgeTxClear);
        }

        private static void ThrowWin32(string message) =>
            throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint SetRts = 3;
    private const uint SetDtr = 5;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxClear = 0x0008;
    private const uint DigcfPresent = 0x00000002;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const uint KeyRead = 0x00020019;
    private const int ErrorNoMoreItems = 259;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle handle, uint flags);

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
}
