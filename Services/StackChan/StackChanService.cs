using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.StackChan;

/// <summary>App-owned robot companion. One bounded writer prevents stale or interleaved commands.</summary>
public sealed class StackChanService : IDisposable
{
    private readonly object _gate = new();
    private readonly AppConfig _config;
    private readonly IStackChanTransport _transport;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _session;
    private CancellationTokenSource? _gesture;
    private Settings _settings;
    private StackChanSnapshot _snapshot = new(StackChanConnectionState.Disconnected, "",
        StackChanCapabilities.None, "Disconnected");
    private DateTimeOffset _alertUntil, _manualUntil, _nextUpdate, _nextReconnect;
    private DateTimeOffset _nextAutomaticGesture;
    private StackChanDashboardAlert? _dashboardAlert;
    private static readonly TimeSpan ManualWriterWait = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AutoStartRetry = TimeSpan.FromMinutes(2);
    private PresenceReaction _presence;
    private bool _headTracking;
    private string? _lastReaction;
    private bool _highLoad, _manualDisconnect, _disposed, _operationAutomatic, _automaticGestureArmed,
        _alertDashboardPendingClear, _automaticUsbPaused;
    private float _cpu = -1, _ram = -1, _gpu = -1;
    private int _generation, _settingsGeneration;

    public StackChanService(AppConfig config, IStackChanTransport transport, TimeProvider? clock = null,
        bool startBackground = true)
    {
        _config = config;
        _transport = transport;
        _clock = clock ?? TimeProvider.System;
        _settings = ReadSettings();
        _transport.Disconnected += OnDisconnected;
        CompanionActivity.Changed += OnActivityChanged;
        if (startBackground) _ = Task.Run(RunAsync);
    }

    public event Action? Changed;
    public StackChanSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public PresenceReaction Presence { get { lock (_gate) return _presence; } }

    public void SetPresence(PresenceReaction reaction)
    {
        lock (_gate)
        {
            if (_presence == reaction) return;
            _presence = reaction;
            _nextUpdate = default;
        }
    }

    /// <summary>Best-effort presence command (camera, gaze, scene); skipped while another command is in flight.</summary>
    public Task<bool> SendPresenceAsync(byte[] json, CancellationToken cancellationToken = default) =>
        SendAsync([(StackChanChannel.Dashboard, json)], true, cancellationToken);

    /// <summary>Automatic head aim; refused in Quiet mode or without "Allow automatic head movement".</summary>
    public Task<bool> AimHeadAsync(double yawDegrees, double pitchDegrees, CancellationToken cancellationToken = default)
    {
        // A manual head position holds for 30 s; automatic aim waits it out.
        lock (_gate) { if (_clock.GetUtcNow() < _manualUntil) return Task.FromResult(false); }
        return SendAsync([(StackChanChannel.Motion, StackChanProtocol.Motion(yawDegrees, pitchDegrees, 100))], true, cancellationToken);
    }

    /// <summary>
    /// A small calibration move for the head direction check: it waits its turn like a person's
    /// command, but sets no 30 s manual hold, so following can start right after the check.
    /// </summary>
    public async Task NudgeHeadAsync(double yawDegrees, double pitchDegrees, CancellationToken cancellationToken = default) =>
        await SendAsync([(StackChanChannel.Motion, StackChanProtocol.Motion(yawDegrees, pitchDegrees, 100))], false,
            cancellationToken).ConfigureAwait(false);

    /// <summary>While presence aims the head, the occasional automatic gestures stay out of its way.</summary>
    public void SetHeadTracking(bool tracking)
    {
        lock (_gate) _headTracking = tracking;
    }

    public void RefreshSettings()
    {
        CancellationTokenSource? gesture;
        CancellationTokenSource? operation = null;
        bool disable;
        lock (_gate)
        {
            if (_disposed) return;
            var next = ReadSettings();
            disable = (_settings.Enabled && !next.Enabled) || _settings.UseUsb != next.UseUsb;
            if (disable || (_operationAutomatic && !next.Reconnect)) operation = _operation;
            if ((!_settings.Enabled && next.Enabled) || (!_settings.Reconnect && next.Reconnect))
            {
                _manualDisconnect = false;
                _automaticUsbPaused = false;
            }
            _settings = next;
            _settingsGeneration++;
            _lastReaction = null;
            _manualUntil = default;
            _nextUpdate = default;
            gesture = _gesture;
        }
        CancelSafely(gesture);
        CancelSafely(operation);
        if (disable) _ = DisconnectAfterDisableAsync();
        Publish();
    }

