using System;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// Folds one-second telemetry snapshots into one <see cref="HistoryRow"/> per UTC minute.
    ///
    /// <para>
    /// Pure and clock-free (the caller passes each snapshot's time), so a test can feed a minute in
    /// microseconds. A minute ends when a snapshot arrives from any other minute, forward or
    /// backward: a clock that jumps back only produces an out-of-order row, which the store sorts
    /// on read. Not thread-safe; <see cref="HistoryRecorder"/> holds a lock around it.
    /// </para>
    /// </summary>
    public sealed class MinuteAggregator
    {
        /// <summary>Running average and peak of one reading; empty when never seen.</summary>
        private struct Stat
        {
            private double _sum;
            private float _max;

            public int Count { get; private set; }

            public void Add(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) return;
                if (Count == 0 || value > _max) _max = value;
                _sum += value;
                Count++;
            }

            public float? Average => Count == 0 ? null : (float)(_sum / Count);

            public float? Peak => Count == 0 ? null : _max;
        }

        private DateTime _minute;
        private int _samples;
        private Stat _cpu, _cpuTemp, _ram, _gpu, _gpuTemp, _netUp, _netDown, _diskActivity;
        private float? _diskFreeMin;
        private float? _battery;
        private bool? _onAc;
        private TopProcessSample? _top;

        /// <summary>
        /// Adds one snapshot taken at <paramref name="utc"/>. Returns the finished row when this
        /// snapshot belongs to a different minute than the one in progress, else null.
        /// </summary>
        public HistoryRow? Add(SystemMetrics m, DateTime utc)
        {
            ArgumentNullException.ThrowIfNull(m);

            DateTime minute = MinuteOf(utc);
            HistoryRow? finished = null;
            if (_samples > 0 && minute != _minute) finished = Finish();
            if (_samples == 0) _minute = minute;
            _samples++;

            _cpu.Add(m.CpuUsage);
            if (m.CpuTemperature > 0) _cpuTemp.Add(m.CpuTemperature);
            _ram.Add(m.RamPercent);
            if (m.GpuUsage >= 0) _gpu.Add(m.GpuUsage);
            if (m.GpuTemperature > 0) _gpuTemp.Add(m.GpuTemperature);
            _netUp.Add(m.NetUpKbps);
            _netDown.Add(m.NetDownKbps);

            // Telemetry reports DiskUsage = 0 when it has no disk counters at all, so activity only
            // counts when the snapshot lists a disk.
            if (m.Disks != null && m.Disks.Count > 0)
            {
                _diskActivity.Add(m.DiskUsage);
                foreach (var disk in m.Disks)
                {
                    // A drive that was not ready reports zero total; it has not run out of space.
                    if (disk == null || disk.TotalBytes == 0) continue;
                    float free = (float)(disk.FreeBytes * 100d / disk.TotalBytes);
                    if (_diskFreeMin == null || free < _diskFreeMin) _diskFreeMin = free;
                }
            }

            if (m.HasBattery)
            {
                _battery = m.BatteryPercent;
                _onAc = m.BatteryOnAc;
            }

            return finished;
        }

        /// <summary>Records the top processes for the minute in progress (or the next one, if none is).</summary>
        public void SetTop(TopProcessSample sample) => _top = sample;

        /// <summary>Finishes the minute in progress; null when it holds no snapshot.</summary>
        public HistoryRow? Flush() => _samples == 0 ? null : Finish();

        /// <summary>The same instant as UTC: Local is converted, Unspecified is taken to be UTC already.</summary>
        internal static DateTime ToUtc(DateTime time) => time.Kind switch
        {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time,
        };

        /// <summary>The start of the UTC minute holding <paramref name="time"/>.</summary>
        internal static DateTime MinuteOf(DateTime time)
        {
            DateTime utc = ToUtc(time);
            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        }

        private HistoryRow Finish()
        {
            var row = new HistoryRow
            {
                Utc = _minute,
                Seconds = 60,
                CpuAvg = _cpu.Average,
                CpuMax = _cpu.Peak,
                CpuTempAvg = _cpuTemp.Average,
                CpuTempMax = _cpuTemp.Peak,
                RamAvg = _ram.Average,
                RamMax = _ram.Peak,
                GpuAvg = _gpu.Average,
                GpuMax = _gpu.Peak,
                GpuTempMax = _gpuTemp.Peak,
                NetUpAvg = _netUp.Average,
                NetDownAvg = _netDown.Average,
                DiskFreeMinPercent = _diskFreeMin,
                DiskActivityMax = _diskActivity.Peak,
                BatteryPercent = _battery,
                OnAc = _onAc,
                TopCpuName = _top?.CpuName,
                TopCpuPath = _top?.CpuPath,
                TopCpuPercent = _top?.CpuPercent,
                TopRamName = _top?.RamName,
                TopRamMb = _top?.RamMb,
            };

            _samples = 0;
            _cpu = _cpuTemp = _ram = _gpu = _gpuTemp = _netUp = _netDown = _diskActivity = default;
            _diskFreeMin = null;
            _battery = null;
            _onAc = null;
            _top = null;
            return row;
        }
    }
}
