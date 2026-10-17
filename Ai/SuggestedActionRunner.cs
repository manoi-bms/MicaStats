using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.Localization;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Everything a suggestion may do to the machine, behind an interface so the checks in
    /// <see cref="SuggestedActionRunner"/> can be tested with a fake. The live implementation
    /// goes through <see cref="ProcessControl"/> and the existing windows.
    /// </summary>
    internal interface ISuggestedActionHost
    {
        /// <summary>This process, which a suggestion must never end.</summary>
        int OwnProcessId { get; }

        /// <summary>Every running process with its start time, read now.</summary>
        IReadOnlyList<ProcessSampler.RawProcess> Processes();

        /// <summary>Ends one process; <see cref="ProcessControl.TryEndTask"/> in the live host.</summary>
        EndTaskResult EndProcess(int pid, long createTime, string name, out string message);

        /// <summary>Saves the slowdown recorder's window and returns the sentence to show.</summary>
        string RecordSlowdown();

        /// <summary>Opens the Diagnostics window.</summary>
        void OpenDiagnostics();

        /// <summary>Opens the process window.</summary>
        void OpenProcessWindow();
    }

    /// <summary>
    /// Runs a suggested action after its button is clicked, and only then.
    ///
    /// <para>
    /// The model proposed the action some seconds or minutes earlier, and its words are not
    /// trusted: a process target is looked up again and must still have the same PID, the same
    /// start time and the same name; the critical-process guard is applied to the name read now,
    /// not the one the model gave; MicaStats never ends itself. What remains goes through the
    /// same <see cref="ProcessControl.TryEndTask"/> the process window uses. The elevation retry
    /// stays in the process window, where the user can see what they are elevating for.
    /// </para>
    /// </summary>
    public static class SuggestedActionRunner
    {
        /// <summary>Runs <paramref name="action"/> against the live machine and returns the sentence to show.</summary>
        public static string Run(SuggestedAction action) => Run(action, LiveHost.Instance);

        /// <summary>Runs <paramref name="action"/> through <paramref name="host"/> (tests pass a fake).</summary>
        internal static string Run(SuggestedAction action, ISuggestedActionHost host)
        {
            switch (action.Kind)
            {
                case SuggestedActionKind.EndProcess:
                    return EndProcess(action, host);
                case SuggestedActionKind.RecordSlowdown:
                    return host.RecordSlowdown();
                case SuggestedActionKind.OpenDiagnostics:
                    host.OpenDiagnostics();
                    return UiText.Tr("Opened Diagnostics.");
                case SuggestedActionKind.OpenProcessWindow:
                    host.OpenProcessWindow();
                    return UiText.Tr("Opened Processes.");
                default:
                    return UiText.Tr("MicaStats does not know how to do that, so nothing was done.");
            }
        }

        private static string EndProcess(SuggestedAction action, ISuggestedActionHost host)
        {
            if (action.Pid is not int pid || action.CreateTime is not long createTime || pid <= 0 || createTime <= 0)
                return UiText.Tr("This suggestion does not say exactly which process to end, so nothing was ended. Open Processes to choose one.");

            if (pid == host.OwnProcessId)
                return UiText.Tr("That is MicaStats itself, so nothing was ended. Quit it from the overlay menu instead.");

            string pidText = pid.ToString(CultureInfo.InvariantCulture);
            string label = string.IsNullOrWhiteSpace(action.ProcessName) ? UiText.Tr("That process") : action.ProcessName!;

            ProcessSampler.RawProcess? live = null;
            foreach (var process in host.Processes())
            {
                if (process.Pid == pid)
                {
                    live = process;
                    break;
                }
            }

            if (live is not { } found || found.CreateTime != createTime)
                return UiText.Format("{0} (PID {1}) is no longer running, or its PID now belongs to another process. Nothing was ended.", label, pidText);

            if (!string.IsNullOrWhiteSpace(action.ProcessName) &&
                !string.Equals(found.Name, action.ProcessName, StringComparison.OrdinalIgnoreCase))
                return UiText.Format("PID {0} is now {1}, not {2}. Nothing was ended.", pidText, found.Name, action.ProcessName!);

            // Applied here as well as inside TryEndTask, to the name read just now.
            if (ProcessControl.IsCriticalProcess(found.Name))
                return UiText.Format("{0} is a core Windows process. Ending it would stop the machine immediately, so MicaStats will not do it.", found.Name);

            EndTaskResult result = host.EndProcess(pid, createTime, found.Name, out string message);
            return result == EndTaskResult.AccessDenied
                ? message + " " + UiText.Tr("To end it anyway, open Processes, select it, press End task, then Retry as administrator.")
                : message;
        }

        /// <summary>The real machine: the kernel process list, ProcessControl and the app's windows.</summary>
        private sealed class LiveHost : ISuggestedActionHost
        {
            public static readonly LiveHost Instance = new();

            public int OwnProcessId => Environment.ProcessId;

            public IReadOnlyList<ProcessSampler.RawProcess> Processes() => ProcessSampler.SnapshotOnce();

            public EndTaskResult EndProcess(int pid, long createTime, string name, out string message) =>
                ProcessControl.TryEndTask(pid, createTime, name, out message);

            public string RecordSlowdown()
            {
                var recorder = App.Recorder;
                if (recorder == null || !recorder.IsRunning)
                {
                    DiagnosticsWindow.ShowDiagnostics(0);
                    return UiText.Tr("Slowdown recording is off, so there is nothing to save. Switch it on in Diagnostics > Slowdowns.");
                }

                string? path = recorder.Capture(SlowdownCause.Manual);
                DiagnosticsWindow.ShowDiagnostics(0);
                return path == null
                    ? UiText.Tr("Nothing has been sampled yet. Try again in a few seconds.")
                    : UiText.Format("Saved {0}. It is listed in Diagnostics > Slowdowns.", Path.GetFileName(path));
            }

            public void OpenDiagnostics() => DiagnosticsWindow.ShowDiagnostics();

            public void OpenProcessWindow() => TaskManagerWindow.ShowOrActivate(App.SharedProcessSampler);
        }
    }
}
