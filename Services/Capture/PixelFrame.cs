using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>One captured frame: 32-bit ARGB pixels, row by row from the top.</summary>
    public sealed class PixelFrame
    {
        public PixelFrame(int width, int height, int[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels.Length != width * height)
                throw new ArgumentException("The pixel count must be width x height.", nameof(pixels));
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        public int[] Pixels { get; }

        public ReadOnlySpan<int> Row(int y) => Pixels.AsSpan(y * Width, Width);

        public bool SameAs(PixelFrame other) =>
            Width == other.Width && Height == other.Height && Pixels.AsSpan().SequenceEqual(other.Pixels);

        /// <summary>Rows (each <paramref name="width"/> pixels) stacked into one frame.</summary>
        public static PixelFrame Stack(IReadOnlyList<int[]> rows, int width)
        {
            var px = new int[rows.Count * width];
            for (int y = 0; y < rows.Count; y++) Array.Copy(rows[y], 0, px, y * width, width);
            return new PixelFrame(width, rows.Count, px);
        }
    }
}
