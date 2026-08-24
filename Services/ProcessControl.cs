using System;
using System.Collections.Generic;

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
    }
}
