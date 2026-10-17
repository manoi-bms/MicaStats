using System;

namespace Kil0bitSystemMonitor.Services.Turzx;

public enum TurzxOrientation
{
    Portrait = 0,
    ReversePortrait = 1,
    Landscape = 2,
    ReverseLandscape = 3,
}

public enum TurzxViewMode
{
    Dashboard = 0,
    Thermal = 1,
    StorageNet = 2,
    Clock = 3,
    Splash = 4,
}

public enum TurzxConnectionState
{
    Off,
    Disconnected,
    Discovering,
    Connecting,
    Connected,
    Faulted,
}

public sealed record TurzxDevice(string PortName, string Name, string InstanceId)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(PortName) ? Name : $"{Name} ({PortName})";
}

public readonly record struct TurzxRect(int X, int Y, int Width, int Height)
{
    public int EndX => X + Width - 1;
    public int EndY => Y + Height - 1;
}

public sealed record TurzxSnapshot(
    TurzxConnectionState State,
    string Status,
    string PortName,
    string DeviceName,
    TurzxOrientation Orientation,
    TurzxViewMode ViewMode,
    int Brightness,
    string? Error = null)
{
    public static TurzxSnapshot Off(TurzxOrientation orientation = TurzxOrientation.Landscape, TurzxViewMode viewMode = TurzxViewMode.Dashboard, int brightness = 85) =>
        new(TurzxConnectionState.Off, "Turzx display is off.", string.Empty, string.Empty, orientation, viewMode, brightness);
}

public static class TurzxDimensions
{
    public const int ShortSide = 320;
    public const int LongSide = 480;

    public static (int Width, int Height) ForOrientation(TurzxOrientation orientation) =>
        orientation is TurzxOrientation.Landscape or TurzxOrientation.ReverseLandscape
            ? (LongSide, ShortSide)
            : (ShortSide, LongSide);

    public static TurzxOrientation ParseOrientation(string? value) =>
        value?.Trim() switch
        {
            "Portrait" => TurzxOrientation.Portrait,
            "ReversePortrait" => TurzxOrientation.ReversePortrait,
            "ReverseLandscape" => TurzxOrientation.ReverseLandscape,
            _ => TurzxOrientation.Landscape,
        };

    public static string FormatOrientation(TurzxOrientation orientation) =>
        orientation switch
        {
            TurzxOrientation.Portrait => "Portrait",
            TurzxOrientation.ReversePortrait => "ReversePortrait",
            TurzxOrientation.ReverseLandscape => "ReverseLandscape",
            _ => "Landscape",
        };

    public static TurzxViewMode ParseViewMode(string? value) =>
        value?.Trim() switch
        {
            "Thermal" => TurzxViewMode.Thermal,
            "StorageNet" => TurzxViewMode.StorageNet,
            "Clock" => TurzxViewMode.Clock,
            "Splash" => TurzxViewMode.Splash,
            _ => TurzxViewMode.Dashboard,
        };

    public static string FormatViewMode(TurzxViewMode mode) =>
        mode switch
        {
            TurzxViewMode.Thermal => "Thermal",
            TurzxViewMode.StorageNet => "StorageNet",
            TurzxViewMode.Clock => "Clock",
            TurzxViewMode.Splash => "Splash",
            _ => "Dashboard",
        };
}
