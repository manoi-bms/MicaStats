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
            // Cap 290 fits level 0 (284) but not with the 24px slack (284 > 266): stay put.
            var held = Fit(cap: 290f, prev: 1);
            Assert.Equal(1, held.Level);

            // Cap 320 clears the slack (284 <= 296): restore to full.
            var restored = Fit(cap: 320f, prev: 1);
            Assert.Equal(0, restored.Level);
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
    }
}
