using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Renders sparklines through the same GDI+ path OverlayWindow uses — bars filled with
    /// SmoothingMode.None into a 32bpp ARGB bitmap — and asserts the resulting pixels.
    ///
    /// This is what makes the "bars, not a polyline" decision verifiable: the reason for bars is
    /// that a 1px antialiased line is mostly translucent at taskbar size, and only inspecting
    /// pixels can confirm the bars really are solid.
    /// </summary>
    public class SparklineRenderTests
    {
        private const int W = 40;
        private const int H = 12;
        private const float BarW = 2f;
        private const float Gap = 1f;

        /// <summary>Mirrors OverlayWindow.DrawSparkline's drawing calls.</summary>
        private static Bitmap Render(Series s, float max, bool antialias = false)
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;
            g.Clear(Color.Transparent);

            Span<RectangleF> buf = stackalloc RectangleF[128];
            int n = SparklineGeometry.Bars(s, W, H, max, BarW, Gap, buf);
            if (n > 0)
            {
                var exact = new RectangleF[n];
                for (int i = 0; i < n; i++) exact[i] = buf[i];
                g.FillRectangles(Brushes.White, exact);
            }
            return bmp;
        }

        private static Series Flat(int capacity, int count, float value)
        {
            var s = new Series(capacity);
            for (int i = 0; i < count; i++) s.Add(value);
            return s;
        }

        private static int CountFullyOpaque(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A == 255) n++;
            return n;
        }

        private static int CountPainted(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 0) n++;
            return n;
        }

        [Fact]
        public void Bars_render_as_fully_opaque_ink()
        {
            using var bmp = Render(Flat(120, 120, 100f), 100f);

            int painted = CountPainted(bmp);
            int opaque = CountFullyOpaque(bmp);

            Assert.True(painted > 0, "something should have been drawn");
            // Every painted pixel must be fully opaque. This is the entire justification for
            // choosing bars over an antialiased polyline at this size.
            Assert.Equal(painted, opaque);
        }

        [Fact]
        public void An_antialiased_line_would_be_mostly_translucent_at_this_size()
        {
            // Demonstrates the alternative that was rejected: the same geometry stroked as a 1px
            // antialiased polyline leaves a majority of its pixels partially transparent.
            var s = new Series(120);
            for (int i = 0; i < 120; i++) s.Add(i % 100);

            using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Span<PointF> pts = stackalloc PointF[256];
                int n = SparklineGeometry.Project(s, W, H, 100f, pts);
                var exact = new PointF[n];
                for (int i = 0; i < n; i++) exact[i] = pts[i];
                using var pen = new Pen(Color.White, 1f);
                g.DrawLines(pen, exact);
            }

            int painted = CountPainted(bmp);
            int opaque = CountFullyOpaque(bmp);

            Assert.True(painted > 0);
            Assert.True(opaque < painted / 2,
                $"expected an AA line to be mostly translucent; {opaque} of {painted} were opaque");
        }

        [Fact]
        public void A_full_scale_value_paints_the_top_row()
        {
            using var bmp = Render(Flat(16, 1, 100f), 100f);

            bool topPainted = false;
            for (int x = 0; x < W; x++) if (bmp.GetPixel(x, 0).A > 0) topPainted = true;

            Assert.True(topPainted, "100% should reach the top of the box");
        }

        [Fact]
        public void Every_bar_touches_the_bottom_row()
        {
            using var bmp = Render(Flat(120, 120, 50f), 100f);

            int bottomPainted = 0;
            for (int x = 0; x < W; x++) if (bmp.GetPixel(x, H - 1).A > 0) bottomPainted++;

            // Bars are bottom-anchored, so the baseline must carry ink for each bar.
            Assert.True(bottomPainted > 0, "bars should be anchored to the bottom edge");
        }

        [Fact]
        public void All_zero_samples_paint_nothing()
        {
            using var bmp = Render(Flat(120, 120, 0f), 100f);
            Assert.Equal(0, CountPainted(bmp));
        }

        [Fact]
        public void A_tiny_nonzero_value_still_paints_at_least_one_row()
        {
            using var bmp = Render(Flat(16, 1, 0.05f), 100f);
            Assert.True(CountPainted(bmp) > 0, "a small reading must not vanish entirely");
        }

        [Fact]
        public void Partial_history_paints_only_the_right_hand_side()
        {
            // Three of 120 samples must occupy the right edge, leaving the left blank, so the
            // graph visibly fills in over time.
            using var bmp = Render(Flat(120, 3, 100f), 100f);

            int leftHalf = 0, rightHalf = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W / 2; x++) if (bmp.GetPixel(x, y).A > 0) leftHalf++;
                for (int x = W / 2; x < W; x++) if (bmp.GetPixel(x, y).A > 0) rightHalf++;
            }

            Assert.Equal(0, leftHalf);
            Assert.True(rightHalf > 0);
        }

        [Fact]
        public void Autoscaled_series_fills_the_box_at_its_own_peak()
        {
            // Network has no fixed ceiling; passing max=0 scales to the series peak.
            var s = new Series(16, peakFloor: 0f);
            s.Add(4096f);

            using var bmp = Render(s, 0f);

            bool topPainted = false;
            for (int x = 0; x < W; x++) if (bmp.GetPixel(x, 0).A > 0) topPainted = true;

            Assert.True(topPainted, "the peak sample should reach full height when autoscaling");
        }

        [Fact]
        public void GetHbitmap_premultiplies_alpha_as_UpdateLayeredWindow_requires()
        {
            // SetBitmap blits with AlphaFormat = AC_SRC_ALPHA, which requires premultiplied
            // channels. Confirm GDI+ produces them, otherwise semi-transparent pixels would
            // render too bright.
            using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            bmp.SetPixel(0, 0, Color.FromArgb(128, 255, 255, 255));

            IntPtr h = bmp.GetHbitmap(Color.FromArgb(0));
            try
            {
                using var round = Image.FromHbitmap(h);
                var p = round.GetPixel(0, 0);
                // 255 * 128/255 = 128, so premultiplied white at half alpha reads as mid grey.
                Assert.InRange(p.R, 120, 136);
                Assert.InRange(p.G, 120, 136);
                Assert.InRange(p.B, 120, 136);
            }
            finally
            {
                DeleteObject(h);
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr o);
    }
}
