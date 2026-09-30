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
        [InlineData(2, 120, 1)]     // dragged left, between the first and second centers: the middle
        [InlineData(2, 20, 0)]      // before the first center
        [InlineData(1, 140, 1)]     // over its own place
        [InlineData(0, -50, 0)]     // left of the strip
        [InlineData(0, 900, 2)]     // right of the strip
        public void The_target_is_the_count_of_other_tab_centers_left_of_the_pointer(int dragged, double x, int expected)
        {
            Assert.Equal(expected, TabDrop.TargetIndex(x, Tabs, dragged));
        }

        // Widths 40, 200, 60: centers at 20, 140, 270.
        [Theory]
        [InlineData(0, 130, 0)]     // the narrow tab, not yet past the wide one's center
        [InlineData(0, 150, 1)]     // the narrow tab, past the wide one's center
        [InlineData(1, 10, 0)]      // the wide tab, before the narrow one's center
        [InlineData(1, 30, 1)]      // the wide tab, just past the narrow one's center: its own place
        [InlineData(1, 300, 2)]     // the wide tab, past the last center
        public void Unequal_widths_land_once_and_stay(int dragged, double x, int expected)
        {
            double[] widths = { 40, 200, 60 };
            int target = TabDrop.TargetIndex(x, Lay(widths), dragged);
            Assert.Equal(expected, target);

            // Laid out again with the tab in its new place, the same pointer keeps it there: no oscillation.
            var moved = new System.Collections.Generic.List<double>(widths);
            moved.RemoveAt(dragged);
            moved.Insert(target, widths[dragged]);
            Assert.Equal(target, TabDrop.TargetIndex(x, Lay(moved), target));
        }

        private static (double Left, double Width)[] Lay(System.Collections.Generic.IReadOnlyList<double> widths)
        {
            var tabs = new (double Left, double Width)[widths.Count];
            double left = 0;
            for (int i = 0; i < widths.Count; i++)
            {
                tabs[i] = (left, widths[i]);
                left += widths[i];
            }
            return tabs;
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
