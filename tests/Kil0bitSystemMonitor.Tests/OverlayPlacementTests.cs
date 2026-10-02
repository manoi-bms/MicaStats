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

        // Explorer restart, measured on 26200: the old taskbar 0x8C2C3C died, Windows set the
        // overlay's owner to none, and the new taskbar 0x752D5C came up 1.5 s later.
        private static readonly nint OldTaskbar = 0x8C2C3C;
        private static readonly nint NewTaskbar = 0x752D5C;

        [Fact]
        public void Overlay_left_without_an_owner_by_an_Explorer_restart_attaches_to_the_new_taskbar()
        {
            Assert.True(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: NewTaskbar, taskbarShown: true, owner: 0, lastAttempt: OldTaskbar));
        }

        [Fact]
        public void Overlay_still_owned_by_a_dead_taskbar_attaches_to_the_new_one()
        {
            Assert.True(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: NewTaskbar, taskbarShown: true, owner: OldTaskbar, lastAttempt: OldTaskbar));
        }

        [Fact]
        public void Taskbar_that_appeared_after_startup_is_attached_once_it_is_there()
        {
            Assert.True(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: NewTaskbar, taskbarShown: true, owner: 0, lastAttempt: 0));
        }

        [Fact]
        public void Overlay_owned_by_the_live_taskbar_is_left_alone()
        {
            Assert.False(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: NewTaskbar, taskbarShown: true, owner: NewTaskbar, lastAttempt: NewTaskbar));
        }

        [Fact]
        public void Nothing_is_attached_while_Explorer_has_no_taskbar_yet()
        {
            Assert.False(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: 0, taskbarShown: true, owner: 0, lastAttempt: OldTaskbar));
        }

        [Fact]
        public void A_taskbar_Explorer_has_not_shown_yet_is_left_alone()
        {
            // Still being built: its rectangle is not the final one, and aligning to it would move the saved X.
            Assert.False(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: NewTaskbar, taskbarShown: false, owner: 0, lastAttempt: OldTaskbar));
        }

        // Measured through an Explorer restart: attached again at 1.2 s, the new taskbar still sat above the
        // overlay, and stayed there while it was the foreground window.
        [Fact]
        public void A_taskbar_covering_the_stuck_overlay_is_answered_even_while_it_is_active()
        {
            Assert.True(OverlayPlacement.ShouldRaise(alwaysOnTop: true, stickToTaskbar: true, taskbarActive: true, atTop: false, taskbarAbove: true));
            Assert.True(OverlayPlacement.ShouldRaise(alwaysOnTop: false, stickToTaskbar: true, taskbarActive: true, atTop: false, taskbarAbove: true));
        }

        [Fact]
        public void An_active_taskbar_below_the_overlay_is_not_fought()
        {
            // Raising TOPMOST while the taskbar manages its own z-order made it blink.
            Assert.False(OverlayPlacement.ShouldRaise(alwaysOnTop: true, stickToTaskbar: true, taskbarActive: true, atTop: false, taskbarAbove: false));
        }

        [Fact]
        public void Keep_on_top_raises_an_overlay_something_else_covers()
        {
            Assert.True(OverlayPlacement.ShouldRaise(alwaysOnTop: true, stickToTaskbar: false, taskbarActive: false, atTop: false, taskbarAbove: false));
            Assert.False(OverlayPlacement.ShouldRaise(alwaysOnTop: true, stickToTaskbar: false, taskbarActive: false, atTop: true, taskbarAbove: false));
        }

        [Fact]
        public void A_floating_overlay_without_keep_on_top_is_never_raised()
        {
            Assert.False(OverlayPlacement.ShouldRaise(alwaysOnTop: false, stickToTaskbar: false, taskbarActive: false, atTop: false, taskbarAbove: true));
        }

        [Fact]
        public void Keep_on_top_raises_the_overlay()
        {
            Assert.True(OverlayPlacement.TopmostFor(alwaysOnTop: true, stickToTaskbar: true));
            Assert.True(OverlayPlacement.TopmostFor(alwaysOnTop: true, stickToTaskbar: false));
        }

        [Fact]
        public void Keep_on_top_off_lowers_a_floating_overlay()
        {
            Assert.False(OverlayPlacement.TopmostFor(alwaysOnTop: false, stickToTaskbar: false));
        }

        [Fact]
        public void Keep_on_top_off_never_lowers_an_overlay_owned_by_the_taskbar()
        {
            // Made not-topmost, an owned window takes its owner down too: the taskbar would lose topmost.
            Assert.Null(OverlayPlacement.TopmostFor(alwaysOnTop: false, stickToTaskbar: true));
        }

        [Fact]
        public void Floating_overlay_is_never_attached()
        {
            Assert.False(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: false, taskbar: NewTaskbar, taskbarShown: true, owner: 0, lastAttempt: OldTaskbar));
        }

        [Fact]
        public void A_taskbar_that_refused_the_attach_is_not_asked_again_every_tick()
        {
            Assert.False(OverlayPlacement.NeedsTaskbarAttach(stickToTaskbar: true, taskbar: NewTaskbar, taskbarShown: true, owner: 0, lastAttempt: NewTaskbar));
        }
    }
}
