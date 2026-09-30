using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiMicaDataTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        private static HistoryStore Store(AiTestEnv env)
        {
            env.Clock.UtcNow = Now;
            return new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow, _ => { });
        }

        private static Dispatcher UiDispatcher()
        {
            Dispatcher? ui = null;
            UiThread.Run(() => ui = Dispatcher.CurrentDispatcher);
            return ui!;
        }

        private static string WriteReport(string folder, string name, DateTime lastWriteUtc, string text = "report")
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        // ----- offline -----------------------------------------------------------------------

        [Fact]
        public void Offline_serves_history_and_only_slowdown_reports_from_disk()
        {
            using var env = new AiTestEnv();
            HistoryStore store = Store(env);
            store.Append(new HistoryRow { Utc = Now.AddMinutes(-1), Seconds = 60, CpuAvg = 12f });
            string reports = env.PathOf("reports");
            Directory.CreateDirectory(reports);
            WriteReport(reports, "slowdown-20260930-110000.txt", Now.AddHours(-1), "newer");
            WriteReport(reports, "slowdown-20260929-090000.txt", Now.AddDays(-1), "older");
            WriteReport(reports, "hardware-report-20260930-100000.txt", Now);
            WriteReport(reports, "slowdown-notes.txt", Now);
            var offline = new OfflineMicaData(store, reports, () => env.Clock.UtcNow);

            Assert.False(offline.IsLive);
            Assert.Single(offline.History(Now.AddHours(-1), Now));
            Assert.Equal(new[] { "slowdown-20260930-110000.txt", "slowdown-20260929-090000.txt" },
                offline.SlowdownReports().Select(r => r.Name));
            Assert.Equal("older", offline.ReadSlowdownReport("slowdown-20260929-090000"));
            Assert.Null(offline.ReadSlowdownReport(@"..\reports\hardware-report-20260930-100000"));
            Assert.Null(offline.ReadSlowdownReport("slowdown-20200101-000000"));
        }

        [Fact]
        public async Task Offline_live_tools_say_MicaStats_is_not_running()
        {
            using var env = new AiTestEnv();
            var offline = new OfflineMicaData(Store(env), env.PathOf("reports"), () => env.Clock.UtcNow);
            var tools = new MicaTools(offline, new Redactor(@"C:\Users\alice", "alice", "DESK-7"));

            var ex = Assert.Throws<DataUnavailableException>(() => offline.Latest());
            var live = await tools.GetLiveStatusAsync();
            var top = await tools.GetTopProcessesAsync();
            var history = await tools.GetHistoryAsync("cpu", "-1h", "now");

            Assert.Equal("MicaStats is not running", ex.Message);
            Assert.Equal("MicaStats is not running", live["error"]!.GetValue<string>());
            Assert.Equal("MicaStats is not running", top["error"]!.GetValue<string>());
            Assert.NotNull(history["points"]);
        }

        // ----- live --------------------------------------------------------------------------

        [Fact]
        public void Live_latest_is_null_before_the_first_sample_and_read_on_the_ui_thread()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var history = new MetricsHistory(capacity: 8);
            var live = new LiveMicaData(history, UiDispatcher(), sampler, Store(env), () => null, () => null, () => env.Clock.UtcNow);

            Assert.Null(live.Latest());

            UiThread.Run(() => history.Append(new SystemMetrics { CpuUsage = 20f }));

            Assert.Equal(20f, live.Latest()!.CpuUsage);
            Assert.True(live.IsLive);
            Assert.Equal(Now, live.UtcNow);
        }

        [Fact]
        public void Live_recent_stats_skip_series_that_have_no_readings()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var history = new MetricsHistory(capacity: 8);
            var live = new LiveMicaData(history, UiDispatcher(), sampler, Store(env), () => null, () => null, () => env.Clock.UtcNow);
            UiThread.Run(() =>
            {
                foreach (float cpu in new[] { 10f, 20f, 30f })
                    history.Append(new SystemMetrics { CpuUsage = cpu, RamPercent = 50f, GpuUsage = -1f, CpuTemperature = -1f, GpuTemperature = -1f });
            });

            IReadOnlyDictionary<string, SeriesStats> stats = live.RecentStats();

            Assert.Equal(new SeriesStats(10f, 20f, 30f, 3), stats["cpu"]);
            Assert.True(stats.ContainsKey("ram"));
            Assert.False(stats.ContainsKey("gpu"));
            Assert.False(stats.ContainsKey("temp"));
        }

        [Fact]
        public async Task Live_top_processes_rank_by_memory_and_release_the_sampler()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var live = new LiveMicaData(new MetricsHistory(capacity: 8), UiDispatcher(), sampler, Store(env),
                () => null, () => null, () => env.Clock.UtcNow);

            IReadOnlyList<ProcessInfo> top = await live.TopProcessesAsync("memory", 3, CancellationToken.None);

            Assert.Equal(3, top.Count);
            Assert.All(top, p => Assert.True(p.Pid > 0 && p.CreateTime > 0));
            Assert.True(top[0].WorkingSetMb >= top[1].WorkingSetMb && top[1].WorkingSetMb >= top[2].WorkingSetMb);
            Assert.False(sampler.Enabled);
        }

        [Fact]
        public async Task Live_top_processes_release_the_sampler_when_cancelled()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var live = new LiveMicaData(new MetricsHistory(capacity: 8), UiDispatcher(), sampler, Store(env),
                () => null, () => null, () => env.Clock.UtcNow);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.TopProcessesAsync("cpu", 5, cts.Token));

            Assert.False(sampler.Enabled);
        }

        [Fact]
        public void Live_alerts_and_history_come_from_the_app_services()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var history = new MetricsHistory(capacity: 8);
            HistoryStore store = Store(env);
            store.Append(new HistoryRow { Utc = Now.AddMinutes(-2), Seconds = 60, RamAvg = 40f });
            AlertMonitor? monitor = null;
            var live = new LiveMicaData(history, UiDispatcher(), sampler, store, () => monitor, () => null, () => env.Clock.UtcNow);

            Assert.Empty(live.AlertRules());
            Assert.Empty(live.RecentAlerts());

            monitor = new AlertMonitor(history, null);

            Assert.Equal(AlertRule.Defaults.Count, live.AlertRules().Count);
            Assert.Empty(live.RecentAlerts());
            Assert.Equal(40f, live.History(Now.AddHours(-1), Now).Single().RamAvg);
        }
    }
}
