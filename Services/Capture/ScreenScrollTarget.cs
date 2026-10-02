using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Helpers;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// The real screen behind <see cref="IScrollTarget"/> (scrolling capture spec 3 and 6): grabs the
    /// picked area with the BitBlt engine and scrolls it with real mouse-wheel input.
    ///
    /// <para>
    /// The only place the scrolling capture moves the pointer or sends input, so tests use fakes
    /// and never come here. The pointer goes to the area's centre before each scroll, because
    /// wheel input reaches the view under the pointer; <see cref="Dispose"/> puts it back.
    /// </para>
    ///
    /// <para>
    /// The wheel goes only to the picked window: the top-level window under the area's centre at
    /// the first scroll. When Windows sends the wheel to the focused window rather than the one
    /// under the pointer ("Scroll inactive windows when I hover over them" off), that window is
    /// brought to the front first. Before every notch it must still be there, under the centre
    /// and, with focus routing, in front, with no modifier key held; otherwise scrolling stops.
    /// The rules are <see cref="ScrollInputGuard"/>'s; this class only asks Windows and acts.
    /// </para>
    /// </summary>
    internal sealed class ScreenScrollTarget : IScrollTarget, IDisposable
    {
        /// <summary>One wheel notch, in the units <c>WM_MOUSEWHEEL</c> carries.</summary>
        private const int WheelDelta = 120;

        private const int ModifierPollMs = 50;

        private readonly PixelRect _area;
        private readonly Action<int> _wait;
        private Win32Helper.POINT _savedCursor;
        private bool _cursorSaved;
        private bool _warned;
        private bool _started;
        private IntPtr _window;
        private int? _routing;

        /// <param name="wait">Waits without freezing the UI, while a held modifier key is let go.</param>
        public ScreenScrollTarget(PixelRect area, Action<int> wait)
        {
            _area = area;
            _wait = wait;
        }

        /// <summary>The area's pixels now. Never with the cursor: it would repeat in every frame.</summary>
        public PixelFrame Grab()
        {
            using var bitmap = ScreenCaptureEngine.CaptureRect(_area, includeCursor: false)
                ?? throw new InvalidOperationException("The screen could not be read for the scrolling capture");
            return ToFrame(bitmap);
        }

        private static PixelFrame ToFrame(System.Drawing.Bitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height;
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new int[width * height];
                // Row by row: the stride may be padded, and the frame's rows are not.
                for (int y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width, width);
                return new PixelFrame(width, height, pixels);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>
        /// Positive scrolls down (negative wheel data), negative scrolls up. False, and nothing
        /// sent, when the wheel would not reach the picked window.
        /// </summary>
        public bool Scroll(int notches)
        {
            if (notches == 0) return true;

            int cx = _area.X + _area.Width / 2, cy = _area.Y + _area.Height / 2;
            if (!_cursorSaved) _cursorSaved = Win32Helper.GetCursorPos(out _savedCursor);
            SetCursorPos(cx, cy);

            if (!_started)
            {
                _started = true;
                _window = WindowAt(cx, cy);
                _routing = WheelRouting();
                // Routed by focus, the wheel goes to the window in front: put the picked one there.
                // Check below confirms it got there; Windows may refuse.
                if (_window != IntPtr.Zero && ScrollInputGuard.NeedsForeground(_routing))
                    Win32Helper.SetForegroundWindow(_window);
            }

            // Ctrl or Shift held would turn the wheel into zoom or sideways scrolling.
            for (int waited = 0; ; waited += ModifierPollMs)
            {
                var gate = ScrollInputGuard.Modifiers(ModifierHeld(), waited);
                if (gate == ModifierGate.Send) break;
                if (gate == ModifierGate.Stop) return Refuse("Ctrl, Shift, Alt or Win stayed held");
                _wait(ModifierPollMs);
            }
            SetCursorPos(cx, cy);   // the pointer may have been moved while waiting

            IntPtr under = WindowAt(cx, cy);
            var now = new WheelWindowState(IsWindow(_window), IsIconic(_window), under,
                OwnedBy(under, _window), GetForegroundWindow());
            var check = ScrollInputGuard.Check(_window, _routing, now);
            if (check != WheelCheck.Send) return Refuse(check.ToString());

            int count = Math.Abs(notches);
            int data = -WheelDelta * Math.Sign(notches);
            var inputs = new INPUT[count];
            for (int i = 0; i < count; i++)
            {
                inputs[i].type = INPUT_MOUSE;
                inputs[i].mi.mouseData = data;
                inputs[i].mi.dwFlags = MOUSEEVENTF_WHEEL;
            }

            uint sent = SendInput((uint)count, inputs, Marshal.SizeOf<INPUT>());
            if (sent != count && !_warned)
            {
                // Once per capture. A short count means the input stream was blocked (another
                // thread holding BlockInput, a secure desktop). Wheel input to a window of an app
                // running as administrator is dropped by Windows (UIPI) without SendInput saying
                // so; that run ends as Unscrollable, and WindowMayBeElevated tells the log why.
                _warned = true;
                DiagnosticsLog.Warn("capture", string.Create(CultureInfo.InvariantCulture,
                    $"Scrolling capture: {sent} of {count} wheel notches were sent"));
            }
            return true;
        }

        private bool Refuse(string why)
        {
            DiagnosticsLog.Warn("capture", string.Create(CultureInfo.InvariantCulture,
                $"Scrolling capture: the wheel was not sent ({why}; wheel routing {(_routing.HasValue ? _routing.Value.ToString(CultureInfo.InvariantCulture) : "unknown")})"));
            return false;
        }

        /// <summary>
        /// Whether the picked window seems to belong to an app running with more rights than
        /// MicaStats (as administrator), whose wheel input Windows drops without a word. A best
        /// guess for the log only: false whenever it cannot tell.
        /// </summary>
        public bool WindowMayBeElevated()
        {
            try
            {
                if (_window == IntPtr.Zero || Elevated(GetCurrentProcess()) != false) return false;
                GetWindowThreadProcessId(_window, out uint pid);
                if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
                IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero) return false;
                try
                {
                    // A token MicaStats may not even read belongs to an elevated (or system) process.
                    return Elevated(process) ?? true;
                }
                finally
                {
                    CloseHandle(process);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The process's elevation; null when its token cannot be read.</summary>
        private static bool? Elevated(IntPtr process)
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>Puts the pointer back where it was before the first scroll.</summary>
        public void Dispose()
        {
            if (!_cursorSaved) return;
            _cursorSaved = false;
            SetCursorPos(_savedCursor.X, _savedCursor.Y);
        }

        /// <summary>The top-level window under a screen point.</summary>
        private static IntPtr WindowAt(int x, int y)
        {
            IntPtr hit = WindowFromPoint(new Win32Helper.POINT { X = x, Y = y });
            return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
        }

        /// <summary>Whether <paramref name="window"/> is owned, directly or up the chain, by <paramref name="owner"/>.</summary>
        private static bool OwnedBy(IntPtr window, IntPtr owner)
        {
            if (window == IntPtr.Zero || owner == IntPtr.Zero) return false;
            IntPtr w = window;
            for (int i = 0; i < 16; i++)
            {
                w = GetWindow(w, GW_OWNER);
                if (w == IntPtr.Zero) return false;
                if (w == owner) return true;
            }
            return false;
        }

        /// <summary>Windows' wheel routing setting; null before Windows 10, which routes by focus.</summary>
        private static int? WheelRouting()
        {
            uint value = 0;
            return SystemParametersInfo(SPI_GETMOUSEWHEELROUTING, 0, ref value, 0) ? (int)value : null;
        }

        private static bool ModifierHeld() =>
            Down(VK_SHIFT) || Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN);

        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint SPI_GETMOUSEWHEELROUTING = 0x201C;
        private const uint GA_ROOT = 2;
        private const uint GW_OWNER = 4;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        /// <summary>
        /// <c>INPUT</c> with only its mouse member. MOUSEINPUT is the union's largest member, so
        /// the struct has the native size and layout on both x86 and x64.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Win32Helper.POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    }
}
