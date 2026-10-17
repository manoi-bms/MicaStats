using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Turzx;

public sealed class TurzxDisplayService : IDisposable
{
    private readonly AppConfig _config;
    private readonly ITurzxTransport _transport;
    private readonly TurzxRenderer _renderer = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private TurzxSnapshot _snapshot;
    private ushort[]? _previousFrame;
    private ushort[]? _latestFrame;
    private int _latestWidth = TurzxDimensions.LongSide;
    private int _latestHeight = TurzxDimensions.ShortSide;
    private TurzxOrientation _appliedOrientation = TurzxOrientation.Landscape;
    private int _appliedBrightness = -1;
    private TurzxViewMode _appliedViewMode = TurzxViewMode.Dashboard;
    private SystemMetrics? _latestMetrics;
    private AlertEvent? _activeAlert;
    private DateTime _alertExpiresUtc = DateTime.MinValue;
    private DateTime _splashHoldUntilUtc = DateTime.MinValue;
    private readonly bool _startBackground;
    private int _renderScheduled;
    private bool _disposed;

    public TurzxDisplayService(AppConfig config, ITurzxTransport transport, bool startBackground = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _startBackground = startBackground;
        _transport.Disconnected += OnTransportDisconnected;

        TurzxOrientation orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
        TurzxViewMode viewMode = TurzxDimensions.ParseViewMode(_config.TurzxViewMode);
        _snapshot = _config.TurzxEnabled
            ? new TurzxSnapshot(
                TurzxConnectionState.Disconnected,
                "Disconnected",
                _config.TurzxPortName,
                _config.TurzxDeviceName,
                orientation,
                viewMode,
                _config.TurzxBrightness)
            : TurzxSnapshot.Off(orientation, viewMode, _config.TurzxBrightness);

        // Render initial preview frame even before connecting so Settings preview is never blank
        UpdatePreviewFrame(new SystemMetrics(), orientation, viewMode, DateTime.Now, null);

        if (startBackground && _config.TurzxEnabled && _config.TurzxAutoConnect)
        {
            _ = Task.Run(() => TryAutoConnectAsync(_shutdown.Token));
        }
    }

    public event Action? Changed;

    public TurzxSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public (ushort[] Pixels, int Width, int Height) GetPreviewFrame()
    {
        lock (_gate)
        {
            if (_latestFrame != null)
                return (_latestFrame, _latestWidth, _latestHeight);
        }
        var orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
        var viewMode = TurzxDimensions.ParseViewMode(_config.TurzxViewMode);
        UpdatePreviewFrame(_latestMetrics ?? new SystemMetrics(), orientation, viewMode, DateTime.Now, null);
        lock (_gate)
            return (_latestFrame!, _latestWidth, _latestHeight);
    }

    public async Task<IReadOnlyList<TurzxDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Update(s => s with
            {
                State = s.State == TurzxConnectionState.Connected ? TurzxConnectionState.Connected : TurzxConnectionState.Discovering,
                Status = "Finding Turzx USB serial ports…",
                Error = null,
            });

            IReadOnlyList<TurzxDevice> devices = await _transport.DiscoverAsync(linked.Token).ConfigureAwait(false);

