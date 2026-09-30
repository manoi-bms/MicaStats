using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Where a dragged tab lands (spec 4.2). Pure: the controller passes the tabs' current layout.</summary>
    public static class TabDrop
    {
        /// <summary>
        /// The index the dragged tab belongs at: the number of other tabs whose center is left of
        /// the pointer. Clamped by construction to 0..count-1.
        /// </summary>
        public static int TargetIndex(double pointerX, IReadOnlyList<(double Left, double Width)> tabs, int dragged)
        {
            int index = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                if (i == dragged) continue;
                if (tabs[i].Left + tabs[i].Width / 2 < pointerX) index++;
            }
            return index;
        }

        /// <summary>True once the pointer moved past the system drag threshold on either axis.</summary>
        public static bool PastThreshold(double dx, double dy, double minX, double minY) =>
            Math.Abs(dx) >= minX || Math.Abs(dy) >= minY;
    }
}
