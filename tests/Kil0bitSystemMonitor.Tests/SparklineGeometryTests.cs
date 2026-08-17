using System;
using System.Drawing;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SparklineGeometryTests
    {
        // A 40x12 slot with 2px bars and a 1px gap is the taskbar configuration: pitch 3,
        // so (40 + 1) / 3 = 13 slots.
        private const float BoxW = 40f;
        private const float BoxH = 12f;
        private const float BarW = 2f;
        private const float Gap = 1f;
        private const int Slots = 13;

        private static Series Filled(int capacity, params float[] values)
        {
            var s = new Series(capacity);
            foreach (var v in values) s.Add(v);
            return s;
        }

        [Fact]
        public void Bars_of_a_null_or_empty_series_produce_nothing()
        {
            Span<RectangleF> buf = stackalloc RectangleF[32];
            Assert.Equal(0, SparklineGeometry.Bars(null, BoxW, BoxH, 100, BarW, Gap, buf));
            Assert.Equal(0, SparklineGeometry.Bars(new Series(8), BoxW, BoxH, 100, BarW, Gap, buf));
        }

        [Fact]
        public void Bars_of_a_zero_sized_box_produce_nothing()
        {
            var s = Filled(8, 50);
            Span<RectangleF> buf = stackalloc RectangleF[32];
            Assert.Equal(0, SparklineGeometry.Bars(s, 0, BoxH, 100, BarW, Gap, buf));
            Assert.Equal(0, SparklineGeometry.Bars(s, BoxW, 0, 100, BarW, Gap, buf));
        }

        [Fact]
        public void Partial_history_is_right_aligned_not_stretched()
        {
            // Three samples in a 13-slot box must occupy the last three slots, so history grows
            // in from the right rather than three samples looking like a full minute.
            var s = Filled(120, 10, 20, 30);
            Span<RectangleF> buf = stackalloc RectangleF[32];

            int n = SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, buf);

            Assert.Equal(3, n);
            float pitch = BarW + Gap;
            Assert.Equal((Slots - 3) * pitch, buf[0].X, 3);
            Assert.Equal((Slots - 2) * pitch, buf[1].X, 3);
            Assert.Equal((Slots - 1) * pitch, buf[2].X, 3);
        }

        [Fact]
        public void Full_history_fills_every_slot()
        {
            var s = new Series(120);
            for (int i = 0; i < 120; i++) s.Add(i % 100);

            Span<RectangleF> buf = stackalloc RectangleF[32];
            int n = SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, buf);

            Assert.Equal(Slots, n);
            Assert.Equal(0f, buf[0].X, 3);
        }

        [Fact]
        public void Downsampling_keeps_bucket_maxima_so_spikes_survive()
        {
            // 100 samples into 13 slots. A single spike must not be averaged away.
            var s = new Series(100);
            for (int i = 0; i < 100; i++) s.Add(1);
            var spiky = new Series(100);
            for (int i = 0; i < 100; i++) spiky.Add(i == 50 ? 100f : 1f);

            Span<RectangleF> flat = stackalloc RectangleF[32];
            Span<RectangleF> withSpike = stackalloc RectangleF[32];
            SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, flat);
            SparklineGeometry.Bars(spiky, BoxW, BoxH, 100, BarW, Gap, withSpike);

            bool anyTaller = false;
            for (int i = 0; i < Slots; i++)
            {
                if (withSpike[i].Height > flat[i].Height + 0.5f) anyTaller = true;
            }
            Assert.True(anyTaller, "the spike should raise at least one bucket");
        }

        [Fact]
        public void Bars_are_bottom_anchored()
        {
            var s = Filled(16, 25, 50, 75, 100);
            Span<RectangleF> buf = stackalloc RectangleF[32];
            int n = SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, buf);

            for (int i = 0; i < n; i++)
            {
                Assert.Equal(BoxH, buf[i].Y + buf[i].Height, 3);
            }
        }

        [Fact]
        public void Full_scale_value_fills_the_height_and_zero_draws_nothing()
        {
            var s = Filled(16, 0f, 100f);
            Span<RectangleF> buf = stackalloc RectangleF[32];
            int n = SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, buf);

            Assert.Equal(2, n);
            Assert.Equal(0f, buf[0].Height, 3);   // zero is genuinely empty
            Assert.Equal(BoxH, buf[1].Height, 3); // 100 of 100 fills the box
        }

        [Fact]
        public void Values_above_full_scale_are_clamped_not_overdrawn()
        {
            // Guards against '% Processor Utility'-style counters that exceed 100.
            var s = Filled(16, 250f);
            Span<RectangleF> buf = stackalloc RectangleF[32];
            SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, buf);

            Assert.Equal(BoxH, buf[0].Height, 3);
            Assert.True(buf[0].Y >= 0f, "a clamped bar must not start above the box");
        }

        [Fact]
        public void A_small_but_nonzero_value_stays_visible()
        {
            // 0.1 of 100 in a 12px box is 0.012px, which would disappear entirely.
            var s = Filled(16, 0.1f);
            Span<RectangleF> buf = stackalloc RectangleF[32];
            SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, buf);

            Assert.True(buf[0].Height >= 1f, $"expected at least 1px, got {buf[0].Height}");
        }

        [Fact]
        public void Passing_zero_max_autoscales_to_the_series_peak()
        {
            // Network has no fixed ceiling, so the peak defines full scale.
            var s = new Series(16, peakFloor: 0f);
            s.Add(40f);

            Span<RectangleF> buf = stackalloc RectangleF[32];
            SparklineGeometry.Bars(s, BoxW, BoxH, 0f, BarW, Gap, buf);

            // 40 against a peak of 40 is full scale.
            Assert.Equal(BoxH, buf[0].Height, 3);
        }

        [Fact]
        public void Output_is_truncated_to_the_destination_buffer()
        {
            var s = new Series(120);
            for (int i = 0; i < 120; i++) s.Add(50);

            Span<RectangleF> tiny = stackalloc RectangleF[4];
            int n = SparklineGeometry.Bars(s, BoxW, BoxH, 100, BarW, Gap, tiny);

            Assert.Equal(4, n);
        }

        [Fact]
        public void Project_of_an_empty_series_produces_nothing()
        {
            Span<PointF> buf = stackalloc PointF[64];
            Assert.Equal(0, SparklineGeometry.Project(null, 100, 40, 100, buf));
            Assert.Equal(0, SparklineGeometry.Project(new Series(8), 100, 40, 100, buf));
        }

        [Fact]
        public void Project_places_a_single_sample_at_the_right_edge()
        {
            var s = Filled(60, 50f);
            Span<PointF> buf = stackalloc PointF[64];

            int n = SparklineGeometry.Project(s, 120f, 40f, 100f, buf);

            Assert.Equal(1, n);
            Assert.Equal(120f, buf[0].X, 3);
        }

        [Fact]
        public void Project_right_aligns_partial_history_on_a_capacity_wide_axis()
        {
            // 3 of 60 samples: the newest sits at the right edge and the rest trail left by the
            // pitch a full buffer would use, so the graph fills in over time.
            var s = Filled(60, 10, 20, 30);
            Span<PointF> buf = stackalloc PointF[128];

            int n = SparklineGeometry.Project(s, 118f, 40f, 100f, buf);

            Assert.Equal(3, n);
            float pitch = 118f / 59f;
            Assert.Equal(118f, buf[2].X, 3);
            Assert.Equal(118f - pitch, buf[1].X, 3);
            Assert.Equal(118f - 2 * pitch, buf[0].X, 3);
        }

        [Fact]
        public void Project_spans_the_full_width_once_the_buffer_is_full()
        {
            var s = new Series(60);
            for (int i = 0; i < 60; i++) s.Add(i);

            Span<PointF> buf = stackalloc PointF[128];
            int n = SparklineGeometry.Project(s, 118f, 40f, 100f, buf);

            Assert.Equal(60, n);
            Assert.Equal(0f, buf[0].X, 3);
            Assert.Equal(118f, buf[n - 1].X, 3);
        }

        [Fact]
        public void Project_puts_zero_at_the_baseline_and_full_scale_at_the_top()
        {
            var s = Filled(4, 0f, 100f);
            Span<PointF> buf = stackalloc PointF[16];
            int n = SparklineGeometry.Project(s, 50f, 40f, 100f, buf);

            Assert.Equal(2, n);
            Assert.Equal(40f, buf[0].Y, 3); // zero sits on the bottom edge
            Assert.Equal(0f, buf[1].Y, 3);  // full scale reaches the top
        }

        [Fact]
        public void Project_downsamples_when_there_are_more_samples_than_pixels()
        {
            var s = new Series(500);
            for (int i = 0; i < 500; i++) s.Add(i % 100);

            Span<PointF> buf = stackalloc PointF[512];
            int n = SparklineGeometry.Project(s, 50f, 40f, 100f, buf);

            Assert.True(n <= 50, $"expected at most one point per pixel, got {n}");
            Assert.True(n >= 2);
        }
    }
}
