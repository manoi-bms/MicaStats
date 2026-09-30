using System;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The busiest processes at one moment of a minute: by CPU, with its exe path (already
    /// redacted), and by memory. A null field means that ranking had nothing to offer.
    /// </summary>
    public sealed record TopProcessSample(string? CpuName, string? CpuPath, float? CpuPercent, string? RamName, float? RamMb);

    /// <summary>
    /// One line of the history: a minute of readings, or five minutes once the day has been
    /// thinned. Every reading is nullable because an unreadable sensor must stay distinguishable
    /// from a zero: a missing temperature is not a cold CPU.
    /// </summary>
    public sealed record HistoryRow
    {
        /// <summary>Start of the minute (or 5-minute bucket), always <see cref="DateTimeKind.Utc"/>.</summary>
        public DateTime Utc { get; init; }

        /// <summary>Seconds the row covers: 60, or 300 once its day has been thinned.</summary>
        public int Seconds { get; init; }

        /// <summary>Average CPU use, %.</summary>
        public float? CpuAvg { get; init; }

        /// <summary>Highest one-second CPU use, %.</summary>
        public float? CpuMax { get; init; }

        /// <summary>Average CPU temperature, degrees C, over the seconds that had a reading.</summary>
        public float? CpuTempAvg { get; init; }

        /// <summary>Highest CPU temperature, degrees C.</summary>
        public float? CpuTempMax { get; init; }

        /// <summary>Average memory in use, %.</summary>
        public float? RamAvg { get; init; }

        /// <summary>Highest memory in use, %.</summary>
        public float? RamMax { get; init; }

        /// <summary>Average GPU use, %.</summary>
        public float? GpuAvg { get; init; }

        /// <summary>Highest GPU use, %.</summary>
        public float? GpuMax { get; init; }

        /// <summary>Highest GPU temperature, degrees C.</summary>
        public float? GpuTempMax { get; init; }

        /// <summary>Average upload rate, kbps as telemetry reports it.</summary>
        public float? NetUpAvg { get; init; }

        /// <summary>Average download rate, kbps as telemetry reports it.</summary>
        public float? NetDownAvg { get; init; }

        /// <summary>Lowest free space on any ready drive, % of that drive.</summary>
        public float? DiskFreeMinPercent { get; init; }

        /// <summary>Highest disk activity of the busiest drive, %.</summary>
        public float? DiskActivityMax { get; init; }

        /// <summary>Battery charge at the end of the row, %; null on a desktop.</summary>
        public float? BatteryPercent { get; init; }

        /// <summary>Whether the PC was on mains power at the end of the row; null on a desktop.</summary>
        public bool? OnAc { get; init; }

        /// <summary>The process using the most CPU.</summary>
        public string? TopCpuName { get; init; }

        /// <summary>Its exe path, redacted.</summary>
        public string? TopCpuPath { get; init; }

        /// <summary>Its CPU share, % of the whole machine.</summary>
        public float? TopCpuPercent { get; init; }

        /// <summary>The process with the largest working set.</summary>
        public string? TopRamName { get; init; }

        /// <summary>Its working set, MB.</summary>
        public float? TopRamMb { get; init; }
    }
}
