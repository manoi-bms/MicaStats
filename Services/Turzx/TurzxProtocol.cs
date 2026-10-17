using System;

namespace Kil0bitSystemMonitor.Services.Turzx;

/// <summary>
/// Encodes the Turing Smart Screen / Turzx Revision A (<c>USB35INCHIPSV2</c>) 6-byte and 16-byte
/// USB serial command packets and little-endian RGB565 bitmap payloads.
/// </summary>
public static class TurzxProtocol
{
    public const byte CommandReset = 101;
    public const byte CommandClear = 102;
    public const byte CommandScreenOff = 108;
    public const byte CommandScreenOn = 109;
    public const byte CommandSetBrightness = 110;
    public const byte CommandSetOrientation = 121;
    public const byte CommandDisplayBitmap = 197;

    public static byte[] PackCommand(int x, int y, int ex, int ey, byte command)
    {
        return new byte[]
        {
            (byte)(x >> 2),
            (byte)(((x & 3) << 6) | (y >> 4)),
            (byte)(((y & 15) << 4) | (ex >> 6)),
            (byte)(((ex & 63) << 2) | (ey >> 8)),
            (byte)(ey & 0xFF),
            command,
        };
    }

    public static byte[] BuildReset() => PackCommand(0, 0, 0, 0, CommandReset);

    public static byte[] BuildClear() => PackCommand(0, 0, 0, 0, CommandClear);

    public static byte[] BuildScreenOff() => PackCommand(0, 0, 0, 0, CommandScreenOff);

    public static byte[] BuildScreenOn() => PackCommand(0, 0, 0, 0, CommandScreenOn);

    /// <summary>
    /// Revision A (<c>USB35INCHIPSV2</c>) uses an inverted backlight scale:
    /// <c>0</c> is 100% brightness and <c>255</c> is 0% brightness.
    /// </summary>
    public static byte[] BuildSetBrightness(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        int level = 255 - (int)Math.Round(clamped / 100.0 * 255.0);
        return PackCommand(level, 0, 0, 0, CommandSetBrightness);
    }

    /// <summary>
    /// Builds the 16-byte Revision A orientation command (<c>121</c>) with orientation code
    /// <c>100..103</c> and big-endian width/height.
    /// </summary>
    public static byte[] BuildSetOrientation(TurzxOrientation orientation)
    {
        (int width, int height) = TurzxDimensions.ForOrientation(orientation);
        var packet = new byte[16];
        packet[5] = CommandSetOrientation;
        packet[6] = (byte)((int)orientation + 100);
        packet[7] = (byte)(width >> 8);
        packet[8] = (byte)(width & 0xFF);
        packet[9] = (byte)(height >> 8);
        packet[10] = (byte)(height & 0xFF);
        return packet;
    }

    public static byte[] BuildBitmapHeader(TurzxRect rect) =>
        PackCommand(rect.X, rect.Y, rect.EndX, rect.EndY, CommandDisplayBitmap);

    public static ushort RgbTo565(byte r, byte g, byte b) =>
        (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));

    public static (byte R, byte G, byte B) RgbFrom565(ushort pixel)
    {
        int r5 = (pixel >> 11) & 0x1F;
        int g6 = (pixel >> 5) & 0x3F;
        int b5 = pixel & 0x1F;
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g6 << 2) | (g6 >> 4));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return (r, g, b);
    }

    public static byte[] SliceSubRectBytes(ushort[] frame, int frameWidth, TurzxRect rect)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (rect.Width <= 0 || rect.Height <= 0)
            return Array.Empty<byte>();

        var output = new byte[rect.Width * rect.Height * 2];
        int dst = 0;
        for (int row = 0; row < rect.Height; row++)
        {
            int srcRow = (rect.Y + row) * frameWidth + rect.X;
            for (int col = 0; col < rect.Width; col++)
            {
                ushort pixel = frame[srcRow + col];
                output[dst++] = (byte)(pixel & 0xFF);
                output[dst++] = (byte)(pixel >> 8);
            }
        }
        return output;
    }
}
