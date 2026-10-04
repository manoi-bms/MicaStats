using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The turn the UI tests take on the thread they share (<see cref="UiTurn"/>), over a
    /// dispatcher of this class's own, so nothing here holds up the tests on the shared one. No
    /// result depends on how long anything takes: a body is held by a gate the test opens, a
    /// waiting body is known by the turn's own count, a body that was queued on the dispatcher has
    /// run once the dispatcher is drained, and the clock of a time limit is one the test rings.
    /// </summary>
    public class UiTurnTests
    {
        /// <summary>How long a step that must happen may take before the test fails where it would hang. No result depends on it.</summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        /// <summary>A dispatcher on a thread of its own, the turn on it, and what the bodies noted.</summary>
        private sealed class Ui : IDisposable
        {
            private readonly List<string> _log = new();

            public Ui(Func<TimeSpan, CancellationToken, Task>? delay = null)
            {
                Dispatcher? dispatcher = null;
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "UI turn tests",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
                Dispatcher = dispatcher!;
                Turn = new UiTurn(Dispatcher, delay);
            }

            public Dispatcher Dispatcher { get; }

            public UiTurn Turn { get; }

            public string[] Log
            {
                get
                {
                    lock (_log) return _log.ToArray();
                }
            }

            public void Note(string what)
            {
                lock (_log) _log.Add(what);
            }

            /// <summary>Returns once the dispatcher has run all it held: a body that was queued on it has started.</summary>
            public void Drain() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle, CancellationToken.None, Patience);

            /// <summary>Waits until <paramref name="count"/> bodies are queued for the turn.</summary>
            public void UntilWaiting(int count) =>
                Assert.True(SpinWait.SpinUntil(() => Turn.Waiting == count, Patience), "The body never asked for the turn");

            public void Dispose() => Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        /// <summary>A time limit's clock that the test rings.</summary>
        private sealed class Clock
        {
            private readonly TaskCompletionSource _rings = Signal();
            private int _asked;

            /// <summary>How many limits were started.</summary>
            public int Asked => Volatile.Read(ref _asked);

            public TimeSpan Limit { get; private set; }

            /// <summary>Cancelled when the limit is no longer needed.</summary>
            public CancellationToken Stop { get; private set; }

            public Task Delay(TimeSpan limit, CancellationToken stop)
            {
                Limit = limit;
                Stop = stop;
                Interlocked.Increment(ref _asked);
                return _rings.Task;
            }

            public void Ring() => _rings.SetResult();
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the turn with a body that awaits <paramref name="gate"/>, as a test awaiting its model does; returns once it is inside.</summary>
        private static async Task<Task> Hold(Ui ui, TaskCompletionSource gate)
        {
            var inside = Signal();
            Task first = ui.Turn.RunAsync(async () =>
            {
                ui.Note("first in");
                inside.SetResult();
                await gate.Task;   // the dispatcher is free here
                ui.Note("first out");
            });
            await inside.Task.WaitAsync(Patience);
            return first;
        }

        [Fact]
        public async Task A_body_asked_for_while_another_awaits_starts_only_once_that_one_has_ended()
        {
            using var ui = new Ui();
            var gate = Signal();
            Task first = await Hold(ui, gate);

            Task second = ui.Turn.RunAsync(() =>
            {
                ui.Note("second in");
                return Task.CompletedTask;
            });
            ui.Drain();

            Assert.Equal(new[] { "first in" }, ui.Log);   // not inside the first one's await
            Assert.Equal(1, ui.Turn.Waiting);
            Assert.False(ui.Turn.IsFree);

            gate.SetResult();
            await first.WaitAsync(Patience);
            await second.WaitAsync(Patience);

            Assert.Equal(new[] { "first in", "first out", "second in" }, ui.Log);
            Assert.Equal(0, ui.Turn.Waiting);
            Assert.True(ui.Turn.IsFree);
        }

        [Fact]
        public async Task A_blocking_body_asked_for_from_another_thread_waits_for_the_turn_as_well()
        {
            using var ui = new Ui();
            var gate = Signal();
            Task first = await Hold(ui, gate);

            Task second = Task.Run(() => ui.Turn.Run(() => ui.Note("second in")));
            ui.UntilWaiting(1);
            ui.Drain();

            Assert.Equal(new[] { "first in" }, ui.Log);

            gate.SetResult();
            await first.WaitAsync(Patience);
            await second.WaitAsync(Patience);

            Assert.Equal(new[] { "first in", "first out", "second in" }, ui.Log);
            Assert.True(ui.Turn.IsFree);
        }

        /// <summary>What made the frames eighteen deep: a helper pumps a nested frame, and the next test's body ran inside it.</summary>
        [Fact]
        public async Task A_body_that_pumps_the_dispatcher_is_not_entered_by_another()
        {
            using var ui = new Ui();
            var inside = Signal();
            DispatcherFrame? frame = null;
            Task first = Task.Run(() => ui.Turn.Run(() =>
            {
                ui.Note("first in");
                frame = new DispatcherFrame();
                inside.SetResult();
                Dispatcher.PushFrame(frame);
                ui.Note("first out");
            }));
            await inside.Task.WaitAsync(Patience);

            Task second = Task.Run(() => ui.Turn.Run(() => ui.Note("second in")));
            ui.UntilWaiting(1);
            ui.Drain();   // runs inside the first body's frame, where the second body used to

            Assert.Equal(new[] { "first in" }, ui.Log);

            ui.Dispatcher.Invoke(() => frame!.Continue = false);
            await first.WaitAsync(Patience);
            await second.WaitAsync(Patience);

            Assert.Equal(new[] { "first in", "first out", "second in" }, ui.Log);
        }

        /// <summary>Blocking and async bodies wait in one queue: neither kind goes ahead of the other.</summary>
        [Fact]
        public async Task Bodies_get_the_turn_in_the_order_they_asked_whether_they_block_or_await()
        {
            using var ui = new Ui();
            var gate = Signal();
            Task first = await Hold(ui, gate);

            Task blocking = Task.Run(() => ui.Turn.Run(() => ui.Note("blocking, asked first")));
            ui.UntilWaiting(1);
            Task awaiting = ui.Turn.RunAsync(() =>
            {
                ui.Note("awaiting, asked second");
                return Task.CompletedTask;
            });
            Assert.Equal(2, ui.Turn.Waiting);
            Task blockingToo = Task.Run(() => ui.Turn.Run(() => ui.Note("blocking, asked third")));
            ui.UntilWaiting(3);

            gate.SetResult();
            await Task.WhenAll(first, blocking, awaiting, blockingToo).WaitAsync(Patience);

            Assert.Equal(new[] { "first in", "first out", "blocking, asked first", "awaiting, asked second", "blocking, asked third" }, ui.Log);
        }

        [Fact]
        public async Task A_body_already_on_the_UI_thread_does_not_wait_for_the_turn_it_has()
        {
            using var ui = new Ui();

            Task awaiting = ui.Turn.RunAsync(async () =>
            {
                ui.Turn.Run(() => ui.Note("Run in a body"));
                await ui.Turn.RunAsync(() =>
                {
                    ui.Note("RunAsync in a body");
                    return Task.CompletedTask;
                });
                await ui.Turn.RunAsync(() =>
                {
                    ui.Note("RunAsync with a limit in a body");
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(60));

                var frame = new DispatcherFrame();
                _ = ui.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ui.Turn.Run(() => ui.Note("Run in a nested pump"));
                    frame.Continue = false;
                }));
                Dispatcher.PushFrame(frame);
            });
            await awaiting.WaitAsync(Patience);   // waiting for its own turn would never end

            Task blocking = Task.Run(() => ui.Turn.Run(() => ui.Turn.Run(() => ui.Note("Run in a blocking body"))));
            await blocking.WaitAsync(Patience);

            Assert.Equal(
                new[] { "Run in a body", "RunAsync in a body", "RunAsync with a limit in a body", "Run in a nested pump", "Run in a blocking body" },
                ui.Log);
            Assert.True(ui.Turn.IsFree);
        }

        [Fact]
        public async Task The_turn_is_given_back_when_a_body_throws_fails_or_is_cancelled()
        {
            using var ui = new Ui();
            var limit = TimeSpan.FromSeconds(60);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Task.Run(() => ui.Turn.Run(() => throw new InvalidOperationException("a blocking body threw"))));
            Assert.True(ui.Turn.IsFree);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ui.Turn.RunAsync(() => throw new InvalidOperationException("thrown before the first await")));
            Assert.True(ui.Turn.IsFree);

            await Assert.ThrowsAsync<InvalidOperationException>(() => ui.Turn.RunAsync(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("thrown after an await");
            }));
            Assert.True(ui.Turn.IsFree);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ui.Turn.RunAsync(() => Task.FromException(new InvalidOperationException("a failed task"))));
            Assert.True(ui.Turn.IsFree);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ui.Turn.RunAsync(() => Task.FromCanceled(new CancellationToken(canceled: true))));
            Assert.True(ui.Turn.IsFree);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ui.Turn.RunAsync(() => throw new InvalidOperationException("thrown under a limit"), limit));
            Assert.True(ui.Turn.IsFree);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ui.Turn.RunAsync(() => Task.FromCanceled(new CancellationToken(canceled: true)), limit));
            Assert.True(ui.Turn.IsFree);

            Assert.Equal(0, ui.Turn.Waiting);
            bool ran = false;
            await Task.Run(() => ui.Turn.Run(() => ran = true)).WaitAsync(Patience);   // the next test still gets in
            Assert.True(ran);
        }

        [Fact]
        public async Task A_time_limit_counts_from_when_the_body_starts_and_a_body_past_it_gives_the_turn_back()
        {
            var clock = new Clock();
            using var ui = new Ui(clock.Delay);
            var gate = Signal();
            Task first = await Hold(ui, gate);

            var started = Signal();
            var never = Signal();
            Task second = ui.Turn.RunAsync(async () =>
            {
                started.SetResult();
                await never.Task;
                ui.Note("second out");
            }, TimeSpan.FromSeconds(60));
            ui.Drain();

            Assert.Equal(0, clock.Asked);   // it waits for its turn, and its minute has not begun

            gate.SetResult();
            await first.WaitAsync(Patience);
            await started.Task.WaitAsync(Patience);

            Assert.Equal(1, clock.Asked);   // begun as the body started
            Assert.Equal(TimeSpan.FromSeconds(60), clock.Limit);
            Assert.False(second.IsCompleted);

            clock.Ring();
            var late = await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Patience));

            Assert.Equal("The test body did not end within 60 s of starting", late.Message);
            Assert.True(ui.Turn.IsFree);    // though its body is still there

            await ui.Turn.RunAsync(() =>
            {
                ui.Note("third");
                return Task.CompletedTask;
            }).WaitAsync(Patience);
            never.SetResult();              // the body that hung ends after all
            ui.Drain();

            Assert.Equal(new[] { "first in", "first out", "third", "second out" }, ui.Log);
            Assert.True(ui.Turn.IsFree);    // given back once, not once more
            Assert.Equal(1, clock.Asked);   // a body with no limit starts no clock
        }

        [Fact]
        public async Task A_body_that_ends_within_its_limit_stops_its_clock()
        {
            var clock = new Clock();
            using var ui = new Ui(clock.Delay);

            await ui.Turn.RunAsync(async () =>
            {
                await Task.Yield();
                ui.Note("done");
            }, TimeSpan.FromSeconds(60)).WaitAsync(Patience);

            Assert.Equal(new[] { "done" }, ui.Log);
            Assert.Equal(1, clock.Asked);
            Assert.True(clock.Stop.IsCancellationRequested);   // no timer is left running
            Assert.True(ui.Turn.IsFree);
        }

        /// <summary>The thread every UI test shares runs its bodies through the turn, whichever way they come in.</summary>
        [Fact]
        public async Task The_shared_UI_thread_runs_every_body_in_the_turn()
        {
            bool held = false, heldAsync = false, heldWithLimit = false;

            UiThread.Run(() => held = !UiThread.Turn.IsFree);
            await UiThread.RunAsync(() =>
            {
                heldAsync = !UiThread.Turn.IsFree;
                return Task.CompletedTask;
            });
            await UiThread.RunAsync(() =>
            {
                heldWithLimit = !UiThread.Turn.IsFree;
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(60));

            Assert.True(held);
            Assert.True(heldAsync);
            Assert.True(heldWithLimit);
        }
    }
}
