using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Capture;

// System.Drawing and System.Windows.Forms are in global scope (UseWindowsForms + ImplicitUsings),
// and both define these names. Bind them to the WPF types, as RegionSelectorWindow does.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Kil0bitSystemMonitor.Capture
{
    /// <summary>
    /// The small card shown while a scrolling capture runs (scrolling capture spec 3): how tall
    /// the joined image is so far, and how to stop.
    ///
    /// <para>
    /// It sits outside the picked area (<see cref="ScrollStatusPlacement"/>), so no grab sees it,
    /// and it is also excluded from capture in case the only free spot overlaps the area. It
    /// never takes focus and lets clicks through: the view being scrolled must keep receiving
    /// the wheel. While the capture runs it holds Esc as a temporary global hotkey on its own
    /// window, released by <see cref="ReleaseEsc"/> and again when the card closes.
    /// </para>
    /// </summary>
    public sealed class ScrollStatusWindow : Window
    {
        /// <summary>The card's size before layout, only for the first placement.</summary>
        private const double CardWidthGuessDip = 320, CardHeightGuessDip = 58;

        private const char Dot = (char)0x00B7;

        private readonly PixelRect _area;
        private readonly PixelRect _work;
        private readonly double _scale;
        private readonly TextBlock _status;
        private IntPtr _hwnd;
        private HwndSource? _source;
        private Action? _onEsc;
        private bool _escHeld;

        private ScrollStatusWindow(PixelRect area, PixelRect work, double scale)
        {
            _area = area;
            _work = work;
            _scale = scale > 0 ? scale : 1.0;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;           // the scrolled view keeps the focus
            // Sized to its text, and placed again whenever that size changes (SizeChanged below).
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "MicaStats scrolling capture";

            _status = new TextBlock
            {
                Text = TopText(escHeld: true),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFA)),
            };
            Content = BuildCard(_status);

            SourceInitialized += (s, e) => OnSourceInitialized();
            Loaded += (s, e) => Reposition();
            SizeChanged += (s, e) => Reposition();
            // Moving onto a monitor of another scale resizes the card; place it again once laid out.
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);
            Closed += (s, e) => ReleaseEsc();
        }

        /// <summary>
        /// Shows the card beside <paramref name="area"/>, on the monitor holding the area's centre.
        /// Must be called on the UI thread.
        /// </summary>
        public static ScrollStatusWindow Open(PixelRect area)
        {
            var monitor = CaptureGeometry.MonitorAt(ScreenCaptureEngine.GetMonitors(),
                area.X + area.Width / 2, area.Y + area.Height / 2);
            var work = monitor?.WorkArea ?? ScreenCaptureEngine.VirtualBounds();

            var card = new ScrollStatusWindow(area, work, monitor?.Scale ?? 1.0);
            card.Show();
            return card;
        }

        /// <summary>
        /// Holds Esc as a global hotkey that calls <paramref name="onEsc"/>. Returns false when
        /// another application already owns Esc; the card then says the capture stops at the end.
        /// </summary>
        public bool HoldEsc(Action onEsc)
        {
            if (_escHeld) return true;

            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (_source == null)
            {
                _source = HwndSource.FromHwnd(_hwnd);
                _source?.AddHook(WndProc);
            }
            _onEsc = onEsc;
            _escHeld = RegisterHotKey(_hwnd, EscHotkeyId, MOD_NOREPEAT, VK_ESCAPE);
            _status.Text = TopText(_escHeld);
            return _escHeld;
        }

        /// <summary>Releases Esc. Safe to call more than once; never throws.</summary>
        public void ReleaseEsc()
        {
            _onEsc = null;
            if (_escHeld)
            {
                _escHeld = false;
                try { UnregisterHotKey(_hwnd, EscHotkeyId); } catch { }
            }
            if (_source != null)
            {
                try { _source.RemoveHook(WndProc); } catch { }
                _source = null;
            }
        }

        /// <summary>Shows the height joined so far.</summary>
        public void ShowProgress(int height) => _status.Text = ProgressText(height, _escHeld);

        /// <summary>"Scrolling capture: 3,400 px · Esc to stop", grouped the same in every culture.</summary>
        internal static string ProgressText(int height, bool escHeld) =>
            string.Create(CultureInfo.InvariantCulture, $"Scrolling capture: {height:N0} px {Dot} {StopHint(escHeld, cancels: false)}");

        /// <summary>Shown while the view is scrolled to the top, before anything is joined.</summary>
        internal static string TopText(bool escHeld) =>
            $"Scrolling capture: going to the top {Dot} {StopHint(escHeld, cancels: true)}";

        private static string StopHint(bool escHeld, bool cancels) =>
            !escHeld ? "Stops at the end of the page" : cancels ? "Esc to cancel" : "Esc to stop";

        private static UIElement BuildCard(TextBlock status)
        {
            var stack = new StackPanel();
            stack.Children.Add(status);
            stack.Children.Add(new TextBlock
            {
                Text = "Keep the area on screen until it finishes",
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xDD, 0xE3, 0xEA)),
                Margin = new Thickness(0, 2, 0, 0),
            });

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x14, 0x1C)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD2, 0xE4)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 9),
                Child = stack,
            };
        }

        private void OnSourceInitialized()
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd == IntPtr.Zero) return;

            int ex = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE);
            ex |= (int)(Win32Helper.WS_EX_NOACTIVATE | Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TRANSPARENT);
            Win32Helper.SetWindowLongPtr(_hwnd, Win32Helper.GWL_EXSTYLE, new IntPtr(ex));

            // Belt and braces: the card is placed outside the area, but the corner fallback can
            // overlap it. Windows 10 2004 and later leave an excluded window out of every grab.
            try { SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }

            // A first placement from the expected size, in physical pixels on the area's monitor,
            // so the card appears there; Reposition corrects it with the real size once laid out.
            var guess = ScrollStatusPlacement.Place(_area, _work,
                (int)Math.Round(CardWidthGuessDip * _scale), (int)Math.Round(CardHeightGuessDip * _scale));
            Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, guess.X, guess.Y, 0, 0,
                Win32Helper.SWP_NOSIZE | SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
        }

        /// <summary>Moves the card to its place for its real pixel size.</summary>
        private void Reposition()
        {
            if (_hwnd == IntPtr.Zero || !Win32Helper.GetWindowRect(_hwnd, out var r)) return;
            if (r.Width <= 0 || r.Height <= 0) return;

            var at = ScrollStatusPlacement.Place(_area, _work, r.Width, r.Height);
            if (at.X == r.Left && at.Y == r.Top) return;
            Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, at.X, at.Y, 0, 0,
                Win32Helper.SWP_NOSIZE | SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == EscHotkeyId)
            {
                handled = true;
                _onEsc?.Invoke();
            }
            return IntPtr.Zero;
        }

        private const int WM_HOTKEY = 0x0312;
        /// <summary>Hotkey ids are per window; any id in the application range will do.</summary>
        private const int EscHotkeyId = 0xA5C0;
        private const uint VK_ESCAPE = 0x1B;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    }
}