    private async Task DisconnectAfterDisableAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); }
        catch (Exception) { /* The disconnected state is already published; disposal remains available. */ }
    }

    public void ObserveMetrics(SystemMetrics metrics)
    {
        lock (_gate)
        {
            _cpu = metrics.CpuUsage;
            _ram = metrics.RamPercent;
            _gpu = metrics.GpuUsage;
            _highLoad = _cpu >= 90 || _ram >= 90 || _gpu >= 90;
        }
    }

    public void NotifyAlert(AlertEvent? alert = null)
    {
        lock (_gate)
        {
            _alertUntil = _clock.GetUtcNow().AddSeconds(20);
            _dashboardAlert = alert == null ? null : ToDashboardAlert(alert);
            _nextUpdate = default;
        }
    }

    private void OnActivityChanged()
    {
        lock (_gate) _nextUpdate = default;
    }

    public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        DiscoverCoreAsync(cancellationToken, false);

    /// <summary>
    /// Lists StackChan USB ports without touching the connection state or status text, so Settings
    /// can pre-select the remembered robot. A quick SetupAPI lookup; never opens a port.
    /// </summary>
    public Task<IReadOnlyList<StackChanDevice>> FindUsbPortsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_settings.Enabled || !_settings.UseUsb)
                return Task.FromResult<IReadOnlyList<StackChanDevice>>(Array.Empty<StackChanDevice>());
        }
        return _transport.DiscoverAsync(TimeSpan.FromSeconds(4), cancellationToken);
    }

    private async Task<IReadOnlyList<StackChanDevice>> DiscoverCoreAsync(CancellationToken cancellationToken, bool automatic)
    {
        using var operation = BeginOperation(cancellationToken, automatic);
        bool acquired = false;
        try
        {
            await _lifecycle.WaitAsync(operation.Token).ConfigureAwait(false);
            acquired = true;
            operation.Token.ThrowIfCancellationRequested();
            bool usb;
            lock (_gate) usb = _settings.UseUsb;
            Update(s => s with { State = StackChanConnectionState.Discovering,
                Status = usb ? "Finding USB serial ports" : "Scanning for StackChan", Error = null });
            var devices = await _transport.DiscoverAsync(TimeSpan.FromSeconds(8), operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            Update(s => s with { State = s.Capabilities != StackChanCapabilities.None && HasLiveSession()
                    ? StackChanConnectionState.Connected : StackChanConnectionState.Disconnected,
                Status = devices.Count == 0
                    ? usb ? "No USB robot found. Connect the USB cable and find ports again."
                        : "No StackChan found. Open DANCE or run the UIFlow companion, then scan again."
                    : "Scan complete" });
            return devices;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(operation))
                Update(s => s with { State = s.Capabilities != StackChanCapabilities.None && HasLiveSession()
                    ? StackChanConnectionState.Connected : StackChanConnectionState.Disconnected, Status = "Scan cancelled" });
            throw;
        }
        catch (Exception)
        {
            if (IsCurrentOperation(operation))
                Update(s => s with { State = s.Capabilities != StackChanCapabilities.None && HasLiveSession()
                    ? StackChanConnectionState.Connected : StackChanConnectionState.Faulted,
                    Error = _config.StackChanUseUsb
                        ? "USB discovery failed. Check the cable and USB serial driver."
                        : "Bluetooth discovery failed. Enable Bluetooth and open DANCE or run the UIFlow companion.", Status = "Scan failed" });
            throw;
        }
        finally { EndOperation(operation); if (acquired) _lifecycle.Release(); }
    }

    public Task ConnectAsync(StackChanDevice device, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(device, cancellationToken, false);

    public Task StartUsbCompanionAsync(StackChanDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Connection != StackChanConnectionKind.Usb || device.Firmware != StackChanFirmware.UiFlow2)
            throw new InvalidOperationException("Select a UIFlow USB device first.");
        if (_transport is not IStackChanUsbCompanionStarter)
            throw new InvalidOperationException("USB companion startup is unavailable in this build.");
        return ConnectCoreAsync(device, cancellationToken, false, startUsbCompanion: true);
    }

    private async Task ConnectCoreAsync(StackChanDevice device, CancellationToken cancellationToken, bool automatic,
        bool startUsbCompanion = false)
    {
        using var operation = BeginOperation(cancellationToken, automatic);
        InvalidateSession();
        if (!automatic) lock (_gate) _automaticUsbPaused = false;
        bool acquired = false;
        try
        {
            await _lifecycle.WaitAsync(operation.Token).ConfigureAwait(false);
            acquired = true;
            operation.Token.ThrowIfCancellationRequested();
            await _transport.DisconnectAsync().ConfigureAwait(false);
            Update(_ => new(StackChanConnectionState.Connecting, device.Name, StackChanCapabilities.None,
                startUsbCompanion ? "Starting USB companion" : "Connecting", Firmware: device.Firmware,
                Connection: device.Connection));
            CancellationTokenSource provisional;
            lock (_gate)
            {
                operation.Token.ThrowIfCancellationRequested();
                provisional = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, operation.Token);
                _session = provisional;
            }
            StackChanCapabilities capabilities;
            if (startUsbCompanion)
            {
                byte[] script = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
                    "firmware", "uiflow2", "micastats_companion.py"), operation.Token).ConfigureAwait(false);
                capabilities = await ((IStackChanUsbCompanionStarter)_transport)
                    .StartCompanionAsync(device, script, operation.Token).ConfigureAwait(false);
            }
            else capabilities = await _transport.ConnectAsync(device, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (capabilities == StackChanCapabilities.None)
                throw new InvalidOperationException("No compatible controls.");
            lock (_gate)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_session, provisional) || provisional.IsCancellationRequested)
                    throw new OperationCanceledException("The robot disconnected during connection.", operation.Token);
                _session = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                provisional.Dispose();
                _manualDisconnect = false;
                _lastReaction = null;
                _nextUpdate = default;
            }
            _config.StackChanDeviceAddress = device.AddressText;
            _config.StackChanDeviceName = device.Name;
            RefreshSettings();
            lock (_gate)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (!HasLiveSession()) throw new OperationCanceledException("The robot disconnected during connection.", operation.Token);
                _snapshot = new(StackChanConnectionState.Connected, device.Name, capabilities, "Connected",
                    Firmware: device.Firmware, Connection: device.Connection);
            }
            Publish();
        }
        catch (OperationCanceledException)
        {
            if (acquired && IsCurrentOperation(operation)) InvalidateSession();
            if (acquired) await _transport.DisconnectAsync().ConfigureAwait(false);
            if (IsCurrentOperation(operation))
                Update(_ => new(StackChanConnectionState.Disconnected, device.Name, StackChanCapabilities.None,
                    "Connection cancelled", Firmware: device.Firmware, Connection: device.Connection));
            throw;
        }
        catch (Exception ex)
        {
            if (acquired && IsCurrentOperation(operation)) InvalidateSession();
            if (acquired) await _transport.DisconnectAsync().ConfigureAwait(false);
            // Retrying into a robot that is not reading only fills its USB input further: pause, or with
            // automatic companion start, retry only every two minutes (a power cycle clears the robot).
            bool robotSilent = automatic && ex is StackChanUsbException
                { Failure: StackChanUsbFailure.NotAccepting or StackChanUsbFailure.NoCompanion };
            bool autoStart;
            lock (_gate) autoStart = _settings.AutoStart && _settings.UseUsb;
            bool pause = robotSilent && !autoStart;
            if (pause) lock (_gate) _automaticUsbPaused = true;
            else if (robotSilent || (automatic && startUsbCompanion))
                lock (_gate) _nextReconnect = _clock.GetUtcNow() + AutoStartRetry;
            var (status, error) = DescribeConnectFailure(device, ex, startUsbCompanion, pause);
            if (IsCurrentOperation(operation))
                Update(_ => new(StackChanConnectionState.Faulted, device.Name, StackChanCapabilities.None, status, error,
                    Firmware: device.Firmware, Connection: device.Connection));
            throw;
        }
        finally { EndOperation(operation); if (acquired) _lifecycle.Release(); }
    }

    internal static (string Status, string Error) DescribeConnectFailure(StackChanDevice device, Exception error,
        bool startingCompanion, bool automaticPaused) => error switch
    {
        StackChanUsbException { Failure: StackChanUsbFailure.PortMissing } => ("USB port not found",
            "Windows cannot find the StackChan USB port. Check the USB cable, then choose Find USB ports."),
        StackChanUsbException { Failure: StackChanUsbFailure.PortBusy } => ("USB port in use",
            "Another program has the StackChan USB port open. Close UIFlow, Thonny, Arduino or other serial tools, then try again."),
        StackChanUsbException { Failure: StackChanUsbFailure.NotAccepting } => ("Robot not accepting USB data", automaticPaused
            ? "StackChan is plugged in but not reading USB data, usually after the robot restarts. Automatic reconnect is paused. Turn StackChan off and on with its power button, then choose Start temporary USB companion."
            : "StackChan is plugged in but not reading USB data, usually after the robot restarts. Turn StackChan off and on with its power button, then choose Start temporary USB companion."),
        StackChanUsbException { Failure: StackChanUsbFailure.NoCompanion } => ("Companion not running", automaticPaused
            ? "StackChan is reachable, but the MicaStats companion is not running. It lives only in robot memory and stops when StackChan restarts. Automatic reconnect is paused. Choose Start temporary USB companion."
            : "StackChan is reachable, but the MicaStats companion is not running. It lives only in robot memory and stops when StackChan restarts. Choose Start temporary USB companion."),
        _ when device.Connection == StackChanConnectionKind.Usb && startingCompanion => ("Companion start failed",
            "StackChan did not start the companion. Confirm UIFlow 2.0 for StackChan is installed, turn the robot off and on, then try again."),
        _ => ("Connection failed", device.Connection == StackChanConnectionKind.Usb
            ? "Cannot connect by USB. Confirm UIFlow 2.0 for StackChan is active, close other serial tools, then start the USB companion."
            : device.Firmware == StackChanFirmware.UiFlow2
            ? "Cannot connect to the UIFlow companion. Run the supplied script and disconnect other controller apps."
            : "Cannot connect to the factory control service. Keep DANCE open and disconnect other controller apps.")
    };

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? operation;
        lock (_gate) { _manualDisconnect = true; operation = _operation; }
        CancelSafely(operation);
        InvalidateSession();
        Update(s => s with { State = StackChanConnectionState.Disconnected, Capabilities = StackChanCapabilities.None,
            Status = "Disconnected", Error = null });
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await _transport.DisconnectAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public Task SetFaceAsync(StackChanFace face, CancellationToken cancellationToken = default) =>
        ManualAsync(StackChanChannel.Avatar, StackChanProtocol.Avatar(face), cancellationToken);
    public Task SetAvatarAsync(StackChanAvatar avatar, CancellationToken cancellationToken = default) =>
        ManualAsync(StackChanChannel.Avatar, StackChanProtocol.Avatar(avatar), cancellationToken);
    public Task SetRgbAsync(string left, string right, double durationSeconds = 0.3, CancellationToken cancellationToken = default)
    {
        Settings settings;
        lock (_gate) settings = _settings;
        return ManualAsync(StackChanChannel.Rgb, StackChanProtocol.Rgb(left, right,
            settings.Quiet ? Math.Min(settings.Brightness, 5) : settings.Brightness, durationSeconds), cancellationToken);
    }
    public Task MoveAsync(double yawDegrees, double pitchDegrees, int speed = 150, CancellationToken cancellationToken = default) =>
        ManualAsync(StackChanChannel.Motion, StackChanProtocol.Motion(yawDegrees, pitchDegrees, speed), cancellationToken);

    private async Task ManualAsync(StackChanChannel channel, byte[] json, CancellationToken token)
    {
        CancelGesture();
        lock (_gate)
        {
            _manualUntil = _clock.GetUtcNow().AddSeconds(30);
            if (channel is StackChanChannel.Avatar or StackChanChannel.Rgb)
                _lastReaction = null;
            if (_nextUpdate > _manualUntil) _nextUpdate = _manualUntil;
        }
        await SendAsync([(channel, json)], false, token).ConfigureAwait(false);
        UpdateConnectedStatus("Command sent");
    }

    public Task GestureAsync(StackChanGesture gesture, CancellationToken cancellationToken = default) =>
        RunGestureAsync(gesture, false, cancellationToken);

    public void CancelGesture()
    {
        CancellationTokenSource? gesture;
        lock (_gate) gesture = _gesture;
        CancelSafely(gesture);
    }

    private async Task RunGestureAsync(StackChanGesture gesture, bool automatic, CancellationToken token)
    {
        CancellationTokenSource cancellation;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_settings.Quiet || (automatic && !_settings.AutoMotion))
                throw new InvalidOperationException("Head movement is disabled in quiet mode.");
            previous = _gesture;
            cancellation = CreateSessionToken(token);
            _gesture = cancellation;
            if (!automatic) _manualUntil = _clock.GetUtcNow().AddSeconds(30);
        }
        CancelSafely(previous);
        try
        {
            (double Yaw, double Pitch)[] steps = gesture switch
            {
                StackChanGesture.Nod when automatic => [(0, 40), (0, 50), (0, 45)],
                StackChanGesture.Shake when automatic => [(-12, 45), (12, 45), (0, 45)],
                StackChanGesture.LookAround when automatic => [(-12, 45), (12, 45), (0, 45)],
                StackChanGesture.Nod => [(0, 35), (0, 55), (0, 45)],
                StackChanGesture.Shake => [(-20, 45), (20, 45), (0, 45)],
                StackChanGesture.LookAround => [(-30, 45), (30, 45), (0, 45)],
                _ => throw new ArgumentOutOfRangeException(nameof(gesture))
            };
            foreach (var step in steps)
            {
                bool sent = await SendAsync([(StackChanChannel.Motion,
                    StackChanProtocol.Motion(step.Yaw, step.Pitch, automatic ? 100 : 150))], automatic,
                    cancellation.Token).ConfigureAwait(false);
                if (!sent) return;
                await Task.Delay(TimeSpan.FromMilliseconds(700), _clock, cancellation.Token).ConfigureAwait(false);
            }
            if (!automatic) UpdateConnectedStatus("Gesture sent");
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_gesture, cancellation)) _gesture = null; }
            cancellation.Dispose();
        }
    }

    private async Task<bool> SendAsync((StackChanChannel Channel, byte[] Json)[] commands, bool automatic,
        CancellationToken token, int? settingsGeneration = null)
    {
        using var cancellation = CreateSessionToken(token);
        int generation;
        lock (_gate) generation = _generation;
        // Automatic traffic never queues; a person's command waits briefly for one in flight.
        if (!await _writer.WaitAsync(automatic ? TimeSpan.Zero : ManualWriterWait, cancellation.Token).ConfigureAwait(false))
        {
            if (automatic) return false;
            throw new InvalidOperationException("A robot command is already being sent. Try again shortly.");
        }
        try
        {
            foreach (var command in commands)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (generation != _generation) throw new OperationCanceledException(cancellation.Token);
                    if (!_settings.Enabled) throw new InvalidOperationException("Enable StackChan companion first.");
                    if (automatic && (!_settings.Enabled || (settingsGeneration.HasValue && settingsGeneration != _settingsGeneration)))
                        return false;
                    if (command.Channel == StackChanChannel.Motion && (_settings.Quiet || (automatic && !_settings.AutoMotion)))
                        throw new InvalidOperationException("Head movement is disabled in quiet mode.");
                    var capability = Capability(command.Channel);
                    if ((_snapshot.Capabilities & capability) == 0)
                        throw new InvalidOperationException("This device does not expose the requested control.");
                }
                await _transport.WriteAsync(command.Channel, command.Json, cancellation.Token).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ex is not InvalidOperationException)
            {
                InvalidateSession();
                Update(s => s with { State = StackChanConnectionState.Faulted, Capabilities = StackChanCapabilities.None,
                    Status = "Command failed", Error = s.Connection == StackChanConnectionKind.Usb
                        ? "USB command failed. Check the cable and reconnect to the running companion."
                        : s.Firmware == StackChanFirmware.UiFlow2
                        ? "Bluetooth command failed. Restart the UIFlow companion script and reconnect."
                        : "Bluetooth command failed. Reconnect with DANCE open." });
            }
            throw;
        }
        finally { _writer.Release(); }
    }

    private CancellationTokenSource CreateSessionToken(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session == null || _session.IsCancellationRequested)
                throw new InvalidOperationException("Connect to StackChan first.");
            return CancellationTokenSource.CreateLinkedTokenSource(token, _session.Token, _shutdown.Token);
        }
    }

    internal async Task TickAsync(CancellationToken token = default)
    {
        Settings settings;
        StackChanSnapshot snapshot;
        int settingsGeneration;
        DateTimeOffset now = _clock.GetUtcNow();
        bool reconnect;
        lock (_gate)
        {
            settings = _settings;
            settingsGeneration = _settingsGeneration;
            snapshot = _snapshot;
            reconnect = settings.Enabled && settings.Reconnect && !_manualDisconnect &&
                !(settings.UseUsb && _automaticUsbPaused) &&
                _operation == null && (snapshot.State is StackChanConnectionState.Disconnected or StackChanConnectionState.Faulted) &&
                now >= _nextReconnect;
            if (reconnect) _nextReconnect = now.AddSeconds(20);
        }
        if (reconnect && !string.IsNullOrWhiteSpace(settings.Address) &&
            settings.UseUsb == settings.Address.StartsWith("USB:", StringComparison.OrdinalIgnoreCase))
        {
            var devices = await DiscoverCoreAsync(token, true).ConfigureAwait(false);
            foreach (var device in devices)
            {
                if (!string.Equals(device.AddressText, settings.Address, StringComparison.OrdinalIgnoreCase)) continue;
                try { await ConnectCoreAsync(device, token, true).ConfigureAwait(false); }
                catch (StackChanUsbException error) when (error.Failure == StackChanUsbFailure.NoCompanion &&
                                                          settings.AutoStart && settings.UseUsb)
                {
                    // The robot restarted and lost its in-memory companion: load it again (RAM only).
                    await ConnectCoreAsync(device, token, true, startUsbCompanion: true).ConfigureAwait(false);
                }
                break;
            }
            return;
        }
        if (!settings.Enabled || snapshot.State != StackChanConnectionState.Connected) return;
        Reaction reaction;
        bool reactionChanged;
        bool activityChanged;
        bool realAlertActive;
        bool alertWindowActive;
        bool alertClearDue;
        bool manualDashboardOnly;
        StackChanDashboardAlert? dashboardAlert;
        float cpu, ram, gpu;
        lock (_gate)
        {
            alertWindowActive = settings.Alerts && now < _alertUntil;
            realAlertActive = alertWindowActive && _dashboardAlert != null;
            alertClearDue = _alertDashboardPendingClear && !alertWindowActive;
            bool manualHoldActive = now < _manualUntil;
            // Onlooker, welcome and break outrank a manual face/LED hold; doze does not.
            bool presenceOverridesHold = _presence is PresenceReaction.Onlooker or PresenceReaction.Welcome or PresenceReaction.Break;
            manualDashboardOnly = manualHoldActive && (realAlertActive || alertClearDue) && !presenceOverridesHold;
            if ((now < _manualUntil && !realAlertActive && !alertClearDue && !presenceOverridesHold) ||
                (now < _nextUpdate && !alertClearDue)) return;
            DateTimeOffset intervalUpdate = now.AddSeconds(settings.Interval);
            _nextUpdate = alertWindowActive && _alertUntil < intervalUpdate
                ? _alertUntil : intervalUpdate;
            if (manualDashboardOnly && _manualUntil < _nextUpdate)
                _nextUpdate = _manualUntil;
            reaction = ChooseReaction(settings, now);
            reactionChanged = reaction.Key != _lastReaction;
            activityChanged = reaction.Activity != snapshot.Activity;
            if (!reactionChanged && !snapshot.Capabilities.HasFlag(StackChanCapabilities.Dashboard)) return;
            cpu = _cpu; ram = _ram; gpu = _gpu;
            dashboardAlert = settings.Alerts && now < _alertUntil ? _dashboardAlert : null;
        }
        var commands = new List<(StackChanChannel, byte[])>();
        if (!manualDashboardOnly && reactionChanged && snapshot.Capabilities.HasFlag(StackChanCapabilities.Avatar)) commands.Add((StackChanChannel.Avatar, StackChanProtocol.Avatar(reaction.Face)));
        if (!manualDashboardOnly && reactionChanged && snapshot.Capabilities.HasFlag(StackChanCapabilities.Rgb)) commands.Add((StackChanChannel.Rgb, StackChanProtocol.Rgb(reaction.Color, reaction.Color, reaction.Brightness)));
        if (snapshot.Capabilities.HasFlag(StackChanCapabilities.Dashboard)) commands.Add((StackChanChannel.Dashboard,
            StackChanProtocol.Dashboard(cpu, ram, gpu, reaction.Activity, dashboardAlert,
                ambient: now >= _manualUntil)));
        if (commands.Count == 0) return;
        if (!await SendAsync(commands.ToArray(), true, token, settingsGeneration).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_settingsGeneration == settingsGeneration) _nextUpdate = default;
            }
            return;
        }
        StackChanGesture? automaticGesture = null;
        lock (_gate)
        {
            if (_settingsGeneration != settingsGeneration) return;
            if (!manualDashboardOnly) _lastReaction = reaction.Key;
            if (snapshot.Capabilities.HasFlag(StackChanCapabilities.Dashboard))
                _alertDashboardPendingClear = alertWindowActive;
            if (!_automaticGestureArmed)
            {
                _automaticGestureArmed = true;
                _nextAutomaticGesture = now.AddSeconds(90);
            }
            else if (settings.AutoMotion && !settings.Quiet && !manualDashboardOnly && now >= _manualUntil && !_headTracking &&
                snapshot.Capabilities.HasFlag(StackChanCapabilities.Motion) && now >= _nextAutomaticGesture)
            {
                automaticGesture = activityChanged ? reaction.Activity switch
                {
                    "Alert" => StackChanGesture.Shake,
                    "AI working" or "Meeting active" or "PC busy" => StackChanGesture.Nod,
                    _ => null
                } : reaction.Activity switch
                {
                    "Alert" => StackChanGesture.Shake,
                    "Ready" => StackChanGesture.LookAround,
                    _ => null
                };
                if (automaticGesture != null) _nextAutomaticGesture = now.AddSeconds(90);
            }
        }
        Update(s => s with { Activity = reaction.Activity, Status = "Companion active", Error = null });
        if (automaticGesture is { } gesture)
            await RunGestureAsync(gesture, true, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Alert, then onlooker, welcome, break, (manual hold, gated in the tick), doze, then the
    /// everyday activity. Quiet keeps presence reactions visible but dims the lights.
    /// </summary>
    private Reaction ChooseReaction(Settings settings, DateTimeOffset now)
    {
        int brightness = settings.Quiet ? Math.Min(settings.Brightness, 5) : settings.Brightness;
        string activity = settings.Quiet ? "Quiet" : EverydayActivity(settings, now);
        if (!settings.Quiet && settings.Alerts && now < _alertUntil) return new("Alert", StackChanFace.Concerned, "#FF7043", brightness);
        switch (_presence)
        {
            case PresenceReaction.Onlooker: return new(activity, StackChanFace.Concerned, "#F43F5E", brightness);
            case PresenceReaction.Welcome: return new(activity, StackChanFace.Happy, "#FDBA74", brightness);
            case PresenceReaction.Break: return new(activity, StackChanFace.Happy, "#34D399", brightness);
        }
        if (settings.Quiet) return new("Quiet", StackChanFace.Sleepy, "#64748B", brightness);
        if (_presence == PresenceReaction.Doze)
            return new(activity, StackChanFace.Sleepy, "#64748B", Math.Min(brightness, 5));
        return activity switch
        {
            "Alert" => new("Alert", StackChanFace.Concerned, "#FF7043", brightness),
            "AI working" => new("AI working", StackChanFace.Busy, "#A78BFA", brightness),
            "Meeting active" => new("Meeting active", StackChanFace.Happy, "#38BDF8", brightness),
            "PC busy" => new("PC busy", StackChanFace.Concerned, "#FBBF24", brightness),
            _ => new("Ready", StackChanFace.Neutral, "#34D399", brightness)
        };
    }

    private string EverydayActivity(Settings settings, DateTimeOffset now)
    {
        if (settings.Alerts && now < _alertUntil) return "Alert";
        if (settings.Ai && CompanionActivity.IsActive(StackChanActivity.Ai)) return "AI working";
        if (settings.Meeting && CompanionActivity.IsActive(StackChanActivity.Meeting)) return "Meeting active";
        if (settings.Load && _highLoad) return "PC busy";
        return "Ready";
    }

    private static StackChanDashboardAlert ToDashboardAlert(AlertEvent alert) => new(
        alert.Rule.Metric switch
        {
            AlertMetric.CpuTemperature => "cpu-temperature",
            AlertMetric.CpuUsage => "cpu-usage",
            AlertMetric.MemoryUsed => "memory-used",
            AlertMetric.DiskSpaceFree => "disk-free",
            AlertMetric.GpuUsage => "gpu-usage",
            AlertMetric.BatteryHealth => "battery-health",
            AlertMetric.BatteryCharge => "battery-charge",
            _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Rule.Metric,
                "Unknown alert metric.")
        }, alert.Value, alert.Rule.Threshold, alert.Rule.Above);

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try { await TickAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested) { }
                catch (Exception) when (!_shutdown.IsCancellationRequested) { /* Visible state is set by the operation. Retry remains opt-in. */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private Settings ReadSettings() => new(_config.StackChanEnabled, _config.StackChanAutoReconnect,
        _config.StackChanDeviceAddress, _config.StackChanReactToLoad, _config.StackChanReactToAlerts,
        _config.StackChanReactToAi, _config.StackChanReactToMeeting, _config.StackChanAllowAutoMotion,
        _config.StackChanQuietMode, Math.Clamp(_config.StackChanLedBrightness, 0, 100), Math.Clamp(_config.StackChanUpdateSeconds, 2, 60),
        _config.StackChanUseUsb, _config.StackChanAutoStartCompanion);

    private CancellationTokenSource BeginOperation(CancellationToken token, bool automatic = false)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_settings.Enabled) throw new InvalidOperationException("Enable StackChan companion first.");
            if (automatic && (_manualDisconnect || !_settings.Reconnect || _operation != null))
                throw new OperationCanceledException("Automatic reconnect was disabled.", token);
            previous = _operation;
            operation = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            _operation = operation;
            _operationAutomatic = automatic;
        }
        CancelSafely(previous);
        return operation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        lock (_gate) { if (ReferenceEquals(_operation, operation)) _operation = null; }
    }

    private bool IsCurrentOperation(CancellationTokenSource operation)
    {
        lock (_gate) return ReferenceEquals(_operation, operation);
    }

    private bool HasLiveSession()
    {
        lock (_gate) return _session is { IsCancellationRequested: false };
    }

    private void InvalidateSession()
    {
        CancellationTokenSource? session;
        lock (_gate)
        {
            _generation++;
            session = _session;
            _session = null;
            _lastReaction = null;
            _automaticGestureArmed = false;
            _nextAutomaticGesture = default;
            _alertDashboardPendingClear = false;
        }
        CancelSafely(session);
        session?.Dispose();
        CancelGesture();
    }

    private void OnDisconnected()
    {
        InvalidateSession();
        Update(s => s with { State = StackChanConnectionState.Disconnected, Capabilities = StackChanCapabilities.None,
            Status = s.Connection == StackChanConnectionKind.Usb
                ? "USB disconnected. Reconnect the cable and start the companion if the robot restarted."
                : s.Firmware == StackChanFirmware.UiFlow2
                ? "Robot disconnected. Run the UIFlow companion script to reconnect."
                : "Robot disconnected. Keep DANCE open to reconnect.", Error = null });
    }

    private void Update(Func<StackChanSnapshot, StackChanSnapshot> update)
    {
        lock (_gate) { if (_disposed) return; _snapshot = update(_snapshot); }
        Publish();
    }

    private void UpdateConnectedStatus(string status)
    {
        Update(s => s.State == StackChanConnectionState.Connected
            ? s with { Status = status, Error = null } : s);
    }
    private void Publish()
    {
        if (Changed is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch { /* A closed settings window cannot interrupt Bluetooth work. */ }
    }

    private static void CancelSafely(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private static StackChanCapabilities Capability(StackChanChannel channel) => channel switch
    {
        StackChanChannel.Avatar => StackChanCapabilities.Avatar,
        StackChanChannel.Rgb => StackChanCapabilities.Rgb,
        StackChanChannel.Motion => StackChanCapabilities.Motion,
        StackChanChannel.Dashboard => StackChanCapabilities.Dashboard,
        _ => StackChanCapabilities.None
    };

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _transport.Disconnected -= OnDisconnected;
        CompanionActivity.Changed -= OnActivityChanged;
        _shutdown.Cancel();
        InvalidateSession();
        _transport.Dispose();
    }

    private sealed record Settings(bool Enabled, bool Reconnect, string Address, bool Load, bool Alerts,
        bool Ai, bool Meeting, bool AutoMotion, bool Quiet, int Brightness, int Interval, bool UseUsb, bool AutoStart);
    private sealed record Reaction(string Activity, StackChanFace Face, string Color, int Brightness)
    {
        public string Key => $"{Activity}:{Face}:{Color}:{Brightness}";
    }
}
