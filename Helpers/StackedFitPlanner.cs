using System;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Decides how much of the stacked taskbar layout fits inside a width cap, shedding
    /// content in fidelity order: level 0 draws everything, level 1 hides every sparkline
    /// graph, and each level above 1 additionally hides one trailing module (so the leftmost
    /// modules — network and CPU in the default order — survive longest).
    ///
    /// <para>
    /// Pure arithmetic, no rendering types: the overlay measures, this ranks. Restoring to a
    /// richer level demands the cap fit with extra slack, because the obstacle (the centered
    /// Start button) moves in ~22px steps as taskbar icons come and go — without hysteresis a
    /// boundary-straddling width would flap between levels once per tick.
    /// </para>
    /// </summary>
    public static class StackedFitPlanner
    {
        public sealed class Plan
        {
            /// <summary>0 = full; 1 = graphs hidden; 1+k = graphs hidden and k trailing modules hidden.</summary>
            public int Level { get; init; }

            public bool ShowGraphs { get; init; }

            /// <summary>How many leading columns to draw.</summary>
            public int VisibleColumns { get; init; }

            /// <summary>Total layer width at this level, chrome included.</summary>
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
        public static Plan Fit(float[] columnWidths, float[] graphParts, float columnGap, float chrome,
                               float? cap, int previousLevel, float restoreSlack)
        {
            int n = columnWidths.Length;
            int maxLevel = Math.Max(0, n); // level n keeps only the first column

            if (cap is not float limit || n == 0)
                return At(columnWidths, graphParts, columnGap, chrome, 0, fits: true);

            int prev = Math.Clamp(previousLevel, 0, maxLevel);

            int? FirstFitting(float within)
            {
                for (int l = 0; l <= maxLevel; l++)
                    if (WidthAt(columnWidths, graphParts, columnGap, chrome, l) <= within) return l;
                return null;
            }

            int? baseline = FirstFitting(limit);
            if (baseline == null)
                return At(columnWidths, graphParts, columnGap, chrome, maxLevel, fits: false);

            // Degrading (or holding) is immediate; restoring requires slack-clearance.
            if (baseline.Value >= prev)
                return At(columnWidths, graphParts, columnGap, chrome, baseline.Value, fits: true);

            int? restore = FirstFitting(limit - restoreSlack);
            int chosen = restore != null && restore.Value < prev ? restore.Value : prev;
            return At(columnWidths, graphParts, columnGap, chrome, chosen, fits: true);
        }

        /// <summary>
        /// A plan pinned at <paramref name="level"/> (clamped to the valid range). Used while
        /// the user is dragging the overlay: re-measuring obstacles mid-gesture would make
        /// the window resize and slide under the cursor, fighting the drag.
        /// </summary>
        public static Plan FitAtLevel(float[] columnWidths, float[] graphParts, float columnGap,
                                      float chrome, int level)
        {
            int clamped = Math.Clamp(level, 0, Math.Max(0, columnWidths.Length));
            return At(columnWidths, graphParts, columnGap, chrome, clamped, fits: true);
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

        private static float WidthAt(float[] widths, float[] graphParts, float gap, float chrome, int level)
        {
            int n = widths.Length;
            int visible = n - Math.Max(0, level - 1);
            if (visible <= 0) return chrome;
            float sum = chrome + gap * (visible - 1);
            for (int i = 0; i < visible; i++)
                sum += widths[i] - (level >= 1 ? graphParts[i] : 0f);
            return sum;
        }

        private static Plan At(float[] widths, float[] graphParts, float gap, float chrome, int level, bool fits) => new()
        {
            Level = level,
            ShowGraphs = level == 0,
            VisibleColumns = widths.Length - Math.Max(0, level - 1),
            Width = WidthAt(widths, graphParts, gap, chrome, level),
            Fits = fits,
        };
    }
}
