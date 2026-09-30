namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>What a suggested-action button would do. The assistant only proposes; the user clicks.</summary>
    public enum SuggestedActionKind
    {
        /// <summary>End one process, identified by pid and start time so a recycled pid is never hit.</summary>
        EndProcess,

        /// <summary>Save a slowdown report of the last minutes now.</summary>
        RecordSlowdown,

        /// <summary>Open the Diagnostics window.</summary>
        OpenDiagnostics,

        /// <summary>Open the process window.</summary>
        OpenProcessWindow,
    }

    /// <summary>
    /// A button the model proposed through <c>suggest_action</c>. Nothing runs when it is
    /// created: the Ask window shows it, and a click re-checks the target (same pid and start
    /// time, the critical-process guard) before MicaStats acts.
    /// </summary>
    /// <param name="Kind">What the button does.</param>
    /// <param name="Label">The button text, in the language of the conversation.</param>
    /// <param name="Reason">One sentence from the model saying why it helps.</param>
    /// <param name="Pid">For <see cref="SuggestedActionKind.EndProcess"/>: the process id.</param>
    /// <param name="CreateTime">For <see cref="SuggestedActionKind.EndProcess"/>: the process start time as a FILETIME.</param>
    /// <param name="ProcessName">For <see cref="SuggestedActionKind.EndProcess"/>: the image name the model saw.</param>
    public sealed record SuggestedAction(SuggestedActionKind Kind, string Label, string Reason,
                                         int? Pid = null, long? CreateTime = null, string? ProcessName = null);
}
