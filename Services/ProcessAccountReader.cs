using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>Who a process runs as, and whether it holds administrator rights.</summary>
    /// <param name="User">DOMAIN\name, or the SID string when the account cannot be named.</param>
    /// <param name="Elevated">Null when the token could be opened for its user but not its elevation.</param>
    public sealed record ProcessAccount(string User, bool? Elevated)
    {
        /// <summary>The empty answer, for a process that could not be read.</summary>
        public static ProcessAccount None { get; } = new("", null);
    }

    /// <summary>
    /// Reads a process's account and elevation from its token, for one process at a time.
    ///
    /// <para>
    /// Deliberately separate from <see cref="Watchdog.ProcessDetails"/>: that reader's contract is
    /// relied on by the watchdog, and a token read is a different, more often refused operation.
    /// This is called only for the row the user has selected, off the UI thread — never for the
    /// list, which must not open a handle per row.
    /// </para>
    /// </summary>
    public static class ProcessAccountReader
    {
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int TOKEN_QUERY = 0x0008;
        private const int TokenUser = 1;
        private const int TokenElevation = 20;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        /// <summary>
        /// Reads both, or explains why not. Never throws: the caller is a background task whose
        /// only job is to fill in a pane.
        /// </summary>
        /// <param name="reason">
        /// "Process has exited", "Access denied", or "Unavailable (error N)" when false.
        /// </param>
        public static bool TryRead(int pid, out ProcessAccount account, out string reason)
        {
            account = ProcessAccount.None;
            reason = "";

            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                reason = Describe(Marshal.GetLastWin32Error());
                return false;
            }

            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY, out token))
                {
                    reason = Describe(Marshal.GetLastWin32Error());
                    return false;
                }

                string? user = ReadUser(token);
                if (user == null)
                {
                    reason = "Unavailable";
                    return false;
                }

                account = new ProcessAccount(user, ReadElevation(token));
                return true;
            }
            catch (Exception)
            {
                account = ProcessAccount.None;
                reason = "Unavailable";
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(process);
            }
        }

        /// <summary>
        /// The account as DOMAIN\name. Asked twice: once for the size, once for real. Falls back
        /// to the SID string for an account that cannot be translated, such as one deleted since
        /// the process started.
        /// </summary>
        private static string? ReadUser(IntPtr token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);
            if (needed <= 0 || needed > 64 * 1024) return null;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;

                // TOKEN_USER begins with SID_AND_ATTRIBUTES, whose first field is the PSID.
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                try { return ((NTAccount)sid.Translate(typeof(NTAccount))).Value; }
                catch (IdentityNotMappedException) { return sid.Value; }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>TOKEN_ELEVATION is a single DWORD: non-zero when elevated.</summary>
        private static bool? ReadElevation(IntPtr token)
        {
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                return GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out _)
                    ? Marshal.ReadInt32(buffer) != 0
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string Describe(int error) => error switch
        {
            ERROR_INVALID_PARAMETER => "Process has exited",
            ERROR_ACCESS_DENIED => "Access denied",
            _ => "Unavailable (error " + error.ToString(CultureInfo.InvariantCulture) + ")",
        };
    }
}
