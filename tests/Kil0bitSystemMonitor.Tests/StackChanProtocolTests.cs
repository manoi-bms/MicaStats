using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.StackChan;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanProtocolTests
{
    [Fact]
    public void Twenty_byte_json_is_sent_raw_and_copied()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"mouth\":{\"size\":8}}");

        IReadOnlyList<byte[]> packets = StackChanProtocol.Fragment(json);

        Assert.Equal(20, json.Length);
        Assert.Single(packets);
        Assert.Equal(json, packets[0]);
        Assert.NotSame(json, packets[0]);
    }

    [Fact]
    public void Fragments_reconstruct_utf8_json_with_factory_header()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"message\":\"สวัสดี StackChan\"}");

        IReadOnlyList<byte[]> packets = StackChanProtocol.Fragment(json);

        Assert.True(packets.Count > 1);
        Assert.All(packets, packet => Assert.InRange(packet.Length, 11, 20));
        for (int index = 0; index < packets.Count; index++)
        {
            byte[] packet = packets[index];
            Assert.Equal(new byte[] { 0xAA, 0x55, 0xC3, 0x01 }, packet[..4]);
            Assert.Equal(index, ReadUInt16BigEndian(packet, 4));
            Assert.Equal(packets.Count, ReadUInt16BigEndian(packet, 6));
            Assert.Equal(json.Length, ReadUInt16BigEndian(packet, 8));
        }

        byte[] reconstructed = packets.SelectMany(packet => packet.Skip(10)).ToArray();
        Assert.Equal(json, reconstructed);
    }

    [Fact]
    public void Maximum_json_size_round_trips_and_one_more_byte_is_rejected()
    {
        byte[] maximum = Enumerable.Range(0, StackChanProtocol.MaxJsonBytes)
            .Select(index => (byte)(index % 251))
            .ToArray();

        IReadOnlyList<byte[]> packets = StackChanProtocol.Fragment(maximum);

        Assert.Equal(205, packets.Count);
        Assert.Equal(maximum, packets.SelectMany(packet => packet.Skip(10)).ToArray());
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Fragment(new byte[StackChanProtocol.MaxJsonBytes + 1]));
    }

    [Fact]
    public void Avatar_writes_complete_features_without_type_or_residue()
    {
        using JsonDocument happy = JsonDocument.Parse(StackChanProtocol.Avatar(StackChanFace.Happy));
        using JsonDocument neutral = JsonDocument.Parse(StackChanProtocol.Avatar(StackChanFace.Neutral));

        Assert.False(happy.RootElement.TryGetProperty("type", out _));
        foreach (string name in new[] { "leftEye", "rightEye", "mouth" })
        {
            JsonElement happyFeature = happy.RootElement.GetProperty(name);
            JsonElement neutralFeature = neutral.RootElement.GetProperty(name);
            Assert.Equal(new[] { "x", "y", "rotation", "weight", "size" },
                happyFeature.EnumerateObject().Select(property => property.Name));
            Assert.All(neutralFeature.EnumerateObject(), property => Assert.Equal(0, property.Value.GetInt32()));
        }

        Assert.True(happy.RootElement.GetProperty("mouth").GetProperty("size").GetInt32() > 0);
    }

    [Fact]
    public void Every_face_preset_is_a_distinct_complete_state()
    {
        byte[][] presets = Enum.GetValues<StackChanFace>().Select(StackChanProtocol.Avatar).ToArray();

        Assert.Equal(presets.Length, presets.Select(Encoding.UTF8.GetString).Distinct(StringComparer.Ordinal).Count());
        Assert.All(presets, json =>
        {
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal(3, document.RootElement.EnumerateObject().Count());
            Assert.All(document.RootElement.EnumerateObject(), feature => Assert.Equal(5, feature.Value.EnumerateObject().Count()));
        });
    }

    [Theory]
    [InlineData(-101, 0, 0, 0, 0)]
    [InlineData(0, 101, 0, 0, 0)]
    [InlineData(0, 0, 3601, 0, 0)]
    [InlineData(0, 0, 0, -1, 0)]
    [InlineData(0, 0, 0, 0, 101)]
    public void Avatar_rejects_values_outside_factory_feature_ranges(int x, int y, int rotation, int weight, int size)
    {
        var invalid = new StackChanFeature(x, y, rotation, weight, size);

        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Avatar(
            new StackChanAvatar(invalid, new StackChanFeature(), new StackChanFeature())));
    }

    [Fact]
    public void Rgb_normalizes_and_scales_each_channel()
    {
        using JsonDocument document = JsonDocument.Parse(StackChanProtocol.Rgb("#ff8040", "#010203", 50, 1.25));
        JsonElement root = document.RootElement;

        Assert.Equal("#804020", root.GetProperty("leftRgbColor").GetString());
        Assert.Equal("#010102", root.GetProperty("rightRgbColor").GetString());
        Assert.Equal(1.25, root.GetProperty("leftRgbDuration").GetDouble());
        Assert.Equal(1.25, root.GetProperty("rightRgbDuration").GetDouble());
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#FFFF")]
    [InlineData("#GG0000")]
    [InlineData(" FFFFFF")]
    public void Rgb_rejects_non_hex_colors(string color)
    {
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Rgb(color, "#000000"));
    }

    [Fact]
    public void Motion_uses_tenths_of_a_degree_and_never_rotate()
    {
        using JsonDocument document = JsonDocument.Parse(StackChanProtocol.Motion(-12.25, 45.1, 175));
        JsonElement root = document.RootElement;

        Assert.Equal(-123, root.GetProperty("yawServo").GetProperty("angle").GetInt32());
        Assert.Equal(451, root.GetProperty("pitchServo").GetProperty("angle").GetInt32());
        Assert.Equal(175, root.GetProperty("yawServo").GetProperty("speed").GetInt32());
        Assert.DoesNotContain("rotate", Encoding.UTF8.GetString(StackChanProtocol.Motion(0, 45)), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-45.01, 45, 150)]
    [InlineData(45.01, 45, 150)]
    [InlineData(0, 4.99, 150)]
    [InlineData(0, 85.01, 150)]
    [InlineData(0, 45, 99)]
    [InlineData(0, 45, 301)]
    [InlineData(double.NaN, 45, 150)]
    [InlineData(0, double.PositiveInfinity, 150)]
    public void Motion_rejects_unsafe_values(double yaw, double pitch, int speed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Motion(yaw, pitch, speed));
    }

    [Fact]
    public void Rgb_rejects_invalid_brightness_and_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Rgb("#000000", "#FFFFFF", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Rgb("#000000", "#FFFFFF", 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Rgb("#000000", "#FFFFFF", durationSeconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Rgb("#000000", "#FFFFFF", durationSeconds: 10.01));
    }

    [Fact]
    public void Dashboard_clamps_metrics_and_keeps_only_companion_status()
    {
        using JsonDocument document = JsonDocument.Parse(
            StackChanProtocol.Dashboard(42.5f, 100.01f, 250, "AI working"));
        JsonElement companion = document.RootElement.GetProperty("companion");

        Assert.Equal(42.5f, companion.GetProperty("cpu").GetSingle());
        Assert.Equal(100, companion.GetProperty("ram").GetSingle());
        Assert.Equal(100, companion.GetProperty("gpu").GetSingle());
        Assert.Equal("AI working", companion.GetProperty("activity").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void Dashboard_encodes_unavailable_metrics_as_null()
    {
        using JsonDocument document = JsonDocument.Parse(
            StackChanProtocol.Dashboard(float.NaN, float.NegativeInfinity, float.PositiveInfinity, "Ready"));
        JsonElement companion = document.RootElement.GetProperty("companion");

        Assert.Equal(JsonValueKind.Null, companion.GetProperty("cpu").ValueKind);
        Assert.Equal(JsonValueKind.Null, companion.GetProperty("ram").ValueKind);
        Assert.Equal(JsonValueKind.Null, companion.GetProperty("gpu").ValueKind);

        using JsonDocument sentinel = JsonDocument.Parse(StackChanProtocol.Dashboard(-1, 0, 0, "Ready"));
        Assert.Equal(JsonValueKind.Null,
            sentinel.RootElement.GetProperty("companion").GetProperty("cpu").ValueKind);
    }

    [Fact]
    public void Dashboard_encodes_only_typed_public_alert_fields()
    {
        using JsonDocument document = JsonDocument.Parse(StackChanProtocol.Dashboard(10, 20, 30, "Alert",
            new StackChanDashboardAlert("cpu-temperature", 97.25, 95, true)));

        JsonElement alert = document.RootElement.GetProperty("companion").GetProperty("alert");
        Assert.Equal(new[] { "kind", "value", "threshold", "above" },
            alert.EnumerateObject().Select(property => property.Name));
        Assert.Equal("cpu-temperature", alert.GetProperty("kind").GetString());
        Assert.Equal(97.25, alert.GetProperty("value").GetDouble());
        Assert.Equal(95, alert.GetProperty("threshold").GetDouble());
        Assert.True(alert.GetProperty("above").GetBoolean());
    }

    [Fact]
    public void Dashboard_serializes_ambient_only_when_automatic_effects_are_suspended()
    {
        using JsonDocument automatic = JsonDocument.Parse(
            StackChanProtocol.Dashboard(10, 20, 30, "Ready"));
        Assert.False(automatic.RootElement.GetProperty("companion")
            .TryGetProperty("ambient", out _));

        using JsonDocument manual = JsonDocument.Parse(
            StackChanProtocol.Dashboard(10, 20, 30, "Alert", ambient: false));
        Assert.False(manual.RootElement.GetProperty("companion")
            .GetProperty("ambient").GetBoolean());
    }

    [Theory]
    [InlineData("private-rule", 10, 20)]
    [InlineData("cpu-usage", -1, 20)]
    [InlineData("cpu-usage", 10, -1)]
    [InlineData("cpu-usage", 1000000.01, 20)]
    [InlineData("cpu-usage", 10, 1000000.01)]
    [InlineData("cpu-usage", double.NaN, 20)]
    [InlineData("cpu-usage", 10, double.PositiveInfinity)]
    public void Dashboard_rejects_unknown_or_invalid_alert_data(string kind, double value, double threshold)
    {
        Assert.ThrowsAny<ArgumentException>(() => StackChanProtocol.Dashboard(10, 20, 30, "Alert",
            new StackChanDashboardAlert(kind, value, threshold, true)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Thinking about the user's private prompt")]
    [InlineData("ready")]
    [InlineData("CPU alert: secret process name")]
    public void Dashboard_rejects_unknown_or_private_activity_text(string activity)
    {
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Dashboard(10, 20, 30, activity));
    }

    [Fact]
    public void Dashboard_rejects_null_activity()
    {
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Dashboard(10, 20, 30, null!));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Quiet")]
    [InlineData("Alert")]
    [InlineData("AI working")]
    [InlineData("Meeting active")]
    [InlineData("PC busy")]
    public void Dashboard_accepts_each_defined_activity(string activity)
    {
        using JsonDocument document = JsonDocument.Parse(StackChanProtocol.Dashboard(0, 0, 0, activity));

        Assert.Equal(activity, document.RootElement.GetProperty("companion").GetProperty("activity").GetString());
    }

    private static int ReadUInt16BigEndian(byte[] value, int offset) =>
        (value[offset] << 8) | value[offset + 1];
}

public sealed class StackChanPresenceProtocolTests
{
    [Fact]
    public void Camera_json_shape()
    {
        using JsonDocument on = JsonDocument.Parse(StackChanProtocol.Camera(true, 4, 50));
        JsonElement camera = on.RootElement.GetProperty("camera");
        Assert.True(camera.GetProperty("on").GetBoolean());
        Assert.Equal(4, camera.GetProperty("fps").GetInt32());
        Assert.Equal(50, camera.GetProperty("quality").GetInt32());

        using JsonDocument off = JsonDocument.Parse(StackChanProtocol.Camera(false));
        Assert.False(off.RootElement.GetProperty("camera").GetProperty("on").GetBoolean());
        Assert.False(off.RootElement.GetProperty("camera").TryGetProperty("fps", out _));
    }

    [Fact]
    public void Camera_rejects_fps_7()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Camera(true, 7, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Camera(true, 4, 90));
    }

    [Fact]
    public void Gaze_release_writes_null()
    {
        using JsonDocument release = JsonDocument.Parse(StackChanProtocol.Gaze(null, null));
        Assert.Equal(JsonValueKind.Null, release.RootElement.GetProperty("presence").GetProperty("gaze").ValueKind);

        using JsonDocument look = JsonDocument.Parse(StackChanProtocol.Gaze(40, -20));
        JsonElement gaze = look.RootElement.GetProperty("presence").GetProperty("gaze");
        Assert.Equal(40, gaze.GetProperty("x").GetInt32());
        Assert.Equal(-20, gaze.GetProperty("y").GetInt32());
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Gaze(10, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Gaze(101, 0));
    }

    [Fact]
    public void Scene_rejects_non_ascii_and_fourth_line()
    {
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Scene("welcome", ["สวัสดี"], 5));
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Scene("welcome", ["a", "b", "c", "d"], 5));
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Scene("welcome", [new string('a', 33)], 5));
        Assert.Throws<ArgumentException>(() => StackChanProtocol.Scene("party", [], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackChanProtocol.Scene("break", [], 61));

        using JsonDocument scene = JsonDocument.Parse(StackChanProtocol.Scene("welcome", ["Welcome back", "2 alerts"], 15));
        JsonElement presence = scene.RootElement.GetProperty("presence");
        Assert.Equal("welcome", presence.GetProperty("scene").GetString());
        Assert.Equal(["Welcome back", "2 alerts"], presence.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(15, presence.GetProperty("seconds").GetInt32());
    }
}

public sealed class StackChanTiltRangeTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(85)]
    public void Motion_accepts_the_full_tilt_range(double pitch)
    {
        using System.Text.Json.JsonDocument motion = System.Text.Json.JsonDocument.Parse(StackChanProtocol.Motion(0, pitch, 100));
        Assert.Equal((int)(pitch * 10), motion.RootElement.GetProperty("pitchServo").GetProperty("angle").GetInt32());
    }
}
