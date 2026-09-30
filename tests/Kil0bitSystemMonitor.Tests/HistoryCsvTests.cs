using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The history line format: round trips, empty means unavailable, no culture leaks in.</summary>
    public class HistoryCsvTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static HistoryRow Full() => new()
        {
            Utc = T0,
            Seconds = 60,
            CpuAvg = 12.5f,
            CpuMax = 88.1f,
            CpuTempAvg = 54.2f,
            CpuTempMax = 71f,
            RamAvg = 63.4f,
            RamMax = 70f,
            GpuAvg = 3.3f,
            GpuMax = 20.8f,
            GpuTempMax = 48f,
            NetUpAvg = 12.7f,
            NetDownAvg = 1450.2f,
            DiskFreeMinPercent = 18.6f,
            DiskActivityMax = 97.3f,
            BatteryPercent = 81f,
            OnAc = true,
            TopCpuName = "chrome.exe",
            TopCpuPath = @"%USERPROFILE%\AppData\Local\Google\Chrome\chrome.exe",
            TopCpuPercent = 23.4f,
            TopRamName = "Code.exe",
            TopRamMb = 812.3f,
        };

        [Fact]
        public void A_full_row_round_trips()
        {
            string line = HistoryCsv.Format(Full());

            Assert.Equal("2026-09-30T10:00:00Z,60,12.5,88.1,54.2,71.0,63.4,70.0,3.3,20.8,48.0,12.7,1450.2,18.6,97.3,81.0,1," +
                         "chrome.exe,%USERPROFILE%\\AppData\\Local\\Google\\Chrome\\chrome.exe,23.4,Code.exe,812.3", line);
            Assert.True(HistoryCsv.TryParse(line, out HistoryRow? back));
            Assert.Equal(Full(), back);
            Assert.Equal(DateTimeKind.Utc, back!.Utc.Kind);
        }

        [Fact]
        public void An_unavailable_reading_is_an_empty_field_never_zero()
        {
            var row = new HistoryRow { Utc = T0, Seconds = 60 };

            string line = HistoryCsv.Format(row);

            Assert.Equal("2026-09-30T10:00:00Z,60" + new string(',', 20), line);
            Assert.True(HistoryCsv.TryParse(line, out HistoryRow? back));
            Assert.Equal(row, back);
        }

        [Theory]
        [InlineData("th-TH")]   // Buddhist-era calendar: 2026 would print as 2569
        [InlineData("de-DE")]   // comma decimal separator
        public void The_current_culture_changes_nothing(string culture)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);

                string line = HistoryCsv.Format(Full());

                Assert.StartsWith("2026-09-30T10:00:00Z,60,12.5,88.1,", line);
                Assert.True(HistoryCsv.TryParse(line, out HistoryRow? back));
                Assert.Equal(2026, back!.Utc.Year);
                Assert.Equal(Full(), back);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Names_with_commas_and_quotes_survive()
        {
            var row = new HistoryRow
            {
                Utc = T0,
                Seconds = 60,
                TopCpuName = "odd, \"name\".exe",
                TopCpuPath = @"C:\Program Files\A, B\odd.exe",
                TopRamName = "\"q\"",
            };

            Assert.True(HistoryCsv.TryParse(HistoryCsv.Format(row), out HistoryRow? back));
            Assert.Equal(row, back);
        }

        [Fact]
        public void Headers_and_damaged_lines_are_rejected()
        {
            string good = HistoryCsv.Format(Full());
            Assert.True(HistoryCsv.TryParse(good, out _));

            Assert.False(HistoryCsv.TryParse(HistoryCsv.VersionLine, out _));
            Assert.False(HistoryCsv.TryParse(HistoryCsv.ColumnLine, out _));
            Assert.False(HistoryCsv.TryParse("", out _));
            Assert.False(HistoryCsv.TryParse(good.Substring(0, 40), out _));                    // cut short
            Assert.False(HistoryCsv.TryParse(good.Replace(",12.5,", ",twelve,"), out _));       // not a number
            Assert.False(HistoryCsv.TryParse(good.Replace(",81.0,1,", ",81.0,yes,"), out _));   // not a flag
            Assert.False(HistoryCsv.TryParse("2026-09-30T10:00:00Z,60" + new string(',', 17) + "\"cut, mid", out _));   // open quote
        }

        [Fact]
        public void Combine_averages_averages_keeps_peaks_and_the_most_frequent_top_process()
        {
            // Given newest first on purpose: Combine orders by time itself.
            var rows = new List<HistoryRow>
            {
                new() { Utc = T0.AddMinutes(4), Seconds = 60, CpuAvg = 50, CpuMax = 55, DiskFreeMinPercent = 60,
                        TopCpuName = "a.exe", TopCpuPercent = 30, TopRamName = "n.exe", TopRamMb = 300 },
                new() { Utc = T0.AddMinutes(3), Seconds = 60, CpuAvg = 40, CpuMax = 45, DiskFreeMinPercent = 50, BatteryPercent = 78,
                        TopCpuName = "A.EXE", TopCpuPath = @"C:\A\a.exe", TopCpuPercent = 40, TopRamName = "n.exe", TopRamMb = 200 },
                new() { Utc = T0.AddMinutes(2), Seconds = 60, CpuAvg = 30, CpuMax = 35, DiskFreeMinPercent = 38, OnAc = false,
                        TopCpuName = "b.exe", TopCpuPercent = 60, TopRamName = "n.exe", TopRamMb = 100 },
                new() { Utc = T0.AddMinutes(1), Seconds = 60, CpuAvg = 20, CpuMax = 90, DiskFreeMinPercent = 35, BatteryPercent = 79, OnAc = true,
                        TopCpuName = "b.exe", TopCpuPercent = 50, TopRamName = "m.exe", TopRamMb = 900 },
                new() { Utc = T0, Seconds = 60, CpuAvg = 10, CpuMax = 15, DiskFreeMinPercent = 40, BatteryPercent = 80, OnAc = true,
                        TopCpuName = "a.exe", TopCpuPercent = 20, TopRamName = "m.exe", TopRamMb = 800 },
            };

            HistoryRow five = HistoryCsv.Combine(rows, T0, 300);

            Assert.Equal(T0, five.Utc);
            Assert.Equal(300, five.Seconds);
            Assert.Equal(30f, five.CpuAvg);                 // (10 + 20 + 30 + 40 + 50) / 5
            Assert.Equal(90f, five.CpuMax);
            Assert.Equal(35f, five.DiskFreeMinPercent);
            Assert.Equal(78f, five.BatteryPercent);         // the last reading by time
            Assert.False(five.OnAc);                        // the last reading by time
            Assert.Null(five.GpuAvg);                       // never read
            Assert.Equal("a.exe", five.TopCpuName);         // three times in any case, b.exe twice
            Assert.Equal(@"C:\A\a.exe", five.TopCpuPath);
            Assert.Equal(30f, five.TopCpuPercent);          // (20 + 40 + 30) / 3
            Assert.Equal("n.exe", five.TopRamName);
            Assert.Equal(200f, five.TopRamMb);              // (100 + 200 + 300) / 3
        }
    }
}
