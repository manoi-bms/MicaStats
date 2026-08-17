using System;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SeriesTests
    {
        [Fact]
        public void Empty_series_reports_no_data_yet()
        {
            var s = new Series(4);
            Assert.Equal(0, s.Count);
            Assert.Equal(Availability.NoDataYet, s.Availability);
            Assert.Equal(0f, s.Latest);
        }

        [Fact]
        public void Indexing_is_oldest_to_newest_before_wrap()
        {
            var s = new Series(4);
            s.Add(1);
            s.Add(2);
            s.Add(3);

            Assert.Equal(3, s.Count);
            Assert.Equal(1f, s[0]);
            Assert.Equal(2f, s[1]);
            Assert.Equal(3f, s[2]);
            Assert.Equal(3f, s.Latest);
        }

        [Fact]
        public void Ring_wraps_and_drops_the_oldest_sample()
        {
            var s = new Series(3);
            s.Add(1);
            s.Add(2);
            s.Add(3);
            s.Add(4); // evicts 1

            Assert.Equal(3, s.Count);
            Assert.Equal(2f, s[0]);
            Assert.Equal(3f, s[1]);
            Assert.Equal(4f, s[2]);
        }

        [Fact]
        public void Count_never_exceeds_capacity()
        {
            var s = new Series(2);
            for (int i = 0; i < 50; i++) s.Add(i);

            Assert.Equal(2, s.Count);
            Assert.Equal(48f, s[0]);
            Assert.Equal(49f, s[1]);
        }

        [Fact]
        public void Indexer_rejects_positions_beyond_count()
        {
            var s = new Series(4);
            s.Add(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => s[1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => s[-1]);
        }

        [Fact]
        public void Zero_capacity_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Series(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Series(-1));
        }

        [Fact]
        public void A_real_sample_marks_the_series_available()
        {
            var s = new Series(4);
            s.Add(42);
            Assert.Equal(Availability.Value, s.Availability);
        }

        [Fact]
        public void Unavailable_without_any_sample_is_distinguishable_from_no_data_yet()
        {
            var s = new Series(4);
            s.AddUnavailable();

            // The distinction matters: a graph cannot draw "N/A", so an unreadable sensor must not
            // be rendered as a flat line at zero.
            Assert.Equal(Availability.Unavailable, s.Availability);
            Assert.Equal(0, s.Count);
        }

        [Fact]
        public void NaN_and_infinity_are_treated_as_unavailable_not_as_data()
        {
            var s = new Series(4);
            s.Add(float.NaN);
            s.Add(float.PositiveInfinity);
            s.Add(float.NegativeInfinity);

            Assert.Equal(0, s.Count);
            Assert.Equal(Availability.Unavailable, s.Availability);
        }

        [Fact]
        public void Once_a_value_arrives_availability_stays_value()
        {
            var s = new Series(4);
            s.AddUnavailable();
            s.Add(10);
            s.AddUnavailable();

            Assert.Equal(Availability.Value, s.Availability);
        }

        [Fact]
        public void Peak_rises_immediately_to_a_new_maximum()
        {
            var s = new Series(8);
            s.Add(10);
            s.Add(50);

            // A new high must take effect at once, or an autoscaled graph would clip the very
            // spike that raised it.
            Assert.Equal(50f, s.Peak);
        }

        [Fact]
        public void Peak_stays_near_the_maximum_immediately_after_it()
        {
            var s = new Series(8);
            s.Add(10);
            s.Add(50);
            s.Add(20);

            // Decay begins on the next lower sample, so the peak trails the maximum slightly
            // rather than holding it exactly. It must not collapse to the current value.
            Assert.InRange(s.Peak, 45f, 50f);
        }

        [Fact]
        public void Peak_decays_when_values_fall_so_one_spike_does_not_flatten_the_graph()
        {
            var s = new Series(256);
            s.Add(1000);
            float afterSpike = s.Peak;

            for (int i = 0; i < 100; i++) s.Add(1);

            Assert.True(s.Peak < afterSpike, "peak should decay once values drop");
        }

        [Fact]
        public void Peak_never_falls_below_the_floor()
        {
            var s = new Series(64, peakFloor: 64f);
            for (int i = 0; i < 500; i++) s.Add(0);

            Assert.Equal(64f, s.Peak);
        }

        [Fact]
        public void Clear_resets_samples_availability_and_peak()
        {
            var s = new Series(4, peakFloor: 5f);
            s.Add(100);
            s.Clear();

            Assert.Equal(0, s.Count);
            Assert.Equal(Availability.NoDataYet, s.Availability);
            Assert.Equal(5f, s.Peak);
        }
    }
}
