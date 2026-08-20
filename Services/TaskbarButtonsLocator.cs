using System;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Helpers;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Locates the taskbar's own buttons so the overlay can stay clear of them.
    ///
    /// <para>
    /// Primary source is the legacy <c>"Start"</c> child window of <c>Shell_TrayWnd</c>:
    /// verified on Windows 11 26200 that it still exists and its rect tracks the centered
    /// Start button live (L451 vs UIA 452 on this machine), so the common path is two Win32
    /// calls per tick. UI Automation (<c>AutomationId "StartButton"</c>) is the fallback for
    /// builds without that window — but a cold UIA query measured 196ms here, so it only ever
    /// runs on a background task feeding a cached snapshot, never on the render path.
    /// </para>
    /// </summary>
    public static class TaskbarButtonsLocator
    {
        private const int CacheTtlMs = 2000;
        private const int FailureBackoffMs = 15000;

        private static readonly object Sync = new();
        private static Win32Helper.RECT? _uiaRect;
        private static long _uiaFreshUntil;
        private static long _uiaRetryAfter;
        private static int _uiaRefreshRunning;

        // The widgets button has no Win32 child window at all (probed on 26200), so it is
        // UIA-only — but it also never moves, parked at the taskbar's far left, so a long
        // TTL keeps the background traffic negligible.
        private const int WidgetsTtlMs = 30000;
        private static Win32Helper.RECT? _widgetsRect;
        private static long _widgetsFreshUntil;
        private static int _widgetsRefreshRunning;

        /// <summary>The Start button's screen rect in physical pixels, or null when unknowable.</summary>
        public static Win32Helper.RECT? GetStartButtonRect()
        {
            IntPtr tray = Win32Helper.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;

            IntPtr start = Win32Helper.FindWindowEx(tray, IntPtr.Zero, "Start", null!);
            if (start != IntPtr.Zero
                && Win32Helper.GetWindowRect(start, out var rect)
                && rect.Right > rect.Left && rect.Bottom > rect.Top)
                return rect;

            return GetUiaStartRect(tray);
        }

        /// <summary>
        /// The Widgets button's screen rect (taskbar far left), or null when hidden, absent,
        /// or not yet resolved — it is UIA-only, so the first call after startup returns null
        /// while a background query fills the 30-second cache.
        /// </summary>
        public static Win32Helper.RECT? GetWidgetsRect()
        {
            IntPtr tray = Win32Helper.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;

            long now = Environment.TickCount64;
            Win32Helper.RECT? cached;
            bool stale;
            lock (Sync)
            {
                cached = _widgetsRect;
                stale = now >= _widgetsFreshUntil;
            }

            if (stale && Interlocked.CompareExchange(ref _widgetsRefreshRunning, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    Win32Helper.RECT? found = null;
                    try { found = QueryUiaRect(tray, "WidgetsButton"); }
                    catch { /* absent counts the same as hidden: no obstacle */ }
                    finally
                    {
                        lock (Sync)
                        {
                            _widgetsRect = found;
                            _widgetsFreshUntil = Environment.TickCount64 + WidgetsTtlMs;
                        }
                        Interlocked.Exchange(ref _widgetsRefreshRunning, 0);
                    }
                });
            }

            return cached;
        }

        /// <summary>The notification-area rect (clock, tray icons), or null when unknowable.</summary>
        public static Win32Helper.RECT? GetTrayNotifyRect()
        {
            IntPtr tray = Win32Helper.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;

            IntPtr notify = Win32Helper.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null!);
            if (notify != IntPtr.Zero
                && Win32Helper.GetWindowRect(notify, out var rect)
                && rect.Right > rect.Left && rect.Bottom > rect.Top)
                return rect;
            return null;
        }

        /// <summary>
        /// Cached UIA answer, refreshed off-thread when stale. Returns whatever the cache
        /// holds right now — possibly null on the very first call — because the caller runs
        /// once per second and can simply pick the value up next tick.
        /// </summary>
        private static Win32Helper.RECT? GetUiaStartRect(IntPtr tray)
        {
            long now = Environment.TickCount64;
            Win32Helper.RECT? cached;
            bool stale;
            lock (Sync)
            {
                cached = _uiaRect;
                stale = now >= _uiaFreshUntil && now >= _uiaRetryAfter;
            }

            if (stale && Interlocked.CompareExchange(ref _uiaRefreshRunning, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    try
                    {
                        var found = QueryUiaRect(tray, "StartButton");
                        lock (Sync)
                        {
                            _uiaRect = found;
                            _uiaFreshUntil = Environment.TickCount64 + CacheTtlMs;
                            if (found == null) _uiaRetryAfter = Environment.TickCount64 + FailureBackoffMs;
                        }
                    }
                    catch
                    {
                        lock (Sync) { _uiaRetryAfter = Environment.TickCount64 + FailureBackoffMs; }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _uiaRefreshRunning, 0);
                    }
                });
            }

            return cached;
        }

        private static Win32Helper.RECT? QueryUiaRect(IntPtr tray, string automationId)
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(tray);
            var el = root.FindFirst(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.AutomationIdProperty, automationId));
            if (el == null || el.Current.IsOffscreen) return null;

            // UIA bounding rectangles are physical screen pixels, same space as GetWindowRect.
            var b = el.Current.BoundingRectangle;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return null;
            return new Win32Helper.RECT
            {
                Left = (int)Math.Round(b.Left),
                Top = (int)Math.Round(b.Top),
                Right = (int)Math.Round(b.Right),
                Bottom = (int)Math.Round(b.Bottom),
            };
        }
    }
}
