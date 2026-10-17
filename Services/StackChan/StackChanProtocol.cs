using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Kil0bitSystemMonitor.Services.StackChan;

public static class StackChanProtocol
{
    private static readonly HashSet<string> DashboardActivities = new(StringComparer.Ordinal)
    {
        "Ready",
        "Quiet",
        "Alert",
        "AI working",
        "Meeting active",
        "PC busy"
    };
    private static readonly HashSet<string> DashboardAlertKinds = new(StringComparer.Ordinal)
    {
        "cpu-temperature",
        "cpu-usage",
        "memory-used",
        "disk-free",
        "gpu-usage",
        "battery-health",
        "battery-charge"
    };

    private static readonly HashSet<string> PresenceScenes = new(StringComparer.Ordinal)
    {
        "welcome", "guard", "break", "doze", "none"
    };

    public const int MaxJsonBytes = 2048;
    private const int RawPayloadBytes = 20;
    private const int FragmentHeaderBytes = 10;
    private const int FragmentPayloadBytes = RawPayloadBytes - FragmentHeaderBytes;

    public static IReadOnlyList<byte[]> Fragment(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxJsonBytes)
            throw new ArgumentException($"StackChan JSON cannot exceed {MaxJsonBytes} UTF-8 bytes.", nameof(json));

        if (json.Length <= RawPayloadBytes)
            return new[] { (byte[])json.Clone() };

        int packetCount = (json.Length + FragmentPayloadBytes - 1) / FragmentPayloadBytes;
        var packets = new byte[packetCount][];
        for (int index = 0; index < packetCount; index++)
        {
            int offset = index * FragmentPayloadBytes;
            int payloadLength = Math.Min(FragmentPayloadBytes, json.Length - offset);
            var packet = new byte[FragmentHeaderBytes + payloadLength];
            packet[0] = 0xAA;
            packet[1] = 0x55;
            packet[2] = 0xC3;
            packet[3] = 0x01;
            WriteUInt16BigEndian(packet, 4, index);
            WriteUInt16BigEndian(packet, 6, packetCount);
            WriteUInt16BigEndian(packet, 8, json.Length);
            Buffer.BlockCopy(json, offset, packet, FragmentHeaderBytes, payloadLength);
            packets[index] = packet;
        }

