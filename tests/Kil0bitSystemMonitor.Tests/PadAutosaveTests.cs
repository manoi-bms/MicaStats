using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>When a save happens, and that the one writer thread never loses or reorders one.</summary>
    public class PadAutosaveTests
    {
        private static readonly DateTime T0 = new(2026, 9, 29, 5, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

        [Fact]
        public void A_change_is_due_one_second_after_the_last_edit()
        {
            var scheduler = new AutosaveScheduler();
            scheduler.MarkChanged("n", T0);

            Assert.Empty(scheduler.TakeDue(T0.AddMilliseconds(900)));
            Assert.Equal(new[] { "n" }, scheduler.TakeDue(T0.AddSeconds(1)));
            Assert.False(scheduler.IsPending("n"));
        }

        [Fact]
        public void Continuous_typing_is_saved_within_five_seconds()
        {
            var scheduler = new AutosaveScheduler();
            DateTime t = T0;
            for (int i = 0; i < 10; i++)
            {
                scheduler.MarkChanged("n", t);
                Assert.Empty(scheduler.TakeDue(t));
                t = t.AddMilliseconds(500);
            }

            scheduler.MarkChanged("n", t);   // T0 + 5 s, still typing
            Assert.Equal(new[] { "n" }, scheduler.TakeDue(t));
        }

        [Fact]
        public void TakeAll_and_Forget_clear_pending_changes()
        {
            var scheduler = new AutosaveScheduler();
            scheduler.MarkChanged("a", T0);
            scheduler.MarkChanged("b", T0);
            scheduler.Forget("a");

            Assert.Equal(new[] { "b" }, scheduler.TakeAll());
            Assert.False(scheduler.IsPending("b"));
        }

        [Fact]
        public void The_default_backoff_steps_up_to_ten_seconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(1), AutosaveWriter.DefaultBackoff(1));
            Assert.Equal(TimeSpan.FromSeconds(2), AutosaveWriter.DefaultBackoff(2));
            Assert.Equal(TimeSpan.FromSeconds(5), AutosaveWriter.DefaultBackoff(3));
            Assert.Equal(TimeSpan.FromSeconds(10), AutosaveWriter.DefaultBackoff(4));
            Assert.Equal(TimeSpan.FromSeconds(10), AutosaveWriter.DefaultBackoff(40));
        }

        [Fact]
        public void Work_runs_in_the_order_it_was_queued()
        {
            using var writer = new AutosaveWriter(_ => Fast);
            var order = new ConcurrentQueue<string>();
            foreach (string key in new[] { "a", "b", "c" })
                writer.Enqueue(key, () => order.Enqueue(key));

            Assert.True(writer.FlushAll(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "a", "b", "c" }, order);
        }

        [Fact]
        public void Newer_work_for_a_key_replaces_work_still_waiting()
        {
            using var writer = new AutosaveWriter(_ => Fast);
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var ran = new ConcurrentQueue<string>();

            writer.Enqueue("blocker", () => { started.Set(); release.Wait(); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            writer.Enqueue("note", () => ran.Enqueue("v1"));
            writer.Enqueue("note", () => ran.Enqueue("v2"));
            release.Set();

            Assert.True(writer.FlushAll(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "v2" }, ran);
        }

        [Fact]
        public void Failed_work_is_retried_until_it_succeeds()
        {
            using var writer = new AutosaveWriter(_ => Fast);
            int attempts = 0;
            var outcomes = new ConcurrentQueue<Exception?>();
            writer.Completed += (key, error) => outcomes.Enqueue(error);

            writer.Enqueue("note", () =>
            {
                if (Interlocked.Increment(ref attempts) < 3) throw new IOException("disk busy");
            });

            Assert.True(writer.FlushAll(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, attempts);
            Assert.Equal(3, outcomes.Count);
            Assert.Null(outcomes.Last());
        }

        [Fact]
        public void FlushAll_gives_up_on_work_that_keeps_failing()
        {
            using var writer = new AutosaveWriter(_ => Fast);
            writer.Enqueue("note", () => throw new IOException("read-only"));

            Assert.False(writer.FlushAll(TimeSpan.FromMilliseconds(300)));
            Assert.Equal(1, writer.PendingCount);
        }
    }
}
