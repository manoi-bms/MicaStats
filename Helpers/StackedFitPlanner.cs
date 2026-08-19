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

            float WidthAt(int level)
            {
                int visible = n - Math.Max(0, level - 1);
                if (visible <= 0) return chrome;
                float sum = chrome + columnGap * (visible - 1);
                for (int i = 0; i < visible; i++)
                    sum += columnWidths[i] - (level >= 1 ? graphParts[i] : 0f);
                return sum;
            }

            Plan At(int level, bool fits) => new()
            {
                Level = level,
                ShowGraphs = level == 0,
                VisibleColumns = n - Math.Max(0, level - 1),
                Width = WidthAt(level),
                Fits = fits,
            };

            if (cap is not float limit || n == 0) return At(0, fits: true);

            int prev = Math.Clamp(previousLevel, 0, maxLevel);

            int? FirstFitting(float within)
            {
                for (int l = 0; l <= maxLevel; l++)
                    if (WidthAt(l) <= within) return l;
                return null;
            }

            int? baseline = FirstFitting(limit);
            if (baseline == null) return At(maxLevel, fits: false);

            // Degrading (or holding) is immediate; restoring requires slack-clearance.
            if (baseline.Value >= prev) return At(baseline.Value, fits: true);

            int? restore = FirstFitting(limit - restoreSlack);
            int chosen = restore != null && restore.Value < prev ? restore.Value : prev;
            return At(chosen, fits: true);
        }
    }
}
