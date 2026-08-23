using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// The Win32 side of screen capture: enumerating displays and windows, and pulling pixels
    /// off the desktop.
    ///
    /// <para>
    /// Everything is in physical pixels. The process is declared PerMonitorV2 in the manifest,
    /// so the Win32 rectangles here are already true device pixels and need no scaling — which
    /// is exactly why the capture path avoids WPF coordinates entirely.
    /// </para>
    ///
    /// <para>
    /// Two details separate a correct screenshot from an almost-correct one. <c>CAPTUREBLT</c>
    /// is required or layered windows (menus, tooltips, this app's own overlay) come out
    /// missing. And window bounds come from DWM's <i>extended frame bounds</i>, not
    /// <c>GetWindowRect</c>: since Vista the latter includes an invisible resize border, so
    /// capturing a window by it yields a few pixels of desktop on three sides.
    /// </para>
    /// </summary>
    public static class ScreenCaptureEngine
    {
        // ----- Displays ----------------------------------------------------------------------

        public static IReadOnlyList<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
                {
                    var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        double scale = 1.0;
                        try
                        {
                            if (GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                                scale = dpiX / 96.0;
                        }
                        catch { }

                        list.Add(new MonitorInfo(
                            mi.szDevice,
                            ToPixelRect(mi.rcMonitor),
                            ToPixelRect(mi.rcWork),
                            (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                            scale));
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { DiagnosticsLog.Error("capture", "Monitor enumeration failed", ex); }

            if (list.Count == 0)
            {
                // Never leave the caller with nothing to capture.
                var fallback = new PixelRect(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
                list.Add(new MonitorInfo("PRIMARY", fallback, fallback, true, 1.0));
            }
            return list;
        }

        /// <summary>Bounding box of every display, straight from the system metrics.</summary>
        public static PixelRect VirtualBounds()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w > 0 && h > 0) return new PixelRect(x, y, w, h);

            var monitors = new List<PixelRect>();
            foreach (var m in GetMonitors()) monitors.Add(m.Bounds);
            return CaptureGeometry.VirtualBounds(monitors);
        }

        // ----- Windows -----------------------------------------------------------------------

        /// <summary>
        /// Visible top-level windows, front-most first, as click-to-capture targets. Minimised,
        /// zero-size and DWM-cloaked windows are skipped — cloaked ones are the invisible ghosts
        /// UWP apps leave behind, which otherwise sit over everything as unclickable targets.
        /// </summary>
        public static IReadOnlyList<CaptureTarget> EnumerateWindows(PixelRect bounds, IntPtr exclude = default)
        {
            var list = new List<CaptureTarget>();
            try
            {
                EnumWindows((hWnd, l) =>
                {
                    if (hWnd == exclude || !IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

                    try
                    {
                        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                            return true;
                    }
                    catch { }

                    var rect = GetWindowBounds(hWnd);
                    if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) return true;
                    if (!bounds.IsEmpty && !rect.IntersectsWith(bounds)) return true;

                    string title = GetWindowTitle(hWnd);
                    int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                    // A title-less tool window is a helper surface, not something to capture.
                    if (title.Length == 0 && (exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                    list.Add(new CaptureTarget(hWnd, rect, title));
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { DiagnosticsLog.Error("capture", "Window enumeration failed", ex); }
            return list;
        }

        /// <summary>
        /// A window's true visible bounds: DWM's extended frame bounds where available, which
        /// exclude the invisible resize border <c>GetWindowRect</c> reports.
        /// </summary>
        public static PixelRect GetWindowBounds(IntPtr hWnd)
        {
            try
            {
                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame,
                        Marshal.SizeOf<RECT>()) == 0)
                {
                    var r = ToPixelRect(frame);
                    if (!r.IsEmpty) return r;
                }
            }
            catch { }

            return GetWindowRect(hWnd, out RECT w) ? ToPixelRect(w) : default;
        }

        public static IntPtr ForegroundWindow() => GetForegroundWindow();

        private static string GetWindowTitle(IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return "";
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        // ----- Pixels ------------------------------------------------------------------------

        /// <summary>
        /// Copies a screen rectangle. CAPTUREBLT is essential: without it layered windows —
        /// menus, tooltips, MicaStats' own overlay — are absent from the result.
        /// </summary>
        public static Bitmap? CaptureRect(PixelRect rect, bool includeCursor)
        {
            if (rect.IsEmpty) return null;
            Bitmap? bmp = null;
            try
            {
                bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    // BitBlt directly rather than Graphics.CopyFromScreen: that API validates its
                    // argument against the CopyPixelOperation enum, and SRCCOPY | CAPTUREBLT is a
                    // combination the enum does not define, so it throws. Dropping CAPTUREBLT
                    // instead is not an option — layered windows (menus, tooltips, this app's own
                    // overlay) would be missing from every capture.
                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    if (screenDc == IntPtr.Zero) throw new InvalidOperationException("No screen DC");
                    IntPtr memDc = g.GetHdc();
                    try
                    {
                        BitBlt(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y,
                            SRCCOPY | CAPTUREBLT);
                    }
                    finally
                    {
                        g.ReleaseHdc(memDc);
                        ReleaseDC(IntPtr.Zero, screenDc);
                    }

                    if (includeCursor) DrawCursor(g, rect);
                }
                return bmp;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("capture", $"CaptureRect {rect} failed", ex);
                bmp?.Dispose();
                return null;
            }
        }

        /// <summary>Captures straight to a frozen WPF image, ready for the UI thread.</summary>
        public static BitmapSource? CaptureRectSource(PixelRect rect, bool includeCursor)
        {
            using var bmp = CaptureRect(rect, includeCursor);
            return bmp == null ? null : ToBitmapSource(bmp);
        }

        /// <summary>
        /// Composites the mouse pointer, honouring its hotspot so the arrow tip lands where the
        /// pointer actually is rather than offset by the cursor bitmap's origin.
        /// </summary>
        private static void DrawCursor(Graphics g, PixelRect rect)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;

            IntPtr copy = CopyIcon(ci.hCursor);
            if (copy == IntPtr.Zero) return;
            try
            {
                if (!GetIconInfo(copy, out ICONINFO info)) return;
                try
                {
                    int x = ci.ptScreenPos.x - rect.X - info.xHotspot;
                    int y = ci.ptScreenPos.y - rect.Y - info.yHotspot;
                    IntPtr hdc = g.GetHdc();
                    try { DrawIconEx(hdc, x, y, copy, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
                    finally { g.ReleaseHdc(hdc); }
                }
                finally
                {
                    if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                    if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                }
            }
            catch (Exception ex) { DiagnosticsLog.Warn("capture", "Cursor composite failed: " + ex.Message); }
            finally { DestroyIcon(copy); }
        }

        /// <summary>
        /// GDI+ bitmap to a frozen WPF source. The intermediate HBITMAP is always released —
        /// leaking it would burn a GDI handle on every single capture.
        /// </summary>
        public static BitmapSource ToBitmapSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hBitmap); }
        }

        // ----- Output ------------------------------------------------------------------------

        public static BitmapEncoder CreateEncoder(CaptureFormat format, int jpegQuality) => format switch
        {
            CaptureFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            _ => new PngBitmapEncoder(),
        };

        public static void Save(BitmapSource image, string path, CaptureFormat format, int jpegQuality)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = CreateEncoder(format, jpegQuality);
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }

        /// <summary>
        /// Puts the capture on the clipboard in several formats at once. Applications disagree
        /// about what they want — Office and most Win32 apps paste a DIB, while browsers and
        /// chat clients prefer PNG (and PNG is the only one of the two that carries an alpha
        /// channel) — so offering both is what makes paste "just work" everywhere.
        /// </summary>
        public static bool CopyToClipboard(BitmapSource image)
        {
            try
            {
                var data = new System.Windows.DataObject();
                data.SetImage(image);

                using var png = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(image));
                enc.Save(png);
                data.SetData("PNG", png, autoConvert: false);

                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                // The clipboard is a shared, lockable resource; another app can hold it open.
                DiagnosticsLog.Error("capture", "Clipboard copy failed", ex);
                return false;
            }
        }

        // ----- Native ------------------------------------------------------------------------

        private static PixelRect ToPixelRect(RECT r) => PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);

        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const int MONITORINFOF_PRIMARY = 1;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;
        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;
        private const uint SRCCOPY = 0x00CC0020;
        /// <summary>Includes layered (per-pixel-alpha) windows in the blit.</summary>
        private const uint CAPTUREBLT = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int srcX, int srcY, uint rop);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
    }
}
