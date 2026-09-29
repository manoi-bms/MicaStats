using System;
using System.Globalization;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>When a version is kept, and for how long.</summary>
    public class PadHistoryPolicyTests
    {
        private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0);

        [Fact]
        public void A_pause_is_due_after_three_idle_seconds_and_a_minute_since_the_last_snapshot()
        {
            Assert.False(HistoryPolicy.IsDueOnPause(Now, Now.AddSeconds(-2), Now.AddMinutes(-5)));
            Assert.False(HistoryPolicy.IsDueOnPause(Now, Now.AddSeconds(-3), Now.AddSeconds(-59)));
            Assert.True(HistoryPolicy.IsDueOnPause(Now, Now.AddSeconds(-3), Now.AddSeconds(-60)));
        }

        [Fact]
        public void Equal_text_has_an_equal_hash_and_different_text_does_not()
        {
            Assert.Equal(HistoryPolicy.Hash("abc"), HistoryPolicy.Hash("abc"));
            Assert.NotEqual(HistoryPolicy.Hash("abc"), HistoryPolicy.Hash("abd"));
            Assert.Equal(64, HistoryPolicy.Hash("").Length);
        }

        [Fact]
        public void Stamps_use_the_gregorian_year_under_a_thai_culture()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                var when = new DateTime(2026, 9, 29, 14, 30, 12, 417);

                string stamp = HistoryPolicy.FormatStamp(when);

                Assert.Equal("20260929-143012-417", stamp);
                Assert.True(HistoryPolicy.TryParseStamp(stamp, out var back));
                Assert.Equal(when, back);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void A_name_that_is_not_a_stamp_does_not_parse()
        {
            Assert.False(HistoryPolicy.TryParseStamp("notes-backup", out _));
        }

        [Fact]
        public void Everything_under_a_day_old_is_kept()
        {
            var stamps = new[] { Now.AddMinutes(-1), Now.AddHours(-2), Now.AddHours(-23) };

            Assert.Empty(HistoryPolicy.SelectToPrune(stamps, Now, 90));
        }

        [Fact]
        public void Between_one_and_seven_days_the_newest_per_hour_is_kept()
        {
            DateTime day = Now.AddDays(-2).Date;
            var early = day.AddHours(10).AddMinutes(5);
            var late = day.AddHours(10).AddMinutes(40);
            var nextHour = day.AddHours(11).AddMinutes(10);

            var pruned = HistoryPolicy.SelectToPrune(new[] { early, late, nextHour, Now }, Now, 90);

            Assert.Equal(new[] { early }, pruned);
        }

        [Fact]
        public void Between_seven_days_and_the_limit_the_newest_per_day_is_kept()
        {
            DateTime day = Now.AddDays(-10).Date;
            var morning = day.AddHours(9);
            var evening = day.AddHours(18);

            var pruned = HistoryPolicy.SelectToPrune(new[] { morning, evening, Now }, Now, 90);

            Assert.Equal(new[] { morning }, pruned);
        }

        [Fact]
        public void Older_than_the_limit_is_pruned_but_the_newest_snapshot_always_survives()
        {
            var old = Now.AddDays(-100);
            var older = Now.AddDays(-120);

            Assert.Equal(new[] { older }, HistoryPolicy.SelectToPrune(new[] { old, older }, Now, 90));
            Assert.Equal(new[] { old }, HistoryPolicy.SelectToPrune(new[] { Now.AddHours(-1), old }, Now, 90));
        }

        [Fact]
        public void The_limit_is_never_below_seven_days()
        {
            var threeDays = Now.AddDays(-3);
            var eightDays = Now.AddDays(-8);

            var pruned = HistoryPolicy.SelectToPrune(new[] { Now, threeDays, eightDays }, Now, historyDays: 1);

            Assert.Equal(new[] { eightDays }, pruned);
        }

        [Fact]
        public void Nothing_to_prune_in_an_empty_history()
        {
            Assert.Empty(HistoryPolicy.SelectToPrune(Array.Empty<DateTime>(), Now, 90));
        }
    }
}
