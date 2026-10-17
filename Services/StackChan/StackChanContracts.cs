using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.StackChan;

[Flags]
public enum StackChanCapabilities { None = 0, Avatar = 1, Rgb = 2, Motion = 4, Dashboard = 8 }
public enum StackChanChannel { Avatar, Rgb, Motion, Dashboard }
public enum StackChanFirmware { Factory, UiFlow2 }
public enum StackChanConnectionKind { Bluetooth, Usb }
public enum StackChanConnectionState { Disconnected, Discovering, Connecting, Connected, Faulted }
public enum StackChanFace { Neutral, Happy, Busy, Concerned, Sleepy }
public enum StackChanGesture { Nod, Shake, LookAround }
public enum StackChanActivity { Ai, Meeting }
/// <summary>What presence detection wants the robot to show; the service ranks it against alerts and holds.</summary>
public enum PresenceReaction { None, Onlooker, Welcome, Break, Doze }

public sealed record StackChanDevice(ulong Address, string Name, StackChanFirmware Firmware = StackChanFirmware.Factory,
    StackChanConnectionKind Connection = StackChanConnectionKind.Bluetooth, string PortName = "")
{
    public string AddressText => Connection == StackChanConnectionKind.Usb
        ? "USB:" + (Address == 0 ? PortName.ToUpperInvariant() : Address.ToString("X12"))
        : Address.ToString("X12");
    public override string ToString() => Connection == StackChanConnectionKind.Usb
        ? $"{Name} ({PortName}, USB)" : $"{Name} ({AddressText})";
}

public sealed record StackChanFeature(int X = 0, int Y = 0, int Rotation = 0, int Weight = 0, int Size = 0);
public sealed record StackChanAvatar(StackChanFeature LeftEye, StackChanFeature RightEye, StackChanFeature Mouth);
public sealed record StackChanDashboardAlert(string Kind, double Value, double Threshold, bool Above);
public sealed record StackChanSnapshot(StackChanConnectionState State, string DeviceName,
    StackChanCapabilities Capabilities, string Status, string? Error = null, string Activity = "Ready",
    StackChanFirmware Firmware = StackChanFirmware.Factory,
    StackChanConnectionKind Connection = StackChanConnectionKind.Bluetooth);

public interface IStackChanTransport : IDisposable
{
    event Action? Disconnected;
    Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken);
    Task<StackChanCapabilities> ConnectAsync(StackChanDevice device, CancellationToken cancellationToken);
    Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken cancellationToken);
    Task DisconnectAsync();
}

/// <summary>One camera picture the companion streamed; only ever held in memory.</summary>
public sealed record StackChanFrame(int Sequence, byte[] Jpeg, DateTimeOffset ReceivedAt);

public interface IStackChanFrameSource
{
    event Action<StackChanFrame>? FrameReceived;
}

/// <summary>Why a USB link failed, so the panel can say what to do instead of a generic failure.</summary>
public enum StackChanUsbFailure { PortMissing, PortBusy, NotAccepting, NoCompanion }

public sealed class StackChanUsbException(StackChanUsbFailure failure, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public StackChanUsbFailure Failure { get; } = failure;
}

public interface IStackChanUsbCompanionStarter
{
    Task<StackChanCapabilities> StartCompanionAsync(StackChanDevice device, byte[] script,
        CancellationToken cancellationToken);
}
