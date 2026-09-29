using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Hands a <c>--pad [path]</c> launch to the MicaStats instance already running, the same way
    /// <c>WM_SHOW_SETTINGS</c> already hands over a plain second launch. <c>WM_COPYDATA</c> is used
    /// because Windows copies the payload into the receiving process, which is the one safe way to
    /// pass a string between processes by message.
    /// </summary>
    public static class PadIpc
    {
        /// <summary>The Win32 message number for WM_COPYDATA.</summary>
        public const int WM_COPYDATA = 0x004A;

        /// <summary>"MPAD": marks the message as MicaPad's, so another program's WM_COPYDATA is ignored.</summary>
        public static readonly IntPtr Tag = new(0x4D504144);

        /// <summary>Longest accepted path; Windows' own extended-path limit.</summary>
        public const int MaxChars = 32768;

        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint MSGFLT_ALLOW = 1;

        /// <summary>COPYDATASTRUCT.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CopyDataStruct
        {
            /// <summary>Sender-defined tag; MicaPad's messages carry <see cref="Tag"/>.</summary>
            public IntPtr dwData;

            /// <summary>Payload size in bytes.</summary>
            public int cbData;

            /// <summary>Pointer to the payload.</summary>
            public IntPtr lpData;
        }

        /// <summary>
        /// Asks the running instance to open MicaPad, with <paramref name="path"/> when given.
        /// Waits at most five seconds, and not at all for a hung window.
        /// </summary>
        public static bool SendOpen(IntPtr target, string? path)
        {
            string payload = path ?? "";
            if (payload.Length > MaxChars) return false;

            IntPtr buffer = Marshal.StringToHGlobalUni(payload);
            try
            {
                var data = new CopyDataStruct { dwData = Tag, cbData = payload.Length * 2, lpData = buffer };
                IntPtr sent = SendMessageTimeout(target, WM_COPYDATA, IntPtr.Zero, ref data, SMTO_ABORTIFHUNG, 5000, out IntPtr result);
                return sent != IntPtr.Zero && result != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Looks for the running instance's window, retrying while it is still starting: MicaStats
        /// takes its single-instance mutex before its window exists, so a launch in that gap (a
        /// pinned MicaPad button clicked right after sign-in) would otherwise find nothing.
        /// </summary>
        /// <param name="find">One lookup; <see cref="IntPtr.Zero"/> when the window is not there yet.</param>
        /// <param name="attempts">How many lookups at most.</param>
        /// <param name="interval">The pause between lookups; none follows the last one.</param>
        /// <param name="sleep">How to pause; tests record the pauses instead.</param>
        /// <returns>The window, or <see cref="IntPtr.Zero"/> when it never appeared.</returns>
        public static IntPtr FindWithRetry(Func<IntPtr> find, int attempts, TimeSpan interval, Action<TimeSpan>? sleep = null)
        {
            sleep ??= pause => System.Threading.Thread.Sleep(pause);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                IntPtr found = find();
                if (found != IntPtr.Zero) return found;
                if (attempt < attempts) sleep(interval);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Reads a WM_COPYDATA message. False unless it carries MicaPad's tag and a whole UTF-16
        /// string of at most <see cref="MaxChars"/>; an empty string means "just open MicaPad".
        /// </summary>
        public static bool TryRead(IntPtr lParam, out string path)
        {
            path = "";
            if (lParam == IntPtr.Zero) return false;

            var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if (data.dwData != Tag) return false;
            if (data.cbData < 0 || data.cbData % 2 != 0 || data.cbData > MaxChars * 2) return false;
            if (data.cbData > 0 && data.lpData == IntPtr.Zero) return false;

            path = data.cbData == 0 ? "" : Marshal.PtrToStringUni(data.lpData, data.cbData / 2);

            // A MicaStats sender always resolves to a full path first, so anything else is not ours.
            if (path.Length > 0 && !System.IO.Path.IsPathFullyQualified(path))
            {
                path = "";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Lets an unelevated sender (Explorer's Open with) reach this window, but only when MicaStats
        /// itself is elevated. Unelevated, Windows already admits same-integrity senders and keeps
        /// lower-integrity (sandboxed) ones out, which is what we want; relaxing the filter there would
        /// let a sandboxed process drive the overlay. Elevated, Explorer would be blocked without it.
        /// </summary>
        public static void AllowFromLowerIntegrityWhenElevated(IntPtr hwnd)
        {
            // Under UAC an unelevated administrator has a filtered token, so this is false unless actually elevated.
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return;

            ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam,
                                                        uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);
    }
}
