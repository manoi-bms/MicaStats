using System;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Decides how much of the stacked taskbar layout fits inside a width cap, shedding in
    /// fidelity order. Level 0 is elastic — sparklines render anywhere between full width and
    /// <see cref="MinGraphScale"/> of it, soaking up exactly the space available. Below that
    /// the ladder is discrete: level 1 hides every sparkline, level 2 additionally tightens
    /// the chrome (pod padding, column gaps, leading/trailing margins — the reclaimable
    /// whitespace the eye sees), and each level above 2 hides one trailing module (the
    /// leftmost modules — network and CPU in the default order — survive longest).
    ///
    /// <para>
    /// Pure arithmetic, no rendering types: the overlay measures, this ranks. Restoring to a
    /// richer level demands the cap fit with extra slack, because the obstacle (the centered
    /// Start button) moves in ~22px steps as taskbar icons come and go — without hysteresis a
    /// boundary-straddling width would flap between levels once per tick. Scale changes
    /// within level 0 carry no such risk: they are continuous, a few pixels per step.
    /// </para>
    /// </summary>
    public static class StackedFitPlanner
    {
        /// <summary>Smallest factor sparklines may squeeze to before being dropped entirely.</summary>
        public const float MinGraphScale = 0.4f;

        public sealed class Plan
        {
            /// <summary>
            /// 0 = full (graphs, possibly squeezed); 1 = graphs hidden; 2 = chrome tightened
            /// too; 2+k = k trailing modules hidden as well.
            /// </summary>
            public int Level { get; init; }

            public bool ShowGraphs { get; init; }

            /// <summary>1 = full-width sparklines, down to <see cref="MinGraphScale"/> when squeezed.</summary>
            public float GraphScale { get; init; } = 1f;

            /// <summary>True when the tightened chrome (level ≥ 2) applies.</summary>
            public bool Compact { get; init; }

            /// <summary>How many leading columns to draw.</summary>
            public int VisibleColumns { get; init; }

            /// <summary>Total layer width at this level and scale, chrome included.</summary>
            public float Width { get; init; }

            /// <summary>False when even the most degraded level exceeds the cap.</summary>
            public bool Fits { get; init; }
        }

        /// <param name="columnWidths">Full per-column widths, graphs included, in draw order.</param>
        /// <param name="graphParts">Per-column width the sparkline contributes (0 when it has none).</param>
        /// <param name="columnGap">Gap between adjacent columns.</param>
        /// <param name="chrome">Fixed leading + trailing padding.</param>
        /// <param name="cap">Maximum allowed width, or null for unconstrained.</param>
        /// <param name="previousLevel">Last tick's level, for hysteresis.</param>
        /// <param name="restoreSlack">Extra clearance a richer level must fit within before restoring.</param>
        /// <param name="padSavePerColumn">Width each column gives back when the chrome tightens.</param>
        /// <param name="gapSave">Width each column gap gives back when the chrome tightens.</param>
        /// <param name="chromeSave">Width the leading/trailing padding gives back when tightened.</param>
        public static Plan Fit(float[] columnWidths, float[] graphParts, float columnGap, float chrome,
                               float? cap, int previousLevel, float restoreSlack,
                               float padSavePerColumn = 0f, float gapSave = 0f, float chromeSave = 0f)
        {
            int n = columnWidths.Length;
            int maxLevel = n == 0 ? 0 : n + 1; // level n+1 keeps only the first column, compact

            float WidthOf(int level) => WidthAt(columnWidths, graphParts, columnGap, chrome,
                                                padSavePerColumn, gapSave, chromeSave, level);

            if (cap is not float limit || n == 0)
                return At(columnWidths, graphParts, columnGap, chrome,
                          padSavePerColumn, gapSave, chromeSave, 0, 1f, fits: true);

            float w1 = WidthOf(1);
            float parts = WidthOf(0) - w1;

            int prev = Math.Clamp(previousLevel, 0, maxLevel);

            bool Fittable(int level, float within) => level == 0
                ? within >= w1 + MinGraphScale * parts
                : WidthOf(level) <= within;

            int? FirstFitting(float within)
            {
                for (int l = 0; l <= maxLevel; l++)
                    if (Fittable(l, within)) return l;
                return null;
            }

            int? baseline = FirstFitting(limit);
            if (baseline == null)
                return At(columnWidths, graphParts, columnGap, chrome,
                          padSavePerColumn, gapSave, chromeSave, maxLevel, 1f, fits: false);

            // Degrading (or holding) is immediate; restoring requires slack-clearance.
            int chosen;
            if (baseline.Value >= prev) chosen = baseline.Value;
            else
            {
                int? restore = FirstFitting(limit - restoreSlack);
                chosen = restore != null && restore.Value < prev ? restore.Value : prev;
            }

            if (chosen == 0)
            {
                float graphScale = parts > 0 ? Math.Clamp((limit - w1) / parts, MinGraphScale, 1f) : 1f;
                return At(columnWidths, graphParts, columnGap, chrome,
                          padSavePerColumn, gapSave, chromeSave, 0, graphScale, fits: true);
            }
            return At(columnWidths, graphParts, columnGap, chrome,
                      padSavePerColumn, gapSave, chromeSave, chosen, 1f, fits: true);
        }

        /// <summary>
        /// A plan pinned at <paramref name="level"/> and <paramref name="graphScale"/>, both
        /// clamped. Used while the user is dragging the overlay: re-measuring obstacles
        /// mid-gesture would make the window resize and slide under the cursor.
        /// </summary>
        public static Plan FitAtLevel(float[] columnWidths, float[] graphParts, float columnGap,
                                      float chrome, int level, float graphScale = 1f,
                                      float padSavePerColumn = 0f, float gapSave = 0f, float chromeSave = 0f)
        {
            int maxLevel = columnWidths.Length == 0 ? 0 : columnWidths.Length + 1;
            int clamped = Math.Clamp(level, 0, maxLevel);
            float scale = clamped == 0 ? Math.Clamp(graphScale, MinGraphScale, 1f) : 1f;
            return At(columnWidths, graphParts, columnGap, chrome,
                      padSavePerColumn, gapSave, chromeSave, clamped, scale, fits: true);
        }

        /// <summary>
        /// The free horizontal span around <paramref name="anchorX"/> between obstacles that
        /// share the overlay's vertical band: an obstacle left of the anchor bounds the span
        /// with its right edge, one at or right of the anchor with its left edge, and
        /// <paramref name="bounds"/> (the taskbar rect) covers whichever side no obstacle
        /// does. Null when no obstacle shares the band — a free-floating overlay away from
        /// the taskbar is nobody's business.
        /// </summary>
        public static (float Left, float Right)? Corridor(float anchorX, float selfTop, float selfBottom,
            Win32Helper.RECT bounds, params Win32Helper.RECT?[] obstacles)
        {
            float left = bounds.Left, right = bounds.Right;
            bool any = false;
            foreach (var candidate in obstacles)
            {
                if (candidate is not Win32Helper.RECT o) continue;
                if (selfTop >= o.Bottom || selfBottom <= o.Top) continue; // different band
                any = true;
                if (o.Left >= anchorX) right = Math.Min(right, o.Left);
                else left = Math.Max(left, o.Right); // includes an obstacle containing the anchor
            }
            return any ? (left, right) : null;
        }

        private static float WidthAt(float[] widths, float[] graphParts, float gap, float chrome,
                                     float padSave, float gapSave, float chromeSave, int level)
        {
            int n = widths.Length;
            int visible = n - Math.Max(0, level - 2);
            bool compact = level >= 2;
            if (visible <= 0) return chrome - (compact ? chromeSave : 0f);

            float sum = chrome + gap * (visible - 1);
            for (int i = 0; i < visible; i++)
                sum += widths[i] - (level >= 1 ? graphParts[i] : 0f);
            if (compact) sum -= chromeSave + padSave * visible + gapSave * (visible - 1);
            return sum;
        }

        private static Plan At(float[] widths, float[] graphParts, float gap, float chrome,
                               float padSave, float gapSave, float chromeSave,
                               int level, float graphScale, bool fits)
        {
            float w = WidthAt(widths, graphParts, gap, chrome, padSave, gapSave, chromeSave, level);
            if (level == 0 && graphScale < 1f)
            {
                float w1 = WidthAt(widths, graphParts, gap, chrome, padSave, gapSave, chromeSave, 1);
                w = w1 + graphScale * (w - w1);
            }
            return new()
            {
                Level = level,
                ShowGraphs = level == 0,
                GraphScale = level == 0 ? graphScale : 1f,
                Compact = level >= 2,
                VisibleColumns = widths.Length - Math.Max(0, level - 2),
                Width = w,
                Fits = fits,
            };
        }
    }
}
