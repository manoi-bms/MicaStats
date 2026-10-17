using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Turzx;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class TurzxUsbTransportTests
{
    [Theory]
    [InlineData(@"USB\VID_1A86&PID_5722\USB35INCHIPSV2", true)]
    [InlineData(@"USB\VID_1A86&PID_7523\5&123456&0&1", true)]
    [InlineData(@"USB\VID_303A&PID_1001\64E833834E18", false)]
    [InlineData(@"PCI\VEN_8086&DEV_1234", false)]
    public void IsTurzxUsb_matches_supported_usb_identifiers(string pnpId, bool expected)
    {
        Assert.Equal(expected, TurzxUsbTransport.IsTurzxUsb(pnpId));
    }

    [Theory]
    [InlineData("COM10", @"\\.\COM10")]
    [InlineData("com3", @"\\.\COM3")]
    public void FormatPortPath_normalizes_valid_com_ports(string portName, string expected)
    {
        Assert.Equal(expected, TurzxUsbTransport.FormatPortPath(portName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("COM0")]
    [InlineData("LPT1")]
    [InlineData("COM9999")]
    public void FormatPortPath_rejects_invalid_com_ports(string invalidPort)
    {
        Assert.Throws<ArgumentException>(() => TurzxUsbTransport.FormatPortPath(invalidPort));
    }

    [Fact]
    public async Task Connect_write_and_disconnect_serialize_endpoint_operations()
    {
        var endpoint = new FakeEndpoint();
        using var transport = new TurzxUsbTransport(
            () => new[] { new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2") },
            (_, _) => Task.FromResult<ITurzxSerialEndpoint>(endpoint));

        IReadOnlyList<TurzxDevice> discovered = await transport.DiscoverAsync(CancellationToken.None);
        Assert.Single(discovered);
        Assert.Equal("COM10", discovered[0].PortName);

        await transport.ConnectAsync(discovered[0], CancellationToken.None);
        await transport.WriteAsync(TurzxProtocol.BuildScreenOn(), CancellationToken.None);

        Assert.Single(endpoint.Writes);
        Assert.Equal(TurzxProtocol.BuildScreenOn(), endpoint.Writes[0]);

        await transport.DisconnectAsync();
        Assert.True(endpoint.Disposed);
    }

    [Fact]
    public async Task Write_failure_retires_endpoint_and_raises_Disconnected()
    {
        var endpoint = new FakeEndpoint { ThrowOnWrite = true };
        using var transport = new TurzxUsbTransport(
            () => new[] { new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2") },
            (_, _) => Task.FromResult<ITurzxSerialEndpoint>(endpoint));

        bool disconnectedRaised = false;
        transport.Disconnected += () => disconnectedRaised = true;

        var device = new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2");
        await transport.ConnectAsync(device, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            transport.WriteAsync(TurzxProtocol.BuildScreenOn(), CancellationToken.None));

        Assert.True(endpoint.Disposed);
        Assert.True(disconnectedRaised);
    }

    [Fact]
    public async Task Live_hardware_probe_discovers_turzx_and_renders_live_dashboard_when_connected()
    {
        using var transport = new TurzxUsbTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IReadOnlyList<TurzxDevice> devices = await transport.DiscoverAsync(cts.Token);
        if (devices.Count == 0)
            return; // Hardware not attached on CI/other machines

        TurzxDevice target = devices[0];
        Assert.StartsWith("COM", target.PortName, StringComparison.OrdinalIgnoreCase);

        try
        {
            await transport.ConnectAsync(target, cts.Token);
        }
        catch (IOException)
        {
            // Port is already held open by the running MicaStats process
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // Port is already held open by the running MicaStats process
            return;
        }

        await transport.WriteAsync(TurzxProtocol.BuildScreenOn(), cts.Token);
        await transport.WriteAsync(TurzxProtocol.BuildSetOrientation(TurzxOrientation.Landscape), cts.Token);
        await transport.WriteAsync(TurzxProtocol.BuildSetBrightness(85), cts.Token);

        var renderer = new TurzxRenderer();
        var metrics1 = new Kil0bitSystemMonitor.Models.SystemMetrics
        {
            CpuUsage = 32.5f,
            CpuTemperature = 54.0f,
            CpuFrequencyGhz = 4.75f,
            GpuUsage = 41.0f,
            GpuTemperature = 57.0f,
            GpuVramUsedBytes = 6L * 1024 * 1024 * 1024,
            RamPercent = 48.2f,
            RamUsedBytes = 15L * 1024 * 1024 * 1024,
            RamTotalBytes = 32L * 1024 * 1024 * 1024,
            DiskUsage = 14.0f,
            DiskPercent = 42.0f,
            NetDownKbps = 18500f,
            NetUpKbps = 2400f,
        };

        DateTime t0 = DateTime.Now;
        ushort[] frame1 = renderer.Render(metrics1, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, t0);
        await transport.WriteRectAsync(frame1, 480, new TurzxRect(0, 0, 480, 320), cts.Token);

        var metrics2 = new Kil0bitSystemMonitor.Models.SystemMetrics
        {
            CpuUsage = 38.0f,
            CpuTemperature = 56.0f,
            CpuFrequencyGhz = 4.85f,
            GpuUsage = 46.0f,
            GpuTemperature = 58.0f,
            GpuVramUsedBytes = 6L * 1024 * 1024 * 1024,
            RamPercent = 48.5f,
            RamUsedBytes = 15L * 1024 * 1024 * 1024,
            RamTotalBytes = 32L * 1024 * 1024 * 1024,
            DiskUsage = 18.0f,
            DiskPercent = 42.0f,
            NetDownKbps = 24500f,
            NetUpKbps = 3100f,
        };
        ushort[] frame2 = renderer.Render(metrics2, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, t0.AddSeconds(1));
        IReadOnlyList<TurzxRect> dirty = TurzxRenderer.ComputeDirtyRegions(frame1, frame2, 480, 320);
        Assert.NotEmpty(dirty);
        foreach (TurzxRect rect in dirty)
        {
            await transport.WriteRectAsync(frame2, 480, rect, cts.Token);
        }

        await transport.DisconnectAsync();
    }

    private sealed class FakeEndpoint : ITurzxSerialEndpoint
    {
        public List<byte[]> Writes { get; } = new();
        public bool ThrowOnWrite { get; set; }
        public bool Disposed { get; private set; }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (ThrowOnWrite)
                throw new IOException("Simulated USB disconnect.");
            var slice = new byte[count];
            Buffer.BlockCopy(buffer, offset, slice, 0, count);
            Writes.Add(slice);
            return Task.CompletedTask;
        }

        public Task DisposeAndSettleAsync()
        {
            Disposed = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}
