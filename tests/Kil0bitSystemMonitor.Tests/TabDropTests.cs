using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Where a dragged tab lands.</summary>
    public class TabDropTests
    {
        // Three tabs, 100 wide, touching: centers at 50, 150, 250.
        private static readonly (double Left, double Width)[] Tabs = { (0, 100), (100, 100), (200, 100) };

        [Theory]
        [InlineData(0, 10, 0)]      // still over itself
        [InlineData(0, 160, 1)]     // past the second tab's center
        [InlineData(0, 290, 2)]     // past the last center
        [InlineData(2, 120, 1)]     // dragged left, before the second tab's center... after the first
        [InlineData(2, 20, 0)]      // before the first center
        [InlineData(1, 140, 1)]     // over its own place
        [InlineData(0, -50, 0)]     // left of the strip
        [InlineData(0, 900, 2)]     // right of the strip
        public void The_target_is_the_count_of_other_tab_centers_left_of_the_pointer(int dragged, double x, int expected)
        {
            Assert.Equal(expected, TabDrop.TargetIndex(x, Tabs, dragged));
        }

        [Theory]
        [InlineData(2, 0, false)]
        [InlineData(4, 0, true)]
        [InlineData(0, 4, true)]
        [InlineData(-4, 0, true)]
        public void A_drag_starts_past_the_system_threshold(double dx, double dy, bool expected)
        {
            Assert.Equal(expected, TabDrop.PastThreshold(dx, dy, 4, 4));
        }
    }
}
