using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A hand-written <see cref="IMicaData"/>: every member returns what the test put in and
    /// records how it was asked. Setting <see cref="Failure"/> makes every member throw it.
    /// </summary>
    internal sealed class FakeMicaData : IMicaData
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        public bool IsLive { get; set; } = true;

        public SystemMetrics? Metrics { get; set; } = Sample();

        public Dictionary<string, SeriesStats> Stats { get; } = new();

        public List<ProcessInfo> Processes { get; } = new();

        public List<HistoryRow> Rows { get; } = new();

        public List<SavedReport> Reports { get; } = new();

        public Dictionary<string, string> ReportTexts { get; } = new();

        public List<AlertRule> Rules { get; } = new();

        public List<AlertEvent> Alerts { get; } = new();

        public string? HardwareText { get; set; }

        public BatteryReading? BatteryReading { get; set; }

        public BatteryHealth? Health { get; set; }

        public BootAnalysis? Boot { get; set; }

        public Exception? Failure { get; set; }

        public int LatestCalls { get; private set; }

        public (string By, int Count)? LastTopRequest { get; private set; }

        public (DateTime From, DateTime To)? LastHistoryRange { get; private set; }

        public List<string> ReportReads { get; } = new();

        public SystemMetrics? Latest()
        {
            Check();
            LatestCalls++;
            return Metrics;
        }

        public IReadOnlyDictionary<string, SeriesStats> RecentStats()
        {
            Check();
            return Stats;
        }

        public Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct)
        {
            Check();
            LastTopRequest = (by, count);
            return Task.FromResult<IReadOnlyList<ProcessInfo>>(Processes);
        }

        public IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc)
        {
            Check();
            LastHistoryRange = (fromUtc, toUtc);
            return Rows;
        }

        public IReadOnlyList<SavedReport> SlowdownReports()
        {
            Check();
            return Reports;
        }

        public string? ReadSlowdownReport(string id)
        {
            Check();
            ReportReads.Add(id);
            return ReportTexts.TryGetValue(id, out string? text) ? text : null;
        }

        public IReadOnlyList<AlertRule> AlertRules()
        {
            Check();
            return Rules;
        }

        public IReadOnlyList<AlertEvent> RecentAlerts()
        {
            Check();
            return Alerts;
        }

        public Task<string?> HardwareReportAsync(CancellationToken ct)
        {
            Check();
            return Task.FromResult(HardwareText);
        }

        public BatteryReading? Battery()
        {
            Check();
            return BatteryReading;
        }

        public Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct)
        {
            Check();
            return Task.FromResult(Health);
        }

        public Task<BootAnalysis?> BootAsync(CancellationToken ct)
        {
            Check();
            return Task.FromResult(Boot);
        }

        private void Check()
        {
            if (Failure != null) throw Failure;
        }

        /// <summary>
        /// A desktop with no temperature source, no GPU counter, a drive that is not ready and
        /// no battery, so every "unavailable" path has something to report.
        /// </summary>
        public static SystemMetrics Sample() => new()
        {
            CpuUsage = 12.34f,
            CpuSystem = 3.21f,
            CoreUsage = new[] { 10f, 20f, 30f, 40f },
            CpuFrequencyGhz = 3.456f,
            CpuTemperature = -1f,
            RamPercent = 61.26f,
            RamUsedBytes = 10UL * 1024 * 1024 * 1024,
            RamTotalBytes = 16UL * 1024 * 1024 * 1024,
            CommitPercent = 70f,
            CachedBytes = 2UL * 1024 * 1024 * 1024,
            GpuUsage = -1f,
            GpuTemperature = -1f,
            Disks = new List<DiskMetric>
            {
                new() { Name = "0 C:", FreeBytes = 100UL * 1024 * 1024 * 1024, TotalBytes = 400UL * 1024 * 1024 * 1024, ActivityPercent = 5f },
                new() { Name = "1 D:" },
            },
            NetUpKbps = 12.5f,
            NetDownKbps = 250f,
            NetAdapterName = "Wi-Fi",
            NetIpAddress = "192.168.1.20",
            BatteryPercent = -1,
        };
    }
}