            Update(s => s with
            {
                State = s.State == TurzxConnectionState.Connected ? TurzxConnectionState.Connected : TurzxConnectionState.Disconnected,
                Status = devices.Count == 0
                    ? "No Turzx USB display found. Check the USB-C cable and try again."
                    : "Scan complete",
            });
            return devices;
        }
        catch (OperationCanceledException)
        {
            Update(s => s with
            {
                State = s.State == TurzxConnectionState.Connected ? TurzxConnectionState.Connected : TurzxConnectionState.Disconnected,
                Status = "Scan cancelled",
            });
            throw;
        }
        catch (Exception)
        {
            Update(s => s with
            {
                State = s.State == TurzxConnectionState.Connected ? TurzxConnectionState.Connected : TurzxConnectionState.Faulted,
                Status = "Scan failed",
                Error = "Turzx USB discovery failed. Check the USB cable and serial driver.",
            });
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ConnectAsync(TurzxDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            TurzxOrientation orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
            TurzxViewMode viewMode = TurzxDimensions.ParseViewMode(_config.TurzxViewMode);
            int brightness = _config.TurzxBrightness;

            Update(s => s with
            {
                State = TurzxConnectionState.Connecting,
                Status = "Connecting",
                PortName = device.PortName,
                DeviceName = device.Name,
                Orientation = orientation,
                ViewMode = viewMode,
                Brightness = brightness,
                Error = null,
            });

            await _transport.ConnectAsync(device, linked.Token).ConfigureAwait(false);
            await _transport.WriteAsync(TurzxProtocol.BuildScreenOn(), linked.Token).ConfigureAwait(false);
            await _transport.WriteAsync(TurzxProtocol.BuildSetOrientation(orientation), linked.Token).ConfigureAwait(false);
            await _transport.WriteAsync(TurzxProtocol.BuildSetBrightness(brightness), linked.Token).ConfigureAwait(false);

            _appliedOrientation = orientation;
            _appliedBrightness = brightness;
            _appliedViewMode = viewMode;
            _previousFrame = null;
            _splashHoldUntilUtc = _startBackground ? DateTime.UtcNow.AddSeconds(2.5) : DateTime.MinValue;

            await PushFrameLockedAsync(_latestMetrics ?? new SystemMetrics(), DateTime.Now, linked.Token).ConfigureAwait(false);

            Update(s => s with
            {
                State = TurzxConnectionState.Connected,
                Status = "Connected",
                PortName = device.PortName,
                DeviceName = device.Name,
                Orientation = orientation,
                ViewMode = viewMode,
                Brightness = brightness,
                Error = null,
            });
        }
        catch (OperationCanceledException)
        {
            await SafeDisconnectTransportAsync().ConfigureAwait(false);
            Update(s => s with { State = TurzxConnectionState.Disconnected, Status = "Connection cancelled" });
            throw;
        }
        catch (Exception)
        {
            await SafeDisconnectTransportAsync().ConfigureAwait(false);
            Update(s => s with
            {
                State = TurzxConnectionState.Faulted,
                Status = "Connection failed",
                Error = "Cannot open the Turzx USB display port. Close other serial tools and reconnect.",
            });
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await SafeTurnOffAndDisconnectAsync().ConfigureAwait(false);
            _previousFrame = null;
            Update(s => s with
            {
                State = _config.TurzxEnabled ? TurzxConnectionState.Disconnected : TurzxConnectionState.Off,
                Status = _config.TurzxEnabled ? "Disconnected" : "Turzx display is off.",
                Error = null,
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void ObserveMetrics(SystemMetrics metrics)
    {
        if (metrics == null || _disposed) return;
        lock (_gate) _latestMetrics = metrics;

        if (!_config.TurzxEnabled) return;
        ScheduleBackgroundRender();
    }

    public void NotifyAlert(AlertEvent alert)
    {
        if (alert == null || _disposed) return;
        lock (_gate)
        {
            _activeAlert = alert;
            _alertExpiresUtc = DateTime.UtcNow.AddSeconds(12);
        }
        if (!_config.TurzxEnabled) return;
        ScheduleBackgroundRender();
    }

    public void RefreshSettings()
    {
        if (_disposed) return;
        _ = Task.Run(async () =>
        {
            try { await ApplySettingsNowAsync(_shutdown.Token).ConfigureAwait(false); }
            catch { }
        });
    }

    public async Task ApplySettingsNowAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            TurzxOrientation orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
            TurzxViewMode viewMode = TurzxDimensions.ParseViewMode(_config.TurzxViewMode);
            int brightness = _config.TurzxBrightness;

            if (!_config.TurzxEnabled)
            {
                await SafeTurnOffAndDisconnectAsync().ConfigureAwait(false);
                _previousFrame = null;
                UpdatePreviewFrame(_latestMetrics ?? new SystemMetrics(), orientation, viewMode, DateTime.Now, null);
                Update(_ => TurzxSnapshot.Off(orientation, viewMode, brightness));
                return;
            }

            if (Snapshot.State == TurzxConnectionState.Connected)
            {
                bool fullRedraw = false;
                if (orientation != _appliedOrientation)
                {
                    await _transport.WriteAsync(TurzxProtocol.BuildSetOrientation(orientation), linked.Token).ConfigureAwait(false);
                    _appliedOrientation = orientation;
                    fullRedraw = true;
                }
                if (brightness != _appliedBrightness)
                {
                    await _transport.WriteAsync(TurzxProtocol.BuildSetBrightness(brightness), linked.Token).ConfigureAwait(false);
                    _appliedBrightness = brightness;
                }
                if (viewMode != _appliedViewMode)
                {
                    _appliedViewMode = viewMode;
                    fullRedraw = true;
                }
                if (fullRedraw)
                {
                    _previousFrame = null;
                    await PushFrameLockedAsync(_latestMetrics ?? new SystemMetrics(), DateTime.Now, linked.Token).ConfigureAwait(false);
                }

                Update(s => s with
                {
                    Orientation = orientation,
                    ViewMode = viewMode,
                    Brightness = brightness,
                });
            }
            else
            {
                UpdatePreviewFrame(_latestMetrics ?? new SystemMetrics(), orientation, viewMode, DateTime.Now, null);
                Update(s => s with
                {
                    State = s.State == TurzxConnectionState.Off ? TurzxConnectionState.Disconnected : s.State,
                    Status = s.State == TurzxConnectionState.Off ? "Disconnected" : s.Status,
                    Orientation = orientation,
                    ViewMode = viewMode,
                    Brightness = brightness,
                });
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RenderMetricsNowAsync(SystemMetrics metrics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        lock (_gate) _latestMetrics = metrics;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await PushFrameLockedAsync(metrics, DateTime.Now, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task SendTestPatternAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            TurzxOrientation orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
            (int width, int height) = TurzxDimensions.ForOrientation(orientation);
            ushort[] frame = _renderer.RenderTestPattern(orientation, DateTime.Now);
            lock (_gate)
            {
                _latestFrame = frame;
                _latestWidth = width;
                _latestHeight = height;
            }
            if (Snapshot.State == TurzxConnectionState.Connected)
            {
                await _transport.WriteRectAsync(frame, width, new TurzxRect(0, 0, width, height), linked.Token).ConfigureAwait(false);
                _previousFrame = frame;
            }
            RaiseChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Disconnected -= OnTransportDisconnected;
        _shutdown.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            _transport.WriteAsync(TurzxProtocol.BuildScreenOff(), timeout.Token).GetAwaiter().GetResult();
        }
        catch { }
        try { _transport.Dispose(); } catch { }
        _shutdown.Dispose();
    }

    private void ScheduleBackgroundRender()
    {
        if (Interlocked.Exchange(ref _renderScheduled, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _renderScheduled, 0) == 1 && !_disposed)
                {
                    if (!_lifecycle.Wait(0))
                        break;
                    try
                    {
                        SystemMetrics metrics;
                        lock (_gate) metrics = _latestMetrics ?? new SystemMetrics();
                        if (Snapshot.State == TurzxConnectionState.Connected)
                        {
                            await PushFrameLockedAsync(metrics, DateTime.Now, _shutdown.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            var orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
                            var viewMode = TurzxDimensions.ParseViewMode(_config.TurzxViewMode);
                            UpdatePreviewFrame(metrics, orientation, viewMode, DateTime.Now, GetActiveAlert());
                            RaiseChanged();
                        }
                    }
                    finally
                    {
                        _lifecycle.Release();
                    }
                }
            }
            catch { }
        });
    }

    private async Task PushFrameLockedAsync(SystemMetrics metrics, DateTime now, CancellationToken token)
    {
        TurzxOrientation orientation = TurzxDimensions.ParseOrientation(_config.TurzxOrientation);
        TurzxViewMode viewMode = DateTime.UtcNow < _splashHoldUntilUtc
            ? TurzxViewMode.Splash
            : TurzxDimensions.ParseViewMode(_config.TurzxViewMode);
        (int width, int height) = TurzxDimensions.ForOrientation(orientation);
        AlertEvent? alert = GetActiveAlert();

        ushort[] frame = _renderer.Render(metrics, orientation, viewMode, now, alert);
        lock (_gate)
        {
            _latestFrame = frame;
            _latestWidth = width;
            _latestHeight = height;
        }

        IReadOnlyList<TurzxRect> dirty = TurzxRenderer.ComputeDirtyRegions(_previousFrame, frame, width, height);
        foreach (TurzxRect rect in dirty)
        {
            await _transport.WriteRectAsync(frame, width, rect, token).ConfigureAwait(false);
        }
        _previousFrame = frame;
        RaiseChanged();
    }

    private void UpdatePreviewFrame(
        SystemMetrics metrics,
        TurzxOrientation orientation,
        TurzxViewMode viewMode,
        DateTime now,
        AlertEvent? alert)
    {
        (int width, int height) = TurzxDimensions.ForOrientation(orientation);
        ushort[] frame = _renderer.Render(metrics, orientation, viewMode, now, alert);
        lock (_gate)
        {
            _latestFrame = frame;
            _latestWidth = width;
            _latestHeight = height;
        }
    }

    private AlertEvent? GetActiveAlert()
    {
        lock (_gate)
        {
            if (_activeAlert != null && DateTime.UtcNow <= _alertExpiresUtc)
                return _activeAlert;
            _activeAlert = null;
            return null;
        }
    }

    private async Task TryAutoConnectAsync(CancellationToken token)
    {
        try
        {
            IReadOnlyList<TurzxDevice> devices = await DiscoverAsync(token).ConfigureAwait(false);
            if (devices.Count == 0 || !_config.TurzxEnabled) return;

            string preferredPort = _config.TurzxPortName;
            TurzxDevice target = devices.FirstOrDefault(d =>
                string.Equals(d.PortName, preferredPort, StringComparison.OrdinalIgnoreCase))
                ?? devices[0];

            await ConnectAsync(target, token).ConfigureAwait(false);
        }
        catch { }
    }

    private void OnTransportDisconnected()
    {
        _previousFrame = null;
        Update(s => s with
        {
            State = _config.TurzxEnabled ? TurzxConnectionState.Disconnected : TurzxConnectionState.Off,
            Status = "Turzx USB display disconnected.",
            Error = "USB connection was lost. Check the cable and reconnect.",
        });
    }

    private async Task SafeTurnOffAndDisconnectAsync()
    {
        if (Snapshot.State == TurzxConnectionState.Connected)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                await _transport.WriteAsync(TurzxProtocol.BuildScreenOff(), cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
        await SafeDisconnectTransportAsync().ConfigureAwait(false);
    }

    private async Task SafeDisconnectTransportAsync()
    {
        try { await _transport.DisconnectAsync().ConfigureAwait(false); }
        catch { }
    }

    private void Update(Func<TurzxSnapshot, TurzxSnapshot> transform)
    {
        lock (_gate)
            _snapshot = transform(_snapshot);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Action? handlers = Changed;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { }
        }
    }
}
