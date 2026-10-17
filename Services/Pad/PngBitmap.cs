using System.IO;
using System.Windows.Media.Imaging;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Shared PNG header inspection and frozen <see cref="BitmapSource"/> decoding used by both
    /// <see cref="Kil0bitSystemMonitor.Ai.ChatDiagrams"/> and <see cref="Kil0bitSystemMonitor.Pad.DiagramPicture"/>.
    /// </summary>
    internal static class PngBitmap
    {
        /// <summary>Reads a PNG's width and height in pixels from its IHDR chunk (bytes 16..23, big-endian).</summary>
        public static bool TryReadSize(byte[]? png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
                return false;
            width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            return width > 0 && height > 0;
        }

        /// <summary>A PNG's width in pixels from its header, or 0 when it is not a valid PNG.</summary>
        public static int ReadWidth(byte[]? png) => TryReadSize(png, out int width, out _) ? width : 0;

        /// <summary>Decodes <paramref name="png"/> into a frozen <see cref="BitmapSource"/> loaded immediately.</summary>
        public static BitmapSource DecodeFrozen(byte[] png, int decodeWidth = 0, int decodeHeight = 0)
        {
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) decoded.DecodePixelWidth = decodeWidth;
            else if (decodeHeight > 0) decoded.DecodePixelHeight = decodeHeight;
            decoded.StreamSource = new MemoryStream(png);
            decoded.EndInit();
            decoded.Freeze();
            return decoded;
        }
    }
}
