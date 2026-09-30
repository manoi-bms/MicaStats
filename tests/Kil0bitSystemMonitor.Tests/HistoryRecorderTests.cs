using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Telemetry ticks to history rows: on/off, top processes, maintenance, failures.</summary>
    public class HistoryRecorderTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static readonly TopProcessSample Chrome =
            new("chrome.exe", @"%USERPROFILE%\AppData\Local\chrome.exe", 42.5f, "code.exe", 812.3f);

        /// <summary>A top-process source that answers however the test says, and counts the calls.</summary>
        private sealed class FakeTop : ITopProcessSource
        {
            private readonly Func<CancellationToken, Task<TopProcessSample?>> _answer;
            private int _calls;

            public FakeTop(Func<CancellationToken, Task<TopProcessSample?>> answer) => _answer = answer;

            public int Calls => Volatile.Read(ref _calls);

            public Task<TopProcessSample?> SampleAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref _calls);
                return _answer(ct);
            }
        }

        private static (HistoryStore Store, HistoryRecorder Recorder, FakeTop Top) Build(
            AiTestEnv env, Func<CancellationToken, Task<TopProcessSample?>>? top = null)
        {
            var store = new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow, env.Warn);
            var fake = new FakeTop(top ?? (_ => Task.FromResult<TopProcessSample?>(Chrome)));
            return (store, new HistoryRecorder(store, fake, () => env.Clock.UtcNow, env.Warn), fake);
        }

        /// <summary>One telemetry tick, <paramref name="seconds"/> after 10:00 UTC.</summary>
        private static void Tick(AiTestEnv env, HistoryRecorder recorder, double seconds, float cpu = 10)
        {
            env.Clock.UtcNow = T0.AddSeconds(seconds);
            recorder.OnMetrics(new SystemMetrics { CpuUsage = cpu, RamPercent = 50 });
        }

        private static IReadOnlyList<HistoryRow> Rows(HistoryStore store) => store.Read(T0, T0.AddHours(1));

        [Fact]
        public void A_finished_minute_is_written_when_the_next_one_starts()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);
            recorder.Start();

            Tick(env, recorder, 5, cpu: 10);
            Tick(env, recorder, 30, cpu: 30);
            Assert.Empty(Rows(store));

            Tick(env, recorder, 65, cpu: 99);

            HistoryRow row = Assert.Single(Rows(store));
            Assert.Equal(T0, row.Utc);
            Assert.Equal(20f, row.CpuAvg);
            Assert.Equal(30f, row.CpuMax);
            recorder.Dispose();
        }

        [Fact]
        public void Nothing_is_recorded_while_stopped_and_stop_writes_the_minute_in_progress()
        {
            using var env = new AiTestEnv();
            var (store, recorder, top) = Build(env);

            Tick(env, recorder, 5);
            Tick(env, recorder, 65);
            Assert.False(recorder.IsRunning);
            Assert.Empty(Rows(store));

            recorder.Start();
            Assert.True(recorder.IsRunning);
            Tick(env, recorder, 125, cpu: 40);
            recorder.Stop();
            Assert.False(recorder.IsRunning);

            Tick(env, recorder, 185);
            Tick(env, recorder, 245);

            HistoryRow row = Assert.Single(Rows(store));
            Assert.Equal(T0.AddMinutes(2), row.Utc);
            Assert.Equal(40f, row.CpuAvg);
            Assert.Equal(1, top.Calls);
        }

        [Fact]
        public void Each_minute_asks_for_the_top_processes_once_and_keeps_them_with_that_minute()
        {
            using var env = new AiTestEnv();
            var (store, recorder, top) = Build(env);
            recorder.Start();

            Tick(env, recorder, 5);
            Tick(env, recorder, 35);
            Tick(env, recorder, 65);

            HistoryRow row = Assert.Single(Rows(store));
            Assert.Equal("chrome.exe", row.TopCpuName);
            Assert.Equal(@"%USERPROFILE%\AppData\Local\chrome.exe", row.TopCpuPath);
            Assert.Equal(42.5f, row.TopCpuPercent);
            Assert.Equal("code.exe", row.TopRamName);
            Assert.Equal(812.3f, row.TopRamMb);
            Assert.Equal(2, top.Calls);   // 10:00 and 10:01
            recorder.Dispose();
        }

        [Fact]
        public void A_top_sample_that_arrives_after_its_minute_is_dropped()
        {
            using var env = new AiTestEnv();
            var pending = new TaskCompletionSource<TopProcessSample?>();
            var (store, recorder, top) = Build(env, _ => pending.Task);
            recorder.Start();

            Tick(env, recorder, 5);    // asks for 10:00
            Tick(env, recorder, 65);   // 10:00 is written without it; no second request while one is out
            pending.SetResult(Chrome);
            recorder.Stop();

            var rows = Rows(store);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Null(r.TopCpuName));
            Assert.Equal(1, top.Calls);
        }

        [Fact]
        public async Task Start_runs_the_maintenance()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);
            store.Append(new HistoryRow { Utc = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), Seconds = 60, CpuAvg = 5 });
            string file = Path.Combine(store.Folder, "20260920.csv");
            Assert.True(File.Exists(file));

            recorder.Start();
            await recorder.Maintenance.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(file));   // that day ended over 9 days ago
            recorder.Dispose();
        }

        [Fact]
        public async Task A_new_utc_day_runs_the_maintenance_again()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);
            store.Append(new HistoryRow { Utc = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc), Seconds = 60, CpuAvg = 5 });
            string file = Path.Combine(store.Folder, "20260924.csv");
            env.Clock.UtcNow = new DateTime(2026, 10, 1, 23, 59, 30, DateTimeKind.Utc);

            recorder.Start();
            await recorder.Maintenance.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(file));    // its day ended 6 days 23:59:30 ago

            env.Clock.UtcNow = new DateTime(2026, 10, 2, 0, 0, 10, DateTimeKind.Utc);
            recorder.OnMetrics(new SystemMetrics { CpuUsage = 5 });
            await recorder.Maintenance.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(file));   // now over 7 days
            recorder.Dispose();
        }

        [Fact]
        public void Start_and_stop_are_idempotent_and_dispose_is_final()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);

            recorder.Start();
            recorder.Start();
            Assert.True(recorder.IsRunning);
            Tick(env, recorder, 5);
            recorder.Stop();
            recorder.Stop();
            Assert.Single(Rows(store));   // the second Stop wrote nothing more

            recorder.Dispose();
            recorder.Start();
            Assert.False(recorder.IsRunning);
        }

        [Fact]
        public void A_failing_top_source_warns_once_and_rows_are_still_written()
        {
            using var env = new AiTestEnv();
            var (store, recorder, top) = Build(env, _ => throw new InvalidOperationException("sampler gone"));
            recorder.Start();

            Tick(env, recorder, 5);
            Tick(env, recorder, 65);
            Tick(env, recorder, 125);
            recorder.Stop();

            var rows = Rows(store);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Null(r.TopCpuName));
            Assert.Equal(3, top.Calls);
            string warning = Assert.Single(env.Warnings);
            Assert.Contains("sampler gone", warning, StringComparison.Ordinal);
        }
    }
}
