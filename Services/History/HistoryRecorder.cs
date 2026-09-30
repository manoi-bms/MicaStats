using System;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// Feeds the telemetry ticks into a <see cref="MinuteAggregator"/> and writes each finished
    /// minute to the <see cref="HistoryStore"/>, while <c>Keep 7 days of history</c> is on.
    ///
    /// <para>
    /// At the first tick of each minute it asks the <see cref="ITopProcessSource"/> for the busiest
    /// processes; the answer joins that minute's row if it arrives before the minute ends, and is
    /// dropped otherwise. At most one request is out at a time. Maintenance (thinning and cleanup)
    /// runs off the telemetry thread at <see cref="Start"/> and whenever the UTC date changes.
    /// </para>
    ///
    /// <para>
    /// <see cref="OnMetrics"/> arrives on the telemetry timer thread and <see cref="Start"/> /
    /// <see cref="Stop"/> on the UI thread, so the aggregator lives behind one lock. A write that
    /// fails is logged once by the store and the next minute simply tries again.
    /// </para>
    /// </summary>
    public sealed class HistoryRecorder : IDisposable
    {
        private readonly HistoryStore _store;
        private readonly ITopProcessSource _top;
        private readonly Func<DateTime> _utcClock;
        private readonly Action<string> _warn;
        private readonly object _gate = new();

        private MinuteAggregator _aggregator = new();
        private CancellationTokenSource _cts = new();
        private Task _maintenance = Task.CompletedTask;
        private DateTime _minute;
        private DateTime _date;
        private bool _running;
        private bool _topPending;
        private bool _warnedTop;
        private bool _disposed;

        /// <param name="store">Where finished minutes go.</param>
        /// <param name="top">Asked once a minute for the busiest processes.</param>
        /// <param name="utcClock">The time of each tick; telemetry snapshots carry none.</param>
        /// <param name="warn">Told when the top-process source fails, once.</param>
        public HistoryRecorder(HistoryStore store, ITopProcessSource top, Func<DateTime> utcClock, Action<string>? warn = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _top = top ?? throw new ArgumentNullException(nameof(top));
            _utcClock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
            _warn = warn ?? (_ => { });
        }

        /// <summary>Whether ticks are being recorded.</summary>
        public bool IsRunning
        {
            get { lock (_gate) return _running; }
        }

        /// <summary>The latest maintenance run, so tests can wait for it.</summary>
        internal Task Maintenance
        {
            get { lock (_gate) return _maintenance; }
        }

        /// <summary>Starts recording with an empty minute and runs the maintenance in the background. Idempotent.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_running || _disposed) return;
                _running = true;
                _aggregator = new MinuteAggregator();
                _cts = new CancellationTokenSource();
                _minute = default;
                _date = MinuteAggregator.ToUtc(_utcClock()).Date;
                _maintenance = Task.Run(RunMaintenance);
            }
        }

        /// <summary>Stops recording and writes the minute in progress, so turning history off loses nothing. Idempotent.</summary>
        public void Stop()
        {
            HistoryRow? last;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                last = _aggregator.Flush();
                _cts.Cancel();
            }
            if (last != null) _store.Append(last);
        }

        /// <summary>
        /// One telemetry snapshot (<c>TelemetryService.MetricsUpdated</c>, timer thread). Ignored while
        /// stopped. Writes the previous minute when this one starts a new minute, asks for that
        /// minute's top processes, and starts the maintenance when the UTC date has changed.
        /// </summary>
        public void OnMetrics(SystemMetrics m)
        {
            if (m == null) return;

            HistoryRow? finished;
            DateTime minute;
            bool sampleTop = false;
            bool maintain = false;
            CancellationToken token = default;

            lock (_gate)
            {
                if (!_running) return;

                DateTime utc = MinuteAggregator.ToUtc(_utcClock());
                minute = MinuteAggregator.MinuteOf(utc);
                finished = _aggregator.Add(m, utc);

                if (minute != _minute)
                {
                    _minute = minute;
                    if (!_topPending)
                    {
                        _topPending = true;
                        sampleTop = true;
                        token = _cts.Token;
                    }
                }

                if (utc.Date != _date)
                {
                    _date = utc.Date;
                    maintain = true;
                }
            }

            if (finished != null) _store.Append(finished);
            if (maintain)
            {
                var task = Task.Run(RunMaintenance);
                lock (_gate) _maintenance = task;
            }
            if (sampleTop) _ = SampleTopAsync(minute, token);
        }

        /// <summary>Stops for good; <see cref="Start"/> does nothing afterwards.</summary>
        public void Dispose()
        {
            Stop();
            lock (_gate) _disposed = true;
        }

        private async Task SampleTopAsync(DateTime minute, CancellationToken ct)
        {
            try
            {
                TopProcessSample? sample = await _top.SampleAsync(ct).ConfigureAwait(false);
                if (sample == null) return;

                lock (_gate)
                {
                    // A sample that arrives after its minute was written belongs to no row.
                    if (_running && _minute == minute) _aggregator.SetTop(sample);
                }
            }
            catch (Exception ex)
            {
                bool first;
                lock (_gate)
                {
                    first = !_warnedTop;
                    _warnedTop = true;
                }
                if (first) _warn("History could not read the top processes: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                lock (_gate) _topPending = false;
            }
        }

        private void RunMaintenance()
        {
            try
            {
                _store.Maintain();
            }
            catch (Exception ex)
            {
                _warn("History maintenance failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
