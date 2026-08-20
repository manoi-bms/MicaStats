using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor
{
    public class OverlayWindow : IDisposable
    {
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private readonly WndProcDelegate _wndProc = null!;
        private IntPtr _hWnd;
        private IntPtr _hIcon;

        private readonly MainViewModel _viewModel = null!;
        private readonly ConfigService _config = null!;
        private readonly TelemetryService _telemetry = null!;
        private readonly MetricsHistory _history = null!;
        private readonly System.Windows.Threading.Dispatcher _dispatcher = null!;
        private readonly System.Threading.Timer _zOrderTimer = null!;

        private bool _isHovered = false;
        private bool _trackingMouse = false;

        // Tap-versus-drag state. The native move loop entered by WM_NCLBUTTONDOWN consumes the
        // terminating WM_LBUTTONUP, so a tap cannot be detected once that loop has started. The
        // press is therefore captured first and the loop is entered only after real movement.
        private bool _pressPending;
        private Win32Helper.POINT _pressAnchor;

        // Hover-dropdown state. Zones are the per-module hit ranges of the stacked layout,
        // rebuilt on every repaint in client-pixel space (identical to the bitmap space).
        private readonly System.Collections.Generic.List<(PanelSection Section, float X, float W)> _moduleZones = new();
        private PanelSection _pendingHoverSection = PanelSection.All;
        private System.Windows.Threading.DispatcherTimer? _hoverDwellTimer;
        private bool _shellFullscreen = false;
        private int _stackedFitLevel;   // StackedFitPlanner hysteresis state
        private bool _inSizeMove;       // inside the native move loop: avoidance must not fight the drag
        private float? _testWidthCapPx = null; // render-harness override (reflection) for the Start-menu width cap
        private bool _appbarRegistered = false;
        private readonly Action? _onHistoryUpdated;
        private readonly System.ComponentModel.PropertyChangedEventHandler? _onConfigPropertyChanged;
        private uint _currentDpi = 96;
        private float _dpiScale = 1.0f;

        // Visibility / fade state
        private byte _currentAlpha = 255;
        private byte _targetAlpha = 255;
        private bool _overlayVisible = true;
        private System.Windows.Threading.DispatcherTimer? _fadeTimer;
        private System.Windows.Threading.DispatcherTimer? _hideDebounceTimer;

        private readonly System.Collections.Generic.Dictionary<string, Font> _fontCache = new();
        private readonly System.Collections.Generic.Dictionary<string, float> _measureCache = new();
        private Brush? _cachedBgBrush;
        private Brush? _cachedAccentBrush;
        private Brush? _cachedLabelBrush;
        private Pen? _cachedHoverPen;
        private Brush? _cachedHoverBrush;
        private Brush? _cachedPodBrush;
        // Per-section label brushes (null = use _cachedLabelBrush)
        private Brush? _cachedNetLabelBrush;
        private Brush? _cachedCpuRamLabelBrush;
        private Brush? _cachedGpuLabelBrush;
        private Brush? _cachedDiskLabelBrush;
        // Per-section accent/metric brushes (null = use _cachedAccentBrush)
        private Brush? _cachedNetAccentBrush;
        private Brush? _cachedCpuRamAccentBrush;
        private Brush? _cachedGpuAccentBrush;
        private Brush? _cachedDiskAccentBrush;
        private Bitmap? _offscreenBitmap;
        private Graphics? _offscreenGraphics;

        // Text measurement must not depend on the render buffer. Column widths are computed
        // before EnsureOffscreenBuffer runs, so measuring against _offscreenGraphics returned 0
        // for every string on the first pass and clamped the window to a ~46px sliver until the
        // first telemetry sample arrived — permanently, if telemetry init threw.
        private Bitmap? _measureBitmap;
        private Graphics? _measureGraphics;

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WS_POPUP = 0x80000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_RBUTTONUP = 0x0205;
        private const int HTCAPTION = 2;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int WM_CANCELMODE = 0x001F;
        private const int MK_LBUTTON = 0x0001;
        private const int SM_CXDRAG = 68;
        private const int SM_CYDRAG = 69;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint TME_LEAVE = 0x00000002;
        public const int WM_SETICON = 0x0080;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL = 0;
        public const int WM_SHOW_SETTINGS = 0x0501;
        private const uint WM_APPBAR_CALLBACK = 0x0502;
        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABN_FULLSCREENAPP = 0x00000002;
        private const uint ABM_WINDOWPOSCHANGED = 0x00000009;
        private const uint GW_HWNDPREV = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd; public IntPtr hwndInsertAfter; public int x; public int y; public int cx; public int cy; public uint flags; }

        public OverlayWindow(MainViewModel viewModel, ConfigService config, TelemetryService telemetry, MetricsHistory history)
        {
            try
            {
                _viewModel = viewModel;
                _config = config;
                _telemetry = telemetry;
                _history = history;
                _dispatcher = System.Windows.Application.Current.Dispatcher;
                _wndProc = WndProc;

                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.style = 0x0008;
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = "Kil0bitOverlayWndClass_Main";
                wc.hCursor = LoadCursor(IntPtr.Zero, 32512);

                try
                {
                    string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.png");
                    if (System.IO.File.Exists(iconPath))
                    {
                        using (var bmp = new System.Drawing.Bitmap(iconPath)) _hIcon = bmp.GetHicon();
                    }
                }
                catch { }

                wc.hIcon = _hIcon;
                wc.hIconSm = _hIcon;
                RegisterClassEx(ref wc);

                int x = (int)_config.Config.X;
                int y = (int)_config.Config.Y;
                if (x < -10000 || x > 10000 || y < -10000 || y > 10000) { x = 100; y = 100; }

                _hWnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Kil0bitOverlayWndClass_Main", "Kil0bit System Monitor Overlay", WS_POPUP, x, y, 300, 32, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hWnd == IntPtr.Zero) throw new Exception("Failed to create window");

                if (_hIcon != IntPtr.Zero) { SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, _hIcon); SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, _hIcon); }

                _currentDpi = GetDpiForWindow(_hWnd);
                if (_currentDpi == 0) _currentDpi = 96;
                _dpiScale = _currentDpi / 96.0f;

                // Created before the first UpdateLayer so text can be measured on frame one.
                _measureBitmap = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _measureGraphics = Graphics.FromImage(_measureBitmap);
                _measureGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Disable DWM animations to prevent flickering during Task View zoom transitions
                int disableTransitions = 1;
                Win32Helper.DwmSetWindowAttribute(_hWnd, 3, ref disableTransitions, sizeof(int));

                // Snapping setup (Only attach parent handle if snapping is enabled at launch)
                if (_config.Config.StickToTaskbar)
                {
                    AttachToTaskbar();
                }
                else
                {
                    AlignToTaskbarCenter();
                }
                ShowWindow(_hWnd, 5);
                UpdateCachedColors();
                UpdateLayer();

                // MetricsHistory has already marshalled to the dispatcher and appended the sample,
                // so this runs on the UI thread and the history is current.
                _onHistoryUpdated = () => {
                    _viewModel.Metrics = _history.Latest;
                    // Only re-render if visible or transitioning
                    if (_targetAlpha > 0 || _currentAlpha > 0) UpdateLayer();
                };
                _history.Updated += _onHistoryUpdated;
                _zOrderTimer = new System.Threading.Timer(EnforceZOrder, null, 0, 500);

                _onConfigPropertyChanged = (s, e) => {
                    _dispatcher.BeginInvoke(() => {
                        if (e.PropertyName == nameof(_config.Config.AccentColorHex) || e.PropertyName == nameof(_config.Config.LabelColorHex) || e.PropertyName == nameof(_config.Config.BackgroundColorHex) || e.PropertyName == nameof(_config.Config.PodColorHex) || e.PropertyName == nameof(_config.Config.FontFamily)
                            || e.PropertyName == nameof(_config.Config.NetLabelColorHex) || e.PropertyName == nameof(_config.Config.CpuRamLabelColorHex) || e.PropertyName == nameof(_config.Config.GpuLabelColorHex) || e.PropertyName == nameof(_config.Config.DiskLabelColorHex)
                            || e.PropertyName == nameof(_config.Config.NetAccentColorHex) || e.PropertyName == nameof(_config.Config.CpuRamAccentColorHex) || e.PropertyName == nameof(_config.Config.GpuAccentColorHex) || e.PropertyName == nameof(_config.Config.DiskAccentColorHex))
                        {
                            ClearCaches();
                            UpdateCachedColors();
                        }
                        if (e.PropertyName == nameof(_config.Config.StickToTaskbar))
                        {
                            // Snapping toggle handled dynamically to clear/restore HWND parent
                            if (_config.Config.StickToTaskbar)
                            {
                                AttachToTaskbar();
                            }
                            else
                            {
                                // Remove taskbar owner so it floats freely
                                Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_HWNDPARENT, IntPtr.Zero);
                                UnregisterAppBar();
                                AlignToTaskbarCenter();
                            }
                        }
                        if (e.PropertyName == nameof(_config.Config.ShowOverlay) || e.PropertyName == nameof(_config.Config.HideOnFullscreen) || e.PropertyName == nameof(_config.Config.StickToTaskbar) || e.PropertyName == nameof(_config.Config.ShowPods) || e.PropertyName == nameof(_config.Config.ShowBackground) || e.PropertyName == nameof(_config.Config.AlwaysOnTop))
                        {
                            UpdateVisibility();
                            // One-time Z-order update for smooth transition
                            IntPtr zOrder = _config.Config.AlwaysOnTop ? Win32Helper.HWND_TOPMOST : Win32Helper.HWND_NOTOPMOST;
                            SetWindowPos(_hWnd, zOrder, 0, 0, 0, 0, Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | 0x0040);
                        }
                        UpdateLayer();
                    });
                };
                _config.Config.PropertyChanged += _onConfigPropertyChanged;
                if (!_config.Config.ShowOverlay) { ShowWindow(_hWnd, 0); _overlayVisible = false; _currentAlpha = 0; _targetAlpha = 0; }
            }
            catch { throw; }
        }

        private void EnforceZOrder(object? state)
        {
            _dispatcher.BeginInvoke(() =>
            {
                bool show = ShouldShowOverlay();

                if (show)
                {
                    _hideDebounceTimer?.Stop();
                    if (_targetAlpha != 255) { _targetAlpha = 255; StartFade(); }
                }
                else
                {
                    // Debounce hide by 800ms to prevent flickering during shell animations
                    if (_hideDebounceTimer == null)
                    {
                        _hideDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                        _hideDebounceTimer.Tick += (s, e) => { _hideDebounceTimer.Stop(); if (!ShouldShowOverlay()) { _targetAlpha = 0; StartFade(); } };
                    }
                    if (!_hideDebounceTimer.IsEnabled && _targetAlpha != 0) _hideDebounceTimer.Start();
                }

                // Enforce TOPMOST Z-order only if the taskbar is not the foreground active window.
                // Re-asserting TOPMOST while the taskbar is active and managing its Z-order causes blinking.
                // However, we must enforce it when other windows (like Task View) are active to keep the overlay visible.
                if (_overlayVisible && _config.Config.AlwaysOnTop)
                {
                    IntPtr fg = GetForegroundWindow();
                    StringBuilder sb = new StringBuilder(256);
                    Win32Helper.GetClassName(fg, sb, sb.Capacity);
                    string fgClass = sb.ToString();

                    if (fgClass != "Shell_TrayWnd" && fgClass != "Shell_SecondaryTrayWnd")
                    {
                        // Smart check: Only re-assert TOPMOST if we are NOT already the top-most window.
                        IntPtr prev = GetWindow(_hWnd, GW_HWNDPREV);
                        if (prev != IntPtr.Zero)
                        {
                            SetWindowPos(_hWnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0, Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | 0x0040);
                        }
                    }
                }
            });
        }

        private void AttachToTaskbar()
        {
            IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (taskbarHwnd != IntPtr.Zero)
            {
                Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_HWNDPARENT, taskbarHwnd);
                RegisterAppBar();
                AlignToTaskbarCenter();
            }
        }

        private void RegisterAppBar() { if (_appbarRegistered || _hWnd == IntPtr.Zero) return; APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd, uCallbackMessage = WM_APPBAR_CALLBACK }; SHAppBarMessage(ABM_NEW, ref abd); _appbarRegistered = true; }
        private void UnregisterAppBar() { if (!_appbarRegistered || _hWnd == IntPtr.Zero) return; APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd }; SHAppBarMessage(ABM_REMOVE, ref abd); _appbarRegistered = false; }
        private void UpdateVisibility()
        {
            bool show = ShouldShowOverlay();
            if (show)
            {
                _hideDebounceTimer?.Stop();
                _targetAlpha = 255;
                StartFade();
            }
            else
            {
                if (_hideDebounceTimer == null)
                {
                    _hideDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _hideDebounceTimer.Tick += (s, e) => { _hideDebounceTimer.Stop(); if (!ShouldShowOverlay()) { _targetAlpha = 0; StartFade(); } };
                }
                if (!_hideDebounceTimer.IsEnabled && _targetAlpha != 0) _hideDebounceTimer.Start();
            }
        }

        // Starts the fade timer if not already running.
        private void StartFade()
        {
            if (_fadeTimer == null)
            {
                _fadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _fadeTimer.Tick += (s, e) => FadeTick();
            }
            if (!_fadeTimer.IsEnabled) _fadeTimer.Start();
        }

        // Steps _currentAlpha toward _targetAlpha at ~150ms for a full 0↔255 transition.
        private void FadeTick()
        {
            const int step = 30; // 255/30 ≈ 9 frames × 16ms ≈ 144ms
            if (_currentAlpha < _targetAlpha)
            {
                // Fading in — make sure window is shown before first pixel appears
                if (!_overlayVisible) { ShowWindow(_hWnd, 5); _overlayVisible = true; }
                _currentAlpha = (byte)Math.Min(255, _currentAlpha + step);
            }
            else if (_currentAlpha > _targetAlpha)
            {
                _currentAlpha = (byte)Math.Max(0, _currentAlpha - step);
            }

            // Reblit the existing bitmap with the new alpha — no re-render needed
            if (_offscreenBitmap != null) SetBitmap(_offscreenBitmap);

            if (_currentAlpha == _targetAlpha)
            {
                _fadeTimer!.Stop();
                // Only call ShowWindow(0) once we are fully transparent to avoid blink
                if (_currentAlpha == 0 && _overlayVisible)
                {
                    ShowWindow(_hWnd, 0);
                    _overlayVisible = false;

                    // Panel visibility is a strict subset of overlay visibility. Hiding an owner
                    // does not hide its owned windows, so without this the panel would keep
                    // rendering over a fullscreen game with its sampling still running.
                    App.ClosePanelIfOpen();
                }
            }
        }

        private bool ShouldShowOverlay()
        {
            if (!_config.Config.ShowOverlay) return false;

            if (_config.Config.HideOnFullscreen)
            {
                IntPtr fg = GetForegroundWindow();
                // Priority: If we are in the shell (Task View, Desktop), always show
                if (IsShellWindow(fg)) return true;

                // ABN_FULLSCREENAPP fired — shell says a fullscreen app is covering the taskbar
                if (_shellFullscreen) return false;

                // Taskbar rect collapsed = autohide triggered by a fullscreen/exclusive app
                IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
                if (taskbarHwnd != IntPtr.Zero && Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT tbRect))
                    if ((tbRect.Bottom - tbRect.Top) <= 4 || (tbRect.Right - tbRect.Left) <= 4) return false;

                // Fallback: check foreground window — catches windowed-fullscreen games that never
                // fire ABN_FULLSCREENAPP (most modern titles, browser F11, video players, etc.)
                // Exempt every window of our own process, not just the overlay: the detail panel
                // and settings window are separate HWNDs and would otherwise be measured against
                // the monitor rect as if they were a foreign fullscreen app.
                if (fg != IntPtr.Zero && !IsOwnProcessWindow(fg))
                {
                    // Maximized windows on no-taskbar displays cover the full monitor rect; require borderless to qualify as fullscreen.
                    const long WS_CAPTION = 0x00C00000L;
                    long style = Win32Helper.GetWindowLong(fg, Win32Helper.GWL_STYLE);
                    bool hasCaption = (style & WS_CAPTION) != 0;

                    // Check if foreground window covers the whole monitor
                    if (!hasCaption && Win32Helper.GetWindowRect(fg, out Win32Helper.RECT fgRect))
                    {
                        IntPtr hMon = MonitorFromWindow(fg, 1); // MONITOR_DEFAULTTONEAREST
                        MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                        if (GetMonitorInfo(hMon, ref mi))
                        {
                            var s = mi.rcMonitor;
                            if (fgRect.Left <= s.Left && fgRect.Top <= s.Top &&
                                fgRect.Right >= s.Right && fgRect.Bottom >= s.Bottom)
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>The overlay's window handle, used to anchor and own the detail panel.</summary>
        public IntPtr Handle => _hWnd;

        /// <summary>The overlay's screen rectangle in physical pixels, or null if unavailable.</summary>
        public Win32Helper.RECT? GetScreenRect()
            => _hWnd != IntPtr.Zero && Win32Helper.GetWindowRect(_hWnd, out var r) ? r : null;

        /// <summary>
        /// True when the window belongs to this process. Used to exempt our own panel and settings
        /// window from the fullscreen-detection heuristic, which would otherwise treat them as a
        /// foreign app and hide the overlay.
        /// </summary>
        private static bool IsOwnProcessWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hWnd, out uint pid);
            return pid != 0 && pid == (uint)Environment.ProcessId;
        }

        /// <summary>
        /// Whether the cursor has moved far enough from the press point to count as a drag.
        /// Uses the system drag threshold rather than a hard-coded value, scaled for this window's
        /// DPI because the non-DPI-aware GetSystemMetrics must not be used from a PerMonitorV2
        /// process.
        /// </summary>
        private bool ExceedsDragThreshold(Win32Helper.POINT now)
        {
            int cx = 4, cy = 4;
            try
            {
                int mx = GetSystemMetricsForDpi(SM_CXDRAG, _currentDpi);
                int my = GetSystemMetricsForDpi(SM_CYDRAG, _currentDpi);
                if (mx > 0) cx = mx;
                if (my > 0) cy = my;
            }
            catch { /* pre-1607 hosts lack the DPI-aware variant; the defaults stand in. */ }

            return Math.Abs(now.X - _pressAnchor.X) > cx || Math.Abs(now.Y - _pressAnchor.Y) > cy;
        }

        private bool IsShellWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(256);
            Win32Helper.GetClassName(hWnd, sb, sb.Capacity);
            string cls = sb.ToString();

            // Core Windows Shell and UWP overlay window classes
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                cls == "MultitaskingViewFrame" || cls == "TaskView" || cls == "Windows.UI.Core.CoreWindow" ||
                cls == "XamlExplorerViewHostWindow" || cls == "DesktopWindowXamlSource" ||
                cls == "Windows.UI.Input.InputSite.WindowClass" || cls == "PopupHost")
                return true;

            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0)
                {
                    IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProcess != IntPtr.Zero)
                    {
                        try
                        {
                            uint size = 1024;
                            StringBuilder buffer = new StringBuilder((int)size);
                            if (QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                            {
                                string fullPath = buffer.ToString();
                                string pname = System.IO.Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();
                                return (pname == "explorer" || pname == "shellexperiencehost" ||
                                        pname == "startmenuexperiencehost" || pname == "searchhost" ||
                                        pname == "dwm");
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                    else
                    {
                        // OpenProcess failed (Access Denied / protected process).
                        // Highly protected UWP/system processes (like StartMenuExperienceHost or SYSTEM processes)
                        // are definitely shell/system windows, not standard user apps or games.
                        if (Marshal.GetLastWin32Error() == 5) // ERROR_ACCESS_DENIED
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void AlignToTaskbarCenter()
        {
            if (!_config.Config.StickToTaskbar) { SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, (int)_config.Config.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); return; }
            IntPtr taskbar = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb))
            {
                int h = tb.Bottom - tb.Top;
                int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor);
                int cy = tb.Top + (h - oh) / 2;
                SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, cy, 0, 0, 0x0001 | 0x0004 | 0x0010);
                _config.Config.Y = cy;
            }
        }

        /// <summary>
        /// Which telemetry group a column belongs to, and therefore which per-section colour
        /// override applies. This must travel with the column: columns are only emitted for
        /// enabled sensors, so a column's position is not a reliable indicator of its section.
        /// </summary>
        private enum SectionKind { Net, CpuRam, Gpu, Disk }

        // Reserve: worst-case string to measure for stable column width. Null = use live Value width.
        private class MetricItem
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public string? Reserve { get; set; } = null;

            /// <summary>Samples to draw as a sparkline, or null for a metric with no useful history.</summary>
            public Series? History { get; set; }

            /// <summary>Full-scale value for the sparkline. 0 autoscales to the series peak.</summary>
            public float GraphMax { get; set; } = 100f;

            /// <summary>
            /// Current level 0-100 for the stacked layout's right-edge mini bar, or negative
            /// for none. Shown for CPU, RAM and GPU so the instant reading is visible at a
            /// glance even with graphs off.
            /// </summary>
            public float Level { get; set; } = -1f;

            /// <summary>
            /// One mini bar per entry instead of the single <see cref="Level"/> bar. Used by
            /// the combined storage zone, where each bar is one drive's activity.
            /// </summary>
            public float[]? MultiLevels { get; set; }
        }

        private sealed class MetricColumn
        {
            public SectionKind Kind { get; init; }
            public MetricItem? Top { get; init; }
            public MetricItem? Bottom { get; init; }

            /// <summary>Which hover dropdown this column belongs to. All = no dropdown.</summary>
            public PanelSection Panel { get; init; } = PanelSection.All;
        }

        private void UpdateLayer()
        {
            if (_targetAlpha == 0 && _currentAlpha == 0) return;
            if (_config.Config.StackedTaskbar) { UpdateLayerStacked(); return; }
            var columns = PrepareMetricsData();
            float scale = _dpiScale * (float)_config.Config.ScaleFactor;
            float textScale = (float)_config.Config.ScaleFactor;
            bool pods = _config.Config.ShowPods;
            string fontName = _config.Config.FontFamily;
            if (string.IsNullOrEmpty(fontName) || fontName == "Default") fontName = "Segoe UI";
            System.Drawing.FontStyle style = _config.Config.IsTextBold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular;
            Font font = GetCachedFont(fontName, 8.5f * textScale, style);

            int h = (int)((pods ? 36 : 32) * scale); // pods get 4px extra height for top/bottom breathing room
            float gap = 2 * scale;                          // label→value gap
            float podGap = Math.Max(0, _config.Config.ColumnSpacing) * scale;  // user-controlled column spacing
            float pad = (pods ? 4 : 0) * scale;             // pod inner horizontal padding

            // Graph slot geometry derives from the font height, not from `scale`. `scale` includes
            // _dpiScale while textScale does not, and the offscreen Graphics is always 96dpi, so a
            // scale-derived slot would drift away from the glyphs at 150%.
            bool graphs = _config.Config.ShowGraphs;
            float graphSlot = graphs ? MathF.Round(font.Height * 2.5f) : 0f;

            float[] widths = new float[columns.Count];
            float total = 2 * scale;                         // left outer margin
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                float GetItemWidth(MetricItem? item) {
                    if (item == null) return 0;
                    // Use the reserve string width when available so the column never resizes on value change
                    float valW = item.Reserve != null ? GetCachedMeasure(item.Reserve, font) : GetCachedMeasure(item.Value, font);
                    // A fixed-width graph slot keeps the total width constant, so enabling graphs
                    // does not make the window resize as values change.
                    float graphW = (graphs && item.History != null) ? graphSlot + gap : 0f;
                    return GetCachedMeasure(item.Label, font) + gap + graphW + valW;
                }

                widths[i] = Math.Max(GetItemWidth(col.Top), GetItemWidth(col.Bottom)) + (pad * 2);

                total += widths[i] + podGap;
            }
            total = total - podGap + (2 * scale);           // right outer margin (was 4)

            int w = (int)Math.Max(20, total);
            EnsureOffscreenBuffer(w, h);
            if (_offscreenGraphics == null || _offscreenBitmap == null) return;

            _offscreenGraphics.Clear(Color.Transparent);
            RenderBackground(_offscreenGraphics, w, h, scale);
            RenderHoverEffect(_offscreenGraphics, w, h, scale);

            Brush vBrush = _cachedAccentBrush ?? Brushes.White;
            Brush lBrush = _cachedLabelBrush ?? Brushes.Cyan;
            bool ownPBrush = _cachedPodBrush == null;
            Brush pBrush = _cachedPodBrush ?? new SolidBrush(Color.FromArgb(15, 255, 255, 255));
            using var pPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1);

            // Section brushes: fall back to global brush when per-section color is not set
            Brush netLBrush  = _cachedNetLabelBrush    ?? lBrush;
            Brush cpuLBrush  = _cachedCpuRamLabelBrush ?? lBrush;
            Brush gpuLBrush  = _cachedGpuLabelBrush    ?? lBrush;
            Brush dskLBrush  = _cachedDiskLabelBrush   ?? lBrush;
            Brush netVBrush  = _cachedNetAccentBrush    ?? vBrush;
            Brush cpuVBrush  = _cachedCpuRamAccentBrush ?? vBrush;
            Brush gpuVBrush  = _cachedGpuAccentBrush    ?? vBrush;
            Brush dskVBrush  = _cachedDiskAccentBrush   ?? vBrush;

            float cx = 2 * scale;                          // start drawing from left margin (was 4)
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (pods)
                {
                    using (var path = CreateRoundedRectPath((int)cx, (int)(2 * scale), (int)widths[i], (int)(h - 4 * scale), (int)(6 * scale)))
                    { _offscreenGraphics.FillPath(pBrush, path); _offscreenGraphics.DrawPath(pPen, path); }
                }

                // Pick brushes from the column's own section, not from its position. Columns are
                // only emitted for enabled sensors, so indexing by position shifted every
                // section's colour by one whenever an earlier section was switched off.
                Brush sectionLBrush = col.Kind switch
                {
                    SectionKind.Net => netLBrush,
                    SectionKind.CpuRam => cpuLBrush,
                    SectionKind.Gpu => gpuLBrush,
                    _ => dskLBrush,
                };
                Brush sectionVBrush = col.Kind switch
                {
                    SectionKind.Net => netVBrush,
                    SectionKind.CpuRam => cpuVBrush,
                    SectionKind.Gpu => gpuVBrush,
                    _ => dskVBrush,
                };

                float contentX = cx + pad;
                // Fix: calculate y positions so both text rows are fully contained within h
                float lineH = font.Height;
                float totalTextH = lineH * 2 + (2 * scale);
                float y1 = (h - totalTextH) / 2f;
                float y2 = y1 + lineH + (2 * scale);

                // Draw with the same StringFormat the widths were measured with. The default
                // format is 4-5px wider per string than GenericTypographic, and that discrepancy
                // was only absorbed by the pod padding.
                Action<MetricItem, float> drawItem = (item, y) => {
                    float lw = GetCachedMeasure(item.Label, font);
                    _offscreenGraphics.DrawString(item.Label, font, sectionLBrush, contentX, y, StringFormat.GenericTypographic);
                    float vx = contentX + lw + gap;
                    if (graphs && item.History != null)
                    {
                        DrawSparkline(_offscreenGraphics, item, vx, y, graphSlot, lineH, sectionVBrush);
                        vx += graphSlot + gap;
                    }
                    _offscreenGraphics.DrawString(item.Value, font, sectionVBrush, vx, y, StringFormat.GenericTypographic);
                };

                if (col.Top != null && col.Bottom != null)
                {
                    drawItem(col.Top, y1);
                    drawItem(col.Bottom, y2);
                }
                else
                {
                    var item = col.Top ?? col.Bottom;
                    if (item != null) drawItem(item, (h - font.Height) / 2f);
                }
                cx += widths[i] + podGap;
            }
            SetBitmap(_offscreenBitmap);
            if (ownPBrush) pBrush.Dispose();
        }

        private string FormatDiskSpeed(float kbps)
        {
            if (kbps >= 1024 * 1024) return $"{(kbps / 1024f / 1024f):F1} GB/s";
            if (kbps >= 1024f) return $"{(kbps / 1024f):F1} MB/s";
            return $"{kbps:F0} KB/s";
        }

        // The stacked (iStat) taskbar uses a fixed palette rather than the per-section colour
        // settings: dim grey labels, white values, cyan graphs, red for upload. Honouring
        // arbitrary user colours here would break the single cohesive theme the mode exists
        // to provide; the classic layout keeps full colour customisation.
        private static readonly Brush StackedLabelBrush = new SolidBrush(Color.FromArgb(0xB0, 0xA6, 0xAC, 0xB4));
        private static readonly Brush StackedValueBrush = new SolidBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        private static readonly Brush StackedGraphBrush = new SolidBrush(Color.FromArgb(0xFF, 0x3F, 0xD2, 0xE4));
        private static readonly Brush StackedUpBrush = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0x51, 0x47));
        private static readonly Brush StackedTrackBrush = new SolidBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF));

        /// <summary>
        /// iStat-style layout: every metric is its own module with a small dim label stacked
        /// above a bold value, network as paired ↑/↓ lines with a mirrored graph. Graph slots
        /// span the full text-block height, so sparklines get roughly twice the resolution of
        /// the classic single-row slot.
        /// </summary>
        private void UpdateLayerStacked()
        {
            var columns = PrepareStackedColumns();
            float scale = _dpiScale * (float)_config.Config.ScaleFactor;
            float textScale = (float)_config.Config.ScaleFactor;
            bool pods = _config.Config.ShowPods;
            string fontName = _config.Config.FontFamily;
            if (string.IsNullOrEmpty(fontName) || fontName == "Default") fontName = "Segoe UI";
            var valueStyle = _config.Config.IsTextBold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular;

            Font labelFont = GetCachedFont(fontName, 6.6f * textScale, System.Drawing.FontStyle.Regular);
            Font valueFont = GetCachedFont(fontName, 9.0f * textScale, valueStyle);
            Font netFont = GetCachedFont(fontName, 7.6f * textScale, valueStyle);

            int h = (int)((pods ? 36 : 32) * scale);
            float gap = 3 * scale;
            float podGap = Math.Max(0, _config.Config.ColumnSpacing) * scale;
            float pad = (pods ? 4 : 2) * scale;
            bool graphs = _config.Config.ShowGraphs;
            float levelW = 4 * scale;      // right-edge mini level bar (CPU/RAM/GPU)
            float levelGap = 3 * scale;

            float textBlockH = labelFont.Height + valueFont.Height;
            float netBlockH = netFont.Height * 2 + (1 * scale);
            float graphH = Math.Max(textBlockH, netBlockH);
            float graphW = graphs ? MathF.Round(graphH * 1.5f) : 0f;

            float[] widths = new float[columns.Count];
            float[] graphParts = new float[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                float textW, graphPart = 0f, levelPart = 0f;
                if (col.Kind == SectionKind.Net)
                {
                    // Live width with a typical-case floor (the Reserve string): worst-case
                    // reservations left dead space, but a pure live width made the module
                    // shrink and grow as digit counts changed, which read as flicker. The
                    // floor covers the common range so the zone only moves on genuine
                    // magnitude changes.
                    textW = Math.Max(FlooredWidth(col.Top, netFont), FlooredWidth(col.Bottom, netFont));
                    if (graphs && (col.Top?.History != null || col.Bottom?.History != null)) graphPart = graphW + gap;
                }
                else
                {
                    var item = (col.Top ?? col.Bottom)!;
                    textW = Math.Max(GetCachedMeasure(item.Label, labelFont), FlooredWidth(item, valueFont));
                    if (graphs && item.History != null) graphPart = graphW + gap;
                    int nBars = item.MultiLevels?.Length ?? (item.Level >= 0f ? 1 : 0);
                    if (nBars > 0) levelPart = levelGap + nBars * levelW + (nBars - 1) * (2 * scale);
                }
                widths[i] = textW + graphPart + levelPart + pad * 2;
                graphParts[i] = graphPart;
            }

            // Auto-avoid the taskbar's own buttons — but spend free space before shedding
            // content. The corridor is the gap between the obstacles flanking the overlay's
            // saved anchor (widgets button / Start button / tray); the overlay slides left
            // inside it to make room, and only when the whole corridor cannot hold a level
            // does the planner shed sparklines, then trailing modules. Position and level
            // freeze during a drag so the gesture never fights the avoidance.
            float margin = 6 * scale;
            float? cap = _testWidthCapPx;
            (float Left, float Right)? corridor = null;
            Win32Helper.RECT selfRect = default;
            bool frozen = _config.Config.AvoidStartMenu && (_inSizeMove || _pressPending);
            if (cap == null && _config.Config.AvoidStartMenu && !frozen && _hWnd != IntPtr.Zero
                && Win32Helper.GetWindowRect(_hWnd, out selfRect))
            {
                IntPtr tray = Win32Helper.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero && Win32Helper.GetWindowRect(tray, out Win32Helper.RECT trayRect))
                {
                    corridor = StackedFitPlanner.Corridor(
                        (float)_config.Config.X, selfRect.Top, selfRect.Bottom, trayRect,
                        TaskbarButtonsLocator.GetWidgetsRect(),
                        TaskbarButtonsLocator.GetStartButtonRect(),
                        TaskbarButtonsLocator.GetTrayNotifyRect());
                    if (corridor is (float cLeft, float cRight))
                        cap = Math.Max(20, cRight - cLeft - 2 * margin);
                }
            }

            var plan = frozen
                ? StackedFitPlanner.FitAtLevel(widths, graphParts, podGap, 4 * scale, _stackedFitLevel)
                : StackedFitPlanner.Fit(widths, graphParts, podGap, 4 * scale, cap, _stackedFitLevel, 24 * scale);

            // When modules must hide, re-plan with room for a trailing "⋯" marker so elision
            // is visible rather than looking like a dead sensor. The restore slack exceeds
            // the marker's width, so the marker cannot itself cause level flapping.
            float ellipsisW = 0f;
            if (plan.VisibleColumns < columns.Count && cap is float capValue)
            {
                ellipsisW = GetCachedMeasure("⋯", valueFont) + pad;
                plan = StackedFitPlanner.Fit(widths, graphParts, podGap, 4 * scale,
                    Math.Max(20, capValue - ellipsisW), plan.Level, 24 * scale);
            }
            if (plan.VisibleColumns >= columns.Count) ellipsisW = 0f;
            _stackedFitLevel = plan.Level;
            bool drawGraphs = graphs && plan.ShowGraphs;
            if (!plan.ShowGraphs)
                for (int i = 0; i < widths.Length; i++) widths[i] -= graphParts[i];

            int w = (int)Math.Max(20, plan.Width + ellipsisW);

            // Slide inside the corridor: hold the saved anchor while it fits, give ground
            // leftward as the Start button encroaches, drift home when it retreats. The
            // anchor in config is never rewritten, so the user's chosen spot is permanent.
            if (corridor is (float lo0, float hi0))
            {
                float lo = lo0 + margin;
                float hi = Math.Max(lo, hi0 - margin - w);
                int targetX = (int)Math.Round(Math.Clamp((float)_config.Config.X, lo, hi));
                if (targetX != selfRect.Left)
                    SetWindowPos(_hWnd, IntPtr.Zero, targetX, selfRect.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
            }
            EnsureOffscreenBuffer(w, h);
            if (_offscreenGraphics == null || _offscreenBitmap == null) return;

            _offscreenGraphics.Clear(Color.Transparent);
            RenderBackground(_offscreenGraphics, w, h, scale);
            RenderHoverEffect(_offscreenGraphics, w, h, scale);

            bool ownPBrush = _cachedPodBrush == null;
            Brush pBrush = _cachedPodBrush ?? new SolidBrush(Color.FromArgb(15, 255, 255, 255));
            _moduleZones.Clear();
            float cx = 2 * scale;
            for (int i = 0; i < plan.VisibleColumns; i++)
            {
                var col = columns[i];
                if (pods)
                {
                    // Fill only — the hairline outline read as clutter at this size.
                    using (var path = CreateRoundedRectPath((int)cx, (int)(2 * scale), (int)widths[i], (int)(h - 4 * scale), (int)(6 * scale)))
                        _offscreenGraphics.FillPath(pBrush, path);
                }

                float contentX = cx + pad;

                if (col.Kind == SectionKind.Net)
                {
                    if (drawGraphs && (col.Top?.History != null || col.Bottom?.History != null))
                    {
                        float gy = (h - graphH) / 2f;
                        float max = col.Top?.GraphMax ?? col.Bottom?.GraphMax ?? 1f;
                        DrawMirroredSparkline(_offscreenGraphics, col.Top?.History, col.Bottom?.History,
                            contentX, gy, graphW, graphH, StackedUpBrush, StackedGraphBrush, max);
                        contentX += graphW + gap;
                    }

                    // A single enabled direction centres alone; two stack around the midline.
                    if (col.Top != null && col.Bottom != null)
                    {
                        float ty = (h - netBlockH) / 2f;
                        _offscreenGraphics.DrawString(col.Top.Value, netFont, StackedUpBrush, contentX, ty, StringFormat.GenericTypographic);
                        _offscreenGraphics.DrawString(col.Bottom.Value, netFont, StackedGraphBrush, contentX, ty + netFont.Height + (1 * scale), StringFormat.GenericTypographic);
                    }
                    else
                    {
                        var single = col.Top ?? col.Bottom;
                        if (single != null)
                        {
                            Brush b = col.Top != null ? StackedUpBrush : StackedGraphBrush;
                            _offscreenGraphics.DrawString(single.Value, netFont, b, contentX, (h - netFont.Height) / 2f, StringFormat.GenericTypographic);
                        }
                    }
                }
                else
                {
                    var item = (col.Top ?? col.Bottom)!;
                    if (drawGraphs && item.History != null)
                    {
                        float gy = (h - graphH) / 2f;
                        DrawSparklineRect(_offscreenGraphics, item.History, item.GraphMax, contentX, gy, graphW, graphH, StackedGraphBrush);
                        contentX += graphW + gap;
                    }

                    float ty = (h - textBlockH) / 2f;
                    _offscreenGraphics.DrawString(item.Label, labelFont, StackedLabelBrush, contentX, ty, StringFormat.GenericTypographic);
                    _offscreenGraphics.DrawString(item.Value, valueFont, StackedValueBrush, contentX, ty + labelFont.Height, StringFormat.GenericTypographic);

                    int barCount = item.MultiLevels?.Length ?? (item.Level >= 0f ? 1 : 0);
                    if (barCount > 0)
                    {
                        // Right-edge mini level bars: dark tracks filled bottom-up with the
                        // instant reading. CPU/RAM/GPU carry one; the storage zone carries
                        // one per drive.
                        float miniGap = 2 * scale;
                        float barsW = barCount * levelW + (barCount - 1) * miniGap;
                        float bx = cx + widths[i] - pad - barsW;
                        float bh = textBlockH;
                        float by = (h - bh) / 2f;
                        var prevMode = _offscreenGraphics.SmoothingMode;
                        _offscreenGraphics.SmoothingMode = SmoothingMode.None;
                        for (int b = 0; b < barCount; b++)
                        {
                            float level = item.MultiLevels != null ? item.MultiLevels[b] : item.Level;
                            float fill = Math.Max(1f, Math.Clamp(level, 0f, 100f) / 100f * bh);
                            float x0 = bx + b * (levelW + miniGap);
                            _offscreenGraphics.FillRectangle((SolidBrush)StackedTrackBrush, x0, by, levelW, bh);
                            _offscreenGraphics.FillRectangle((SolidBrush)StackedGraphBrush, x0, by + bh - fill, levelW, fill);
                        }
                        _offscreenGraphics.SmoothingMode = prevMode;
                    }
                }
                _moduleZones.Add((col.Panel, cx, widths[i]));
                cx += widths[i] + podGap;
            }

            if (ellipsisW > 0f)
            {
                // Elision marker: everything hidden here is one click away in the stats panel.
                float ex = Math.Min(cx, w - GetCachedMeasure("⋯", valueFont) - 2 * scale);
                _offscreenGraphics.DrawString("⋯", valueFont, StackedLabelBrush,
                    ex, (h - valueFont.Height) / 2f, StringFormat.GenericTypographic);
            }
            SetBitmap(_offscreenBitmap);
            if (ownPBrush) pBrush.Dispose();
        }

        /// <summary>Column list for the stacked layout: one module per metric, network combined.</summary>
        private System.Collections.Generic.List<MetricColumn> PrepareStackedColumns()
        {
            var m = _viewModel.Metrics; var c = _config.Config;
            float netMax = _history.SharedNetPeak;

            MetricItem Item(string label, string value, string reserve, Series? hist, float max = 100f, float level = -1f)
                => new MetricItem { Label = label, Value = value, Reserve = reserve, History = hist, GraphMax = max, Level = level };

            var list = new System.Collections.Generic.List<MetricColumn>();

            // Reserve strings are FLOORS, not worst cases: wide enough that everyday digit
            // changes never move the layout, narrow enough to leave no dead space.
            if (c.ShowNetUp || c.ShowNetDown)
                list.Add(new MetricColumn {
                    Kind = SectionKind.Net,
                    Panel = PanelSection.Network,
                    Top = c.ShowNetUp ? Item("↑", "↑ " + m.NetUpText, "↑ 88.8 MB/s", _history.NetUp, netMax) : null,
                    Bottom = c.ShowNetDown ? Item("↓", "↓ " + m.NetDownText, "↓ 88.8 MB/s", _history.NetDown, netMax) : null,
                });

            if (c.ShowCpu)
                list.Add(new MetricColumn { Kind = SectionKind.CpuRam, Panel = PanelSection.Cpu, Top = Item("CPU", $"{(int)m.CpuUsage}%", "88%", _history.Cpu, level: m.CpuUsage) });
            if (c.ShowRam)
                list.Add(new MetricColumn { Kind = SectionKind.CpuRam, Panel = PanelSection.Memory, Top = Item("RAM", $"{(int)m.RamPercent}%", "88%", _history.Ram, level: m.RamPercent) });
            if (c.ShowGpu)
                list.Add(new MetricColumn { Kind = SectionKind.Gpu, Panel = PanelSection.Gpu, Top = Item("GPU", $"{(int)m.GpuUsage}%", "88%", _history.Gpu, level: m.GpuUsage) });
            if (c.ShowTemp)
            {
                // CPU package first (that is what a taskbar temperature means to people, and it
                // matches Core Temp when it is running), GPU sensor as the fallback. The hover
                // dropdown follows the source.
                float displayTemp = m.CpuTemperature > 0 ? m.CpuTemperature : m.GpuTemperature;
                string tempStr = displayTemp > 0 ? $"{(int)displayTemp}°" : "N/A";
                var tempPanel = m.CpuTemperature > 0 ? PanelSection.Cpu : PanelSection.Gpu;
                list.Add(new MetricColumn { Kind = SectionKind.Gpu, Panel = tempPanel, Top = Item("TMP", tempStr, "88°", _history.Temp) });
            }

            if ((c.ShowDisk || c.ShowDiskSpeed) && m.Disks != null && m.Disks.Count > 0)
            {
                // One compact zone for every drive: the text follows the busiest drive and the
                // right edge carries one mini bar per drive, so a machine with three drives
                // shows one module instead of three. The Disks hover dropdown has the detail.
                bool byActivity = c.ShowDiskSpeed;

                DiskMetric busiest = m.Disks[0];
                foreach (var d in m.Disks)
                {
                    float dv = byActivity ? d.ActivityPercent : d.SpacePercent;
                    float bv = byActivity ? busiest.ActivityPercent : busiest.SpacePercent;
                    if (dv > bv) busiest = d;
                }

                var levels = new float[m.Disks.Count];
                for (int i = 0; i < m.Disks.Count; i++)
                    levels[i] = byActivity ? m.Disks[i].ActivityPercent : m.Disks[i].SpacePercent;

                float value = byActivity ? busiest.ActivityPercent : busiest.SpacePercent;
                var zone = Item("DSK", $"{DriveLetter(busiest.Name)} {(int)value}%", "C 88%",
                                byActivity ? _history.Disk(busiest.Name) : null);
                zone.MultiLevels = levels;
                list.Add(new MetricColumn { Kind = SectionKind.Disk, Panel = PanelSection.Disks, Top = zone });
            }

            return list;
        }

        /// <summary>
        /// Dense full-height sparkline for the stacked layout. Bar sizing derives from the slot
        /// height so the taller slot gets thin iStat-style bars rather than scaled-up chunks.
        /// </summary>
        private void DrawSparklineRect(Graphics g, Series? series, float max, float x, float y, float w, float h, Brush brush)
        {
            if (series == null || w <= 0 || h <= 0) return;

            if (series.Availability != Availability.Value)
            {
                var prevMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                    g.DrawLine(pen, x, y + h - 1f, x + w, y + h - 1f);
                g.SmoothingMode = prevMode;
                return;
            }

            float barW = Math.Max(1f, MathF.Round(h / 12f));
            float barGap = Math.Max(1f, MathF.Round(barW / 2f));

            int n = Helpers.SparklineGeometry.Bars(series, w, h, max, barW, barGap, _barScratch);
            if (n <= 0) return;

            var rects = new RectangleF[n];
            for (int i = 0; i < n; i++)
            {
                var r = _barScratch[i];
                rects[i] = new RectangleF(x + r.X, y + r.Y, r.Width, r.Height);
            }

            var prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangles(brush, rects);
            g.SmoothingMode = prev;
        }

        /// <summary>
        /// Upload above and download below a dashed centre axis, one shared scale — the iStat
        /// network graph. Either series may be null when that direction is disabled.
        /// </summary>
        private void DrawMirroredSparkline(Graphics g, Series? up, Series? down, float x, float y, float w, float h, Brush upBrush, Brush downBrush, float max)
        {
            if (w <= 0 || h <= 0) return;

            float axis = MathF.Round(y + h / 2f);
            float half = h / 2f - 1f;
            if (half < 2f) return;

            float barW = Math.Max(1f, MathF.Round(half / 6f));
            float barGap = Math.Max(1f, MathF.Round(barW / 2f));

            var prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;

            if (up != null && up.Availability == Availability.Value)
            {
                int n = Helpers.SparklineGeometry.Bars(up, w, half, max, barW, barGap, _barScratch);
                if (n > 0)
                {
                    var rects = new RectangleF[n];
                    for (int i = 0; i < n; i++)
                    {
                        var r = _barScratch[i];
                        rects[i] = new RectangleF(x + r.X, axis - r.Height, r.Width, r.Height);
                    }
                    g.FillRectangles(upBrush, rects);
                }
            }

            if (down != null && down.Availability == Availability.Value)
            {
                int n = Helpers.SparklineGeometry.Bars(down, w, half, max, barW, barGap, _barScratch);
                if (n > 0)
                {
                    var rects = new RectangleF[n];
                    for (int i = 0; i < n; i++)
                    {
                        var r = _barScratch[i];
                        rects[i] = new RectangleF(x + r.X, axis + 1f, r.Width, r.Height);
                    }
                    g.FillRectangles(downBrush, rects);
                }
            }

            using (var axisPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            {
                axisPen.DashStyle = DashStyle.Dash;
                g.DrawLine(axisPen, x, axis, x + w, axis);
            }

            g.SmoothingMode = prev;
        }

        private System.Collections.Generic.List<MetricColumn> PrepareMetricsData()
        {
            bool compact = (_config.Config.DisplayStyle ?? "Text") == "Compact";
            var m = _viewModel.Metrics; var c = _config.Config;

            // Upload and download share one scale so a trickle of upload is not drawn as a
            // saturated link. 0 would autoscale each direction independently.
            float netMax = _history.SharedNetPeak;

            MetricItem Pct(string f, string cp, string v, Series? hist = null)  => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "100%", History = hist, GraphMax = 100f };
            MetricItem Temp(string f, string cp, string v, Series? hist = null) => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "100°", History = hist, GraphMax = 100f };
            // Reserve "1023 MB/s": widest net format before switching to GB/s (M glyph is wider than K)
            MetricItem Net(string f, string cp, string v, Series? hist = null)  => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "1023 MB/s", History = hist, GraphMax = netMax };

            var list = new System.Collections.Generic.List<MetricColumn>();

            if (c.ShowNetUp || c.ShowNetDown)
                list.Add(new MetricColumn {
                    Kind = SectionKind.Net,
                    Top = c.ShowNetUp ? Net("UP ", "U", m.NetUpText, _history.NetUp) : null,
                    Bottom = c.ShowNetDown ? Net("DN ", "D", m.NetDownText, _history.NetDown) : null,
                });

            if (c.ShowCpu || c.ShowRam)
                list.Add(new MetricColumn {
                    Kind = SectionKind.CpuRam,
                    Top = c.ShowCpu ? Pct("CPU", "C", $"{(int)m.CpuUsage}%", _history.Cpu) : null,
                    Bottom = c.ShowRam ? Pct("RAM", "R", $"{(int)m.RamPercent}%", _history.Ram) : null,
                });

            string tempStr = m.GpuTemperature > 0 ? $"{(int)m.GpuTemperature}°" : "N/A";
            if (c.ShowGpu || c.ShowTemp)
                list.Add(new MetricColumn {
                    Kind = SectionKind.Gpu,
                    Top = c.ShowGpu ? Pct("GPU", "G", $"{(int)m.GpuUsage}%", _history.Gpu) : null,
                    Bottom = c.ShowTemp ? Temp("TMP", "T", tempStr, _history.Temp) : null,
                });

            if (c.ShowDisk || c.ShowDiskSpeed)
            {
                if (m.Disks != null && m.Disks.Count > 0)
                {
                    foreach (var d in m.Disks)
                    {
                        // Clean name: "0 C: D:" -> "C"
                        string letter = d.Name;
                        int colonIdx = letter.IndexOf(':');
                        if (colonIdx > 0) letter = letter.Substring(colonIdx - 1, 1);
                        else if (letter.Length > 0) letter = letter.Substring(0, 1);

                        string cdkLabel = letter.ToUpper() + "DK";

                        list.Add(new MetricColumn {
                            Kind = SectionKind.Disk,
                            // Used-space barely moves, so it gets no sparkline; activity does.
                            Top = c.ShowDisk ? Pct(cdkLabel, letter, $"{(int)d.SpacePercent}%") : null,
                            Bottom = c.ShowDiskSpeed ? Pct("SPD", "S", $"{(int)d.ActivityPercent}%", _history.Disk(d.Name)) : null,
                        });
                    }
                }
            }

            return list;
        }

        // Reused across frames so drawing a sparkline allocates nothing in steady state.
        private readonly RectangleF[] _barScratch = new RectangleF[128];
        private RectangleF[]? _barsExact;

        /// <summary>
        /// Draws one metric's sparkline into the row's graph slot.
        /// </summary>
        /// <remarks>
        /// Bars, not a polyline: a 1px antialiased line is only ~17% solid ink at this size and
        /// reads as haze. Antialiasing is disabled for the bars and restored afterwards, because
        /// the offscreen Graphics persists across frames.
        /// </remarks>
        private void DrawSparkline(Graphics g, MetricItem item, float x, float y, float w, float rowH, Brush brush)
        {
            var series = item.History;
            if (series == null || w <= 0 || rowH <= 0) return;

            float h = Math.Max(3f, rowH - 3f);
            float top = y + (rowH - h) / 2f;

            // An unreadable sensor gets a baseline rule, never bars. Zero-height bars would look
            // identical to a genuinely idle sensor.
            if (series.Availability != Availability.Value)
            {
                var prev = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                    g.DrawLine(pen, x, top + h - 1f, x + w, top + h - 1f);
                g.SmoothingMode = prev;
                return;
            }

            float barW = Math.Max(1f, MathF.Round(h / 6f));
            float gap = Math.Max(1f, MathF.Round(barW / 2f));

            int n = Helpers.SparklineGeometry.Bars(series, w, h, item.GraphMax, barW, gap, _barScratch);
            if (n <= 0) return;

            // FillRectangles has no span overload, so keep an exact-length array. The bar count is
            // stable frame to frame, so this allocates once rather than per frame.
            if (_barsExact == null || _barsExact.Length != n) _barsExact = new RectangleF[n];
            for (int i = 0; i < n; i++)
            {
                var r = _barScratch[i];
                _barsExact[i] = new RectangleF(x + r.X, top + r.Y, r.Width, r.Height);
            }

            var previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangles(brush, _barsExact);
            g.SmoothingMode = previous;
        }

        private void EnsureHoverDwellTimer()
        {
            if (_hoverDwellTimer != null) return;
            _hoverDwellTimer = new System.Windows.Threading.DispatcherTimer
            {
                // Long enough that sweeping the pointer across the taskbar opens nothing.
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _hoverDwellTimer.Tick += (s, e) =>
            {
                _hoverDwellTimer!.Stop();
                var section = _pendingHoverSection;
                if (section == PanelSection.All) return;
                if (GetModuleScreenRect(section, out var rect))
                    App.ShowHoverPanel(section, rect, _config, this);
            };
        }

        private PanelSection HitTestModule(int clientX)
        {
            foreach (var zone in _moduleZones)
            {
                if (clientX >= zone.X && clientX < zone.X + zone.W) return zone.Section;
            }
            return PanelSection.All;
        }

        /// <summary>Screen rectangle spanning every module of one section (disks can span several).</summary>
        private bool GetModuleScreenRect(PanelSection section, out Win32Helper.RECT rect)
        {
            rect = default;
            if (!Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT wr)) return false;

            float left = float.MaxValue, right = float.MinValue;
            foreach (var zone in _moduleZones)
            {
                if (zone.Section != section) continue;
                left = Math.Min(left, zone.X);
                right = Math.Max(right, zone.X + zone.W);
            }
            if (left > right) return false;

            rect = new Win32Helper.RECT
            {
                Left = wr.Left + (int)left,
                Top = wr.Top,
                Right = wr.Left + (int)right,
                Bottom = wr.Bottom,
            };
            return true;
        }

        private void EnsureOffscreenBuffer(int w, int h)
        {
            if (_offscreenBitmap == null || _offscreenBitmap.Width != w || _offscreenBitmap.Height != h)
            {
                _offscreenGraphics?.Dispose(); _offscreenBitmap?.Dispose();
                _offscreenBitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _offscreenGraphics = Graphics.FromImage(_offscreenBitmap);
                _offscreenGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                _offscreenGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
        }

        private void RenderBackground(Graphics g, int w, int h, float s) { if (!_config.Config.ShowBackground || _cachedBgBrush == null) return; using (var p = CreateRoundedRectPath(0, 0, w, h, (int)(12 * s))) g.FillPath(_cachedBgBrush, p); }
        private void RenderHoverEffect(Graphics g, int w, int h, float s) { if (!_isHovered || _cachedHoverBrush == null || _cachedHoverPen == null) return; using (var p = CreateRoundedRectPath(0, 0, w - 1, h - 1, (int)(12 * s))) { g.FillPath(_cachedHoverBrush, p); g.DrawPath(_cachedHoverPen, p); } }
        private GraphicsPath CreateRoundedRectPath(int x, int y, int w, int h, int r) { GraphicsPath p = new GraphicsPath(); if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; } p.AddArc(x, y, r, r, 180, 90); p.AddArc(x + w - r, y, r, r, 270, 90); p.AddArc(x + w - r, y + h - r, r, r, 0, 90); p.AddArc(x, y + h - r, r, r, 90, 90); p.CloseFigure(); return p; }
        private Font GetCachedFont(string f, float s, System.Drawing.FontStyle st) { string k = $"{f}_{s}_{st}"; if (!_fontCache.TryGetValue(k, out var font)) { font = new Font(f, s, st); _fontCache[k] = font; } return font; }
        private void UpdateCachedColors()
        {
            _cachedBgBrush?.Dispose(); _cachedAccentBrush?.Dispose(); _cachedLabelBrush?.Dispose(); _cachedPodBrush?.Dispose(); _cachedHoverPen?.Dispose(); _cachedHoverBrush?.Dispose();
            _cachedNetLabelBrush?.Dispose(); _cachedCpuRamLabelBrush?.Dispose(); _cachedGpuLabelBrush?.Dispose(); _cachedDiskLabelBrush?.Dispose();
            _cachedNetAccentBrush?.Dispose(); _cachedCpuRamAccentBrush?.Dispose(); _cachedGpuAccentBrush?.Dispose(); _cachedDiskAccentBrush?.Dispose();
            _cachedBgBrush = new SolidBrush(HexToColor(_config.Config.BackgroundColorHex ?? "#B4141414"));
            _cachedAccentBrush = new SolidBrush(HexToColor(_config.Config.AccentColorHex ?? "#FFFFFF"));
            _cachedLabelBrush = new SolidBrush(HexToColor(_config.Config.LabelColorHex ?? "#00CCFF"));
            _cachedPodBrush = new SolidBrush(HexToColor(_config.Config.PodColorHex ?? "#0FFFFFFF"));
            _cachedHoverPen = new Pen(Color.FromArgb(20, 255, 255, 255));
            _cachedHoverBrush = new SolidBrush(Color.FromArgb(25, 255, 255, 255));
            // Per-section label brushes: only create if a custom color is set
            _cachedNetLabelBrush    = string.IsNullOrEmpty(_config.Config.NetLabelColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.NetLabelColorHex));
            _cachedCpuRamLabelBrush = string.IsNullOrEmpty(_config.Config.CpuRamLabelColorHex) ? null : new SolidBrush(HexToColor(_config.Config.CpuRamLabelColorHex));
            _cachedGpuLabelBrush    = string.IsNullOrEmpty(_config.Config.GpuLabelColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.GpuLabelColorHex));
            _cachedDiskLabelBrush   = string.IsNullOrEmpty(_config.Config.DiskLabelColorHex)   ? null : new SolidBrush(HexToColor(_config.Config.DiskLabelColorHex));
            // Per-section accent brushes: only create if a custom color is set
            _cachedNetAccentBrush    = string.IsNullOrEmpty(_config.Config.NetAccentColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.NetAccentColorHex));
            _cachedCpuRamAccentBrush = string.IsNullOrEmpty(_config.Config.CpuRamAccentColorHex) ? null : new SolidBrush(HexToColor(_config.Config.CpuRamAccentColorHex));
            _cachedGpuAccentBrush    = string.IsNullOrEmpty(_config.Config.GpuAccentColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.GpuAccentColorHex));
            _cachedDiskAccentBrush   = string.IsNullOrEmpty(_config.Config.DiskAccentColorHex)   ? null : new SolidBrush(HexToColor(_config.Config.DiskAccentColorHex));
        }

        private float GetCachedMeasure(string t, Font f) { if (_measureGraphics == null) return 0; string k = $"{t}_{f.Name}_{f.Size}_{f.Style}"; if (!_measureCache.TryGetValue(k, out var w)) { w = _measureGraphics.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width; _measureCache[k] = w; } return w; }

        // Live values change every tick; caching them would grow the measure cache without bound.
        private float MeasureNoCache(string t, Font f) => _measureGraphics == null ? 0f : _measureGraphics.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width;

        /// <summary>"0 C:" (PerformanceCounter instance name) → "C".</summary>
        private static string DriveLetter(string name)
        {
            int colonIdx = name.IndexOf(':');
            if (colonIdx > 0) return name.Substring(colonIdx - 1, 1).ToUpperInvariant();
            return name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
        }

        /// <summary>
        /// A value's display width: the live string, but never below the width of its Reserve
        /// string. The floor keeps a zone from breathing as digits come and go (3% vs 10%);
        /// the live part still lets it grow for genuinely long readings.
        /// </summary>
        private float FlooredWidth(MetricItem? item, Font f)
        {
            if (item == null) return 0f;
            float w = MeasureNoCache(item.Value, f);
            if (item.Reserve != null) w = Math.Max(w, GetCachedMeasure(item.Reserve, f));
            return w;
        }
        private void ClearCaches() { foreach (var f in _fontCache.Values) f.Dispose(); _fontCache.Clear(); _measureCache.Clear(); }
        private void SetBitmap(Bitmap bitmap)
        {
            IntPtr windowDC = GetWindowDC(_hWnd); IntPtr memDC = CreateCompatibleDC(windowDC); IntPtr hBitmap = IntPtr.Zero; IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0)); oldBitmap = SelectObject(memDC, hBitmap);
                SIZE size = new SIZE { cx = bitmap.Width, cy = bitmap.Height }; POINT ps = new POINT { x = 0, y = 0 }; POINT tp;
                if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT wr)) tp = new POINT { x = wr.Left, y = wr.Top }; else tp = new POINT { x = (int)_config.Config.X, y = (int)_config.Config.Y };
                BLENDFUNCTION b = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = _currentAlpha, AlphaFormat = 1 };
                UpdateLayeredWindow(_hWnd, windowDC, ref tp, ref size, memDC, ref ps, 0, ref b, 2);
            }
            finally { if (hBitmap != IntPtr.Zero) { SelectObject(memDC, oldBitmap); DeleteObject(hBitmap); } DeleteDC(memDC); ReleaseDC(_hWnd, windowDC); }
        }

        private Color HexToColor(string hex)
        {
            try { hex = hex.Replace("#", ""); if (hex.Length == 8) return Color.FromArgb(int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
                if (hex.Length == 6) return Color.FromArgb(255, int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)); } catch { } return Color.White;
        }

        public void Dispose()
        {
            try { if (_onHistoryUpdated != null) _history.Updated -= _onHistoryUpdated; _config.Config.PropertyChanged -= _onConfigPropertyChanged; _zOrderTimer?.Dispose(); _fadeTimer?.Stop(); UnregisterAppBar(); ClearCaches(); _offscreenGraphics?.Dispose(); _offscreenBitmap?.Dispose(); _measureGraphics?.Dispose(); _measureBitmap?.Dispose(); _cachedBgBrush?.Dispose(); _cachedAccentBrush?.Dispose(); _cachedLabelBrush?.Dispose(); _cachedPodBrush?.Dispose(); _cachedHoverPen?.Dispose(); _cachedHoverBrush?.Dispose(); _cachedNetLabelBrush?.Dispose(); _cachedCpuRamLabelBrush?.Dispose(); _cachedGpuLabelBrush?.Dispose(); _cachedDiskLabelBrush?.Dispose(); _cachedNetAccentBrush?.Dispose(); _cachedCpuRamAccentBrush?.Dispose(); _cachedGpuAccentBrush?.Dispose(); _cachedDiskAccentBrush?.Dispose(); if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd); if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); } catch { }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == 0x0084) return (IntPtr)1;
            if (msg == 0x0010) return IntPtr.Zero; // WM_CLOSE — ignore, overlay is not closeable
            if (msg == WM_WINDOWPOSCHANGING && _config.Config.StickToTaskbar)
            {
                WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                IntPtr taskbar = Win32Helper.FindWindow("Shell_TrayWnd", "");
                if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb)) { int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor); pos.y = tb.Top + (tb.Bottom - tb.Top - oh) / 2; Marshal.StructureToPtr(pos, lParam, false); }
            }
            if (msg == WM_WINDOWPOSCHANGED) { if (_appbarRegistered) { APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd }; SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd); } return IntPtr.Zero; }
            if (msg == WM_APPBAR_CALLBACK) { if ((uint)wParam.ToInt32() == ABN_FULLSCREENAPP) { _shellFullscreen = (lParam != IntPtr.Zero); _dispatcher.BeginInvoke(UpdateVisibility); } return IntPtr.Zero; }
            if (msg == WM_ENTERSIZEMOVE) { _inSizeMove = true; }
            if (msg == WM_EXITSIZEMOVE) { _inSizeMove = false; if (Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT r)) { _config.Config.X = r.Left; _config.Config.Y = r.Top; _config.SaveConfig(); } }
            if (msg == WM_SHOW_SETTINGS) { _dispatcher.BeginInvoke(() => App.OpenSettings(_viewModel, _config)); return IntPtr.Zero; }
            if (msg == WM_DPICHANGED) { _currentDpi = (uint)(wParam.ToInt32() & 0xFFFF); _dpiScale = _currentDpi / 96.0f; ClearCaches(); AlignToTaskbarCenter(); UpdateLayer(); return IntPtr.Zero; }
            if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE) { AlignToTaskbarCenter(); UpdateLayer(); return IntPtr.Zero; }
            if (msg == WM_MOUSEMOVE)
            {
                if (!_trackingMouse) { TRACKMOUSEEVENT tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = hWnd }; TrackMouseEvent(ref tme); _trackingMouse = true; _isHovered = true; UpdateLayer(); }

                if (_pressPending)
                {
                    // Require the button to still be down. Without this, a swallowed button-up
                    // would leave the flag set and a later hover would start a phantom drag.
                    if ((wParam.ToInt64() & MK_LBUTTON) == 0)
                    {
                        _pressPending = false;
                        ReleaseCapture();
                    }
                    else if (Win32Helper.GetCursorPos(out Win32Helper.POINT now) && ExceedsDragThreshold(now))
                    {
                        // Clear before releasing capture: ReleaseCapture synthesises
                        // WM_CAPTURECHANGED, which also clears the flag.
                        _pressPending = false;
                        ReleaseCapture();
                        if (!_config.Config.LockPosition)
                        {
                            SendMessage(hWnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                        }
                    }
                }
                else if (_config.Config.HoverPanels && _config.Config.StackedTaskbar)
                {
                    // Per-module hover dropdowns, the iStat model: dwell over a module opens
                    // its section; sliding to another module retargets the open dropdown
                    // immediately, dwell applies only to the first open.
                    int mx = unchecked((short)((long)lParam & 0xFFFF));
                    var section = HitTestModule(mx);
                    if (section != _pendingHoverSection)
                    {
                        _pendingHoverSection = section;
                        EnsureHoverDwellTimer();
                        _hoverDwellTimer!.Stop();
                        if (section != PanelSection.All)
                        {
                            if (App.StatsPanel is { IsHoverMode: true })
                            {
                                if (GetModuleScreenRect(section, out var rect))
                                    App.ShowHoverPanel(section, rect, _config, this);
                            }
                            else
                            {
                                _hoverDwellTimer.Start();
                            }
                        }
                    }
                }
            }
            if (msg == WM_MOUSELEAVE)
            {
                _trackingMouse = false; _isHovered = false;
                _pendingHoverSection = PanelSection.All;
                _hoverDwellTimer?.Stop();
                App.OverlayHoverLost();
                UpdateLayer();
            }
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK)
            {
                // CS_DBLCLKS means the second press of a double-tap arrives as WM_LBUTTONDBLCLK
                // rather than WM_LBUTTONDOWN, so both must arm the same gesture.
                Win32Helper.GetCursorPos(out _pressAnchor);
                SetCapture(hWnd);
                _pressPending = true;
                return IntPtr.Zero;
            }
            if (msg == WM_LBUTTONUP)
            {
                if (_pressPending)
                {
                    _pressPending = false;
                    ReleaseCapture();
                    if (_config.Config.ShowPanelOnClick)
                        _dispatcher.BeginInvoke(() => App.TogglePanel(_viewModel, _config, this));
                }
                return IntPtr.Zero;
            }
            // Both must fall through to DefWindowProc: it is what actually releases the capture,
            // so returning early here would leak it.
            if (msg == WM_CAPTURECHANGED || msg == WM_CANCELMODE) { _pressPending = false; }
            if (msg == WM_RBUTTONUP)
            {
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
                {
                    SetPreferredAppMode(2); AllowDarkModeForWindow(hWnd, true); FlushMenuThemes();
                    IntPtr hMenu = CreatePopupMenu();
                    AppendMenu(hMenu, 0, 1010, "Show Stats Panel");
                    AppendMenu(hMenu, 0, 1001, "Settings");
                    AppendMenu(hMenu, 0, 1002, "Task Manager");
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, (_config.Config.AlwaysOnTop ? 0x0008U : 0), 1008, "Keep on Top");
                    AppendMenu(hMenu, (_config.Config.HideOnFullscreen ? 0x0008U : 0), 1009, "Hide in Fullscreen");
                    AppendMenu(hMenu, (_config.Config.LockPosition ? 0x0008U : 0), 1006, "Lock Position");
                    AppendMenu(hMenu, (_config.Config.StickToTaskbar ? 0x0008U : 0), 1007, "Snap to Taskbar");
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, 0, 1003, "About");
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, 0, 1004, "Exit");
                    SetForegroundWindow(hWnd);

                    Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT wr);
                    IntPtr hMon = MonitorFromWindow(hWnd, 1);
                    MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(hMon, ref mi);

                    int my;
                    uint alignFlag;
                    // If the overlay is in the bottom half of the screen, pop the menu UP
                    if (wr.Top > (mi.rcWork.Top + mi.rcWork.Bottom) / 2)
                    {
                        my = wr.Top - 4;
                        alignFlag = 0x0020; // TPM_BOTTOMALIGN
                    }
                    else
                    {
                        my = wr.Bottom + 4;
                        alignFlag = 0x0000; // TPM_TOPALIGN
                    }

                    int ch = TrackPopupMenuEx(hMenu, 0x0100 | 0x0002 | alignFlag, pt.X, my, hWnd, IntPtr.Zero);
                    DestroyMenu(hMenu);
                    if (ch == 1010) _dispatcher.BeginInvoke(() => App.TogglePanel(_viewModel, _config, this));
                    else if (ch == 1001) _dispatcher.BeginInvoke(() => App.OpenSettings(_viewModel, _config));
                    else if (ch == 1006) { _config.Config.LockPosition = !_config.Config.LockPosition; _config.SaveConfig(); }
                    else if (ch == 1007) { _config.Config.StickToTaskbar = !_config.Config.StickToTaskbar; _config.SaveConfig(); }
                    else if (ch == 1008) { _config.Config.AlwaysOnTop = !_config.Config.AlwaysOnTop; _config.SaveConfig(); }
                    else if (ch == 1009) { _config.Config.HideOnFullscreen = !_config.Config.HideOnFullscreen; _config.SaveConfig(); }
                    else if (ch == 1002) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                    else if (ch == 1003) _dispatcher.BeginInvoke(() => { App.OpenSettings(_viewModel, _config); App.SettingsWindow?.SelectSection("About"); });
                    else if (ch == 1004) _dispatcher.BeginInvoke(() => App.Quit());
                }
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEX { public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cl, string nm, uint st, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr ha, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? n);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr i, int n);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hd, ref POINT pd, ref SIZE ps, IntPtr hs, ref POINT pr, int c, ref BLENDFUNCTION b, int f);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr hd);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr h, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint f, uint id, string? n);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m, uint f, int x, int y, IntPtr h, IntPtr t);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool a);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int m);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public uint cbSize; public Win32Helper.RECT rcMonitor; public Win32Helper.RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO m);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public Win32Helper.RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr SHAppBarMessage(uint m, ref APPBARDATA d);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    }
}
