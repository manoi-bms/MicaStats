using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.StackChan;

/// <summary>Routes companion operations to the selected Bluetooth or USB provider.</summary>
public sealed class StackChanTransport : IStackChanTransport, IStackChanUsbCompanionStarter, IStackChanFrameSource
{
    public event Action<StackChanFrame>? FrameReceived;

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IStackChanTransport _bluetooth;
    private readonly IStackChanTransport _usb;
    private readonly Func<StackChanConnectionKind> _mode;
    private IStackChanTransport? _active;
    private IStackChanTransport? _pending;
    private long _generation;
    private long _pendingGeneration;
    private bool _pendingDisconnected;
    private bool _disposed;

    public StackChanTransport(
        IStackChanTransport bluetooth,
        IStackChanTransport usb,
        Func<StackChanConnectionKind> mode)
    {
        _bluetooth = bluetooth ?? throw new ArgumentNullException(nameof(bluetooth));
        _usb = usb ?? throw new ArgumentNullException(nameof(usb));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _bluetooth.Disconnected += OnBluetoothDisconnected;
        _usb.Disconnected += OnUsbDisconnected;
        if (_usb is IStackChanFrameSource frames) frames.FrameReceived += OnUsbFrame;
    }

    private void OnUsbFrame(StackChanFrame frame)
    {
        // Camera frames travel only on a live USB link; stale ones from a closing link are dropped.
        lock (_stateGate)
        {
            if (!ReferenceEquals(_active, _usb)) return;
        }
        var handlers = FrameReceived;
        if (handlers is null) return;
        foreach (Action<StackChanFrame> handler in handlers.GetInvocationList())
        {
            try { handler(frame); }
            catch { }
        }
    }

    public event Action? Disconnected;

    public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return ProviderFor(_mode()).DiscoverAsync(duration, cancellationToken);
    }

    public Task<StackChanCapabilities> ConnectAsync(
        StackChanDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        return ActivateAsync(ProviderFor(device.Connection),
            token => ProviderFor(device.Connection).ConnectAsync(device, token), cancellationToken);
    }

    public Task<StackChanCapabilities> StartCompanionAsync(
        StackChanDevice device,
        byte[] script,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(script);
        if (device.Connection != StackChanConnectionKind.Usb)
            throw new ArgumentException("The companion script can only start over USB.", nameof(device));
        if (_usb is not IStackChanUsbCompanionStarter starter)
            throw new NotSupportedException("The configured USB transport cannot start a companion script.");
        var source = (byte[])script.Clone();
        return ActivateAsync(_usb, token => starter.StartCompanionAsync(device, source, token), cancellationToken);
    }

    public async Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        IStackChanTransport active;
        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            active = _active ?? throw new InvalidOperationException("StackChan is not connected.");
        }
        await active.WriteAsync(channel, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        if (IsDisposed())
            return;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IStackChanTransport? active;
            lock (_stateGate)
            {
                _generation++;
                active = _active;
                _active = null;
                _pending = null;
                _pendingDisconnected = false;
            }
            if (active is not null)
            {
                await active.DisconnectAsync().ConfigureAwait(false);
                RaiseDisconnected();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            _active = null;
            _pending = null;
        }
        _bluetooth.Disconnected -= OnBluetoothDisconnected;
        _usb.Disconnected -= OnUsbDisconnected;
        if (_usb is IStackChanFrameSource frames) frames.FrameReceived -= OnUsbFrame;
        _bluetooth.Dispose();
        if (!ReferenceEquals(_usb, _bluetooth))
            _usb.Dispose();
    }

    private async Task<StackChanCapabilities> ActivateAsync(
        IStackChanTransport provider,
        Func<CancellationToken, Task<StackChanCapabilities>> connect,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IStackChanTransport? previous;
            lock (_stateGate)
            {
                ThrowIfDisposedLocked();
                previous = _active;
                _active = null;
                _generation++;
                _pending = null;
                _pendingDisconnected = false;
            }
            if (previous is not null)
                await previous.DisconnectAsync().ConfigureAwait(false);

            long attempt;
            lock (_stateGate)
            {
                ThrowIfDisposedLocked();
                attempt = ++_generation;
                _pending = provider;
                _pendingGeneration = attempt;
                _pendingDisconnected = false;
            }

            try
            {
                var capabilities = await connect(cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    ThrowIfDisposedLocked();
                    if (_generation != attempt || !ReferenceEquals(_pending, provider) || _pendingDisconnected)
                        throw new IOException("StackChan disconnected while the connection was being established.");
                    _active = provider;
                    _pending = null;
                }
                return capabilities;
            }
            finally
            {
                lock (_stateGate)
                {
                    if (_pendingGeneration == attempt && ReferenceEquals(_pending, provider))
                        _pending = null;
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void OnBluetoothDisconnected() => OnProviderDisconnected(_bluetooth);
    private void OnUsbDisconnected() => OnProviderDisconnected(_usb);

    private void OnProviderDisconnected(IStackChanTransport provider)
    {
        var notify = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_pending, provider))
            {
                _pendingDisconnected = true;
                return;
            }
            if (!ReferenceEquals(_active, provider))
                return;
            _generation++;
            _active = null;
            notify = true;
        }
        if (notify)
            RaiseDisconnected();
    }

    private IStackChanTransport ProviderFor(StackChanConnectionKind kind) => kind switch
    {
        StackChanConnectionKind.Bluetooth => _bluetooth,
        StackChanConnectionKind.Usb => _usb,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown StackChan connection kind.")
    };

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
}
