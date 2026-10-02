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

        /// <summary>
        /// The smallest rectangle holding every pixel that differs from <paramref name="other"/>
        /// in the first <paramref name="columns"/> columns, in this frame's coordinates; null when
        /// none differs. Frames of another size differ everywhere.
        /// </summary>
        public PixelRect? DiffBox(PixelFrame other, int columns = int.MaxValue)
        {
            if (Width != other.Width || Height != other.Height) return new PixelRect(0, 0, Width, Height);
            int to = Math.Min(columns, Width);
            int left = int.MaxValue, right = -1, top = -1, bottom = -1;
            for (int y = 0; y < Height; y++)
            {
                ReadOnlySpan<int> a = Row(y)[..to], b = other.Row(y)[..to];
                if (a.SequenceEqual(b)) continue;
                int first = 0, last = to - 1;
                while (a[first] == b[first]) first++;
                while (a[last] == b[last]) last--;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
                if (top < 0) top = y;
                bottom = y;
            }
            return top < 0 ? null : PixelRect.FromEdges(left, top, right + 1, bottom + 1);
        }

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
