using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Application = System.Windows.Application;

namespace Kil0bitSystemMonitor.Services.StackChan;

/// <summary>
/// Native Windows BLE transport for the control service exposed by StackChan's
/// factory DANCE application.
/// </summary>
public sealed class StackChanBleTransport : IStackChanTransport
{
    private static readonly Guid ControlServiceUuid = Guid.Parse("e2e5e5e0-1234-5678-1234-56789abcdef0");
    private static readonly Guid NordicUartServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    private static readonly Guid NordicUartRxUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    private const string UiFlowCompanionName = "MicaStatsStackChan";
    private static readonly IReadOnlyDictionary<StackChanChannel, Guid> CharacteristicUuids =
        new Dictionary<StackChanChannel, Guid>
        {
            [StackChanChannel.Motion] = Guid.Parse("e2e5e5e1-1234-5678-1234-56789abcdef0"),
            [StackChanChannel.Avatar] = Guid.Parse("e2e5e5e2-1234-5678-1234-56789abcdef0"),
            [StackChanChannel.Rgb] = Guid.Parse("e2e5e5e4-1234-5678-1234-56789abcdef0")
        };

    private static readonly TimeSpan MaximumDiscoveryDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dispatcher? _dispatcher;

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private IReadOnlyDictionary<StackChanChannel, GattCharacteristic> _characteristics =
        new Dictionary<StackChanChannel, GattCharacteristic>();
    private CancellationTokenSource? _connectionAttempt;
    private CancellationTokenSource? _session;
    private StackChanFirmware _firmware = StackChanFirmware.Factory;
    private long _generation;
    private bool _disposed;

    public StackChanBleTransport(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
    }

    public event Action? Disconnected;

    public async Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Discovery duration must be positive.");

