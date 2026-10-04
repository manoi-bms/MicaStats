using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>How long a request took, as the AI pane and the Ask window say it.</summary>
    public class ChatDurationTests
    {
        [Theory]
        [InlineData(0, "1 s")]                 // never less than a second
        [InlineData(999, "1 s")]
        [InlineData(1000, "1 s")]
        [InlineData(4000, "4 s")]
        [InlineData(4400, "4 s")]              // to the nearest second
        [InlineData(4500, "5 s")]
        [InlineData(59_400, "59 s")]
        [InlineData(59_500, "1 min 0 s")]      // what rounds up to a minute is said as one
        [InlineData(60_000, "1 min 0 s")]
        [InlineData(65_000, "1 min 5 s")]
        [InlineData(120_000, "2 min 0 s")]
        [InlineData(3_600_000, "60 min 0 s")]  // an hour is still counted in minutes
        public void A_duration_is_said_in_seconds_and_from_a_minute_on_in_minutes_and_seconds(int milliseconds, string text) =>
            Assert.Equal(text, ChatDuration.Text(TimeSpan.FromMilliseconds(milliseconds)));

        [Fact]
        public void A_clock_that_went_backwards_still_gives_a_second() =>
            Assert.Equal("1 s", ChatDuration.Text(TimeSpan.FromSeconds(-30)));

        [Fact]
        public void The_longest_time_there_is_does_not_throw()
        {
            string text = ChatDuration.Text(TimeSpan.MaxValue);

            Assert.EndsWith(" s", text, StringComparison.Ordinal);
            Assert.Contains(" min ", text, StringComparison.Ordinal);
        }

        /// <summary>A culture whose number format shares nothing with the invariant one: its own group and decimal separators.</summary>
        private static CultureInfo OddNumbers()
        {
            var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            odd.NumberFormat.NumberGroupSeparator = "'";
            odd.NumberFormat.NumberDecimalSeparator = "·";
            odd.NumberFormat.NumberGroupSizes = new[] { 2 };
            return odd;
        }

        /// <summary>
        /// What this pins is the number format, not the digits: a whole number's plain
        /// <c>ToString()</c> gives the digits 0 to 9 in every .NET culture, with or without
        /// <c>InvariantCulture</c>, so no test can tell those two apart. What a culture, or a
        /// format string, could bring in is a group separator ("N0" writes 1,234 or 1.234 or
        /// 12'34), and that shows only from 1,000 minutes on, which the other tests never reach.
        /// </summary>
        [Fact]
        public void A_long_duration_is_written_without_a_group_separator_and_the_same_under_every_culture()
        {
            CultureInfo old = CultureInfo.CurrentCulture;
            try
            {
                // This PC's culture, one that groups with a dot, one with a space, and one that shares nothing with the invariant one.
                foreach (CultureInfo culture in new[] { new CultureInfo("th-TH"), new CultureInfo("de-DE"), new CultureInfo("fr-FR"), OddNumbers() })
                {
                    CultureInfo.CurrentCulture = culture;

                    Assert.Equal("4 s", ChatDuration.Text(TimeSpan.FromSeconds(4)));
                    Assert.Equal("1 min 5 s", ChatDuration.Text(TimeSpan.FromSeconds(65)));
                    Assert.Equal("1234 min 56 s", ChatDuration.Text(TimeSpan.FromSeconds(1234 * 60 + 56)));
                    Assert.Equal("1234567 min 8 s", ChatDuration.Text(TimeSpan.FromSeconds(1234567L * 60 + 8)));
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }
    }
}
