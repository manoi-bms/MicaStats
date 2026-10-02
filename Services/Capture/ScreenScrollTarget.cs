using System;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Helpers;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// The real screen behind <see cref="IScrollTarget"/> (scrolling capture spec 3): grabs the
    /// picked area with the BitBlt engine and scrolls it with real mouse-wheel input.
    ///
    /// <para>
    /// The only place the scrolling capture moves the pointer or sends input, so tests use fakes
    /// and never come here. The pointer goes to the area's centre before each scroll, because
    /// wheel input reaches the view under the pointer; <see cref="Dispose"/> puts it back.
    /// </para>
    /// </summary>
    internal sealed class ScreenScrollTarget : IScrollTarget, IDisposable
    {
        /// <summary>One wheel notch, in the units <c>WM_MOUSEWHEEL</c> carries.</summary>
        private const int WheelDelta = 120;

        private readonly PixelRect _area;
        private Win32Helper.POINT _savedCursor;
        private bool _cursorSaved;
        private bool _warned;

        public ScreenScrollTarget(PixelRect area)
        {
            _area = area;
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

        /// <summary>Positive scrolls down (negative wheel data), negative scrolls up.</summary>
        public bool Scroll(int notches)
        {
            if (notches == 0) return true;

            if (!_cursorSaved) _cursorSaved = Win32Helper.GetCursorPos(out _savedCursor);
            SetCursorPos(_area.X + _area.Width / 2, _area.Y + _area.Height / 2);

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
                // Once per capture: a blocked SendInput (a higher-integrity window under the
                // pointer) fails every step the same way.
                _warned = true;
                DiagnosticsLog.Warn("capture", $"Scrolling capture: {sent} of {count} wheel notches were sent");
            }
            return true;
        }

        /// <summary>Puts the pointer back where it was before the first scroll.</summary>
        public void Dispose()
        {
            if (!_cursorSaved) return;
            _cursorSaved = false;
            SetCursorPos(_savedCursor.X, _savedCursor.Y);
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

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
    }
}
