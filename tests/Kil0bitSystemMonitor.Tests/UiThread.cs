using System;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// One STA thread with a WPF Application carrying the app's ModernWpf resources, shared by
    /// every UI test. WPF allows one Application per process, and a window must be built on the
    /// thread that owns the resources it looks up. The tests take turns on it: see
    /// <see cref="UiTurn"/>.
    /// </summary>
    internal static class UiThread
    {
        private static readonly Lazy<Dispatcher> s_dispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<UiTurn> s_turn = new(() => new UiTurn(s_dispatcher.Value), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The turn every test body on the shared thread takes.</summary>
        internal static UiTurn Turn => s_turn.Value;

        /// <summary>Runs <paramref name="action"/> on the UI thread, in its turn; its exceptions surface here.</summary>
        public static void Run(Action action) => Turn.Run(action);

        /// <summary>
        /// Starts an async <paramref name="action"/> on the UI thread, in its turn. Its awaits resume
        /// there, the dispatcher stays free in between, and its exceptions surface in the returned task.
        /// </summary>
        public static Task RunAsync(Func<Task> action) => Turn.RunAsync(action);

        /// <summary>
        /// As <see cref="RunAsync(Func{Task})"/>, for a test that must not hang: the returned task
        /// fails with a <see cref="TimeoutException"/> when the body has not ended
        /// <paramref name="limit"/> after it started. The time it waits for its turn does not count.
        /// </summary>
        public static Task RunAsync(Func<Task> action, TimeSpan limit) => Turn.RunAsync(action, limit);

        private static Dispatcher Start()
        {
            Dispatcher? dispatcher = null;
            Exception? failure = null;
            using var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                try
                {
                    var app = System.Windows.Application.Current
                              ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    var resources = new System.Windows.ResourceDictionary();
                    resources.MergedDictionaries.Add(new ModernWpf.ThemeResources());
                    resources.MergedDictionaries.Add(new ModernWpf.Controls.XamlControlsResources());
                    app.Resources = resources;
                    dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    ready.Set();
                }

                if (failure == null) Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "UI tests",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (failure != null) throw new InvalidOperationException("The UI test thread could not start", failure);
            return dispatcher!;
        }
    }

    /// <summary>
    /// One test body at a time on a dispatcher that the test classes share while they run in
    /// parallel.
    /// <para>
    /// Without it the bodies ran inside each other. A blocking body came in through
    /// Dispatcher.Invoke, at Send priority, and so started inside whatever frame was being pumped:
    /// another test's helper waiting for a task, eighteen frames deep in one measured run. An async
    /// body came in at Normal priority and resumed at it, so while blocking bodies kept arriving it
    /// neither started nor went on, and its own time limit, counted from the moment it was queued,
    /// ran out before its first line.
    /// </para>
    /// <para>
    /// A body waits for the turn on the thread that asked, never on the dispatcher, and a body
    /// already on the UI thread (a helper calling Run inside a test body, or from a nested pump) is
    /// inside the turn that test has and does not wait for another.
    /// </para>
    /// </summary>
    internal sealed class UiTurn
    {
        private readonly Dispatcher _dispatcher;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly SemaphoreSlim _turn = new(1, 1);
        private int _waiting;

        /// <param name="dispatcher">The thread the bodies run on.</param>
        /// <param name="delay">The clock of a time limit; null (everywhere but the turn's own tests) is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
        public UiTurn(Dispatcher dispatcher, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _dispatcher = dispatcher;
            _delay = delay ?? Task.Delay;
        }

        /// <summary>How many bodies are queued for the turn and have not been given it.</summary>
        public int Waiting => Volatile.Read(ref _waiting);

        /// <summary>True while no body has the turn.</summary>
        public bool IsFree => _turn.CurrentCount == 1;

        /// <summary>Runs <paramref name="action"/> on the UI thread, in its turn; its exceptions surface here.</summary>
        public void Run(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(action);
                return;
            }

            // Queued as the async bodies queue, and waited for here on the caller's thread. With
            // every waiter in that one queue the semaphore hands the turn to the body that asked
            // first. A blocking Wait() is only woken to take it, and a body arriving just then may
            // take it first: no order to rely on.
            Task turn = Queue();
            try
            {
                turn.GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }

            try
            {
                _dispatcher.Invoke(action);
            }
            finally
            {
                _turn.Release();
            }
        }

        /// <summary>
        /// Starts an async <paramref name="action"/> on the UI thread, in its turn. With a
        /// <paramref name="limit"/>, the returned task fails with a <see cref="TimeoutException"/>
        /// when the body has not ended that long after it started, and the turn goes to the next
        /// body; the time spent waiting for the turn does not count.
        /// </summary>
        public Task RunAsync(Func<Task> action, TimeSpan? limit = null) =>
            _dispatcher.CheckAccess() ? Body(action, limit) : InTurn(action, limit);

        /// <summary>Joins the queue for the turn. The count goes up once the body is in the queue, so a count read later is a place in it.</summary>
        private Task Queue()
        {
            Task turn = _turn.WaitAsync();
            Interlocked.Increment(ref _waiting);
            return turn;
        }

        /// <summary>
        /// Nothing here resumes on the caller's context: the test threads may all be blocked
        /// waiting for the turn, and the one that has it must still be able to give it back.
        /// </summary>
        private async Task InTurn(Func<Task> action, TimeSpan? limit)
        {
            Task turn = Queue();
            try
            {
                await turn.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }

            try
            {
                await Body(action, limit).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();   // the one place an async body's turn ends: when it ended, failed, was cancelled or ran out of time
            }
        }

        private Task Body(Func<Task> action, TimeSpan? limit) =>
            limit is { } allowed ? Bounded(action, allowed) : _dispatcher.InvokeAsync(action).Task.Unwrap();

        private async Task Bounded(Func<Task> action, TimeSpan limit)
        {
            using var stop = new CancellationTokenSource();
            var clock = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task body = _dispatcher.InvokeAsync(() =>
            {
                clock.SetResult(_delay(limit, stop.Token));   // the limit counts from here: the body is starting
                return action();
            }).Task.Unwrap();

            Task first = await Task.WhenAny(body, clock.Task.Unwrap()).ConfigureAwait(false);
            stop.Cancel();
            if (first != body)
            {
                throw new TimeoutException(
                    "The test body did not end within " + limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s of starting");
            }
            await body.ConfigureAwait(false);
        }
    }
}
