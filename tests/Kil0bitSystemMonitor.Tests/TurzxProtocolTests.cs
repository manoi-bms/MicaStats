using System;
using Kil0bitSystemMonitor.Services.Turzx;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class TurzxProtocolTests
{
    [Fact]
    public void PackCommand_packs_coordinates_and_command_into_six_bytes()
    {
        // x=0, y=0, ex=479 (0x1DF), ey=319 (0x13F), cmd=197
        byte[] packet = TurzxProtocol.PackCommand(0, 0, 479, 319, TurzxProtocol.CommandDisplayBitmap);

        Assert.Equal(6, packet.Length);
        Assert.Equal(0, packet[0]);
        Assert.Equal(0, packet[1]);
        Assert.Equal((byte)(479 >> 6), packet[2]);
        Assert.Equal((byte)(((479 & 63) << 2) | (319 >> 8)), packet[3]);
        Assert.Equal((byte)(319 & 0xFF), packet[4]);
        Assert.Equal(197, packet[5]);
    }

    [Fact]
    public void PackCommand_encodes_non_zero_subrect_coordinates_exact()
    {
        int x = 123, y = 45, ex = 300, ey = 210;
        byte[] packet = TurzxProtocol.PackCommand(x, y, ex, ey, TurzxProtocol.CommandDisplayBitmap);

        int unpackedX = (packet[0] << 2) | (packet[1] >> 6);
        int unpackedY = ((packet[1] & 0x3F) << 4) | (packet[2] >> 4);
        int unpackedEx = ((packet[2] & 0x0F) << 6) | (packet[3] >> 2);
        int unpackedEy = ((packet[3] & 0x03) << 8) | packet[4];

        Assert.Equal(x, unpackedX);
        Assert.Equal(y, unpackedY);
        Assert.Equal(ex, unpackedEx);
        Assert.Equal(ey, unpackedEy);
        Assert.Equal(TurzxProtocol.CommandDisplayBitmap, packet[5]);
    }

    [Fact]
    public void Basic_commands_build_expected_six_byte_packets()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 101 }, TurzxProtocol.BuildReset());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 102 }, TurzxProtocol.BuildClear());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 108 }, TurzxProtocol.BuildScreenOff());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 109 }, TurzxProtocol.BuildScreenOn());
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 255)]
    [InlineData(50, 127)]
    [InlineData(150, 0)]
    [InlineData(-20, 255)]
    public void BuildSetBrightness_inverts_percentage_to_revision_a_level(int percent, int expectedLevel)
    {
        byte[] packet = TurzxProtocol.BuildSetBrightness(percent);

        int unpackedLevel = (packet[0] << 2) | (packet[1] >> 6);
        Assert.Equal(expectedLevel, unpackedLevel);
        Assert.Equal(110, packet[5]);
    }

    [Theory]
    [InlineData(TurzxOrientation.Portrait, 100, 320, 480)]
    [InlineData(TurzxOrientation.ReversePortrait, 101, 320, 480)]
    [InlineData(TurzxOrientation.Landscape, 102, 480, 320)]
    [InlineData(TurzxOrientation.ReverseLandscape, 103, 480, 320)]
    public void BuildSetOrientation_builds_sixteen_byte_revision_a_packet(
        TurzxOrientation orientation,
        byte expectedCode,
        int expectedWidth,
        int expectedHeight)
    {
        byte[] packet = TurzxProtocol.BuildSetOrientation(orientation);

        Assert.Equal(16, packet.Length);
        Assert.Equal(121, packet[5]);
        Assert.Equal(expectedCode, packet[6]);
        Assert.Equal(expectedWidth, (packet[7] << 8) | packet[8]);
        Assert.Equal(expectedHeight, (packet[9] << 8) | packet[10]);
    }

    [Fact]
    public void RgbTo565_and_SliceSubRectBytes_produce_little_endian_rgb565_stream()
    {
        ushort red = TurzxProtocol.RgbTo565(255, 0, 0);
        ushort green = TurzxProtocol.RgbTo565(0, 255, 0);
        ushort blue = TurzxProtocol.RgbTo565(0, 0, 255);

        Assert.Equal(0xF800, red);
        Assert.Equal(0x07E0, green);
        Assert.Equal(0x001F, blue);

        ushort[] frame =
        {
            0x1122, 0x3344, 0x5566, 0x7788,
            0x99AA, 0xBBCC, 0xDDEE, 0xFF00,
        };

        byte[] sub = TurzxProtocol.SliceSubRectBytes(frame, frameWidth: 4, new TurzxRect(1, 0, 2, 2));
        Assert.Equal(new byte[]
        {
            0x44, 0x33, 0x66, 0x55,
            0xCC, 0xBB, 0xEE, 0xDD,
        }, sub);
    }
}
