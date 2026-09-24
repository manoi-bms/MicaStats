using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// The two facts the snapshot does not carry: the full image path and the command line.
    ///
    /// <para>
    /// Both cost a handle and a call, so they are read only for processes whose bare image name
    /// already matched the allowlist — on a normal machine, none. Reading them for every
    /// process would reproduce the per-process enrichment that makes Windows Task Manager slow
    /// to open on a struggling machine.
    /// </para>
    ///
    /// <para>
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is enough for both, so this works unelevated
    /// against same-user processes, which is what the leaked children are.
    /// </para>
    /// </summary>
    public static class ProcessDetails
    {
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// <c>ProcessCommandLineInformation</c>. Available since Windows 8.1 and readable with
        /// limited-information rights, unlike walking the PEB, which needs
        /// <c>PROCESS_VM_READ</c> and a matching bitness.
        /// </summary>
        private const int ProcessCommandLineInformation = 60;

        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr handle, int flags, StringBuilder buffer, ref int size);

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryInformationProcess(
            IntPtr handle, int infoClass, IntPtr buffer, uint bufferSize, out uint returnLength);

        /// <summary>
        /// Reads both, or reports that it could not.
        ///
        /// <para>
        /// Returns false rather than throwing on every failure, including the ordinary one: a
        /// process that exits between the snapshot and this call. The watchdog runs unattended
        /// and a candidate that vanished needs no verdict.
        /// </para>
        ///
        /// <para>
        /// Success means the image path was read. The command line can still come back empty
        /// when only that second query failed; <see cref="OrphanScan"/> keeps such a process
        /// with its own reason rather than guessing what the command line said.
        /// </para>
        /// </summary>
        public static bool TryRead(int pid, out string imagePath, out string commandLine)
        {
            imagePath = "";
            commandLine = "";

            if (pid <= 0) return false;

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;

            try
            {
                imagePath = ReadImagePath(handle);
                commandLine = ReadCommandLine(handle);
                return imagePath.Length > 0;
            }
            catch
            {
                imagePath = "";
                commandLine = "";
                return false;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string ReadImagePath(IntPtr handle)
        {
            // 32767 is the documented maximum path length with the extended-length prefix.
            int size = 32768;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : "";
        }

        /// <summary>
        /// The command line, as a UNICODE_STRING whose buffer follows the struct in the same
        /// allocation. Asked for twice: once with no room, to learn the size the kernel wants,
        /// then once for real.
        /// </summary>
        private static string ReadCommandLine(IntPtr handle)
        {
            uint status = NtQueryInformationProcess(
                handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out uint needed);

            if (status != STATUS_INFO_LENGTH_MISMATCH || needed == 0) return "";
            if (needed > 64 * 1024) return "";   // nothing legitimate is this long

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (NtQueryInformationProcess(
                        handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                    return "";

                // UNICODE_STRING on x64: USHORT Length, USHORT MaximumLength, 4 bytes padding,
                // PWSTR Buffer.
                ushort byteLength = unchecked((ushort)Marshal.ReadInt16(buffer, 0));
                IntPtr text = Marshal.ReadIntPtr(buffer, 8);

                if (text == IntPtr.Zero || byteLength == 0) return "";
                return Marshal.PtrToStringUni(text, byteLength / 2) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
