using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>Lowest, mean and highest value of one metric over the in-memory window (about two minutes).</summary>
    public sealed record SeriesStats(float Min, float Avg, float Max, int Count);

    /// <summary>
    /// One process as the tools report it. <see cref="CreateTime"/> is the kernel FILETIME that,
    /// with <see cref="Pid"/>, names this process rather than a pid slot Windows may reuse: a
    /// suggested End action carries both, so MicaStats can check the target again before acting.
    /// </summary>
    public sealed record ProcessInfo(string Name, string? Path, int Pid, long CreateTime,
                                     float CpuPercent, double WorkingSetMb, double DiskKBps);

    /// <summary>
    /// A data source cannot supply a reading at all, for example live data in the MCP bridge
    /// while MicaStats is not running. The tools hand its message to the model unchanged, so it
    /// must read as a plain sentence.
    /// </summary>
    public sealed class DataUnavailableException : Exception
    {
        /// <summary>Creates the exception with the sentence the model will see.</summary>
        public DataUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Everything the data tools read, behind one narrow seam: <c>LiveMicaData</c> in the running
    /// app, <c>OfflineMicaData</c> in the MCP bridge when MicaStats is not running, and a
    /// hand-written fake in tests. Members may throw; <see cref="MicaTools"/> turns every
    /// exception into an <c>{"error": ...}</c> result.
    /// </summary>
    public interface IMicaData
    {
        /// <summary>The current time, UTC. Relative times such as <c>-6h</c> are resolved against it.</summary>
        DateTime UtcNow { get; }

        /// <summary>True in the running app; false in the offline bridge, which has no live readings.</summary>
        bool IsLive { get; }

        /// <summary>The latest telemetry snapshot, or null before the first one has arrived.</summary>
        SystemMetrics? Latest();

        /// <summary>
        /// Min, average and max over the in-memory window, keyed <c>cpu</c>, <c>ram</c>,
        /// <c>gpu</c>, <c>temp</c>, <c>netUp</c> and <c>netDown</c>. A key is missing when that
        /// metric has no data, so a missing sensor never shows up as a row of zeros.
        /// </summary>
        IReadOnlyDictionary<string, SeriesStats> RecentStats();

        /// <summary>The busiest <paramref name="count"/> processes by <c>cpu</c>, <c>memory</c> or <c>disk</c>, busiest first.</summary>
        Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct);

        /// <summary>Recorded history rows between the two instants (inclusive), oldest first.</summary>
        IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc);

        /// <summary>Saved slowdown reports (<c>slowdown-*.txt</c> only), newest first.</summary>
        IReadOnlyList<SavedReport> SlowdownReports();

        /// <summary>The text of one slowdown report, or null when <paramref name="id"/> (the file name without extension) is unknown.</summary>
        string? ReadSlowdownReport(string id);

        /// <summary>The alert rules in force.</summary>
        IReadOnlyList<AlertRule> AlertRules();

        /// <summary>Alerts raised since MicaStats started, newest first.</summary>
        IReadOnlyList<AlertEvent> RecentAlerts();

        /// <summary>The hardware report text, or null when it could not be gathered.</summary>
        Task<string?> HardwareReportAsync(CancellationToken ct);

        /// <summary>A live battery sample, or null when the PC has no battery.</summary>
        BatteryReading? Battery();

        /// <summary>Battery wear figures, or null when there is no battery or Windows reports none.</summary>
        Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct);

        /// <summary>Boot history from the Windows event log, or null when it could not be read.</summary>
        Task<BootAnalysis?> BootAsync(CancellationToken ct);
    }
}
