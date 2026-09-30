using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The daily history files: append, range read, thinning, retention, failures.</summary>
    public class HistoryStoreTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static HistoryStore NewStore(AiTestEnv env) =>
            new(env.PathOf("history"), () => env.Clock.UtcNow, env.Warn);

        private static HistoryRow Row(DateTime utc, float cpu = 10) =>
            new() { Utc = utc, Seconds = 60, CpuAvg = cpu, CpuMax = cpu };

        [Fact]
        public void Append_writes_a_versioned_header_once_and_one_line_per_row()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);

            store.Append(Row(T0));
            store.Append(Row(T0.AddMinutes(1), 20));

            string[] lines = File.ReadAllLines(Path.Combine(store.Folder, "20260930.csv"));
            Assert.Equal(new[]
            {
                HistoryCsv.VersionLine,
                HistoryCsv.ColumnLine,
                HistoryCsv.Format(Row(T0)),
                HistoryCsv.Format(Row(T0.AddMinutes(1), 20)),
            }, lines);
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Each_row_goes_to_the_file_of_its_utc_day()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            var late = new DateTime(2026, 9, 30, 23, 59, 0, DateTimeKind.Utc);

            store.Append(Row(late.AddMinutes(1)));
            store.Append(Row(late));

            Assert.True(File.Exists(Path.Combine(store.Folder, "20260930.csv")));
            Assert.True(File.Exists(Path.Combine(store.Folder, "20261001.csv")));
            Assert.Equal(new[] { late, late.AddMinutes(1) },
                         store.Read(late.AddHours(-1), late.AddHours(1)).Select(r => r.Utc));
        }

        [Fact]
        public void Read_is_inclusive_and_sorted_even_after_a_clock_jump()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(T0.AddMinutes(5)));
            store.Append(Row(T0.AddMinutes(6)));
            store.Append(Row(T0.AddMinutes(2)));   // the clock jumped back
            store.Append(Row(T0.AddMinutes(3)));

            var rows = store.Read(T0.AddMinutes(2), T0.AddMinutes(5));

            Assert.Equal(new[] { T0.AddMinutes(2), T0.AddMinutes(3), T0.AddMinutes(5) }, rows.Select(r => r.Utc));
        }

        [Fact]
        public void A_half_written_last_line_is_skipped_and_the_next_row_starts_on_a_new_line()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(T0));
            string file = Path.Combine(store.Folder, "20260930.csv");
            File.AppendAllText(file, "2026-09-30T10:01:00Z,60,33.");   // a crash in the middle of a write

            Assert.Equal(new[] { T0 }, store.Read(T0, T0.AddHours(1)).Select(r => r.Utc));

            store.Append(Row(T0.AddMinutes(2)));

            Assert.Equal(new[] { T0, T0.AddMinutes(2) }, store.Read(T0, T0.AddHours(1)).Select(r => r.Utc));
        }

        [Fact]
        public void Damaged_lines_are_skipped()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            Directory.CreateDirectory(store.Folder);
            File.WriteAllText(Path.Combine(store.Folder, "20260930.csv"),
                HistoryCsv.VersionLine + "\n" + HistoryCsv.ColumnLine + "\n" +
                "garbage,line\n" +
                HistoryCsv.Format(Row(T0)) + "\r\n" +
                "2026-09-30T10:01:00Z,sixty" + new string(',', 20) + "\n");

            HistoryRow row = Assert.Single(store.Read(T0, T0.AddHours(1)));

            Assert.Equal(T0, row.Utc);
        }

        [Fact]
        public void Another_reader_can_read_while_the_file_is_open_for_writing()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(T0));
            string file = Path.Combine(store.Folder, "20260930.csv");

            using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bridge = new HistoryStore(store.Folder, () => env.Clock.UtcNow, env.Warn);   // as the --mcp bridge would
                Assert.Single(bridge.Read(T0, T0.AddHours(1)));

                store.Append(Row(T0.AddMinutes(1)));
                Assert.Equal(2, bridge.Read(T0, T0.AddHours(1)).Count);
            }
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Maintain_thins_days_that_ended_over_a_day_ago_to_five_minute_rows()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            var old = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
            var recent = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 10; i++) store.Append(Row(old.AddMinutes(i), i * 10));
            for (int i = 0; i < 3; i++) store.Append(Row(recent.AddMinutes(i), 50));
            env.Clock.UtcNow = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            store.Maintain();
            store.Maintain();   // a second pass finds nothing left to thin

            var thinned = store.Read(old, old.AddHours(1));
            Assert.Equal(2, thinned.Count);
            Assert.Equal(old, thinned[0].Utc);
            Assert.Equal(300, thinned[0].Seconds);
            Assert.Equal(20f, thinned[0].CpuAvg);   // (0 + 10 + 20 + 30 + 40) / 5
            Assert.Equal(40f, thinned[0].CpuMax);
            Assert.Equal(old.AddMinutes(5), thinned[1].Utc);
            Assert.Equal(70f, thinned[1].CpuAvg);
            Assert.Equal(90f, thinned[1].CpuMax);
            Assert.Equal(HistoryCsv.VersionLine, File.ReadLines(Path.Combine(store.Folder, "20260930.csv")).First());

            var kept = store.Read(recent, recent.AddHours(1));   // that day ended only 12 h ago
            Assert.Equal(3, kept.Count);
            Assert.All(kept, r => Assert.Equal(60, r.Seconds));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Maintain_deletes_days_past_the_retention_and_leaves_other_files()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc)));
            store.Append(Row(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc)));
            File.WriteAllText(Path.Combine(store.Folder, "notes.txt"), "mine");
            env.Clock.UtcNow = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

            store.Maintain();

            Assert.False(File.Exists(Path.Combine(store.Folder, "20261002.csv")));   // ended 7.5 days ago
            Assert.True(File.Exists(Path.Combine(store.Folder, "20261003.csv")));    // ended 6.5 days ago
            Assert.True(File.Exists(Path.Combine(store.Folder, "notes.txt")));
            Assert.Single(store.Read(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), env.Clock.UtcNow));
        }

        [Fact]
        public void Size_and_delete_all()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            Assert.Equal(0L, store.SizeBytes());

            store.Append(Row(T0));
            Assert.Equal(new FileInfo(Path.Combine(store.Folder, "20260930.csv")).Length, store.SizeBytes());

            store.DeleteAll();
            Assert.False(Directory.Exists(store.Folder));
            Assert.Equal(0L, store.SizeBytes());
            Assert.Empty(store.Read(T0, T0.AddDays(1)));

            store.Append(Row(T0));   // recording goes on after a delete
            Assert.Single(store.Read(T0, T0.AddDays(1)));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void An_unusable_folder_warns_once_and_never_throws()
        {
            using var env = new AiTestEnv();
            string blocked = env.PathOf("blocked");
            File.WriteAllText(blocked, "a file where the folder should be");
            var store = new HistoryStore(blocked, () => env.Clock.UtcNow, env.Warn);

            store.Append(Row(T0));
            store.Append(Row(T0.AddMinutes(1)));
            Assert.Empty(store.Read(T0, T0.AddDays(1)));
            store.Maintain();
            Assert.Equal(0L, store.SizeBytes());
            store.DeleteAll();

            string warning = Assert.Single(env.Warnings);
            Assert.Contains("could not be written", warning);
            Assert.True(File.Exists(blocked));
        }

        [Fact]
        public void Appends_from_many_threads_all_land()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);

            Parallel.For(0, 120, i => store.Append(Row(T0.AddMinutes(i), i)));

            var rows = store.Read(T0, T0.AddHours(2));
            Assert.Equal(120, rows.Count);
            for (int i = 0; i < rows.Count; i++) Assert.Equal(T0.AddMinutes(i), rows[i].Utc);
        }

        [Fact]
        public void Creating_and_reading_an_empty_store_touches_no_disk()
        {
            using var env = new AiTestEnv();
            string folder = env.PathOf("history");

            var store = new HistoryStore(folder, () => env.Clock.UtcNow, env.Warn);
            Assert.Empty(store.Read(T0, T0.AddDays(7)));
            store.Maintain();

            Assert.Equal(folder, store.Folder);
            Assert.Equal(0L, store.SizeBytes());
            Assert.False(Directory.Exists(folder));
            Assert.Equal(Path.Combine(DiagnosticsLog.DataDir, "history"), HistoryStore.DefaultFolder);
            Assert.Empty(env.Warnings);
        }
    }
}
