using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiTimeRangeTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("now", "2026-09-30T05:00:00Z")]
        [InlineData(" NOW ", "2026-09-30T05:00:00Z")]
        [InlineData("-90s", "2026-09-30T04:58:30Z")]
        [InlineData("-30m", "2026-09-30T04:30:00Z")]
        [InlineData("-6h", "2026-09-29T23:00:00Z")]
        [InlineData("-2d", "2026-09-28T05:00:00Z")]
        [InlineData("-2D", "2026-09-28T05:00:00Z")]
        [InlineData("2026-09-29T10:15:00Z", "2026-09-29T10:15:00Z")]
        [InlineData("2026-09-29T10:15:00", "2026-09-29T10:15:00Z")]
        [InlineData("2026-09-29T17:15:00+07:00", "2026-09-29T10:15:00Z")]
        [InlineData("2026-09-29", "2026-09-29T00:00:00Z")]
        public void Reads_now_relative_and_iso_times_as_utc(string text, string expected)
        {
            Assert.True(TimeRange.TryParse(text, Now, out DateTime utc));

            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(expected, utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("yesterday")]
        [InlineData("-5x")]
        [InlineData("5m")]
        [InlineData("-9999999d")]
        [InlineData("30/09/2026")]
        [InlineData("-\u0E51\u0E52m")]
        public void Refuses_anything_else(string? text)
        {
            Assert.False(TimeRange.TryParse(text, Now, out _));
        }

        [Fact]
        public void A_thai_culture_does_not_shift_the_year()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");

                Assert.True(TimeRange.TryParse("2026-09-29T10:15:00Z", Now, out DateTime utc));

                Assert.Equal(2026, utc.Year);
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }
    }
}
