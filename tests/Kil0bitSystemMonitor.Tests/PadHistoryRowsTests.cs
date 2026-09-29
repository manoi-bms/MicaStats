using System;
using System.Globalization;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>What the history list and the closed-notes list say.</summary>
    public class PadHistoryRowsTests
    {
        private static readonly DateTime Now = new(2026, 9, 29, 15, 0, 0);

        [Fact]
        public void Versions_are_grouped_by_day_and_sized_against_the_one_before()
        {
            var snapshots = new[]
            {
                new SnapshotInfo("c", new DateTime(2026, 9, 29, 14, 30, 0), 3072),
                new SnapshotInfo("b", new DateTime(2026, 9, 28, 9, 5, 0), 2048),
                new SnapshotInfo("a", new DateTime(2026, 9, 12, 8, 0, 0), 100),
            };

            var rows = HistoryRows.Build(snapshots, Now);

            Assert.Equal(new[] { "Today", "Yesterday", "12 Sep 2026" }, rows.Select(r => r.Group));
            Assert.Equal("14:30", rows[0].Time);
            Assert.Equal("3.0 KB", rows[0].Size);
            Assert.Equal("+1.0 KB", rows[0].Delta);
            Assert.Equal("", rows[2].Delta);
        }

        [Fact]
        public void Dates_are_gregorian_under_a_thai_culture()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                Assert.Equal("12 Sep 2026", HistoryRows.GroupOf(new DateTime(2026, 9, 12, 8, 0, 0), Now));
                Assert.Equal("Yesterday 09:05", HistoryRows.When(new DateTime(2026, 9, 28, 9, 5, 0), Now));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Closed_rows_say_when_and_where_and_can_be_searched()
        {
            var closedLocal = new DateTime(2026, 9, 29, 14, 30, 0, DateTimeKind.Local);
            var meta = new NoteMeta
            {
                Id = "x",
                Title = "config.ini",
                SourcePath = @"D:\work\config.ini",
                ClosedAtUtc = closedLocal.ToUniversalTime(),
            };

            var row = Assert.Single(HistoryRows.Closed(new[] { meta }, Now));

            Assert.Equal("x", row.Id);
            Assert.Equal("config.ini", row.Title);
            Assert.Equal(@"Closed Today 14:30 · D:\work\config.ini", row.Detail);
            Assert.True(row.Matches("WORK"));
            Assert.True(row.Matches(""));
            Assert.False(row.Matches("zzz"));
        }
    }
}