        return packets;
    }

    public static byte[] Avatar(StackChanFace face) => Avatar(face switch
    {
        StackChanFace.Neutral => Face(),
        StackChanFace.Happy => Face(
            leftEye: new StackChanFeature(Y: -4, Size: 18),
            rightEye: new StackChanFeature(Y: -4, Size: 18),
            mouth: new StackChanFeature(Y: 8, Size: 32)),
        StackChanFace.Busy => Face(
            leftEye: new StackChanFeature(X: -8, Size: -12),
            rightEye: new StackChanFeature(X: 8, Size: -12),
            mouth: new StackChanFeature(Y: 5, Size: -18)),
        StackChanFace.Concerned => Face(
            leftEye: new StackChanFeature(Y: 5, Rotation: 180, Size: -8),
            rightEye: new StackChanFeature(Y: 5, Rotation: 3420, Size: -8),
            mouth: new StackChanFeature(Y: 8, Rotation: 1800, Size: -24)),
        StackChanFace.Sleepy => Face(
            leftEye: new StackChanFeature(Y: 8, Weight: 20, Size: -45),
            rightEye: new StackChanFeature(Y: 8, Weight: 20, Size: -45),
            mouth: new StackChanFeature(Y: 12, Size: -30)),
        _ => throw new ArgumentOutOfRangeException(nameof(face), face, "Unknown StackChan face preset.")
    });

    public static byte[] Avatar(StackChanAvatar avatar)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        ValidateFeature(avatar.LeftEye, nameof(avatar.LeftEye));
        ValidateFeature(avatar.RightEye, nameof(avatar.RightEye));
        ValidateFeature(avatar.Mouth, nameof(avatar.Mouth));

        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            WriteFeature(writer, "leftEye", avatar.LeftEye);
            WriteFeature(writer, "rightEye", avatar.RightEye);
            WriteFeature(writer, "mouth", avatar.Mouth);
            writer.WriteEndObject();
        });
    }

    public static byte[] Rgb(string left, string right, int brightness = 100, double durationSeconds = 0.3)
    {
        if (brightness is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(brightness), brightness, "Brightness must be from 0 to 100.");
        if (!double.IsFinite(durationSeconds) || durationSeconds is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "Duration must be from 0 to 10 seconds.");

        string leftColor = ScaleColor(ParseColor(left, nameof(left)), brightness);
        string rightColor = ScaleColor(ParseColor(right, nameof(right)), brightness);
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("leftRgbColor", leftColor);
            writer.WriteString("rightRgbColor", rightColor);
            writer.WriteNumber("leftRgbDuration", durationSeconds);
            writer.WriteNumber("rightRgbDuration", durationSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] Motion(double yawDegrees, double pitchDegrees, int speed = 150)
    {
        ValidateFiniteRange(yawDegrees, -45, 45, nameof(yawDegrees));
        // M5 recommends 5..85 degrees for the StackChan tilt servo.
        ValidateFiniteRange(pitchDegrees, 5, 85, nameof(pitchDegrees));
        if (speed is < 100 or > 300)
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed must be from 100 to 300.");

        int yaw = checked((int)Math.Round(yawDegrees * 10, MidpointRounding.AwayFromZero));
        int pitch = checked((int)Math.Round(pitchDegrees * 10, MidpointRounding.AwayFromZero));
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("yawServo");
            writer.WriteStartObject();
            writer.WriteNumber("angle", yaw);
            writer.WriteNumber("speed", speed);
            writer.WriteEndObject();
            writer.WritePropertyName("pitchServo");
            writer.WriteStartObject();
            writer.WriteNumber("angle", pitch);
            writer.WriteNumber("speed", speed);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public static byte[] Dashboard(float cpu, float ram, float gpu, string activity,
        StackChanDashboardAlert? alert = null, bool ambient = true)
    {
        if (activity is null || !DashboardActivities.Contains(activity))
            throw new ArgumentException("Activity must be a known companion status.", nameof(activity));
        if (alert != null)
        {
            if (!DashboardAlertKinds.Contains(alert.Kind))
                throw new ArgumentException("Alert kind must be a known public metric.", nameof(alert));
            ValidateAlertNumber(alert.Value, nameof(alert), nameof(alert.Value));
            ValidateAlertNumber(alert.Threshold, nameof(alert), nameof(alert.Threshold));
        }

        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("companion");
            writer.WriteStartObject();
            WriteMetric(writer, "cpu", cpu);
            WriteMetric(writer, "ram", ram);
            WriteMetric(writer, "gpu", gpu);
            writer.WriteString("activity", activity);
            if (!ambient) writer.WriteBoolean("ambient", false);
            if (alert != null)
            {
                writer.WritePropertyName("alert");
                writer.WriteStartObject();
                writer.WriteString("kind", alert.Kind);
                writer.WriteNumber("value", alert.Value);
                writer.WriteNumber("threshold", alert.Threshold);
                writer.WriteBoolean("above", alert.Above);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>Camera on (also the keepalive the companion needs every 10 s) or off.</summary>
    public static byte[] Camera(bool on, int fps = 4, int quality = 50)
    {
        if (on && fps is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "Frame rate must be from 1 to 6.");
        if (on && quality is < 30 or > 80)
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "JPEG quality must be from 30 to 80.");
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("camera");
            writer.WriteStartObject();
            writer.WriteBoolean("on", on);
            if (on)
            {
                writer.WriteNumber("fps", fps);
                writer.WriteNumber("quality", quality);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>Where the eyes look (-100..100 each axis), or both null to resume the robot's own glances.</summary>
    public static byte[] Gaze(int? x, int? y)
    {
        if (x.HasValue != y.HasValue)
            throw new ArgumentException("Gaze needs both coordinates or neither.");
        if (x is { } gazeX) ValidateRange(gazeX, -100, 100, nameof(x), "X");
        if (y is { } gazeY) ValidateRange(gazeY, -100, 100, nameof(y), "Y");
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("presence");
            writer.WriteStartObject();
            if (x is { } targetX && y is { } targetY)
            {
                writer.WritePropertyName("gaze");
                writer.WriteStartObject();
                writer.WriteNumber("x", targetX);
                writer.WriteNumber("y", targetY);
                writer.WriteEndObject();
            }
            else writer.WriteNull("gaze");
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>A presence card. The robot font is ASCII only, so every line must be printable ASCII.</summary>
    public static byte[] Scene(string kind, IReadOnlyList<string> lines, int seconds)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!PresenceScenes.Contains(kind))
            throw new ArgumentException("Scene must be welcome, guard, break, doze or none.", nameof(kind));
        if (lines.Count > 3)
            throw new ArgumentException("A scene shows at most 3 lines.", nameof(lines));
        foreach (string line in lines)
        {
            if (line is null || line.Length > 32 || line.Any(c => c < ' ' || c > '~'))
                throw new ArgumentException("Scene lines must be printable ASCII of at most 32 characters.", nameof(lines));
        }
        ValidateRange(seconds, 1, 60, nameof(seconds), "Seconds");
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("presence");
            writer.WriteStartObject();
            writer.WriteString("scene", kind);
            writer.WritePropertyName("lines");
            writer.WriteStartArray();
            foreach (string line in lines) writer.WriteStringValue(line);
            writer.WriteEndArray();
            writer.WriteNumber("seconds", seconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static StackChanAvatar Face(
        StackChanFeature? leftEye = null,
        StackChanFeature? rightEye = null,
        StackChanFeature? mouth = null) => new(
            leftEye ?? new StackChanFeature(),
            rightEye ?? new StackChanFeature(),
            mouth ?? new StackChanFeature());

    private static void WriteFeature(Utf8JsonWriter writer, string name, StackChanFeature feature)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        // The factory parser applies a position only when both coordinates are present.
        writer.WriteNumber("x", feature.X);
        writer.WriteNumber("y", feature.Y);
        writer.WriteNumber("rotation", feature.Rotation);
        writer.WriteNumber("weight", feature.Weight);
        writer.WriteNumber("size", feature.Size);
        writer.WriteEndObject();
    }

    private static void ValidateFeature(StackChanFeature feature, string parameterName)
    {
        if (feature is null)
            throw new ArgumentNullException(parameterName);
        ValidateRange(feature.X, -100, 100, parameterName, nameof(feature.X));
        ValidateRange(feature.Y, -100, 100, parameterName, nameof(feature.Y));
        ValidateRange(feature.Rotation, 0, 3600, parameterName, nameof(feature.Rotation));
        ValidateRange(feature.Weight, 0, 100, parameterName, nameof(feature.Weight));
        ValidateRange(feature.Size, -100, 100, parameterName, nameof(feature.Size));
    }

    private static void ValidateRange(int value, int minimum, int maximum, string parameterName, string field)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{field} must be from {minimum} to {maximum}.");
    }

    private static void ValidateFiniteRange(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"Value must be finite and from {minimum} to {maximum}.");
    }

    private static void ValidateAlertNumber(double value, string parameterName, string field)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{field} must be finite and from 0 to 1000000.");
    }

    private static void WriteMetric(Utf8JsonWriter writer, string name, float value)
    {
        if (!float.IsFinite(value) || value < 0)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteNumber(name, Math.Min(value, 100));
    }

    private static (byte R, byte G, byte B) ParseColor(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#' ||
            !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
        {
            throw new ArgumentException("Color must use #RRGGBB format.", parameterName);
        }

        return (red, green, blue);
    }

    private static string ScaleColor((byte R, byte G, byte B) color, int brightness)
    {
        int Scale(byte channel) => (channel * brightness + 50) / 100;
        return $"#{Scale(color.R):X2}{Scale(color.G):X2}{Scale(color.B):X2}";
    }

    private static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        byte[] json = stream.ToArray();
        if (json.Length > MaxJsonBytes)
            throw new InvalidOperationException($"Generated StackChan JSON exceeds {MaxJsonBytes} UTF-8 bytes.");
        return json;
    }

    private static void WriteUInt16BigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 8);
        target[offset + 1] = (byte)value;
    }
}
