using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;

// System.Windows.Forms is in scope via UseWindowsForms and defines its own KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The detail panel: an anchored dropdown showing history graphs, per-core load and system
    /// identity.
    ///
    /// <para>
    /// Two behaviours here are non-obvious and were established empirically:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// Z-order comes from <b>ownership</b>, not <c>Topmost</c>. The taskbar itself carries
    /// WS_EX_TOPMOST, so "topmost" is a shared band rather than a guarantee, and the overlay's
    /// 500ms HWND_TOPMOST re-assert will jump above an unowned panel even while that panel has
    /// focus. Setting the overlay as this window's owner makes it inherit WS_EX_TOPMOST and sort
    /// above its owner permanently.
    /// </item>
    /// <item>
    /// Position is applied through <c>SetWindowPos</c> in physical pixels, not via Left/Top. WPF
    /// treats Left/Top as logical units and scales them by the window's <i>current</i> monitor
    /// factor, which is the wrong factor when anchoring to a window on a different-DPI monitor.
    /// </item>
    /// </list>
    /// </summary>
    public partial class StatsPanelWindow : Window
    {
        /// <summary>Gap between the overlay edge and the panel, in device-independent units.</summary>
        private const int AnchorGapDip = 8;

        private readonly StatsPanelViewModel _vm;
        private readonly IntPtr _ownerHwnd;
        private readonly Func<Win32Helper.RECT?> _getAnchorRect;
        private bool _closingForGood;

        /// <summary>
        /// Suppresses close-on-deactivate while a modal child (for example a colour picker) has
        /// focus, which would otherwise dismiss the panel out from under the dialog.
        /// </summary>
        public bool SuppressDeactivateClose { get; set; }

        /// <summary>
        /// When the panel last closed. The overlay consults this to ignore a re-open triggered by
        /// the same click that dismissed it: clicking the overlay activates it, which deactivates
        /// the panel and closes it, and the click would then immediately reopen it.
        /// </summary>
        public static DateTime LastClosedUtc { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// True while this panel is an unfocused hover dropdown. Cleared on activation, at
        /// which point normal dismiss-on-deactivate behaviour takes over.
        /// </summary>
        public bool IsHoverMode { get; private set; }

        /// <summary>Limits the visible cards to one section (hover) or shows all (click).</summary>
        public void SetFilter(PanelSection section) => _vm.Filter = section;

        /// <summary>Re-anchors the panel; used when the hover dropdown moves between modules.</summary>
        public void RefreshPosition() => Reposition();

        /// <summary>Turns a hover dropdown into the full pinned panel (user clicked the overlay).</summary>
        public void PromoteToPinned()
        {
            IsHoverMode = false;
            _vm.Filter = PanelSection.All;
            Activate();
        }

        public StatsPanelWindow(MetricsHistory history, AppConfig config, IntPtr ownerHwnd,
                                Func<Win32Helper.RECT?> getAnchorRect, bool hoverMode = false)
        {
            InitializeComponent();

            _ownerHwnd = ownerHwnd;
            _getAnchorRect = getAnchorRect;

            // A hover dropdown must not steal focus from whatever the user is doing. Clicking
            // inside it activates it, which promotes it to a normal pinned panel.
            IsHoverMode = hoverMode;
            ShowActivated = !hoverMode;

            _vm = new StatsPanelViewModel(history, config);
            DataContext = _vm;

            // Create the HWND without showing, so ownership and position can be applied before the
            // first frame is presented and the panel never appears in the wrong place.
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            if (_ownerHwnd != IntPtr.Zero) helper.Owner = _ownerHwnd;

            ApplyWindowAppearance(helper.Handle);

            // SizeChanged passes its fresh layout size: the native window still has the OLD
            // bounds at that moment, so positioning from GetWindowRect there used a stale
            // height — with a bottom taskbar that height feeds "top = anchor - gap - height"
            // directly and the panel landed at the wrong vertical position.
            SourceInitialized += (s, e) => Reposition();
            SizeChanged += (s, e) => Reposition(e.NewSize);
            DpiChanged += (s, e) => Reposition();
            ContentRendered += (s, e) => Reposition();
            Deactivated += OnDeactivated;
            PreviewKeyDown += OnPreviewKeyDown;
            Activated += (s, e) => IsHoverMode = false; // clicking a hover dropdown pins it
            Closed += (s, e) => { LastClosedUtc = DateTime.UtcNow; _vm.IsLive = false; _vm.Dispose(); };

            _vm.IsLive = true;
        }

        private static void ApplyWindowAppearance(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                // Rounded corners and a system drop shadow. Only reachable because the window is
                // not layered — a per-pixel-alpha window can never be rounded by DWM.
                int corner = Win32Helper.DWMWCP_ROUND;
                Win32Helper.DwmSetWindowAttribute(hwnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int dark = 1;
                Win32Helper.DwmSetWindowAttribute(hwnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }

        /// <summary>
        /// Places the panel against the overlay, flipping above or below using the same rule the
        /// overlay's native context menu applies, and clamping to the monitor's work area.
        /// </summary>
        public void Reposition() => Reposition(null);

        private void Reposition(System.Windows.Size? freshDipSize)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var anchor = _getAnchorRect();
            if (anchor == null) return;
            Win32Helper.RECT a = anchor.Value;

            // Everything below is physical pixels. The scale comes from the ANCHOR's monitor, not
            // this window's, so a mixed-DPI setup anchors correctly.
            IntPtr reference = _ownerHwnd != IntPtr.Zero ? _ownerHwnd : hwnd;
            uint dpi = GetDpiForWindow(reference);
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;

            int gap = (int)Math.Round(AnchorGapDip * scale);

            IntPtr mon = MonitorFromWindow(reference, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref mi)) return;
            Win32Helper.RECT work = mi.rcWork;

            // Never let the panel outgrow the work area: on short screens the fixed 880-DIP cap
            // exceeded it, and after clamping the panel's bottom vanished behind the taskbar.
            uint selfDpi = GetDpiForWindow(hwnd);
            if (selfDpi == 0) selfDpi = dpi;
            double selfScale = selfDpi / 96.0;
            double workDipCap = Math.Max(200, (work.Bottom - work.Top - 2 * gap) / selfScale);
            double targetMax = Math.Min(880, workDipCap);
            if (Math.Abs(MaxHeight - targetMax) > 0.5) MaxHeight = targetMax; // re-layout re-runs Reposition

            // The window's size, freshest source first: during SizeChanged the native window
            // still carries its previous bounds, so the layout size must win over GetWindowRect
            // or the flip-above equation positions with a stale height.
            int w, h;
            double dipW = freshDipSize?.Width ?? ActualWidth;
            double dipH = freshDipSize?.Height ?? ActualHeight;
            if (!double.IsNaN(dipW) && !double.IsNaN(dipH) && dipW > 0 && dipH > 0)
            {
                w = (int)Math.Round(dipW * selfScale);
                h = (int)Math.Round(Math.Min(dipH, MaxHeight) * selfScale);
            }
            else if (GetWindowRect(hwnd, out Win32Helper.RECT self))
            {
                w = self.Right - self.Left;
                h = self.Bottom - self.Top;
            }
            else return;
            if (w <= 0 || h <= 0) return;

            // Flip above when the overlay sits in the lower half of the work area, matching the
            // context menu's behaviour so both popups feel the same.
            bool openUpward = a.Top > (work.Top + work.Bottom) / 2;
            int y = openUpward ? a.Top - gap - h : a.Bottom + gap;

            // Centre horizontally on the overlay, then keep the whole panel on screen.
            int x = a.Left + ((a.Right - a.Left) - w) / 2;
            if (x + w > work.Right) x = work.Right - w;
            if (x < work.Left) x = work.Left;
            if (y + h > work.Bottom) y = work.Bottom - h;
            if (y < work.Top) y = work.Top;

            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DismissNow();
            }
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (SuppressDeactivateClose || _closingForGood) return;
            DismissNow();
        }

        /// <summary>Closes the panel and stamps the dismissal time for the re-open guard.</summary>
        public void DismissNow()
        {
            if (_closingForGood) return;
            _closingForGood = true;
            LastClosedUtc = DateTime.UtcNow;
            try { Close(); } catch { }
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public Win32Helper.RECT rcMonitor;
            public Win32Helper.RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Win32Helper.RECT r);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    }
}
