using System.Collections.Generic;
using Kil0bitSystemMonitor.Helpers;
using Xunit;
using R = Kil0bitSystemMonitor.Helpers.OverlayPlacement.Rect;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Regression coverage for the "overlay invisible on the taskbar" report: a persisted
    /// position that lands in the dead space between two monitors must be detected as
    /// off-screen and snapped back onto the taskbar. Coordinates are the real ones from the
    /// machine that reproduced it — primary 1920×1080 at (0,0) with a bottom taskbar, secondary
    /// 2560×1600 at (1920,339); the saved overlay sat at (167,1158)-(475,1194).
    /// </summary>
    public class OverlayPlacementTests
    {
        private static readonly List<R> TwoMonitors = new()
        {
            new R(0, 0, 1920, 1080),       // primary, DISPLAY2
            new R(1920, 339, 4480, 1939),  // secondary, DISPLAY1
        };

        [Fact]
        public void Saved_position_between_monitors_is_offscreen()
        {
            var overlay = new R(167, 1158, 475, 1194);
            Assert.False(OverlayPlacement.IsVisibleOn(overlay, TwoMonitors));
        }

        [Fact]
        public void Position_on_primary_taskbar_is_visible()
        {
            var overlay = new R(167, 1038, 475, 1074);
            Assert.True(OverlayPlacement.IsVisibleOn(overlay, TwoMonitors));
        }

        [Fact]
        public void One_pixel_edge_touch_is_not_counted_visible()
        {
            // Sitting exactly on the secondary's left edge, only a hair overlapping.
            var overlay = new R(1918, 400, 1922, 440);
            Assert.False(OverlayPlacement.IsVisibleOn(overlay, TwoMonitors));
        }

        [Fact]
        public void Fully_inside_secondary_is_visible()
        {
            var overlay = new R(2000, 1891, 2300, 1927);
            Assert.True(OverlayPlacement.IsVisibleOn(overlay, TwoMonitors));
        }

        [Fact]
        public void No_monitors_is_never_visible()
        {
            Assert.False(OverlayPlacement.IsVisibleOn(new R(0, 0, 100, 36), new List<R>()));
        }

        [Fact]
        public void Snap_centers_y_and_keeps_fitting_x()
        {
            // Primary taskbar (0,1032)-(1920,1080), overlay 308×36, saved X=167 fits.
            var (x, y) = OverlayPlacement.SnapToTaskbar(new R(0, 1032, 1920, 1080), 308, 36, 167);
            Assert.Equal(167, x);
            Assert.Equal(1038, y);   // 1032 + (48-36)/2
        }

        [Fact]
        public void Snap_clamps_x_that_would_overflow_the_taskbar()
        {
            var (x, _) = OverlayPlacement.SnapToTaskbar(new R(0, 1032, 1920, 1080), 308, 36, 5000);
            Assert.Equal(1920 - 308, x);
        }

        [Fact]
        public void Snap_clamps_x_left_of_the_taskbar()
        {
            var (x, _) = OverlayPlacement.SnapToTaskbar(new R(1920, 1891, 4480, 1939), 308, 36, 0);
            Assert.Equal(1920, x);
        }
    }
}
