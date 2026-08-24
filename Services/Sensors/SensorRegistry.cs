using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// Polls every source once per tick and answers the one question the rest of the app asks
    /// of them: what is the CPU die temperature, if anything can actually supply it.
    /// </summary>
    public sealed class SensorRegistry
    {
        /// <summary>
        /// Long enough that a permanently absent tool costs almost nothing, short enough that
        /// starting HWiNFO mid-session is noticed within a minute.
        /// </summary>
        private const int FailureBackoffSeconds = 60;

        private readonly List<ISensorSource> _sources;
        private readonly Dictionary<ISensorSource, DateTime> _retryAfter = new();

        public SensorRegistry(IEnumerable<ISensorSource> sources) => _sources = sources.ToList();

        /// <summary>
        /// Die temperature from the highest-preference publisher currently answering, or -1
        /// when none is. Recomputed every snapshot, so a tool that closes clears the reading
        /// rather than leaving a stale number that still looks live.
        /// </summary>
        public double CpuDieTemperature { get; private set; } = -1;

        /// <summary>Every reading from this tick, in source order.</summary>
        public IReadOnlyList<SensorReading> Snapshot()
        {
            var all = new List<SensorReading>();
            DateTime now = DateTime.UtcNow;

            foreach (var source in _sources)
            {
                if (_retryAfter.TryGetValue(source, out var until) && now < until) continue;

                try
                {
                    all.AddRange(source.Read());
                }
                catch
                {
                    // A source that throws has a broken assumption, not a transient miss.
                    // Stop asking for a while rather than paying the exception every tick.
                    _retryAfter[source] = now.AddSeconds(FailureBackoffSeconds);
                }
            }

            // Selection is on IsCpuDie, never on category: the zone and the GPUs also report
            // Temperature and neither is the die. First in source order wins, so the order the
            // constructor was given is the preference order.
            var die = all.FirstOrDefault(r => r.IsCpuDie && r.Category == SensorCategory.Temperature);
            CpuDieTemperature = die?.Value ?? -1;

            return all;
        }
    }
}
