using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.StackChan;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

[CollectionDefinition("StackChanActivityGlobal", DisableParallelization = true)]
public sealed class StackChanActivityGlobalCollection;

[Collection("StackChanActivityGlobal")]
public sealed class StackChanServiceTests
{
    private static readonly StackChanDevice Robot = new(0x112233445566, "StackChan");
    private static readonly StackChanDevice UsbRobot = new(0x112233445566, "StackChan USB",
        StackChanFirmware.UiFlow2, StackChanConnectionKind.Usb, "COM8");
    private const StackChanCapabilities AllControls =
        StackChanCapabilities.Avatar | StackChanCapabilities.Rgb | StackChanCapabilities.Motion;

    [Fact]
    public async Task Disabled_companion_tick_performs_no_bluetooth_io()
    {
        var transport = new FakeTransport { Devices = [Robot] };
        using var service = new StackChanService(new AppConfig(), transport, startBackground: false);

        await service.TickAsync();

        Assert.Equal(0, transport.DiscoverCalls);
        Assert.Equal(0, transport.ConnectCalls);
        Assert.Empty(transport.StartedWrites);
    }

    [Fact]
    public async Task Manual_connect_remembers_the_selected_device()
    {
        var config = EnabledConfig();
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.ConnectAsync(Robot);

        Assert.Equal(Robot.AddressText, config.StackChanDeviceAddress);
        Assert.Equal(Robot.Name, config.StackChanDeviceName);
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
    }

    [Fact]
    public async Task Alert_reaction_has_priority_over_ai_meeting_and_load()
    {
        var config = EnabledConfig();
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);
        using var ai = CompanionActivity.Enter(StackChanActivity.Ai);
        using var meeting = CompanionActivity.Enter(StackChanActivity.Meeting);
        service.ObserveMetrics(new SystemMetrics { CpuUsage = 99 });
        service.NotifyAlert();
        await service.ConnectAsync(Robot);

        await service.TickAsync();

