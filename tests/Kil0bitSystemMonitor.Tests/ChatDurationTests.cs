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

        [Fact]
        public void The_digits_are_ASCII_whatever_the_culture()
        {
            CultureInfo old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");   // the culture of the PC this is written on

                Assert.Equal("4 s", ChatDuration.Text(TimeSpan.FromSeconds(4)));
                Assert.Equal("1 min 5 s", ChatDuration.Text(TimeSpan.FromSeconds(65)));
                Assert.Equal("1234 min 56 s", ChatDuration.Text(TimeSpan.FromSeconds(1234 * 60 + 56)));   // no group separator either
                Assert.All(ChatDuration.Text(TimeSpan.FromSeconds(9876 * 60 + 54)).Where(char.IsDigit), digit => Assert.InRange(digit, '0', '9'));
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }
    }
}
