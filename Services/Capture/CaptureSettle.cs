using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// Waits for transient shell UI to actually leave the screen before pixels are grabbed.
    ///
    /// <para>
    /// Choosing "Capture Region" from the overlay's right-click menu used to put the menu
    /// itself in the picture. <c>TrackPopupMenuEx</c> returns as soon as the item is chosen and
    /// the menu object is destroyed immediately after — but neither of those is the same event
    /// as the menu's pixels being gone. Windows fades menus out, and the windows underneath
    /// have not repainted yet, so a capture taken on the next dispatcher pump copies a menu
    /// that is logically closed and still visibly there.
    /// </para>
    ///
    /// <para>
    /// The wait has to <b>pump messages</b> rather than sleep. Blocking the UI thread would
    /// stop the very repaints it is waiting for, and the menu would still be on screen at the
    /// end of it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Public rather than internal because the verification harness lives in a separate
    /// assembly and drives a real popup menu through this, the same reason
    /// HardwareInfoWindow.GatherApplied is public.
    /// </remarks>
    public static class CaptureSettle
    {
        /// <summary>
        /// The window class Windows uses for every popup menu, unchanged since Win32 began.
        /// Polling for it is what makes this precise rather than a guessed sleep.
        /// </summary>
        private const string MenuWindowClass = "#32768";

        /// <summary>
        /// How long a chosen menu's pixels actually survive, measured rather than assumed.
        ///
        /// <para>
        /// A harness drove a real popup menu, chose an item, and compared the menu's own
        /// rectangle against the undisturbed desktop at increasing delays:
        /// </para>
        ///
        /// <code>
        ///     0 ms   20.64% of the menu rectangle still showed the menu
        ///   100 ms   18.65%
        ///   200 ms   18.59%
        ///   300 ms    0.00%  clean
        /// </code>
        ///
        /// <para>
        /// <b>This is a repaint, not an animation.</b> The obvious guess is that Windows is
        /// fading the menu out, which would suggest skipping the wait when menu animation is
        /// switched off. That was tried and refused by measurement: the machine these numbers
        /// come from reports menu animation DISABLED, and the pixels still persisted for
        /// 200-300 ms. What lingers is the region under the menu being invalidated and redrawn
        /// by whichever applications own it — in their processes, on their schedule. Pumping
        /// this thread's message loop cannot hurry that along; only wall-clock time can.
        /// </para>
        ///
        /// <para>
        /// 420 ms is the measured figure plus margin for a slower machine or a busier desktop.
        /// An earlier attempt used 130 ms and did not fix the bug at all.
        /// </para>
        /// </summary>
        private const int RepaintSettleMs = 420;

        /// <summary>Ceiling on the whole wait, so a stuck menu can never hang a capture.</summary>
        private const int MaxWaitMs = 700;

        private const int PollMs = 10;

        /// <summary>
        /// Returns once no popup menu is on screen. Call on the UI thread, immediately before
        /// grabbing pixels, when the capture was started from a menu.
        /// </summary>
        public static void WaitForMenusToClose()
        {
            var clock = Stopwatch.StartNew();

            while (clock.ElapsedMilliseconds < MaxWaitMs && IsMenuOnScreen())
                Pump(PollMs);

            // The window handle is gone long before its pixels are. This wait is the part
            // that actually fixes the bug; the poll above rarely does anything.
            Pump(RepaintSettleMs);

            DiagnosticsLog.Log("capture",
                "Waited " + clock.ElapsedMilliseconds + " ms for the menu to clear");
        }

        /// <summary>True while any popup menu window is visible.</summary>
        public static bool IsMenuOnScreen()
        {
            try
            {
                IntPtr menu = FindWindow(MenuWindowClass, null);
                return menu != IntPtr.Zero && IsWindowVisible(menu);
            }
            catch { return false; }
        }

        /// <summary>
        /// Lets the message loop run for a while. A nested dispatcher frame rather than a
        /// sleep, so painting continues — that is the entire point of the wait.
        /// </summary>
        public static void Pump(int milliseconds)
        {
            if (milliseconds <= 0) return;

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(milliseconds),
                DispatcherPriority.Background,
                (s, e) => frame.Continue = false,
                Dispatcher.CurrentDispatcher);

            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
