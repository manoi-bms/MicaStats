using System;
using System.Diagnostics;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>How long a streaming answer waits before it is drawn again: arithmetic, with no clock and no window.</summary>
    public class RedrawPaceTests
    {
        private static readonly TimeSpan Least = TimeSpan.FromMilliseconds(100);

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        [Fact]
        public void A_redraw_may_take_one_part_in_five_of_the_time_and_never_makes_the_next_wait_over_two_seconds()
        {
            Assert.Equal(4, RedrawPace.Share);
            Assert.Equal(TimeSpan.FromSeconds(2), RedrawPace.Longest);
        }

        [Theory]
        [InlineData(0, 100)]       // nothing measured yet: the plain interval
        [InlineData(10, 100)]      // a light answer: four times its cost is under the interval
        [InlineData(25, 100)]      // exactly the interval
        [InlineData(100, 400)]     // a table of 30 rows by 6
        [InlineData(300, 1200)]
        [InlineData(500, 2000)]    // exactly the longest
        [InlineData(1000, 2000)]   // a table of 100 rows by 12: capped
        [InlineData(-50, 100)]     // a negative cost counts as zero
        public void The_next_redraw_waits_four_times_the_last_cost_within_the_interval_and_two_seconds(double costMs, double waitMs)
        {
            Assert.Equal(Ms(waitMs), RedrawPace.Next(Least, Ms(costMs)));
        }

        [Fact]
        public void An_interval_above_the_longest_wait_is_kept()
        {
            TimeSpan least = TimeSpan.FromSeconds(30);

            Assert.Equal(least, RedrawPace.Next(least, TimeSpan.Zero));
            Assert.Equal(least, RedrawPace.Next(least, TimeSpan.FromSeconds(1)));
            Assert.Equal(least, RedrawPace.Next(least, TimeSpan.FromSeconds(60)));
        }

        [Fact]
        public void An_interval_of_zero_waits_only_for_the_cost()
        {
            Assert.Equal(TimeSpan.Zero, RedrawPace.Next(TimeSpan.Zero, TimeSpan.Zero));
            Assert.Equal(Ms(40), RedrawPace.Next(TimeSpan.Zero, Ms(10)));
        }

        [Fact]
        public void A_cost_too_large_to_multiply_is_capped_and_does_not_overflow()
        {
            Assert.Equal(RedrawPace.Longest, RedrawPace.Next(Least, TimeSpan.MaxValue));
            Assert.Equal(Least, RedrawPace.Next(Least, TimeSpan.MinValue));
        }
    }

    /// <summary>
    /// The timer a view redraws on, with no view of an answer: only when it lets a redraw be made.
    /// On the shared UI thread, whose dispatcher stores a cost (at Loaded) and ticks the timer (at
    /// Background). Nothing sleeps.
    /// </summary>
    public class RedrawTimerTests
    {
        private sealed class Drawn
        {
            public int Ticks;
        }

        private static readonly Func<bool> NotHeld = () => false;

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        private static void PumpUntil(Func<bool> done)
        {
            var waited = Stopwatch.StartNew();
            while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10))
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        [Fact]
        public void While_the_cost_of_a_redraw_is_awaited_new_text_starts_the_timer_and_the_tick_finds_the_cost_stored() => UiThread.Run(() =>
        {
            var view = new Drawn();
            var timer = new RedrawTimer<Drawn>(view, static drawn => drawn.Ticks++);

            timer.Measure(Stopwatch.GetTimestamp());   // a redraw has shown its document; its layout, and so its cost, are still to come

            // Text arrives before that layout. With no interval to wait for, the pace as it is known says "now".
            Assert.False(timer.Ready(TimeSpan.Zero, NotHeld, _ => { }));
            Assert.Equal(TimeSpan.Zero, timer.Pending);                    // the timer asks again, as soon as it may
            Assert.Equal(TimeSpan.Zero, timer.LastCost);

            PumpUntil(() => view.Ticks > 0);                               // the cost is stored (Loaded) before the tick (Background)
            Assert.Equal(1, view.Ticks);
            Assert.True(timer.LastCost > TimeSpan.Zero, "the measurement was kept, not dropped by a redraw made too early");
            Assert.Null(timer.Pending);

            // The tick asks again: the cost is in, so the pace is read from it, and a light redraw may be made now.
            timer.LastCost = TimeSpan.Zero;
            Assert.True(timer.Ready(TimeSpan.Zero, NotHeld, _ => { }));
        });

        [Fact]
        public void A_cost_given_up_is_not_waited_for() => UiThread.Run(() =>
        {
            var view = new Drawn();
            var timer = new RedrawTimer<Drawn>(view, static drawn => drawn.Ticks++);

            timer.Measure(Stopwatch.GetTimestamp());
            timer.Cancel();                                                // the view went away: nothing is stored for it
            Assert.True(timer.Ready(TimeSpan.Zero, NotHeld, _ => { }));

            timer.Measure(Stopwatch.GetTimestamp());
            timer.Forget();                                                // another answer: nothing to pace against
            Assert.True(timer.Ready(Ms(100), NotHeld, _ => { }));

            timer.Measure(Stopwatch.GetTimestamp());
            timer.LastCost = TimeSpan.Zero;                                // set by hand (the tests do): that is the cost
            Assert.True(timer.Ready(TimeSpan.Zero, NotHeld, _ => { }));

            RedrawWaits.ToLoaded();                                        // the three callbacks left behind store nothing
            Assert.Equal(TimeSpan.Zero, timer.LastCost);
        });

        [Fact]
        public void A_cost_awaited_keeps_what_is_left_of_the_pace_as_it_is_known_and_a_held_pointer_is_not_even_asked() => UiThread.Run(() =>
        {
            var view = new Drawn();
            var timer = new RedrawTimer<Drawn>(view, static drawn => drawn.Ticks++);
            var clock = Stopwatch.StartNew();
            timer.LastCost = Ms(300);                                      // the redraw before cost 300 ms
            timer.Measure(Stopwatch.GetTimestamp());
            int asked = 0;

            Assert.False(timer.Ready(Ms(100), () => { asked++; return true; }, _ => { }));

            RedrawWaits.AssertWaits(timer.Pending, Ms(1200), clock);       // not zero: the known pace has not passed either
            Assert.Equal(0, asked);
            timer.Cancel();
        });
    }

    /// <summary>What the tests of the four views that redraw an answer share.</summary>
    internal static class RedrawWaits
    {
        /// <summary>
        /// Lets the dispatcher run everything it holds down to <see cref="DispatcherPriority.Loaded"/>:
        /// the layout a redraw caused, and then the callback that stores what the redraw cost.
        /// A redraw timer, which ticks at Background, does not run.
        /// </summary>
        public static void ToLoaded() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        /// <summary>
        /// Asserts that a redraw waits, and that its timer was set to <paramref name="whole"/> less
        /// what had passed since the redraw before it. <paramref name="clock"/> was started before
        /// that redraw, so it has run at least as long as the view's own clock: no test sleeps, and
        /// a slow machine only widens the range.
        /// </summary>
        public static void AssertWaits(TimeSpan? pending, TimeSpan whole, Stopwatch clock)
        {
            TimeSpan passed = clock.Elapsed;
            Assert.True(pending.HasValue, "a redraw waits for its timer");
            Assert.InRange(pending.GetValueOrDefault(), whole - passed, whole);
        }
    }
}
