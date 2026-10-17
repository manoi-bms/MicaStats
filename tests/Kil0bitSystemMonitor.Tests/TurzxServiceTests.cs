using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Turzx;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class TurzxServiceTests
{
    [Fact]
    public void AppConfig_clamps_and_normalizes_turzx_preferences()
    {
        var config = new AppConfig
        {
            TurzxBrightness = 150,
            TurzxOrientation = "invalid",
            TurzxViewMode = "unknown",
            TurzxPortName = " com10 ",
        };

        Assert.Equal(100, config.TurzxBrightness);
        Assert.Equal("Landscape", config.TurzxOrientation);
        Assert.Equal("Dashboard", config.TurzxViewMode);
        Assert.Equal("COM10", config.TurzxPortName);

        config.TurzxBrightness = -10;
        config.TurzxOrientation = "Portrait";
        config.TurzxViewMode = "Thermal";
        Assert.Equal(0, config.TurzxBrightness);
        Assert.Equal("Portrait", config.TurzxOrientation);
        Assert.Equal("Thermal", config.TurzxViewMode);
    }

    [Fact]
    public async Task Connect_initializes_screen_and_pushes_full_frame_then_dirty_subrects()
    {
        var config = new AppConfig
        {
            TurzxEnabled = true,
            TurzxOrientation = "Landscape",
            TurzxBrightness = 80,
            TurzxViewMode = "Dashboard",
        };
        var transport = new FakeTurzxTransport();
        using var service = new TurzxDisplayService(config, transport, startBackground: false);

        var device = new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2");
        await service.ConnectAsync(device);

        Assert.Equal(TurzxConnectionState.Connected, service.Snapshot.State);
        Assert.Equal("COM10", service.Snapshot.PortName);
        Assert.Contains(transport.Commands, cmd => cmd.Length == 6 && cmd[5] == TurzxProtocol.CommandScreenOn);
        Assert.Contains(transport.Commands, cmd => cmd.Length == 16 && cmd[5] == TurzxProtocol.CommandSetOrientation);
        Assert.Contains(transport.Commands, cmd => cmd.Length == 6 && cmd[5] == TurzxProtocol.CommandSetBrightness);
        Assert.Single(transport.RectWrites);
        Assert.Equal(new TurzxRect(0, 0, 480, 320), transport.RectWrites[0]);

        transport.RectWrites.Clear();
        await service.RenderMetricsNowAsync(new SystemMetrics
        {
            CpuUsage = 68f,
            GpuUsage = 44f,
            RamPercent = 62f,
            DiskPercent = 50f,
        });

        Assert.NotEmpty(transport.RectWrites);
        int dirtyPixels = 0;
        foreach (TurzxRect r in transport.RectWrites)
            dirtyPixels += r.Width * r.Height;
        Assert.True(dirtyPixels < 480 * 320);

        await service.DisconnectAsync();
        Assert.Equal(TurzxConnectionState.Disconnected, service.Snapshot.State);
        Assert.Contains(transport.Commands, cmd => cmd.Length == 6 && cmd[5] == TurzxProtocol.CommandScreenOff);
    }

    [Fact]
    public async Task Changing_orientation_or_brightness_syncs_hardware_and_redraws_full_screen()
    {
        var config = new AppConfig
        {
            TurzxEnabled = true,
            TurzxOrientation = "Landscape",
            TurzxBrightness = 85,
        };
        var transport = new FakeTurzxTransport();
        using var service = new TurzxDisplayService(config, transport, startBackground: false);

        await service.ConnectAsync(new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2"));
        transport.Commands.Clear();
        transport.RectWrites.Clear();

        config.TurzxOrientation = "Portrait";
        config.TurzxBrightness = 50;
        await service.ApplySettingsNowAsync();

        Assert.Contains(transport.Commands, cmd => cmd.Length == 16 && cmd[5] == TurzxProtocol.CommandSetOrientation);
        Assert.Contains(transport.Commands, cmd => cmd.Length == 6 && cmd[5] == TurzxProtocol.CommandSetBrightness);
        Assert.Contains(new TurzxRect(0, 0, 320, 480), transport.RectWrites);
    }

    private sealed class FakeTurzxTransport : ITurzxTransport
    {
        public List<byte[]> Commands { get; } = new();
        public List<TurzxRect> RectWrites { get; } = new();
        public event Action? Disconnected { add { } remove { } }

        public Task<IReadOnlyList<TurzxDevice>> DiscoverAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TurzxDevice>>(new[]
            {
                new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2"),
            });

        public Task ConnectAsync(TurzxDevice device, CancellationToken token) => Task.CompletedTask;

        public Task WriteAsync(byte[] payload, CancellationToken token)
        {
            Commands.Add(payload);
            return Task.CompletedTask;
        }

        public Task WriteRectAsync(ushort[] frame, int frameWidth, TurzxRect rect, CancellationToken token)
        {
            RectWrites.Add(rect);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public void Dispose() { }
    }
}
