using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Pure geometry for keeping the taskbar overlay on a visible monitor.
    ///
    /// <para>
    /// The overlay persists its last <c>(X, Y)</c> and re-uses it on the next launch. On a
    /// multi-monitor machine — or after a monitor is unplugged or rearranged — that saved point
    /// can fall into the dead space between monitors (e.g. below a 1080-tall primary yet left of
    /// a secondary that starts at X=1920), leaving the window painting where no screen shows it.
    /// It compounds with <c>LaunchOnStartup</c>: at login the shell's taskbar is not yet
    /// findable, so the normal align-to-taskbar step no-ops and the stale point survives.
    /// </para>
    ///
    /// <para>
    /// These functions are the testable decisions behind the recovery: "is this rectangle
    /// actually visible on some monitor?" and "where on the taskbar should it go instead?" —
    /// kept free of Win32 so they can be verified without real monitors.
    /// </para>
    /// </summary>
    public static class OverlayPlacement
    {
        public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
        {
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        /// <summary>
        /// True when <paramref name="overlay"/> overlaps at least one monitor by at least
        /// <paramref name="minVisible"/> pixels on both axes — i.e. a meaningful sliver is
        /// actually on a screen, not a one-pixel touch at a shared edge.
        /// </summary>
        public static bool IsVisibleOn(Rect overlay, IReadOnlyList<Rect> monitors, int minVisible = 8)
        {
            if (monitors == null) return false;
            foreach (var m in monitors)
            {
                int overlapX = Math.Min(overlay.Right, m.Right) - Math.Max(overlay.Left, m.Left);
                int overlapY = Math.Min(overlay.Bottom, m.Bottom) - Math.Max(overlay.Top, m.Top);
                if (overlapX >= minVisible && overlapY >= minVisible) return true;
            }
            return false;
        }

        /// <summary>
        /// Places the overlay centred vertically in the taskbar band with its X clamped so the
        /// whole width stays within the taskbar's monitor. <paramref name="desiredX"/> is the
        /// user's saved horizontal position, honoured when it fits.
        /// </summary>
        public static (int X, int Y) SnapToTaskbar(Rect taskbar, int overlayWidth, int overlayHeight, int desiredX)
        {
            int y = taskbar.Top + (taskbar.Height - overlayHeight) / 2;
            int maxX = Math.Max(taskbar.Left, taskbar.Right - overlayWidth);
            int x = Math.Clamp(desiredX, taskbar.Left, maxX);
            return (x, y);
        }
    }
}
