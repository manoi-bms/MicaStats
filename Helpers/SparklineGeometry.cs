using System;
using System.Drawing;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Turns a <see cref="Series"/> into drawable geometry. Pure functions, no state, no rendering
    /// dependency — shared by the GDI+ overlay and the WPF panel so the same data produces the
    /// same shape in both.
    ///
    /// <para>
    /// All output coordinates are relative to the graph box: (0,0) is its top-left corner and
    /// larger Y is further down, matching both GDI+ and WPF. The caller applies the offset.
    /// </para>
    ///
    /// <para>
    /// The time axis always spans <see cref="Series.Capacity"/> samples. A partially filled series
    /// is therefore drawn right-aligned, growing in from the right edge, rather than stretched to
    /// fill the width — stretching would make three samples look like a full minute of history,
    /// and zero-padding would invent a cliff climbing out of zero.
    /// </para>
    /// </summary>
    public static class SparklineGeometry
    {
        /// <summary>
        /// Lays out bottom-anchored bars. Bars are preferred over a polyline at taskbar size: a
        /// 1px antialiased line renders only ~17% solid ink in a 12px-tall slot and reads as haze,
        /// while bars are fully opaque and cheaper to draw.
        /// </summary>
        /// <param name="s">Source samples.</param>
        /// <param name="w">Box width.</param>
        /// <param name="h">Box height.</param>
        /// <param name="max">Full-scale value; pass 0 or less to autoscale to <see cref="Series.Peak"/>.</param>
        /// <param name="barW">Bar width in pixels.</param>
        /// <param name="gap">Gap between bars in pixels.</param>
        /// <param name="into">Destination buffer; output is truncated to its length.</param>
        /// <returns>Number of rectangles written.</returns>
        public static int Bars(Series? s, float w, float h, float max, float barW, float gap, Span<RectangleF> into)
        {
            if (s == null || s.Count == 0 || w <= 0 || h <= 0 || barW <= 0 || into.Length == 0) return 0;

            float pitch = barW + gap;
            // The final bar needs no trailing gap, hence the +gap before dividing.
            int slots = (int)Math.Floor((w + gap) / pitch);
            if (slots <= 0) return 0;
            if (slots > into.Length) slots = into.Length;

            float scale = Scale(s, max);
            int n = s.Count;
            int produced = 0;

            if (n <= slots)
            {
                // Fewer samples than slots: occupy the rightmost `n` slots.
                int offset = slots - n;
                for (int i = 0; i < n; i++)
                {
                    into[produced++] = Bar(offset + i, s[i], pitch, barW, h, scale);
                }
            }
            else
            {
                // More samples than slots: bucket by max so a transient spike survives
                // downsampling. Averaging would hide exactly the events worth seeing.
                for (int slot = 0; slot < slots; slot++)
                {
                    float peak = BucketMax(s, slot, slots, n);
                    into[produced++] = Bar(slot, peak, pitch, barW, h, scale);
                }
            }

            return produced;
        }

        /// <summary>
        /// Projects the series into a polyline, oldest point first. Suitable for the panel, where
        /// the box is large enough for a stroked line or filled area to read clearly.
        /// </summary>
        /// <returns>Number of points written.</returns>
        public static int Project(Series? s, float w, float h, float max, Span<PointF> into)
        {
            if (s == null || s.Count == 0 || w <= 0 || h <= 0 || into.Length == 0) return 0;

            float scale = Scale(s, max);
            int n = s.Count;

            // At most one point per horizontal pixel; more cannot be distinguished.
            int slots = Math.Min(into.Length, Math.Max(2, (int)Math.Round(w)));

            if (n == 1)
            {
                into[0] = new PointF(w, Y(s[0], h, scale));
                return 1;
            }

            if (n <= slots)
            {
                // One point per sample on an axis spanning Capacity, anchored to the right edge.
                float pitch = s.Capacity > 1 ? w / (s.Capacity - 1) : w;
                float x0 = w - (n - 1) * pitch;
                for (int i = 0; i < n; i++)
                {
                    into[i] = new PointF(x0 + i * pitch, Y(s[i], h, scale));
                }
                return n;
            }

            for (int k = 0; k < slots; k++)
            {
                float peak = BucketMax(s, k, slots, n);
                float x = slots == 1 ? w : w * k / (slots - 1);
                into[k] = new PointF(x, Y(peak, h, scale));
            }
            return slots;
        }

        /// <summary>Maximum sample in bucket <paramref name="slot"/> of <paramref name="slots"/>.</summary>
        private static float BucketMax(Series s, int slot, int slots, int n)
        {
            int lo = (int)((long)slot * n / slots);
            int hi = (int)((long)(slot + 1) * n / slots);
            if (hi <= lo) hi = lo + 1;
            if (hi > n) hi = n;

            float peak = float.NegativeInfinity;
            for (int j = lo; j < hi; j++)
            {
                float v = s[j];
                if (v > peak) peak = v;
            }
            return float.IsNegativeInfinity(peak) ? 0f : peak;
        }

        private static RectangleF Bar(int slot, float value, float pitch, float barW, float h, float scale)
        {
            float frac = Fraction(value, scale);
            float bh = frac * h;
            // Keep a non-zero reading visible; sub-pixel bars would vanish entirely.
            if (frac > 0f && bh < 1f) bh = 1f;
            return new RectangleF(slot * pitch, h - bh, barW, bh);
        }

        private static float Y(float value, float h, float scale) => h - Fraction(value, scale) * h;

        private static float Fraction(float value, float scale)
        {
            if (float.IsNaN(value) || scale <= 0f) return 0f;
            float f = value / scale;
            if (f < 0f) return 0f;
            if (f > 1f) return 1f;
            return f;
        }

        /// <summary>Resolves the full-scale value, falling back to the series' decayed peak.</summary>
        private static float Scale(Series s, float max)
        {
            if (max > 0f) return max;
            float peak = s.Peak;
            return peak > 0f ? peak : 1f;
        }
    }
}
