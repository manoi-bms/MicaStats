using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>What happened when a termination was attempted.</summary>
    public enum EndTaskResult
    {
        /// <summary>The process is gone.</summary>
        Terminated,

        /// <summary>The process runs at a level this unelevated app cannot reach.</summary>
        AccessDenied,

        /// <summary>It had already exited — from the user's point of view, done.</summary>
        AlreadyExited,

        /// <summary>Terminating it would stop Windows. Refused.</summary>
        Critical,

        /// <summary>The pid now belongs to a different process than the one selected.</summary>
        Recycled,

        /// <summary>Any other Win32 failure, reported with its code.</summary>
        Failed,
    }

    /// <summary>
    /// The only component that terminates a process.
    ///
    /// <para>
    /// Every outcome is classified rather than swallowed. "End task does nothing" is one of the
    /// symptoms this window exists to fix, and a silently discarded error reproduces it exactly
    /// — the process survives and the user is told nothing.
    /// </para>
    /// </summary>
    public static partial class ProcessControl
    {
        /// <summary>
        /// Processes whose termination bugchecks Windows immediately.
        ///
        /// <para>
        /// Refused rather than confirmed. A confirmation dialog is one mis-click away from a
        /// stop error, and this window is used precisely when the machine is struggling and a
        /// re-sorting list is moving under the cursor. Anyone who genuinely intends this has
        /// <c>taskkill</c>, and will not reach it by accident.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "csrss.exe", "wininit.exe", "services.exe", "smss.exe", "lsass.exe", "winlogon.exe",
        };

        /// <summary>
        /// Whether terminating this image would stop the machine.
        ///
        /// <para>
        /// Matches the image name exactly rather than querying whether the kernel has marked
        /// the process critical. This has to answer before any handle is opened, and it must
        /// not depend on a call that could itself block on a starved system — which is the
        /// condition the user is in whenever they reach for this tool.
        /// </para>
        /// </summary>
        public static bool IsCriticalProcess(string name) =>
            !string.IsNullOrEmpty(name) && CriticalNames.Contains(name);

        private const int PROCESS_TERMINATE = 0x0001;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// Required to wait on the process handle. Without it <c>WaitForSingleObject</c> fails
        /// rather than reporting whether the process exited, and an already-dead process then
        /// gets classified as a permissions failure.
        /// </summary>
        private const int SYNCHRONIZE = 0x00100000;

        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr handle,
            out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        private const uint WAIT_OBJECT_0 = 0x00000000;

        /// <summary>
        /// Whether the process behind this handle has already exited.
        ///
        /// <para>
        /// A process handle becomes signalled on exit, so a zero-timeout wait answers this
        /// without blocking. <c>GetExitCodeProcess</c> would be the obvious call and is wrong:
        /// it reports <c>STILL_ACTIVE</c>, which is 259, and a process that genuinely exits
        /// with code 259 is then indistinguishable from a running one.
        /// </para>
        /// </summary>
        private static bool HasExited(IntPtr handle) =>
            WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;

        /// <summary>
        /// Terminates one process, immediately.
        ///
        /// <para>
        /// No graceful close is attempted. Windows Task Manager's End task first asks the
        /// window to close politely, which is exactly why it appears to do nothing against a
        /// hung application: the request lands in a message queue that is never pumped. This
        /// goes straight to <c>TerminateProcess</c>.
        /// </para>
        ///
        /// <para>
        /// <paramref name="createTime"/> is verified against the live process before anything
        /// irreversible happens. Pids are recycled, and when this runs behind a consent prompt
        /// the gap between choosing a row and acting on it is seconds wide — without the check,
        /// a slow confirmation could terminate an unrelated process with administrator rights.
        /// Pass 0 to skip the check only when the caller has no creation time to offer.
        /// </para>
        /// </summary>
        public static EndTaskResult TryEndTask(int pid, long createTime, string name, out string message)
        {
            if (IsCriticalProcess(name))
            {
                message = name + " is a core Windows process. Ending it would stop the machine "
                          + "immediately, so MicaStats will not do it.";
                return EndTaskResult.Critical;
            }

            IntPtr handle = OpenProcess(
                PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);

            // Fall back without SYNCHRONIZE rather than losing the ability to terminate: the
            // wait is a diagnostic nicety, terminating is the job.
            if (handle == IntPtr.Zero && Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                switch (error)
                {
                    case ERROR_INVALID_PARAMETER:
                        message = Label(name) + " had already exited.";
                        return EndTaskResult.AlreadyExited;

                    case ERROR_ACCESS_DENIED:
                        message = Label(name) + " runs at a higher privilege level than MicaStats, "
                                  + "which is unelevated.";
                        return EndTaskResult.AccessDenied;

                    default:
                        message = "Could not open " + Label(name) + " (error "
                                  + error.ToString(CultureInfo.InvariantCulture) + ").";
                        return EndTaskResult.Failed;
                }
            }

            try
            {
                // A terminated process lingers as a zombie while any handle to it remains open,
                // so OpenProcess still succeeds and TerminateProcess then fails with
                // ERROR_ACCESS_DENIED. Reporting that as a permissions problem would tell the
                // user their process is privileged when it is simply already gone. Check first.
                if (HasExited(handle))
                {
                    message = Label(name) + " had already exited.";
                    return EndTaskResult.AlreadyExited;
                }

                // The identity check, before the irreversible step.
                if (createTime != 0 &&
                    GetProcessTimes(handle, out long living, out _, out _, out _) &&
                    living != createTime)
                {
                    message = "That process has already exited, and PID "
                              + pid.ToString(CultureInfo.InvariantCulture)
                              + " now belongs to a different one. Nothing was ended.";
                    return EndTaskResult.Recycled;
                }

                if (TerminateProcess(handle, 1))
                {
                    message = "Ended " + Label(name) + ".";
                    return EndTaskResult.Terminated;
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_ACCESS_DENIED)
                {
                    message = Label(name) + " refused to be ended by an unelevated process.";
                    return EndTaskResult.AccessDenied;
                }

                message = "Could not end " + Label(name) + " (error "
                          + error.ToString(CultureInfo.InvariantCulture) + ").";
                return EndTaskResult.Failed;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>A name for messages when the caller could not resolve one.</summary>
        private static string Label(string name) =>
            string.IsNullOrWhiteSpace(name) ? "That process" : name;
    }
}
