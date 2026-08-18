using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// A fixed-capacity ring of recent samples for one metric.
    ///
    /// Indexing is oldest-to-newest so a renderer can walk left to right without knowing where
    /// the buffer wraps. Capacity is allocated once, so steady-state sampling never allocates.
    /// </summary>
    public sealed class Series
    {
        private readonly float[] _buffer;
        private int _head;      // next write position
        private int _count;
        private bool _sawValue;
        private bool _everUnavailable;

        /// <summary>Rolling peak for autoscaled series, decayed so one spike does not flatten the graph forever.</summary>
        private float _peak;

        /// <summary>Fraction of the peak retained per sample when the current value is lower.</summary>
        private const float PeakDecay = 0.98f;

        public Series(int capacity, float peakFloor = 0f)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new float[capacity];
            PeakFloor = peakFloor;
            _peak = peakFloor;
        }

        public int Capacity => _buffer.Length;

        /// <summary>Number of samples currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        /// <summary>Lower bound for the autoscale peak, so an idle series is not amplified to full scale.</summary>
        public float PeakFloor { get; }

        /// <summary>Current autoscale ceiling: the decayed maximum, never below <see cref="PeakFloor"/>.</summary>
        public float Peak => Math.Max(PeakFloor, _peak);

        /// <summary>
        /// Whether this series has usable data. A graph cannot render "N/A", so an unreadable
        /// sensor must not be drawn as a flat line at zero — that would assert "idle".
        /// </summary>
        public Availability Availability
        {
            get
            {
                if (_sawValue) return Availability.Value;
                return _everUnavailable ? Availability.Unavailable : Availability.NoDataYet;
            }
        }

        /// <summary>The most recent sample, or 0 when empty.</summary>
        public float Latest => _count == 0 ? 0f : this[_count - 1];

        /// <summary>Highest retained sample, or 0 when empty. Shown as the window peak.</summary>
        public float Max
        {
            get
            {
                if (_count == 0) return 0f;
                float m = float.NegativeInfinity;
                for (int i = 0; i < _count; i++) { float v = this[i]; if (v > m) m = v; }
                return float.IsNegativeInfinity(m) ? 0f : m;
            }
        }

        /// <summary>Mean of the retained samples, or 0 when empty.</summary>
        public float Average
        {
            get
            {
                if (_count == 0) return 0f;
                double sum = 0;
                for (int i = 0; i < _count; i++) sum += this[i];
                return (float)(sum / _count);
            }
        }

        /// <summary>Oldest-to-newest access. Index 0 is the oldest retained sample.</summary>
        public float this[int i]
        {
            get
            {
                if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
                // When full, the oldest sample sits at _head; otherwise the buffer starts at 0.
                int start = _count == _buffer.Length ? _head : 0;
                return _buffer[(start + i) % _buffer.Length];
            }
        }

        /// <summary>Appends a sample. NaN and infinity are treated as unavailable, not as data.</summary>
        public void Add(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                _everUnavailable = true;
                return;
            }

            _buffer[_head] = value;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            _sawValue = true;

            if (value > _peak) _peak = value;
            else _peak = Math.Max(PeakFloor, _peak * PeakDecay);
        }

        /// <summary>
        /// Records that the sensor could not be read this tick. No sample is appended, so the
        /// graph shows a gap in time rather than a fabricated zero.
        /// </summary>
        public void AddUnavailable() => _everUnavailable = true;

        public void Clear()
        {
            _head = 0;
            _count = 0;
            _sawValue = false;
            _everUnavailable = false;
            _peak = PeakFloor;
        }
    }

    /// <summary>
    /// Retains recent telemetry and is the single fan-out point for anything that renders it.
    ///
    /// <para>
    /// This type owns the only thread hop. <see cref="TelemetryService"/> raises its event on a
    /// timer thread; this class marshals once to the UI dispatcher and re-raises
    /// <see cref="Updated"/> there. Every consumer downstream is therefore UI-thread-only and
    /// needs no synchronisation — which is why the ring buffers below are lock-free.
    /// </para>
    /// </summary>
    public sealed class MetricsHistory : IDisposable
    {
        /// <summary>
        /// Samples retained per series. At the 1000ms default this is two minutes of history; at
        /// the 500ms setting, one minute. The panel renders 60 and the overlay sparkline ~13.
        /// </summary>
        public const int DefaultCapacity = 120;

        // Network is autoscaled, so give it a floor of 64 KB/s. Without one, an idle link would
        // amplify a few hundred bytes per second into a full-height graph.
        private const float NetPeakFloorKbps = 64f;

        private readonly int _capacity;
        private readonly System.Windows.Threading.Dispatcher? _ui;
        private readonly TelemetryService? _telemetry;
        private readonly Action<SystemMetrics>? _onMetrics;
        private readonly Dictionary<string, Series> _disks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Series> _cores = new();
        private bool _disposed;

        public Series Cpu { get; }

        /// <summary>
        /// Kernel-mode share of total CPU, for the stacked User/System graph. Unavailable when
        /// the "% Privileged Time" counter cannot be read; the graph then falls back to a
        /// single-colour bar rather than asserting "no system time".
        /// </summary>
        public Series CpuSystem { get; }

        public Series Ram { get; }
        public Series Gpu { get; }
        public Series Temp { get; }
        public Series NetUp { get; }
        public Series NetDown { get; }

        /// <summary>Per-logical-processor series, ordered as <see cref="SystemMetrics.CoreUsage"/>.</summary>
        public IReadOnlyList<Series> Cores => _cores;

        /// <summary>The most recent full sample. Never null; defaults to an empty instance.</summary>
        public SystemMetrics Latest { get; private set; } = new();

        /// <summary>Raised on the UI thread after a sample has been appended.</summary>
        public event Action? Updated;

        /// <summary>
        /// Creates a history not attached to any telemetry source. Samples must be supplied by
        /// calling <see cref="Append"/> directly. Used for replaying captured data and by tests,
        /// which must not construct a <see cref="TelemetryService"/> — doing so would open real
        /// PerformanceCounters and spawn nvidia-smi.
        /// </summary>
        public MetricsHistory(int capacity = DefaultCapacity)
        {
            _capacity = capacity;

            Cpu = new Series(capacity);
            CpuSystem = new Series(capacity);
            Ram = new Series(capacity);
            Gpu = new Series(capacity);
            Temp = new Series(capacity);
            NetUp = new Series(capacity, NetPeakFloorKbps);
            NetDown = new Series(capacity, NetPeakFloorKbps);
        }

        public MetricsHistory(TelemetryService telemetry, System.Windows.Threading.Dispatcher ui, int capacity = DefaultCapacity)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _capacity = capacity;

            Cpu = new Series(capacity);
            CpuSystem = new Series(capacity);
            Ram = new Series(capacity);
            Gpu = new Series(capacity);
            Temp = new Series(capacity);
            NetUp = new Series(capacity, NetPeakFloorKbps);
            NetDown = new Series(capacity, NetPeakFloorKbps);

            _onMetrics = m => ui.BeginInvoke(() => Append(m));
            _telemetry.MetricsUpdated += _onMetrics;
        }

        /// <summary>
        /// Network graphs share one scale so upload and download are visually comparable. A
        /// separate scale per direction makes a trickle of upload look like a saturated link.
        /// </summary>
        public float SharedNetPeak => Math.Max(NetUp.Peak, NetDown.Peak);

        /// <summary>
        /// The series for a disk, keyed by PerformanceCounter instance name (e.g. "0 C:").
        /// Returns null for a disk that has never been sampled.
        /// </summary>
        public Series? Disk(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return null;
            return _disks.TryGetValue(instanceName, out var s) ? s : null;
        }

        /// <summary>Disk instance names currently retained.</summary>
        public IEnumerable<string> DiskNames => _disks.Keys;

        /// <summary>
        /// Appends a sample. Public so tests can drive the history without a TelemetryService;
        /// in the running application only the marshalled telemetry callback calls it.
        /// </summary>
        public void Append(SystemMetrics m)
        {
            if (_disposed || m == null) return;

            Latest = m;

            Cpu.Add(m.CpuUsage);

            // -1 is the sampler's "counter unreadable". The value is also capped at the total:
            // the two are computed from separate counter deltas, so a busy tick can race them
            // slightly apart, and a system share above the total would draw outside the bar.
            if (m.CpuSystem >= 0f) CpuSystem.Add(Math.Min(m.CpuSystem, m.CpuUsage));
            else CpuSystem.AddUnavailable();

            Ram.Add(m.RamPercent);
            Gpu.Add(m.GpuUsage);

            // The temperature series prefers the CPU package (what temperature-watchers mean by
            // "temp") and falls back to the GPU sensor. -1 is "no source could supply one";
            // appending it would draw below the axis and claim minus one degree.
            float displayTemp = m.CpuTemperature > 0 ? m.CpuTemperature : m.GpuTemperature;
            if (displayTemp > 0) Temp.Add(displayTemp);
            else Temp.AddUnavailable();

            NetUp.Add(m.NetUpKbps);
            NetDown.Add(m.NetDownKbps);

            UpdateDisks(m);
            UpdateCores(m);

            Updated?.Invoke();
        }

        private void UpdateDisks(SystemMetrics m)
        {
            if (m.Disks == null) return;

            foreach (var d in m.Disks)
            {
                if (string.IsNullOrEmpty(d.Name)) continue;
                if (!_disks.TryGetValue(d.Name, out var series))
                {
                    series = new Series(_capacity);
                    _disks[d.Name] = series;
                }
                series.Add(d.ActivityPercent);
            }

            // Evict series for disks no longer reported. Instance names change when drives are
            // added or removed and when the user edits the selection, so without eviction this
            // dictionary grows for the life of the process.
            if (_disks.Count > m.Disks.Count)
            {
                var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in m.Disks)
                {
                    if (!string.IsNullOrEmpty(d.Name)) live.Add(d.Name);
                }

                List<string>? stale = null;
                foreach (var key in _disks.Keys)
                {
                    if (!live.Contains(key)) (stale ??= new List<string>()).Add(key);
                }
                if (stale != null)
                {
                    foreach (var key in stale) _disks.Remove(key);
                }
            }
        }

        private void UpdateCores(SystemMetrics m)
        {
            var usage = m.CoreUsage;
            if (usage == null || usage.Length == 0) return;

            // Core count can change (VM hot-add), so grow to fit rather than assuming it is fixed.
            while (_cores.Count < usage.Length) _cores.Add(new Series(_capacity));
            if (_cores.Count > usage.Length) _cores.RemoveRange(usage.Length, _cores.Count - usage.Length);

            for (int i = 0; i < usage.Length; i++) _cores[i].Add(usage[i]);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_telemetry != null && _onMetrics != null) _telemetry.MetricsUpdated -= _onMetrics;
        }
    }
}
