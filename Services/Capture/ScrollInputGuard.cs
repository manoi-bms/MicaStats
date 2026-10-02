using System;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>Whether a wheel notch may be sent to the picked window now, or why not.</summary>
    public enum WheelCheck
    {
        Send,
        /// <summary>No window was found under the area's centre.</summary>
        NoWindow,
        /// <summary>The picked window closed or was minimised.</summary>
        Gone,
        /// <summary>Another window is now under the area's centre.</summary>
        Covered,
        /// <summary>Windows sends the wheel to the window in front, and the picked one is not there.</summary>
        NotInFront,
    }

    /// <summary>What to do about a held modifier key before a notch.</summary>
    public enum ModifierGate { Send, Wait, Stop }

    /// <summary>What Windows says about the picked window just before a notch.</summary>
    /// <param name="UnderCentre">The top-level window under the area's centre now.</param>
    /// <param name="UnderCentreOwnedByPicked">Whether that window is owned by the picked one (its tooltip or popup).</param>
    /// <param name="InFront">The foreground window now.</param>
    public readonly record struct WheelWindowState(bool Alive, bool Minimised, IntPtr UnderCentre, bool UnderCentreOwnedByPicked, IntPtr InFront);

    /// <summary>
    /// The rules for sending the scrolling capture's wheel input only to the window picked
    /// (scrolling capture spec 6), kept free of Win32 so they are tested.
    /// <see cref="ScreenScrollTarget"/> asks Windows and acts on the answers.
    /// </summary>
    public static class ScrollInputGuard
    {
        /// <summary>
        /// <c>SPI_GETMOUSEWHEELROUTING</c>'s <c>MOUSEWHEEL_ROUTING_MOUSE_POS</c>: the wheel goes to the
        /// window under the pointer ("Scroll inactive windows when I hover over them" on). 0 sends it
        /// to the focused window, 1 to the focused one for desktop apps.
        /// </summary>
        public const int RoutingUnderPointer = 2;

        /// <summary>How long a held Ctrl, Shift, Alt or Win key is waited out before the capture stops.</summary>
        public const int ModifierWaitMs = 2000;

        /// <summary>
        /// Whether the picked window must be in front for the wheel to reach it. Unknown routing
        /// (Windows before 10 has no such setting) is routing by focus.
        /// </summary>
        public static bool NeedsForeground(int? routing) => routing != RoutingUnderPointer;

        /// <summary>
        /// Before each notch: send only while the picked window is alive, not minimised, still the
        /// one under the area's centre (or its own popup is), and in front when the routing needs it.
        /// </summary>
        public static WheelCheck Check(IntPtr picked, int? routing, WheelWindowState now)
        {
            if (picked == IntPtr.Zero) return WheelCheck.NoWindow;
            if (!now.Alive || now.Minimised) return WheelCheck.Gone;
            if (now.UnderCentre != picked && !now.UnderCentreOwnedByPicked) return WheelCheck.Covered;
            if (NeedsForeground(routing) && now.InFront != picked) return WheelCheck.NotInFront;
            return WheelCheck.Send;
        }

        /// <summary>
        /// A held modifier turns the wheel into zoom (Ctrl) or sideways scrolling (Shift): wait
        /// while one is held, and stop after <see cref="ModifierWaitMs"/>.
        /// </summary>
        public static ModifierGate Modifiers(bool held, int waitedMs) =>
            !held ? ModifierGate.Send : waitedMs < ModifierWaitMs ? ModifierGate.Wait : ModifierGate.Stop;
    }
}
