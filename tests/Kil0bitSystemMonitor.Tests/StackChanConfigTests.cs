using System.Text.Json;
using Kil0bitSystemMonitor.Models;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanConfigTests
{
    [Fact]
    public void Companion_defaults_are_safe_for_an_existing_install()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{}")!;

        Assert.False(config.StackChanEnabled);
        Assert.False(config.StackChanAutoReconnect);
        Assert.False(config.StackChanUseUsb);
        Assert.False(config.StackChanAllowAutoMotion);
        Assert.False(config.StackChanQuietMode);
        Assert.Empty(config.StackChanDeviceAddress);
        Assert.Empty(config.StackChanDeviceName);
        Assert.Equal(30, config.StackChanLedBrightness);
        Assert.Equal(5, config.StackChanUpdateSeconds);
    }

    [Fact]
    public void Companion_reaction_preferences_are_on_by_default()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{}")!;

        Assert.True(config.StackChanReactToLoad);
        Assert.True(config.StackChanReactToAlerts);
        Assert.True(config.StackChanReactToAi);
        Assert.True(config.StackChanReactToMeeting);
    }

    [Fact]
    public void Companion_preferences_survive_a_json_round_trip()
    {
        var config = new AppConfig
        {
            StackChanEnabled = true,
            StackChanAutoReconnect = true,
            StackChanUseUsb = true,
            StackChanDeviceAddress = "112233445566",
            StackChanDeviceName = "Desk StackChan",
            StackChanReactToLoad = false,
            StackChanReactToAlerts = false,
            StackChanReactToAi = false,
            StackChanReactToMeeting = false,
            StackChanAllowAutoMotion = true,
            StackChanQuietMode = true,
            StackChanLedBrightness = 72,
            StackChanUpdateSeconds = 12
        };

        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

        Assert.True(restored.StackChanEnabled);
        Assert.True(restored.StackChanAutoReconnect);
        Assert.True(restored.StackChanUseUsb);
        Assert.Equal("112233445566", restored.StackChanDeviceAddress);
        Assert.Equal("Desk StackChan", restored.StackChanDeviceName);
        Assert.False(restored.StackChanReactToLoad);
        Assert.False(restored.StackChanReactToAlerts);
        Assert.False(restored.StackChanReactToAi);
        Assert.False(restored.StackChanReactToMeeting);
        Assert.True(restored.StackChanAllowAutoMotion);
        Assert.True(restored.StackChanQuietMode);
        Assert.Equal(72, restored.StackChanLedBrightness);
        Assert.Equal(12, restored.StackChanUpdateSeconds);
    }

    [Fact]
    public void Presence_is_off_with_guarding_defaults_for_an_existing_install()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{}")!;

        Assert.False(config.StackChanPresenceEnabled);
        Assert.True(config.StackChanGuardVault);
        Assert.True(config.StackChanGuardBlur);
        Assert.False(config.StackChanLockWindowsWhenAway);
        Assert.Equal(5, config.StackChanLockWindowsMinutes);
        Assert.Equal(50, config.StackChanBreakMinutes);
        Assert.False(config.StackChanHeadDirectionChecked);
    }

    [Fact]
    public void Presence_preferences_survive_a_json_round_trip_and_clamp()
    {
        var config = new AppConfig
        {
            StackChanPresenceEnabled = true,
            StackChanGuardVault = false,
            StackChanGuardBlur = false,
            StackChanLockWindowsWhenAway = true,
            StackChanLockWindowsMinutes = 0,
            StackChanBreakMinutes = 500,
            StackChanHeadInvertYaw = true,
            StackChanHeadInvertPitch = true,
            StackChanHeadDirectionChecked = true
        };

        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

        Assert.True(restored.StackChanPresenceEnabled);
        Assert.False(restored.StackChanGuardVault);
        Assert.False(restored.StackChanGuardBlur);
        Assert.True(restored.StackChanLockWindowsWhenAway);
        Assert.Equal(1, restored.StackChanLockWindowsMinutes);
        Assert.Equal(180, restored.StackChanBreakMinutes);
        Assert.True(restored.StackChanHeadInvertYaw);
        Assert.True(restored.StackChanHeadInvertPitch);
        Assert.True(restored.StackChanHeadDirectionChecked);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(101, 100)]
    public void Led_brightness_is_clamped_to_a_percentage(int requested, int expected)
    {
        var config = new AppConfig { StackChanLedBrightness = requested };

        Assert.Equal(expected, config.StackChanLedBrightness);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(61, 60)]
    public void Companion_update_interval_is_clamped_to_supported_seconds(int requested, int expected)
    {
        var config = new AppConfig { StackChanUpdateSeconds = requested };

        Assert.Equal(expected, config.StackChanUpdateSeconds);
    }

    [Fact]
    public void Remembered_device_text_is_trimmed_before_persistence()
    {
        var config = new AppConfig
        {
            StackChanDeviceAddress = " 112233445566 ",
            StackChanDeviceName = " Desk StackChan "
        };

        Assert.Equal("112233445566", config.StackChanDeviceAddress);
        Assert.Equal("Desk StackChan", config.StackChanDeviceName);
    }
}