        duration = duration > MaximumDiscoveryDuration ? MaximumDiscoveryDuration : duration;
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var observations = new ConcurrentDictionary<ulong, DiscoveryObservation>();
            var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                var advertisedName = args.Advertisement.LocalName;
                var advertisesFactoryService = args.Advertisement.ServiceUuids.Contains(ControlServiceUuid);
                observations.AddOrUpdate(
                    args.BluetoothAddress,
                    _ => new DiscoveryObservation(
                        advertisedName,
                        advertisesFactoryService,
                        string.Equals(advertisedName, UiFlowCompanionName, StringComparison.Ordinal)),
                    (_, current) => current.Merge(advertisedName, advertisesFactoryService));
            }

            void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args) =>
                stopped.TrySetResult(args.Error);

            watcher.Received += OnReceived;
            watcher.Stopped += OnStopped;
            try
            {
                watcher.Start();
                var delay = Task.Delay(duration, linked.Token);
                var completed = await Task.WhenAny(delay, stopped.Task).ConfigureAwait(false);
                if (completed == delay)
                {
                    await delay.ConfigureAwait(false);
                }
                else
                {
                    var error = await stopped.Task.ConfigureAwait(false);
                    if (error != BluetoothError.Success)
                        throw new IOException($"Bluetooth discovery stopped: {error}.");
                }
            }
            finally
            {
                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    watcher.Stop();
                }

                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
            }

            linked.Token.ThrowIfCancellationRequested();
            return observations
                .Select(pair => ToDiscoveredDevice(pair.Key, pair.Value))
                .Where(device => device is not null)
                .Select(device => device!)
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.Address)
                .ToArray();
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    public async Task<StackChanCapabilities> ConnectAsync(
        StackChanDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DisconnectCurrent(raiseEvent: true);

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            timeout.CancelAfter(ConnectTimeout);
            var token = timeout.Token;
            BluetoothLEDevice? openedDevice = null;
            GattDeviceService? openedService = null;

            try
            {
                lock (_stateGate)
                {
                    ThrowIfDisposedLocked();
                    _connectionAttempt = attempt;
                }

                openedDevice = await OpenDeviceAsync(device.Address, token).ConfigureAwait(false)
                    ?? throw new IOException("Windows could not open the selected Bluetooth device.");

                var serviceUuid = device.Firmware == StackChanFirmware.UiFlow2
                    ? NordicUartServiceUuid
                    : ControlServiceUuid;
                var services = await GetServicesAsync(openedDevice, serviceUuid, token).ConfigureAwait(false);
                if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                {
                    foreach (var service in services.Services)
                        service.Dispose();
                    throw new IOException(ServiceFailureMessage(services.Status, device.Firmware));
                }

                openedService = services.Services[0];
                foreach (var extraService in services.Services.Skip(1))
                    extraService.Dispose();

                Dictionary<StackChanChannel, GattCharacteristic> characteristics;
                StackChanCapabilities capabilities;
                if (device.Firmware == StackChanFirmware.UiFlow2)
                {
                    var result = await openedService
                        .GetCharacteristicsForUuidAsync(NordicUartRxUuid, BluetoothCacheMode.Uncached)
                        .AsTask(token)
                        .ConfigureAwait(false);
                    var rx = result.Status == GattCommunicationStatus.Success
                        ? result.Characteristics.FirstOrDefault(candidate =>
                            candidate.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
                        : null;
                    if (rx is null)
                        throw new IOException("The MicaStats StackChan companion does not expose a writable Nordic UART RX characteristic.");

                    characteristics = new Dictionary<StackChanChannel, GattCharacteristic>
                    {
                        [StackChanChannel.Avatar] = rx,
                        [StackChanChannel.Rgb] = rx,
                        [StackChanChannel.Motion] = rx,
                        [StackChanChannel.Dashboard] = rx
                    };
                    capabilities = StackChanCapabilities.Avatar |
                        StackChanCapabilities.Rgb |
                        StackChanCapabilities.Motion |
                        StackChanCapabilities.Dashboard;
                }
                else
                {
                    characteristics = new Dictionary<StackChanChannel, GattCharacteristic>();
                    capabilities = StackChanCapabilities.None;
                    foreach (var pair in CharacteristicUuids)
                    {
                        var result = await openedService
                            .GetCharacteristicsForUuidAsync(pair.Value, BluetoothCacheMode.Uncached)
                            .AsTask(token)
                            .ConfigureAwait(false);
                        if (result.Status != GattCommunicationStatus.Success)
                            continue;

                        var characteristic = result.Characteristics.FirstOrDefault(candidate =>
                            candidate.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));
                        if (characteristic is null)
                            continue;

                        characteristics[pair.Key] = characteristic;
                        capabilities |= CapabilityFor(pair.Key);
                    }

                    if (capabilities == StackChanCapabilities.None)
                        throw new IOException("The StackChan control service has no writable face, RGB, or motion characteristics.");
                }

                token.ThrowIfCancellationRequested();
                bool connected;
                lock (_stateGate)
                {
                    ThrowIfDisposedLocked();
                    _generation++;
                    _device = openedDevice;
                    _service = openedService;
                    _characteristics = characteristics;
                    _firmware = device.Firmware;
                    _session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    openedDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
                    connected = openedDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }

                openedDevice = null;
                openedService = null;
                if (!connected)
                {
                    DisconnectCurrent(raiseEvent: true);
                    throw new IOException("StackChan disconnected while its control service was being opened.");
                }

                return capabilities;
            }
            catch (OperationCanceledException) when (!attempt.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out while connecting to StackChan.");
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_connectionAttempt, attempt))
                        _connectionAttempt = null;
                }

                openedService?.Dispose();
                openedDevice?.Dispose();
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        ThrowIfDisposed();

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GattCharacteristic characteristic;
            CancellationTokenSource operation;
            IReadOnlyList<byte[]> frames;
            long generation;
            lock (_stateGate)
            {
                ThrowIfDisposedLocked();
                if (!_characteristics.TryGetValue(channel, out characteristic!))
                    throw new InvalidOperationException($"StackChan does not expose a writable {channel} characteristic.");

                var sessionToken = _session?.Token
                    ?? throw new InvalidOperationException("StackChan is not connected.");
                frames = _firmware == StackChanFirmware.UiFlow2
                    ? FrameUiFlow(json)
                    : StackChanProtocol.Fragment(json);
                generation = _generation;
                operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    sessionToken,
                    _lifetime.Token);
            }

            using var operationLifetime = operation;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(WriteTimeout);
            var writeStarted = false;

            try
            {
                foreach (var frame in frames)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    EnsureCurrentSession(generation);
                    using var writer = new DataWriter();
                    writer.WriteBytes(frame);
                    writeStarted = true;
                    var status = await characteristic
                        .WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
                        .AsTask(timeout.Token)
                        .ConfigureAwait(false);
                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"StackChan {channel} write failed: {status}.");
                }

                timeout.Token.ThrowIfCancellationRequested();
                EnsureCurrentSession(generation);
            }
            catch (OperationCanceledException) when (!operation.IsCancellationRequested)
            {
                if (writeStarted)
                    DisconnectCurrent(raiseEvent: true);
                throw new TimeoutException($"Timed out while writing StackChan {channel} data.");
            }
            catch
            {
                // A canceled or failed fragment sequence may still be completing in
                // WinRT. Retire its GATT session before another command can begin.
                if (writeStarted)
                    DisconnectCurrent(raiseEvent: true);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (IsDisposed())
            return;

        CancelActiveOperation();
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisconnectCurrent(raiseEvent: true);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public void Dispose()
    {
        BluetoothLEDevice? device;
        GattDeviceService? service;
        CancellationTokenSource? connectionAttempt;
        CancellationTokenSource? session;
        lock (_stateGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _lifetime.Cancel();
            _generation++;
            device = _device;
            service = _service;
            connectionAttempt = _connectionAttempt;
            session = _session;
            _device = null;
            _service = null;
            _connectionAttempt = null;
            _session = null;
            _firmware = StackChanFirmware.Factory;
            _characteristics = new Dictionary<StackChanChannel, GattCharacteristic>();
            if (device is not null)
                device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }

        connectionAttempt?.Cancel();
        session?.Cancel();
        session?.Dispose();
        service?.Dispose();
        device?.Dispose();
        _lifetime.Dispose();
    }

    private async Task<BluetoothLEDevice?> OpenDeviceAsync(ulong address, CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return await OpenDeviceOnCurrentThreadAsync(address, cancellationToken).ConfigureAwait(false);

        var dispatched = dispatcher.InvokeAsync(() =>
            OpenDeviceOnCurrentThreadAsync(address, cancellationToken));
        try
        {
            var openTask = await dispatched.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await openTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            dispatched.Abort();
            throw;
        }
    }

    private static async Task<BluetoothLEDevice?> OpenDeviceOnCurrentThreadAsync(
        ulong address,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = BluetoothLEDevice.FromBluetoothAddressAsync(address);
        var task = operation.AsTask();
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        completed.Result?.Dispose();
                    else
                        _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private static async Task<GattDeviceServicesResult> GetServicesAsync(
        BluetoothLEDevice device,
        Guid serviceUuid,
        CancellationToken cancellationToken)
    {
        var operation = device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
        var task = operation.AsTask();
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        foreach (var lateService in completed.Result.Services)
                            lateService.Dispose();
                    }
                    else
                    {
                        _ = completed.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
            return;

        BluetoothLEDevice? device;
        GattDeviceService? service;
        CancellationTokenSource? session;
        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _device))
                return;

            ClearConnectionLocked(out device, out service, out session);
        }

        session?.Cancel();
        session?.Dispose();
        service?.Dispose();
        device?.Dispose();
        RaiseDisconnected();
    }

    private void DisconnectCurrent(bool raiseEvent)
    {
        BluetoothLEDevice? device;
        GattDeviceService? service;
        CancellationTokenSource? session;
        lock (_stateGate)
        {
            ClearConnectionLocked(out device, out service, out session);
        }

        var hadConnection = device is not null || service is not null || session is not null;
        session?.Cancel();
        session?.Dispose();
        service?.Dispose();
        device?.Dispose();
        if (hadConnection && raiseEvent)
            RaiseDisconnected();
    }

    private void ClearConnectionLocked(
        out BluetoothLEDevice? device,
        out GattDeviceService? service,
        out CancellationTokenSource? session)
    {
        _generation++;
        device = _device;
        service = _service;
        session = _session;
        _device = null;
        _service = null;
        _session = null;
        _firmware = StackChanFirmware.Factory;
        _characteristics = new Dictionary<StackChanChannel, GattCharacteristic>();
        if (device is not null)
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
    }

    private void EnsureCurrentSession(long generation)
    {
        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            if (_generation != generation || _device is null || _session is null)
                throw new IOException("The StackChan connection ended before the command completed.");
        }
    }

    private void CancelActiveOperation()
    {
        lock (_stateGate)
        {
            _connectionAttempt?.Cancel();
            _session?.Cancel();
        }
    }

    private void RaiseDisconnected()
    {
        var handlers = Disconnected;
        if (handlers is null)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
                // A consumer callback must not tear down a WinRT event thread.
            }
        }
    }

    private static string DeviceName(string? advertisedName, ulong address) =>
        string.IsNullOrWhiteSpace(advertisedName) ? $"StackChan {address:X12}" : advertisedName.Trim();

    internal static StackChanDevice? ToDiscoveredDevice(ulong address, DiscoveryObservation observation)
    {
        if (observation.AdvertisesFactoryService)
            return new StackChanDevice(address, DeviceName(observation.Name, address), StackChanFirmware.Factory);
        if (observation.AdvertisesUiFlowCompanionName)
            return new StackChanDevice(address, UiFlowCompanionName, StackChanFirmware.UiFlow2);
        return null;
    }

    internal static IReadOnlyList<byte[]> FrameUiFlow(byte[] json)
    {
        if (json.Length > StackChanProtocol.MaxJsonBytes)
        {
            throw new ArgumentException(
                $"StackChan JSON cannot exceed {StackChanProtocol.MaxJsonBytes} UTF-8 bytes.",
                nameof(json));
        }

        var line = new byte[json.Length + 1];
        System.Buffer.BlockCopy(json, 0, line, 0, json.Length);
        line[^1] = (byte)'\n';

        const int frameBytes = 20;
        var frames = new byte[(line.Length + frameBytes - 1) / frameBytes][];
        for (var index = 0; index < frames.Length; index++)
        {
            var offset = index * frameBytes;
            var length = Math.Min(frameBytes, line.Length - offset);
            var frame = new byte[length];
            System.Buffer.BlockCopy(line, offset, frame, 0, length);
            frames[index] = frame;
        }

        return frames;
    }

    private static StackChanCapabilities CapabilityFor(StackChanChannel channel) => channel switch
    {
        StackChanChannel.Avatar => StackChanCapabilities.Avatar,
        StackChanChannel.Rgb => StackChanCapabilities.Rgb,
        StackChanChannel.Motion => StackChanCapabilities.Motion,
        _ => StackChanCapabilities.None
    };

    private static string ServiceFailureMessage(GattCommunicationStatus status, StackChanFirmware firmware) => status switch
    {
        GattCommunicationStatus.Success => firmware == StackChanFirmware.UiFlow2
            ? "The named device does not expose the MicaStats StackChan companion service. Restart the supplied UIFlow2 companion script."
            : "StackChan's factory control service is unavailable. Open the robot's DANCE application and try again.",
        GattCommunicationStatus.AccessDenied =>
            "Bluetooth access was denied. Allow Bluetooth access in Windows Settings and try again.",
        GattCommunicationStatus.Unreachable =>
            firmware == StackChanFirmware.UiFlow2
                ? "The MicaStats StackChan companion is unreachable. Keep the robot nearby and restart its UIFlow2 companion script."
                : "StackChan's control service is unreachable. Keep the robot nearby and open its DANCE application.",
        _ => $"StackChan control service discovery failed: {status}."
    };

    internal sealed record DiscoveryObservation(
        string? Name,
        bool AdvertisesFactoryService,
        bool AdvertisesUiFlowCompanionName)
    {
        public DiscoveryObservation Merge(string? name, bool advertisesFactoryService) => new(
            string.IsNullOrWhiteSpace(name) ? Name : name,
            AdvertisesFactoryService || advertisesFactoryService,
            AdvertisesUiFlowCompanionName ||
                string.Equals(name, UiFlowCompanionName, StringComparison.Ordinal));
    }

    private bool IsDisposed()
    {
        lock (_stateGate)
            return _disposed;
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate)
            ThrowIfDisposedLocked();
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
