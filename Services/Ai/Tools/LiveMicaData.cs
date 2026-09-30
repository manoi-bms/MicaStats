using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.HardwareInfo;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The data source of the running app. <see cref="MetricsHistory"/> is UI-thread only, so
    /// live readings are taken through the UI dispatcher; tool calls arrive on other threads
    /// (the assistant loop, the tool pipe) and never touch the series directly.
    /// </summary>
    public sealed class LiveMicaData : IMicaData
    {
        /// <summary>How long a process ranking may take: two sampler passes 2 s apart, plus slack.</summary>
        internal static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(6);

        /// <summary>The hardware report and boot history take a second or more and change rarely.</summary>
        private static readonly TimeSpan SlowCacheTtl = TimeSpan.FromMinutes(10);

        private readonly MetricsHistory _history;
        private readonly Dispatcher _ui;
        private readonly ProcessSampler _sampler;
        private readonly HistoryStore _store;
        private readonly Func<AlertMonitor?> _alerts;
        private readonly Func<BatteryMonitor?> _battery;
        private readonly Func<DateTime> _clock;
        private readonly SlowCache<string?> _hardware;
        private readonly SlowCache<BootAnalysis?> _boot;

        /// <summary>
        /// Wires the tools to the app's own services. <paramref name="alerts"/> and
        /// <paramref name="battery"/> are read on each call, because they may start after the tools.
        /// </summary>
        public LiveMicaData(MetricsHistory history, Dispatcher ui, ProcessSampler sampler, HistoryStore store,
                            Func<AlertMonitor?> alerts, Func<BatteryMonitor?> battery, Func<DateTime> utcClock)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
            _battery = battery ?? throw new ArgumentNullException(nameof(battery));
            _clock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
            _hardware = new SlowCache<string?>(
                () => HardwareReportWriter.Write(HardwareInfoService.Gather(), HardwareInfoService.AppVersion), _clock);
            _boot = new SlowCache<BootAnalysis?>(() => BootAnalyzer.Gather(), _clock);
        }

        /// <inheritdoc/>
        public DateTime UtcNow => _clock();

        /// <inheritdoc/>
        public bool IsLive => true;

        /// <inheritdoc/>
        public SystemMetrics? Latest() => OnUi(() => _history.Cpu.Count == 0 ? null : _history.Latest);

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, SeriesStats> RecentStats() => OnUi(() =>
        {
            var stats = new Dictionary<string, SeriesStats>();
            AddStats(stats, "cpu", _history.Cpu);
            AddStats(stats, "ram", _history.Ram);
            AddStats(stats, "gpu", _history.Gpu);
            AddStats(stats, "temp", _history.Temp);
            AddStats(stats, "netUp", _history.NetUp);
            AddStats(stats, "netDown", _history.NetDown);
            return (IReadOnlyDictionary<string, SeriesStats>)stats;
        });

        /// <summary>
        /// Takes a sampler lease, waits for a sample with CPU deltas (two passes, about 2 s),
        /// ranks every process and releases the lease, whatever happens.
        /// </summary>
        public async Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct)
        {
            _sampler.Retain();
            try
            {
                var waited = Stopwatch.StartNew();
                while (!_sampler.HasCpuData)
                {
                    if (waited.Elapsed > SampleTimeout)
                        throw new DataUnavailableException("The process list did not arrive within 6 seconds.");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                IReadOnlyList<ProcessUsage> all = _sampler.AllProcesses;
                IEnumerable<ProcessUsage> ranked = by switch
                {
                    "memory" => all.OrderByDescending(p => p.WorkingSet),
                    "disk" => all.OrderByDescending(p => p.DiskBytesPerSec),
                    _ => all.OrderByDescending(p => p.CpuPercent),
                };
                return ranked.Take(Math.Max(1, count))
                    .Select(p => new ProcessInfo(p.Name, ProcessPaths.TryGetPath(p.Pid), p.Pid, p.CreateTime,
                        p.CpuPercent, p.WorkingSet / 1048576d, p.DiskBytesPerSec / 1024d))
                    .ToList();
            }
            finally
            {
                _sampler.Release();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc) => _store.Read(fromUtc, toUtc);

        /// <inheritdoc/>
        public IReadOnlyList<SavedReport> SlowdownReports() => SlowdownReportFiles.List(SlowdownRecorder.ReportDir);

        /// <inheritdoc/>
        public string? ReadSlowdownReport(string id) => SlowdownReportFiles.Read(SlowdownRecorder.ReportDir, id);

        /// <inheritdoc/>
        public IReadOnlyList<AlertRule> AlertRules() => _alerts()?.Rules ?? Array.Empty<AlertRule>();

        /// <inheritdoc/>
        public IReadOnlyList<AlertEvent> RecentAlerts() => _alerts()?.Recent ?? Array.Empty<AlertEvent>();

        /// <inheritdoc/>
        public Task<string?> HardwareReportAsync(CancellationToken ct) => _hardware.GetAsync(ct);

        /// <inheritdoc/>
        public BatteryReading? Battery()
        {
            BatteryReading reading = BatteryMonitor.ReadCached();
            return reading.Present ? reading : null;
        }

        /// <inheritdoc/>
        public async Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct)
        {
            // A desktop has nothing to read, and asking would spawn powercfg for nothing.
            if (Battery() == null) return null;
            BatteryMonitor? monitor = _battery();
            if (monitor == null) return null;
            BatteryHealth health = await monitor.GetHealthAsync().WaitAsync(ct).ConfigureAwait(false);
            return health.Any ? health : null;
        }

        /// <inheritdoc/>
        public Task<BootAnalysis?> BootAsync(CancellationToken ct) => _boot.GetAsync(ct);

        /// <summary>Min/avg/max of the non-negative samples; negative values are "unavailable" sentinels.</summary>
        private static void AddStats(Dictionary<string, SeriesStats> into, string key, Series series)
        {
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            int n = 0;
            for (int i = 0; i < series.Count; i++)
            {
                float v = series[i];
                if (v < 0 || !float.IsFinite(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                n++;
            }
            if (n > 0) into[key] = new SeriesStats(min, (float)(sum / n), max, n);
        }

        private T OnUi<T>(Func<T> read)
        {
            if (_ui.CheckAccess()) return read();
            if (_ui.HasShutdownStarted) throw new DataUnavailableException("MicaStats is closing.");
            return _ui.Invoke(read);
        }

        /// <summary>One slow value, gathered on the thread pool and kept for <see cref="SlowCacheTtl"/>.</summary>
        private sealed class SlowCache<T>
        {
            private readonly Func<T> _load;
            private readonly Func<DateTime> _clock;
            private readonly object _gate = new();
            private DateTime _at;
            private bool _has;
            private T _value = default!;

            public SlowCache(Func<T> load, Func<DateTime> clock)
            {
                _load = load;
                _clock = clock;
            }

            public async Task<T> GetAsync(CancellationToken ct)
            {
                lock (_gate)
                {
                    if (_has && _clock() - _at < SlowCacheTtl) return _value;
                }
                T value = await Task.Run(_load, ct).WaitAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _value = value;
                    _at = _clock();
                    _has = true;
                }
                return value;
            }
        }
    }
}
