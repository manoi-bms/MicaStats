using System;
using System.Linq;
using System.Text;
using Kil0bitSystemMonitor.Services.StackChan;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanDiscoveryTests
{
    [Fact]
    public void Factory_service_does_not_require_a_name_and_setup_or_generic_uart_names_are_ignored()
    {
        var factory = StackChanBleTransport.ToDiscoveredDevice(1, new(null, true, false));
        Assert.Equal(StackChanFirmware.Factory, factory!.Firmware);
        Assert.Null(StackChanBleTransport.ToDiscoveredDevice(2, new("StackChan", false, false)));
        Assert.Null(StackChanBleTransport.ToDiscoveredDevice(3, new("BLE UART", false, false)));
    }

    [Fact]
    public void Companion_identity_survives_separate_scan_response_packets()
    {
        var observation = new StackChanBleTransport.DiscoveryObservation(null, false, false)
            .Merge("MicaStatsStackChan", false).Merge(null, false);
        var device = StackChanBleTransport.ToDiscoveredDevice(1, observation);
        Assert.Equal(StackChanFirmware.UiFlow2, device!.Firmware);
        Assert.Equal("MicaStatsStackChan", device.Name);
    }

    [Theory]
    [InlineData("{\"companion\":{\"activity\":\"Ready\"}}")]
    [InlineData("{\"value\":\"ไทย\"}")]
    public void Uiflow_frames_reconstruct_one_utf8_line_without_factory_headers(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var frames = StackChanBleTransport.FrameUiFlow(payload);
        Assert.All(frames, frame => Assert.InRange(frame.Length, 1, 20));
        Assert.Equal(Encoding.UTF8.GetBytes(json + "\n"), frames.SelectMany(frame => frame).ToArray());
    }

    [Fact]
    public void Uiflow_frames_reject_oversize_payloads() => Assert.Throws<ArgumentException>(() =>
        StackChanBleTransport.FrameUiFlow(new byte[2049]));
}
