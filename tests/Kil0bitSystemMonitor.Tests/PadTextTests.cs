using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Status-bar text and the window-placement check.</summary>
    public class PadTextTests
    {
        [Theory]
        [InlineData(2, "Saved 2s ago")]
        [InlineData(59, "Saved 59s ago")]
        [InlineData(180, "Saved 3m ago")]
        [InlineData(7200, "Saved 2h ago")]
        [InlineData(-5, "Saved 0s ago")]
        public void Saved_ago_reads_naturally(int seconds, string expected)
        {
            Assert.Equal(expected, PadText.SavedAgo(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void Counts_use_invariant_grouping_under_a_thai_culture()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                Assert.Equal("1,284 chars", PadText.CharCount(1284));
                Assert.Equal("Ln 12, Col 5", PadText.CaretPosition(12, 5));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData(512, "512 B")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(3 * 1024 * 1024, "3.0 MB")]
        public void Sizes_scale_their_unit(long bytes, string expected)
        {
            Assert.Equal(expected, PadText.Size(bytes));
        }

        [Fact]
        public void Size_deltas_carry_a_sign()
        {
            Assert.Equal("+1.0 KB", PadText.SizeDelta(1024));
            Assert.Equal("-2.0 KB", PadText.SizeDelta(-2048));
            Assert.Equal("+0 B", PadText.SizeDelta(0));
        }

        [Theory]
        [InlineData(100, 100, 800, 600, true)]
        [InlineData(5000, 100, 800, 600, false)]
        [InlineData(-750, 100, 800, 600, false)]
        [InlineData(100, -20, 800, 600, false)]
        [InlineData(100, 1060, 800, 600, false)]
        public void A_window_is_reachable_only_when_its_title_bar_is_on_screen(double left, double top, double width, double height, bool expected)
        {
            Assert.Equal(expected, PadPlacement.IsReachable(left, top, width, height, 0, 0, 1920, 1080));
        }

        [Fact]
        public void An_unplaced_window_is_not_reachable()
        {
            Assert.False(PadPlacement.IsReachable(double.NaN, 0, 800, 600, 0, 0, 1920, 1080));
        }
    }
}
