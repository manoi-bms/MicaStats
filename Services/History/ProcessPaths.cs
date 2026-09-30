using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The full exe path of a process, for the history's top-CPU column.
    ///
    /// <para>
    /// <c>QueryFullProcessImageNameW</c> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, the one
    /// access right an unelevated process gets for almost every other process, where
    /// <see cref="System.Diagnostics.Process.MainModule"/> is refused for a third of them. Called
    /// for one pid once a minute, never per row.
    /// </para>
    /// </summary>
    public static class ProcessPaths
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>The exe path, or null when the process is gone, protected, or <paramref name="pid"/> is not a pid.</summary>
        public static string? TryGetPath(int pid)
        {
            if (pid <= 0) return null;

            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;

            try
            {
                var buffer = new char[32768];   // the longest path Windows can return
                int size = buffer.Length;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
                    ? new string(buffer, 0, size)
                    : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
