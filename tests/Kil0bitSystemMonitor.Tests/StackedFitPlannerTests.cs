using Kil0bitSystemMonitor.Helpers;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The width-cap degradation ladder for the stacked taskbar: level 0 full, level 1 graphs
    /// hidden, above that trailing modules hidden one per level. Reference numbers used
    /// throughout: widths [100,50,50,50], graphParts [40,30,0,30], gap 10, chrome 4 —
    /// level widths 284 / 184 / 154 / 94 / 64.
    /// </summary>
    public class StackedFitPlannerTests
    {
        private static readonly float[] Widths = { 100f, 50f, 50f, 50f };
        private static readonly float[] Graphs = { 40f, 30f, 0f, 30f };

        private static StackedFitPlanner.Plan Fit(float? cap, int prev = 0, float slack = 24f)
            => StackedFitPlanner.Fit(Widths, Graphs, columnGap: 10f, chrome: 4f, cap, prev, slack);

        [Fact]
        public void No_cap_draws_everything()
        {
            var p = Fit(cap: null, prev: 3);
            Assert.Equal(0, p.Level);
            Assert.True(p.ShowGraphs);
            Assert.Equal(4, p.VisibleColumns);
            Assert.Equal(284f, p.Width);
            Assert.True(p.Fits);
        }

        [Fact]
        public void Cap_wide_enough_keeps_level_zero()
        {
            var p = Fit(cap: 284f);
            Assert.Equal(0, p.Level);
            Assert.True(p.Fits);
        }

        [Fact]
        public void First_casualty_is_the_graphs_never_a_module()
        {
            var p = Fit(cap: 200f);
            Assert.Equal(1, p.Level);
            Assert.False(p.ShowGraphs);
            Assert.Equal(4, p.VisibleColumns);
            Assert.Equal(184f, p.Width);
        }

        [Fact]
        public void Modules_shed_from_the_trailing_end()
        {
            var p = Fit(cap: 160f);
            Assert.Equal(2, p.Level);
            Assert.Equal(3, p.VisibleColumns);
            Assert.Equal(154f, p.Width);
        }

        [Fact]
        public void Impossible_cap_keeps_the_first_module_and_reports_no_fit()
        {
            // The leftmost module (network/CPU in the real ordering) is never dropped: a
            // one-module overlay beats a vanished one, even if it still overhangs slightly.
            var p = Fit(cap: 30f);
            Assert.Equal(4, p.Level);
            Assert.Equal(1, p.VisibleColumns);
            Assert.Equal(64f, p.Width);
            Assert.False(p.Fits);
        }

        [Fact]
        public void Restore_needs_slack_so_the_boundary_cannot_flap()
        {
            // Squeezed level 0 becomes reachable at 224 (184 + 0.4*100); restoring on top of
            // that demands the 24px slack: 240-24=216 misses it, 260-24=236 clears it.
            var held = Fit(cap: 240f, prev: 1);
            Assert.Equal(1, held.Level);

            var restored = Fit(cap: 260f, prev: 1);
            Assert.Equal(0, restored.Level);
            Assert.Equal(0.76, restored.GraphScale, 2);
        }

        [Fact]
        public void Graphs_squeeze_to_soak_up_the_exact_space_available()
        {
            // Between full (284) and graphless (184) the sparklines scale continuously, so
            // the plan's width equals the cap instead of leaving a blank strip beside it.
            var p = Fit(cap: 250f);
            Assert.Equal(0, p.Level);
            Assert.True(p.ShowGraphs);
            Assert.Equal(0.66, p.GraphScale, 2);
            Assert.Equal(250.0, p.Width, 1);
        }

        [Fact]
        public void Graphs_drop_only_below_the_minimum_squeeze()
        {
            // 184 + 0.4*100 = 224 is the squeeze floor; under it the graphs go entirely.
            var p = Fit(cap: 220f);
            Assert.Equal(1, p.Level);
            Assert.False(p.ShowGraphs);
            Assert.Equal(184f, p.Width);
        }

        [Fact]
        public void Degrading_is_immediate_regardless_of_previous_level()
        {
            var p = Fit(cap: 100f, prev: 0);
            Assert.Equal(3, p.Level);
            Assert.Equal(2, p.VisibleColumns);
        }

        [Fact]
        public void Out_of_range_previous_levels_are_clamped()
        {
            Assert.Equal(0, Fit(cap: 400f, prev: -5).Level);
            // prev far beyond max clamps to max, and 400 does not clear max+slack for a full
            // restore... it does (284 <= 376), so it restores — the clamp just must not throw.
            Assert.Equal(0, Fit(cap: 400f, prev: 99).Level);
        }

        [Fact]
        public void With_graphs_already_absent_the_ladder_still_reaches_a_fit()
        {
            float[] w = { 50f, 50f, 50f };
            float[] g = { 0f, 0f, 0f };
            var p = StackedFitPlanner.Fit(w, g, columnGap: 0f, chrome: 0f, cap: 120f, previousLevel: 0, restoreSlack: 24f);
            Assert.Equal(2, p.Level);
            Assert.Equal(2, p.VisibleColumns);
            Assert.Equal(100f, p.Width);
        }

        // ---- Corridor: the free span the overlay may slide within ----
        // Geometry from the live probe on this machine: taskbar band 1032-1080, widgets
        // button 6-158, Start button 294-339, tray from 1589.

        private static Win32Helper.RECT R(int l, int t, int r, int b)
            => new() { Left = l, Top = t, Right = r, Bottom = b };

        [Fact]
        public void Corridor_spans_between_flanking_obstacles()
        {
            var c = StackedFitPlanner.Corridor(200, 1032, 1080, R(0, 1032, 1920, 1080),
                R(6, 1032, 158, 1080), R(294, 1032, 339, 1080), R(1589, 1032, 1920, 1080));
            Assert.True(c.HasValue);
            Assert.Equal(158f, c!.Value.Left);
            Assert.Equal(294f, c.Value.Right);
        }

        [Fact]
        public void Corridor_is_null_off_the_taskbar_band()
        {
            // A free-floating overlay in mid-screen shares no band with any obstacle.
            var c = StackedFitPlanner.Corridor(200, 400, 436, R(0, 1032, 1920, 1080),
                R(6, 1032, 158, 1080), R(294, 1032, 339, 1080), null);
            Assert.Null(c);
        }

        [Fact]
        public void Corridor_defaults_open_sides_to_the_taskbar_bounds()
        {
            var c = StackedFitPlanner.Corridor(200, 1032, 1080, R(0, 1032, 1920, 1080),
                null, R(294, 1032, 339, 1080), null);
            Assert.True(c.HasValue);
            Assert.Equal(0f, c!.Value.Left);
            Assert.Equal(294f, c.Value.Right);
        }

        [Fact]
        public void Corridor_pushes_out_of_an_obstacle_containing_the_anchor()
        {
            // An anchor dropped onto the Start button treats it as a left bound: the overlay
            // re-homes just right of it instead of sitting underneath.
            var c = StackedFitPlanner.Corridor(300, 1032, 1080, R(0, 1032, 1920, 1080),
                null, R(294, 1032, 339, 1080), R(1589, 1032, 1920, 1080));
            Assert.True(c.HasValue);
            Assert.Equal(339f, c!.Value.Left);
            Assert.Equal(1589f, c.Value.Right);
        }

        [Fact]
        public void FitAtLevel_pins_the_level_and_clamps_out_of_range_values()
        {
            var pinned = StackedFitPlanner.FitAtLevel(Widths, Graphs, 10f, 4f, 2);
            Assert.Equal(2, pinned.Level);
            Assert.Equal(154f, pinned.Width);

            Assert.Equal(0, StackedFitPlanner.FitAtLevel(Widths, Graphs, 10f, 4f, -3).Level);
            Assert.Equal(4, StackedFitPlanner.FitAtLevel(Widths, Graphs, 10f, 4f, 99).Level);
        }
    }
}
