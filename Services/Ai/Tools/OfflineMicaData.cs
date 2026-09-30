using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The data source of the MCP bridge when no MicaStats instance answers the tool pipe. It
    /// serves what is on disk (history files and slowdown reports); every live reading throws
    /// <see cref="DataUnavailableException"/> with <see cref="NotRunningMessage"/>, which the
    /// tools pass to the MCP client as <c>{"error": "MicaStats is not running"}</c>.
    /// </summary>
    public sealed class OfflineMicaData : IMicaData
    {
        /// <summary>The error every live tool returns in the offline bridge.</summary>
        internal const string NotRunningMessage = "MicaStats is not running";

        private readonly HistoryStore _store;
        private readonly string _reportDir;
        private readonly Func<DateTime> _clock;

        /// <summary>Reads history from <paramref name="store"/> and slowdown reports from <paramref name="reportDir"/>.</summary>
        public OfflineMicaData(HistoryStore store, string reportDir, Func<DateTime> utcClock)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _reportDir = reportDir ?? throw new ArgumentNullException(nameof(reportDir));
            _clock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
        }

        /// <inheritdoc/>
        public DateTime UtcNow => _clock();

        /// <inheritdoc/>
        public bool IsLive => false;

        /// <inheritdoc/>
        public IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc) => _store.Read(fromUtc, toUtc);

        /// <inheritdoc/>
        public IReadOnlyList<SavedReport> SlowdownReports() => SlowdownReportFiles.List(_reportDir);

        /// <inheritdoc/>
        public string? ReadSlowdownReport(string id) => SlowdownReportFiles.Read(_reportDir, id);

        /// <inheritdoc/>
        public SystemMetrics? Latest() => throw NotRunning();

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, SeriesStats> RecentStats() => throw NotRunning();

        /// <inheritdoc/>
        public Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct) => throw NotRunning();

        /// <inheritdoc/>
        public IReadOnlyList<AlertRule> AlertRules() => throw NotRunning();

        /// <inheritdoc/>
        public IReadOnlyList<AlertEvent> RecentAlerts() => throw NotRunning();

        /// <inheritdoc/>
        public Task<string?> HardwareReportAsync(CancellationToken ct) => throw NotRunning();

        /// <inheritdoc/>
        public BatteryReading? Battery() => throw NotRunning();

        /// <inheritdoc/>
        public Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct) => throw NotRunning();

        /// <inheritdoc/>
        public Task<BootAnalysis?> BootAsync(CancellationToken ct) => throw NotRunning();

        private static DataUnavailableException NotRunning() => new(NotRunningMessage);
    }
}