        Assert.Equal("Alert", service.Snapshot.Activity);
    }

    [Fact]
    public async Task Ai_reaction_has_priority_over_meeting_and_load()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        using var ai = CompanionActivity.Enter(StackChanActivity.Ai);
        using var meeting = CompanionActivity.Enter(StackChanActivity.Meeting);
        service.ObserveMetrics(new SystemMetrics { RamPercent = 99 });
        await service.ConnectAsync(Robot);

        await service.TickAsync();

        Assert.Equal("AI working", service.Snapshot.Activity);
    }

    [Fact]
    public async Task Meeting_reaction_has_priority_over_pc_load()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        using var meeting = CompanionActivity.Enter(StackChanActivity.Meeting);
        service.ObserveMetrics(new SystemMetrics { GpuUsage = 99 });
        await service.ConnectAsync(Robot);

        await service.TickAsync();

        Assert.Equal("Meeting active", service.Snapshot.Activity);
    }

    [Fact]
    public async Task Quiet_mode_limits_automatic_led_brightness_to_five_percent()
    {
        var config = EnabledConfig();
        config.StackChanQuietMode = true;
        config.StackChanLedBrightness = 80;
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(Robot);

        await service.TickAsync();

        FakeWrite rgb = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Rgb);
        Assert.Equal(StackChanProtocol.Rgb("#64748B", "#64748B", 5), rgb.Json);
        Assert.Equal("Quiet", service.Snapshot.Activity);
    }

    [Fact]
    public async Task Quiet_mode_rejects_manual_head_movement()
    {
        var config = EnabledConfig();
        config.StackChanQuietMode = true;
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(Robot);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.MoveAsync(0, 45));

        Assert.Contains("quiet mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.StartedWrites);
    }

    [Fact]
    public async Task Alert_does_not_move_the_head_when_automatic_motion_is_off()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, new ImmediateClock(), startBackground: false);
        await service.ConnectAsync(Robot);
        service.NotifyAlert();

        await service.TickAsync();

        Assert.DoesNotContain(transport.CompletedWrites, write => write.Channel == StackChanChannel.Motion);
    }

    [Fact]
    public async Task First_connection_does_not_move_then_alert_uses_a_gentle_gesture_after_cooldown()
    {
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        var clock = new ImmediateClock();
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();

        Assert.DoesNotContain(transport.CompletedWrites, write => write.Channel == StackChanChannel.Motion);

        clock.Advance(TimeSpan.FromSeconds(90));
        service.NotifyAlert();

        await service.TickAsync();

        FakeWrite[] motion = transport.CompletedWrites
            .Where(write => write.Channel == StackChanChannel.Motion).ToArray();
        Assert.Equal(3, motion.Length);
        Assert.Equal(StackChanProtocol.Motion(-12, 45, 100), motion[0].Json);
        Assert.Equal(StackChanProtocol.Motion(12, 45, 100), motion[1].Json);
        Assert.Equal(StackChanProtocol.Motion(0, 45, 100), motion[2].Json);
    }

    [Fact]
    public async Task Ready_companion_gestures_only_after_the_shared_ninety_second_cooldown()
    {
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();

        clock.Advance(TimeSpan.FromSeconds(89));
        await service.TickAsync();
        Assert.DoesNotContain(transport.CompletedWrites, write => write.Channel == StackChanChannel.Motion);

        clock.Advance(TimeSpan.FromSeconds(5));
        await service.TickAsync();
        Assert.Equal(3, transport.CompletedWrites.Count(write => write.Channel == StackChanChannel.Motion));

        service.NotifyAlert();
        await service.TickAsync();
        Assert.Equal(3, transport.CompletedWrites.Count(write => write.Channel == StackChanChannel.Motion));

        clock.Advance(TimeSpan.FromSeconds(89));
        await service.TickAsync();
        Assert.Equal(3, transport.CompletedWrites.Count(write => write.Channel == StackChanChannel.Motion));
    }

    [Fact]
    public async Task Ai_activity_transition_uses_a_gentle_nod_after_cooldown()
    {
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();

        clock.Advance(TimeSpan.FromSeconds(90));
        using var ai = CompanionActivity.Enter(StackChanActivity.Ai);
        await service.TickAsync();

        FakeWrite[] motion = transport.CompletedWrites
            .Where(write => write.Channel == StackChanChannel.Motion).ToArray();
        Assert.Equal(3, motion.Length);
        Assert.Equal(StackChanProtocol.Motion(0, 40, 100), motion[0].Json);
        Assert.Equal(StackChanProtocol.Motion(0, 50, 100), motion[1].Json);
        Assert.Equal(StackChanProtocol.Motion(0, 45, 100), motion[2].Json);
    }

    [Fact]
    public async Task Alert_waiting_on_cooldown_shakes_while_the_event_is_still_active()
    {
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();

        clock.Advance(TimeSpan.FromSeconds(89));
        service.NotifyAlert();
        await service.TickAsync();
        Assert.DoesNotContain(transport.CompletedWrites, write => write.Channel == StackChanChannel.Motion);

        clock.Advance(TimeSpan.FromSeconds(5));
        await service.TickAsync();
        Assert.Equal(3, transport.CompletedWrites.Count(write => write.Channel == StackChanChannel.Motion));
    }

    [Fact]
    public async Task Manual_command_holds_automatic_reactions_for_thirty_seconds()
    {
        var clock = new ImmediateClock();
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.SetFaceAsync(StackChanFace.Happy);
        service.NotifyAlert();

        clock.Advance(TimeSpan.FromSeconds(29));
        await service.TickAsync();
        Assert.Single(transport.CompletedWrites);

        clock.Advance(TimeSpan.FromSeconds(1));
        service.NotifyAlert();
        await service.TickAsync();
        Assert.Equal("Alert", service.Snapshot.Activity);
        Assert.Equal(3, transport.CompletedWrites.Count);
    }

    [Fact]
    public async Task Real_alert_bypasses_manual_hold_and_sends_only_public_event_data()
    {
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(EnabledConfig(), transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.SetFaceAsync(StackChanFace.Happy);
        var rule = new AlertRule("private-rule-id", "Private custom label", AlertMetric.DiskSpaceFree,
            10, Above: false, SustainSeconds: 60, Enabled: true);

        service.NotifyAlert(new AlertEvent(rule, 4.2, "C: private path", clock.GetUtcNow().DateTime));
        await service.TickAsync();

        FakeWrite dashboard = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        string json = Encoding.UTF8.GetString(dashboard.Json);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement alert = document.RootElement.GetProperty("companion").GetProperty("alert");
        Assert.Equal("disk-free", alert.GetProperty("kind").GetString());
        Assert.Equal(4.2, alert.GetProperty("value").GetDouble());
        Assert.Equal(10, alert.GetProperty("threshold").GetDouble());
        Assert.False(alert.GetProperty("above").GetBoolean());
        Assert.DoesNotContain("private-rule-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Private custom label", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C: private path", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_mode_keeps_alert_metadata_visible_without_moving()
    {
        var config = EnabledConfig();
        config.StackChanQuietMode = true;
        config.StackChanAllowAutoMotion = true;
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(Robot);
        var rule = new AlertRule("cpu-hot", "CPU hot", AlertMetric.CpuTemperature,
            95, Above: true, SustainSeconds: 30, Enabled: true);
        service.NotifyAlert(new AlertEvent(rule, 97.5, "private", DateTime.UtcNow));

        await service.TickAsync();

        FakeWrite dashboard = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal("Quiet", companion.GetProperty("activity").GetString());
        Assert.Equal("cpu-temperature", companion.GetProperty("alert").GetProperty("kind").GetString());
        Assert.DoesNotContain(transport.CompletedWrites, write => write.Channel == StackChanChannel.Motion);
        Assert.Contains(transport.CompletedWrites, write => write.Channel == StackChanChannel.Rgb &&
            write.Json.SequenceEqual(StackChanProtocol.Rgb("#64748B", "#64748B", 5)));
    }

    [Fact]
    public async Task Alert_metadata_expires_after_twenty_seconds()
    {
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(EnabledConfig(), transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        var rule = new AlertRule("memory", "Memory", AlertMetric.MemoryUsed,
            92, Above: true, SustainSeconds: 120, Enabled: true);
        service.NotifyAlert(new AlertEvent(rule, 96, "", clock.GetUtcNow().DateTime));
        await service.TickAsync();
        transport.CompletedWrites.Clear();

        clock.Advance(TimeSpan.FromSeconds(20));
        await service.TickAsync();

        FakeWrite dashboard = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal("Ready", companion.GetProperty("activity").GetString());
        Assert.False(companion.TryGetProperty("alert", out _));
    }

    [Fact]
    public async Task Alert_clears_at_twenty_seconds_with_a_sixty_second_update_interval()
    {
        var config = EnabledConfig();
        config.StackChanUpdateSeconds = 60;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        var rule = new AlertRule("battery", "Battery", AlertMetric.BatteryCharge,
            15, Above: false, SustainSeconds: 1, Enabled: true);
        service.NotifyAlert(new AlertEvent(rule, 10, "", clock.GetUtcNow().DateTime));
        await service.TickAsync();
        transport.CompletedWrites.Clear();

        clock.Advance(TimeSpan.FromSeconds(20));
        await service.TickAsync();

        FakeWrite dashboard = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal("Ready", companion.GetProperty("activity").GetString());
        Assert.False(companion.TryGetProperty("alert", out _));
    }

    [Fact]
    public async Task Alert_expiry_clears_dashboard_during_an_existing_manual_hold()
    {
        var config = EnabledConfig();
        config.StackChanUpdateSeconds = 60;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.SetFaceAsync(StackChanFace.Happy);
        clock.Advance(TimeSpan.FromSeconds(1));
        var rule = new AlertRule("cpu", "CPU", AlertMetric.CpuUsage,
            90, Above: true, SustainSeconds: 1, Enabled: true);
        service.NotifyAlert(new AlertEvent(rule, 95, "", clock.GetUtcNow().DateTime));
        await service.TickAsync();
        transport.CompletedWrites.Clear();

        clock.Advance(TimeSpan.FromSeconds(20));
        await service.TickAsync();

        Assert.True(clock.GetUtcNow() < DateTimeOffset.Parse("2026-10-13T00:00:30Z"));
        FakeWrite dashboard = Assert.Single(transport.CompletedWrites);
        Assert.Equal(StackChanChannel.Dashboard, dashboard.Channel);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal("Ready", companion.GetProperty("activity").GetString());
        Assert.False(companion.TryGetProperty("alert", out _));
    }

    [Fact]
    public async Task Alert_clear_near_gesture_cooldown_never_moves_during_manual_hold()
    {
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        config.StackChanUpdateSeconds = 60;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();

        clock.Advance(TimeSpan.FromSeconds(70));
        await service.SetFaceAsync(StackChanFace.Happy);
        clock.Advance(TimeSpan.FromSeconds(1));
        var rule = new AlertRule("gpu", "GPU", AlertMetric.GpuUsage,
            90, Above: true, SustainSeconds: 1, Enabled: true);
        service.NotifyAlert(new AlertEvent(rule, 95, "", clock.GetUtcNow().DateTime));
        await service.TickAsync();

        clock.Advance(TimeSpan.FromSeconds(20));
        await service.TickAsync();

        Assert.Equal(DateTimeOffset.Parse("2026-10-13T00:01:31Z"), clock.GetUtcNow());
        Assert.DoesNotContain(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Motion);
        FakeWrite cleared = transport.CompletedWrites.Last();
        Assert.Equal(StackChanChannel.Dashboard, cleared.Channel);
        using JsonDocument document = JsonDocument.Parse(cleared.Json);
        Assert.False(document.RootElement.GetProperty("companion").TryGetProperty("alert", out _));
    }

    [Fact]
    public async Task Real_alert_screen_preserves_manual_rgb_until_hold_expires()
    {
        var config = EnabledConfig();
        config.StackChanUpdateSeconds = 60;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();
        transport.CompletedWrites.Clear();
        await service.SetRgbAsync("#123456", "#654321");

        clock.Advance(TimeSpan.FromSeconds(1));
        var rule = new AlertRule("cpu", "CPU", AlertMetric.CpuUsage,
            90, Above: true, SustainSeconds: 1, Enabled: true);
        service.NotifyAlert(new AlertEvent(rule, 95, "private", clock.GetUtcNow().DateTime));
        await service.TickAsync();

        Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Rgb);
        FakeWrite alertDashboard = transport.CompletedWrites.Last();
        Assert.Equal(StackChanChannel.Dashboard, alertDashboard.Channel);
        using (JsonDocument alertDocument = JsonDocument.Parse(alertDashboard.Json))
        {
            JsonElement companion = alertDocument.RootElement.GetProperty("companion");
            Assert.False(companion.GetProperty("ambient").GetBoolean());
            Assert.True(companion.TryGetProperty("alert", out _));
        }

        clock.Advance(TimeSpan.FromSeconds(20));
        await service.TickAsync();
        Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Rgb);
        FakeWrite clearDashboard = transport.CompletedWrites.Last();
        Assert.Equal(StackChanChannel.Dashboard, clearDashboard.Channel);
        using (JsonDocument clearDocument = JsonDocument.Parse(clearDashboard.Json))
        {
            JsonElement companion = clearDocument.RootElement.GetProperty("companion");
            Assert.False(companion.GetProperty("ambient").GetBoolean());
            Assert.False(companion.TryGetProperty("alert", out _));
        }

        clock.Advance(TimeSpan.FromSeconds(9));
        await service.TickAsync();

        Assert.Equal(2, transport.CompletedWrites.Count(
            write => write.Channel == StackChanChannel.Rgb));
        FakeWrite resumedDashboard = transport.CompletedWrites.Last();
        Assert.Equal(StackChanChannel.Dashboard, resumedDashboard.Channel);
        using JsonDocument resumedDocument = JsonDocument.Parse(resumedDashboard.Json);
        Assert.False(resumedDocument.RootElement.GetProperty("companion")
            .TryGetProperty("ambient", out _));
    }

    [Fact]
    public async Task Manual_rgb_resumes_ready_reaction_at_hold_expiry_with_a_long_interval()
    {
        var config = EnabledConfig();
        config.StackChanUpdateSeconds = 60;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();
        transport.CompletedWrites.Clear();

        await service.SetRgbAsync("#123456", "#654321");
        transport.CompletedWrites.Clear();
        clock.Advance(TimeSpan.FromSeconds(29));
        await service.TickAsync();
        Assert.Empty(transport.CompletedWrites);

        clock.Advance(TimeSpan.FromSeconds(1));
        await service.TickAsync();

        Assert.Contains(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Rgb);
        FakeWrite dashboard = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal("Ready", companion.GetProperty("activity").GetString());
        Assert.False(companion.TryGetProperty("ambient", out _));
    }

    [Fact]
    public async Task Failed_automatic_motion_stops_the_remaining_gesture_and_does_not_report_success()
    {
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        var clock = new ImmediateClock();
        var transport = new FakeTransport { FailOnMotionWrite = 2 };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();
        clock.Advance(TimeSpan.FromSeconds(90));
        service.NotifyAlert();

        await Assert.ThrowsAsync<IOException>(() => service.TickAsync());

        Assert.Equal(2, transport.StartedWrites.Count(write => write.Channel == StackChanChannel.Motion));
        Assert.Equal(1, transport.CompletedWrites.Count(write => write.Channel == StackChanChannel.Motion));
        Assert.NotEqual("Gesture sent", service.Snapshot.Status);
    }

    [Fact]
    public async Task Unchanged_reaction_is_not_written_again_after_the_update_interval()
    {
        var clock = new ImmediateClock();
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.TickAsync();
        int firstWriteCount = transport.CompletedWrites.Count;

        clock.Advance(TimeSpan.FromMinutes(1));
        await service.TickAsync();

        Assert.Equal(firstWriteCount, transport.CompletedWrites.Count);
    }

    [Fact]
    public async Task Dashboard_capability_receives_current_metrics_and_defined_activity_only()
    {
        var transport = new FakeTransport
        {
            Capabilities = AllControls | StackChanCapabilities.Dashboard
        };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        service.ObserveMetrics(new SystemMetrics
        {
            CpuUsage = 12.5f,
            RamPercent = 34.25f,
            GpuUsage = 56.75f,
            NetAdapterName = "private-network-name"
        });
        await service.ConnectAsync(Robot);

        await service.TickAsync();

        FakeWrite dashboard = Assert.Single(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        Assert.Single(document.RootElement.EnumerateObject());
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal(new[] { "cpu", "ram", "gpu", "activity" },
            companion.EnumerateObject().Select(property => property.Name));
        Assert.Equal(12.5f, companion.GetProperty("cpu").GetSingle());
        Assert.Equal(34.25f, companion.GetProperty("ram").GetSingle());
        Assert.Equal(56.75f, companion.GetProperty("gpu").GetSingle());
        Assert.Equal("Ready", companion.GetProperty("activity").GetString());
    }

    [Fact]
    public async Task Dashboard_values_refresh_each_interval_without_repeating_face_or_led()
    {
        var clock = new ImmediateClock();
        var transport = new FakeTransport
        {
            Capabilities = AllControls | StackChanCapabilities.Dashboard
        };
        using var service = new StackChanService(EnabledConfig(), transport, clock, startBackground: false);
        service.ObserveMetrics(new SystemMetrics { CpuUsage = 10, RamPercent = 20, GpuUsage = 30 });
        await service.ConnectAsync(Robot);
        await service.TickAsync();
        transport.CompletedWrites.Clear();

        service.ObserveMetrics(new SystemMetrics { CpuUsage = 40, RamPercent = 50, GpuUsage = 60 });
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.TickAsync();

        FakeWrite dashboard = Assert.Single(transport.CompletedWrites);
        Assert.Equal(StackChanChannel.Dashboard, dashboard.Channel);
        using JsonDocument document = JsonDocument.Parse(dashboard.Json);
        JsonElement companion = document.RootElement.GetProperty("companion");
        Assert.Equal(40f, companion.GetProperty("cpu").GetSingle());
        Assert.Equal(50f, companion.GetProperty("ram").GetSingle());
        Assert.Equal(60f, companion.GetProperty("gpu").GetSingle());
    }

    [Fact]
    public async Task Factory_capabilities_never_transmit_live_dashboard_metrics()
    {
        var transport = new FakeTransport { Capabilities = AllControls };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        service.ObserveMetrics(new SystemMetrics { CpuUsage = 87, RamPercent = 65, GpuUsage = 43 });
        await service.ConnectAsync(Robot);

        await service.TickAsync();

        Assert.DoesNotContain(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Dashboard);
        Assert.Contains(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Avatar);
        Assert.Contains(transport.CompletedWrites,
            write => write.Channel == StackChanChannel.Rgb);
    }

    [Fact]
    public async Task Link_loss_cancels_an_in_flight_command()
    {
        var transport = new FakeTransport { BlockWrites = true };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        Task command = service.SetFaceAsync(StackChanFace.Happy);
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        transport.RaiseDisconnected();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.Empty(transport.CompletedWrites);
        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
    }

    [Fact]
    public async Task Reconnect_does_not_allow_an_old_session_command_to_complete()
    {
        var transport = new FakeTransport { BlockWrites = true };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        Task oldCommand = service.SetFaceAsync(StackChanFace.Happy);
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.RaiseDisconnected();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldCommand);

        transport.BlockWrites = false;
        await service.ConnectAsync(Robot);
        await service.SetFaceAsync(StackChanFace.Neutral);

        FakeWrite completed = Assert.Single(transport.CompletedWrites);
        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Neutral), completed.Json);
    }

    [Fact]
    public async Task A_second_manual_command_is_rejected_while_the_writer_is_busy()
    {
        var transport = new FakeTransport { BlockWrites = true };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        Task first = service.SetFaceAsync(StackChanFace.Happy);
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetRgbAsync("#000000", "#FFFFFF"));

        Assert.Contains("already being sent", error.Message, StringComparison.OrdinalIgnoreCase);
        transport.ReleaseWrites();
        await first;
    }

    [Fact]
    public async Task A_control_missing_from_the_connected_device_is_rejected()
    {
        var transport = new FakeTransport { Capabilities = StackChanCapabilities.Avatar };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetRgbAsync("#000000", "#FFFFFF"));

        Assert.Contains("does not expose", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.StartedWrites);
    }

    [Fact]
    public async Task Automatic_reconnect_connects_only_to_the_remembered_address()
    {
        var other = new StackChanDevice(0xAABBCCDDEEFF, "Other StackChan");
        var config = EnabledConfig();
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = Robot.AddressText;
        var transport = new FakeTransport { Devices = [other, Robot] };
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.TickAsync();

        Assert.Equal(Robot, Assert.Single(transport.ConnectedDevices));
    }

    [Fact]
    public async Task Automatic_reconnect_ignores_nearby_devices_when_the_remembered_robot_is_absent()
    {
        var other = new StackChanDevice(0xAABBCCDDEEFF, "Other StackChan");
        var config = EnabledConfig();
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = Robot.AddressText;
        var transport = new FakeTransport { Devices = [other] };
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.TickAsync();

        Assert.Empty(transport.ConnectedDevices);
    }

    private static byte[] LastWrite(FakeTransport transport, StackChanChannel channel) =>
        transport.CompletedWrites.Last(write => write.Channel == channel).Json;

    [Fact]
    public async Task alert_outranks_onlooker_and_welcome()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        service.SetPresence(PresenceReaction.Onlooker);
        service.NotifyAlert();

        await service.TickAsync();

        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Concerned), LastWrite(transport, StackChanChannel.Avatar));
        Assert.Equal(StackChanProtocol.Rgb("#FF7043", "#FF7043", 30), LastWrite(transport, StackChanChannel.Rgb));
    }

    [Fact]
    public async Task onlooker_shows_concerned_face_and_red_lights()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        service.SetPresence(PresenceReaction.Welcome);
        await service.TickAsync();
        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Happy), LastWrite(transport, StackChanChannel.Avatar));

        service.SetPresence(PresenceReaction.Onlooker);
        await service.TickAsync();

        Assert.Equal(PresenceReaction.Onlooker, service.Presence);
        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Concerned), LastWrite(transport, StackChanChannel.Avatar));
        Assert.Equal(StackChanProtocol.Rgb("#F43F5E", "#F43F5E", 30), LastWrite(transport, StackChanChannel.Rgb));
    }

    [Fact]
    public async Task quiet_shows_presence_reaction_with_dim_leds()
    {
        var config = EnabledConfig();
        config.StackChanQuietMode = true;
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(Robot);
        service.SetPresence(PresenceReaction.Onlooker);

        await service.TickAsync();

        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Concerned), LastWrite(transport, StackChanChannel.Avatar));
        Assert.Equal(StackChanProtocol.Rgb("#F43F5E", "#F43F5E", 5), LastWrite(transport, StackChanChannel.Rgb));
    }

    [Fact]
    public async Task doze_yields_to_alert()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        service.SetPresence(PresenceReaction.Doze);
        await service.TickAsync();
        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Sleepy), LastWrite(transport, StackChanChannel.Avatar));

        service.NotifyAlert();
        await service.TickAsync();

        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Concerned), LastWrite(transport, StackChanChannel.Avatar));
    }

    [Fact]
    public async Task automatic_head_aim_waits_out_a_manual_hold()
    {
        var clock = new ImmediateClock();
        var config = EnabledConfig();
        config.StackChanAllowAutoMotion = true;
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.MoveAsync(30, 50);

        Assert.False(await service.AimHeadAsync(0, 25));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await service.AimHeadAsync(0, 25));
    }

    [Fact]
    public async Task manual_command_waits_briefly_for_an_automatic_one()
    {
        var transport = new FakeTransport { BlockWrites = true, Capabilities = AllControls | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        Task<bool> automatic = service.SendPresenceAsync(StackChanProtocol.Camera(true));
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task manual = service.SetFaceAsync(StackChanFace.Happy);
        await Task.Delay(100);
        transport.ReleaseWrites();

        await manual;
        Assert.True(await automatic);
    }

    [Fact]
    public async Task doze_outranks_everyday_activity()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        using var ai = CompanionActivity.Enter(StackChanActivity.Ai);
        await service.ConnectAsync(Robot);
        service.SetPresence(PresenceReaction.Doze);

        await service.TickAsync();

        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Sleepy), LastWrite(transport, StackChanChannel.Avatar));
        Assert.Equal("AI working", service.Snapshot.Activity);
    }

    [Fact]
    public async Task presence_reaction_overrides_a_manual_hold_but_doze_does_not()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.SetFaceAsync(StackChanFace.Happy);
        service.SetPresence(PresenceReaction.Doze);
        await service.TickAsync();
        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Happy), LastWrite(transport, StackChanChannel.Avatar));

        service.SetPresence(PresenceReaction.Onlooker);
        await service.TickAsync();

        Assert.Equal(StackChanProtocol.Avatar(StackChanFace.Concerned), LastWrite(transport, StackChanChannel.Avatar));
    }

    [Theory]
    [InlineData(StackChanUsbFailure.NotAccepting, "Robot not accepting USB data")]
    [InlineData(StackChanUsbFailure.NoCompanion, "Companion not running")]
    public async Task Automatic_usb_reconnect_pauses_when_the_robot_cannot_answer(StackChanUsbFailure failure,
        string status)
    {
        var clock = new ImmediateClock();
        var config = EnabledConfig();
        config.StackChanAutoStartCompanion = false;
        config.StackChanUseUsb = true;
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport
        {
            Devices = [UsbRobot],
            ConnectFailure = new StackChanUsbException(failure, "synthetic")
        };
        using var service = new StackChanService(config, transport, clock, startBackground: false);

        await Assert.ThrowsAsync<StackChanUsbException>(() => service.TickAsync());
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.TickAsync();

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(status, service.Snapshot.Status);
        Assert.Contains("Automatic reconnect is paused", service.Snapshot.Error);
        Assert.Contains("Start temporary USB companion", service.Snapshot.Error);
    }

    [Fact]
    public async Task Automatic_usb_reconnect_starts_the_companion_after_a_robot_restart()
    {
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport
        {
            Devices = [UsbRobot],
            ConnectFailure = new StackChanUsbException(StackChanUsbFailure.NoCompanion, "synthetic")
        };
        using var service = new StackChanService(config, transport, new ImmediateClock(), startBackground: false);

        await service.TickAsync();

        Assert.Equal(1, transport.StartCompanionCalls);
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
    }

    [Fact]
    public async Task Automatic_start_retries_every_two_minutes_while_the_robot_is_not_reading()
    {
        var clock = new ImmediateClock();
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport
        {
            Devices = [UsbRobot],
            ConnectFailure = new StackChanUsbException(StackChanUsbFailure.NotAccepting, "synthetic")
        };
        using var service = new StackChanService(config, transport, clock, startBackground: false);

        await Assert.ThrowsAsync<StackChanUsbException>(() => service.TickAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.TickAsync();
        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(0, transport.StartCompanionCalls);

        transport.ConnectFailure = new StackChanUsbException(StackChanUsbFailure.NoCompanion, "synthetic");
        clock.Advance(TimeSpan.FromMinutes(1.5));
        await service.TickAsync();

        Assert.Equal(2, transport.ConnectCalls);
        Assert.Equal(1, transport.StartCompanionCalls);
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
    }

    [Fact]
    public async Task Manual_start_resumes_paused_automatic_usb_reconnect()
    {
        var clock = new ImmediateClock();
        var config = EnabledConfig();
        config.StackChanAutoStartCompanion = false;
        config.StackChanUseUsb = true;
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport
        {
            Devices = [UsbRobot],
            ConnectFailure = new StackChanUsbException(StackChanUsbFailure.NotAccepting, "synthetic")
        };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await Assert.ThrowsAsync<StackChanUsbException>(() => service.TickAsync());

        await service.StartUsbCompanionAsync(UsbRobot);
        transport.RaiseDisconnected();
        transport.ConnectFailure = null;
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.TickAsync();

        Assert.Equal(2, transport.ConnectCalls);
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
    }

    [Fact]
    public async Task Automatic_usb_reconnect_keeps_retrying_when_the_port_is_missing()
    {
        var clock = new ImmediateClock();
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport
        {
            Devices = [UsbRobot],
            ConnectFailure = new StackChanUsbException(StackChanUsbFailure.PortMissing, "synthetic")
        };
        using var service = new StackChanService(config, transport, clock, startBackground: false);

        await Assert.ThrowsAsync<StackChanUsbException>(() => service.TickAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<StackChanUsbException>(() => service.TickAsync());

        Assert.Equal(2, transport.ConnectCalls);
        Assert.Equal("USB port not found", service.Snapshot.Status);
    }

    [Theory]
    [InlineData(StackChanUsbFailure.PortMissing, "USB port not found")]
    [InlineData(StackChanUsbFailure.PortBusy, "USB port in use")]
    [InlineData(StackChanUsbFailure.NotAccepting, "Robot not accepting USB data")]
    [InlineData(StackChanUsbFailure.NoCompanion, "Companion not running")]
    public async Task Manual_usb_connect_failure_names_the_cause(StackChanUsbFailure failure, string status)
    {
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        var transport = new FakeTransport { ConnectFailure = new StackChanUsbException(failure, "synthetic") };
        using var service = new StackChanService(config, transport, startBackground: false);

        await Assert.ThrowsAsync<StackChanUsbException>(() => service.ConnectAsync(UsbRobot));

        Assert.Equal(StackChanConnectionState.Faulted, service.Snapshot.State);
        Assert.Equal(status, service.Snapshot.Status);
        Assert.DoesNotContain("Automatic reconnect is paused", service.Snapshot.Error);
    }

    [Fact]
    public async Task Explicit_disconnect_suppresses_automatic_reconnect()
    {
        var clock = new ImmediateClock();
        var config = EnabledConfig();
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = Robot.AddressText;
        var transport = new FakeTransport { Devices = [Robot] };
        using var service = new StackChanService(config, transport, clock, startBackground: false);
        await service.ConnectAsync(Robot);
        await service.DisconnectAsync();
        int discoveriesBeforeTick = transport.DiscoverCalls;

        clock.Advance(TimeSpan.FromMinutes(1));
        await service.TickAsync();

        Assert.Equal(discoveriesBeforeTick, transport.DiscoverCalls);
        Assert.Single(transport.ConnectedDevices);
    }

    [Fact]
    public async Task Cancelling_discovery_restores_the_disconnected_state()
    {
        var transport = new FakeTransport { BlockDiscovery = true };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<StackChanDevice>> discovery = service.DiscoverAsync(cancellation.Token);
        await transport.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
        Assert.Equal("Scan cancelled", service.Snapshot.Status);
    }

    [Fact]
    public async Task Disabled_companion_rejects_manual_connection()
    {
        var transport = new FakeTransport();
        using var service = new StackChanService(new AppConfig(), transport, startBackground: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(Robot));

        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
    }

    [Fact]
    public async Task Disabling_a_connected_companion_disconnects_and_blocks_manual_control()
    {
        var config = EnabledConfig();
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(Robot);
        int disconnectsBeforeDisable = transport.DisconnectCalls;

        config.StackChanEnabled = false;
        service.RefreshSettings();

        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
        Assert.True(transport.DisconnectCalls > disconnectsBeforeDisable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetFaceAsync(StackChanFace.Happy));
    }

    [Fact]
    public async Task Disabling_during_connection_cancels_the_attempt_before_it_can_publish_connected()
    {
        var config = EnabledConfig();
        var transport = new FakeTransport { BlockConnect = true };
        using var service = new StackChanService(config, transport, startBackground: false);
        Task connection = service.ConnectAsync(Robot);
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        config.StackChanEnabled = false;
        service.RefreshSettings();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
    }

    [Fact]
    public async Task Explicit_disconnect_during_scan_cannot_restore_the_old_connected_state()
    {
        var transport = new FakeTransport { BlockDiscovery = true };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);
        await service.ConnectAsync(Robot);
        Task<IReadOnlyList<StackChanDevice>> scan = service.DiscoverAsync();
        await transport.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disconnect = service.DisconnectAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        await disconnect;
        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
        Assert.Equal(StackChanCapabilities.None, service.Snapshot.Capabilities);
    }

    [Fact]
    public async Task Link_loss_reported_inside_connect_cannot_publish_a_connected_snapshot()
    {
        var transport = new FakeTransport { DisconnectDuringConnect = true };
        using var service = new StackChanService(EnabledConfig(), transport, startBackground: false);

        _ = await Record.ExceptionAsync(() => service.ConnectAsync(Robot));

        Assert.NotEqual(StackChanConnectionState.Connected, service.Snapshot.State);
        Assert.Equal(StackChanCapabilities.None, service.Snapshot.Capabilities);
    }

    [Fact]
    public async Task Automatic_reconnect_does_not_cancel_a_manual_connection_started_after_discovery()
    {
        var manualRobot = new StackChanDevice(0xAABBCCDDEEFF, "My StackChan");
        var config = EnabledConfig();
        config.StackChanAutoReconnect = true;
        config.StackChanDeviceAddress = Robot.AddressText;
        var transport = new FakeTransport { BlockConnect = true };
        using var service = new StackChanService(config, transport, startBackground: false);
        Task? manualConnection = null;
        transport.Devices = new CallbackDeviceList([Robot],
            () => manualConnection = service.ConnectAsync(manualRobot));

        Task automaticTick = service.TickAsync();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAny(automaticTick, transport.SecondConnectStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));
        transport.ReleaseConnects();

        Assert.NotNull(manualConnection);
        await manualConnection!;
        _ = await Record.ExceptionAsync(() => automaticTick);
        Assert.Equal(manualRobot.AddressText, config.StackChanDeviceAddress);
        Assert.Equal(manualRobot.Name, service.Snapshot.DeviceName);
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
    }

    [Fact]
    public async Task Reconnect_setting_off_never_scans_for_the_remembered_robot()
    {
        var config = EnabledConfig();
        config.StackChanAutoReconnect = false;
        config.StackChanDeviceAddress = Robot.AddressText;
        var transport = new FakeTransport { Devices = [Robot] };
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.TickAsync();

        Assert.Equal(0, transport.DiscoverCalls);
        Assert.Equal(0, transport.ConnectCalls);
    }

    [Fact]
    public void Usb_identity_stays_stable_when_windows_assigns_a_different_com_port()
    {
        var before = UsbRobot;
        var after = UsbRobot with { PortName = "COM12" };

        Assert.Equal("USB:112233445566", before.AddressText);
        Assert.Equal(before.AddressText, after.AddressText);
        Assert.NotEqual(before.ToString(), after.ToString());
    }

    [Fact]
    public async Task Usb_reconnect_never_matches_a_bluetooth_device_with_the_same_mac()
    {
        StackChanDevice bluetoothTwin = Robot with { Name = "Bluetooth twin" };
        var config = EnabledConfig();
        config.StackChanAutoReconnect = true;
        config.StackChanUseUsb = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport { Devices = [bluetoothTwin] };
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.TickAsync();

        Assert.Empty(transport.ConnectedDevices);
        Assert.Equal(0, transport.StartCompanionCalls);
    }

    [Fact]
    public async Task Starting_usb_companion_passes_the_bundled_script_to_the_usb_starter()
    {
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        var transport = new FakeTransport
        {
            UsbCapabilities = AllControls | StackChanCapabilities.Dashboard
        };
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.StartUsbCompanionAsync(UsbRobot);

        Assert.Equal(1, transport.StartCompanionCalls);
        Assert.Equal(UsbRobot, transport.StartedUsbDevice);
        Assert.NotNull(transport.StartedScript);
        Assert.Contains("MicaStats StackChan companion",
            Encoding.UTF8.GetString(transport.StartedScript!), StringComparison.Ordinal);
        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
        Assert.Equal(StackChanConnectionKind.Usb, service.Snapshot.Connection);
        Assert.Equal(UsbRobot.AddressText, config.StackChanDeviceAddress);
    }

    [Fact]
    public async Task Starting_usb_companion_rejects_a_bluetooth_device_before_transport_io()
    {
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        var transport = new FakeTransport();
        using var service = new StackChanService(config, transport, startBackground: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartUsbCompanionAsync(Robot));

        Assert.Equal(0, transport.StartCompanionCalls);
        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(0, transport.DisconnectCalls);
    }

    [Fact]
    public async Task Starting_usb_companion_requires_a_transport_with_usb_start_support()
    {
        var config = EnabledConfig();
        config.StackChanUseUsb = true;
        var transport = new TransportWithoutUsbStarter();
        using var service = new StackChanService(config, transport, startBackground: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartUsbCompanionAsync(UsbRobot));

        Assert.Equal(0, transport.ConnectCalls);
        Assert.Equal(0, transport.DisconnectCalls);
    }

    [Fact]
    public async Task Automatic_reconnect_never_starts_the_transient_usb_companion()
    {
        var config = EnabledConfig();
        config.StackChanAutoReconnect = true;
        config.StackChanUseUsb = true;
        config.StackChanDeviceAddress = UsbRobot.AddressText;
        var transport = new FakeTransport { Devices = [UsbRobot] };
        using var service = new StackChanService(config, transport, startBackground: false);

        await service.TickAsync();

        Assert.Equal(0, transport.StartCompanionCalls);
        Assert.Equal(1, transport.ConnectCalls);
        Assert.Equal(UsbRobot, Assert.Single(transport.ConnectedDevices));
        Assert.Equal(StackChanConnectionState.Connected, service.Snapshot.State);
        Assert.Equal(StackChanConnectionKind.Usb, service.Snapshot.Connection);
    }

    [Fact]
    public async Task Changing_connection_mode_cancels_an_in_flight_write_and_disconnects()
    {
        var config = EnabledConfig();
        var transport = new FakeTransport { BlockWrites = true };
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(Robot);
        Task write = service.SetFaceAsync(StackChanFace.Happy);
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int disconnectsBeforeChange = transport.DisconnectCalls;

        config.StackChanUseUsb = true;
        service.RefreshSettings();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(StackChanConnectionState.Disconnected, service.Snapshot.State);
        Assert.True(transport.DisconnectCalls > disconnectsBeforeChange);
    }

    [Fact]
    public async Task Changing_connection_mode_cancels_an_in_flight_connection()
    {
        var config = EnabledConfig();
        var transport = new FakeTransport { BlockConnect = true };
        using var service = new StackChanService(config, transport, startBackground: false);
        Task connection = service.ConnectAsync(Robot);
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        config.StackChanUseUsb = true;
        service.RefreshSettings();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        Assert.NotEqual(StackChanConnectionState.Connected, service.Snapshot.State);
    }

    private static AppConfig EnabledConfig() => new()
    {
        StackChanEnabled = true,
        StackChanReactToAlerts = true,
        StackChanReactToAi = true,
        StackChanReactToMeeting = true,
        StackChanReactToLoad = true
    };

    private sealed record FakeWrite(StackChanChannel Channel, byte[] Json);

    private sealed class FakeTransport : IStackChanTransport, IStackChanUsbCompanionStarter
    {
        private TaskCompletionSource _writeRelease = NewSignal();
        private readonly TaskCompletionSource _connectRelease = NewSignal();
        public event Action? Disconnected;
        public StackChanCapabilities Capabilities { get; set; } = AllControls;
        public IReadOnlyList<StackChanDevice> Devices { get; set; } = Array.Empty<StackChanDevice>();
        public bool BlockDiscovery { get; set; }
        public bool BlockConnect { get; set; }
        public bool BlockWrites { get; set; }
        public bool DisconnectDuringConnect { get; set; }
        public int FailOnMotionWrite { get; set; }
        public Exception? ConnectFailure { get; set; }
        public int DiscoverCalls { get; private set; }
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int StartCompanionCalls { get; private set; }
        public StackChanCapabilities UsbCapabilities { get; set; } =
            AllControls | StackChanCapabilities.Dashboard;
        public StackChanDevice? StartedUsbDevice { get; private set; }
        public byte[]? StartedScript { get; private set; }
        public List<StackChanDevice> ConnectedDevices { get; } = [];
        public List<FakeWrite> StartedWrites { get; } = [];
        public List<FakeWrite> CompletedWrites { get; } = [];
        public TaskCompletionSource DiscoveryStarted { get; } = NewSignal();
        public TaskCompletionSource ConnectStarted { get; } = NewSignal();
        public TaskCompletionSource SecondConnectStarted { get; } = NewSignal();
        public TaskCompletionSource WriteStarted { get; } = NewSignal();

        public async Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration,
            CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            DiscoveryStarted.TrySetResult();
            if (BlockDiscovery)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Devices;
        }

        public async Task<StackChanCapabilities> ConnectAsync(StackChanDevice device,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCalls++;
            ConnectedDevices.Add(device);
            ConnectStarted.TrySetResult();
            if (ConnectCalls == 2) SecondConnectStarted.TrySetResult();
            if (DisconnectDuringConnect) Disconnected?.Invoke();
            if (BlockConnect)
                await _connectRelease.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ConnectFailure != null) throw ConnectFailure;
            return Capabilities;
        }

        public async Task WriteAsync(StackChanChannel channel, byte[] json,
            CancellationToken cancellationToken)
        {
            var write = new FakeWrite(channel, (byte[])json.Clone());
            StartedWrites.Add(write);
            WriteStarted.TrySetResult();
            if (BlockWrites)
                await _writeRelease.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (channel == StackChanChannel.Motion && FailOnMotionWrite > 0 &&
                StartedWrites.Count(write => write.Channel == StackChanChannel.Motion) == FailOnMotionWrite)
                throw new IOException("Synthetic motion write failure.");
            CompletedWrites.Add(write);
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            return Task.CompletedTask;
        }

        public Task<StackChanCapabilities> StartCompanionAsync(StackChanDevice device, byte[] script,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCompanionCalls++;
            StartedUsbDevice = device;
            StartedScript = (byte[])script.Clone();
            return Task.FromResult(UsbCapabilities);
        }
        public void RaiseDisconnected() => Disconnected?.Invoke();
        public void ReleaseWrites() => _writeRelease.TrySetResult();
        public void ReleaseConnects() => _connectRelease.TrySetResult();
        public void Dispose()
        {
            _writeRelease.TrySetCanceled();
            _connectRelease.TrySetCanceled();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TransportWithoutUsbStarter : IStackChanTransport
    {
        public event Action? Disconnected { add { } remove { } }
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StackChanDevice>>(Array.Empty<StackChanDevice>());
        public Task<StackChanCapabilities> ConnectAsync(StackChanDevice device,
            CancellationToken cancellationToken)
        {
            ConnectCalls++;
            return Task.FromResult(AllControls);
        }
        public Task WriteAsync(StackChanChannel channel, byte[] json,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class CallbackDeviceList(IReadOnlyList<StackChanDevice> devices, Action callback)
        : IReadOnlyList<StackChanDevice>
    {
        private int _called;
        public int Count => devices.Count;
        public StackChanDevice this[int index] => devices[index];

        public IEnumerator<StackChanDevice> GetEnumerator()
        {
            if (Interlocked.Exchange(ref _called, 1) == 0) callback();
            return devices.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ImmediateClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ImmediateTimer();
            callback(state);
            return timer;
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
