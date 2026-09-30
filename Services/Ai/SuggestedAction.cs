using System.Globalization;
using System.Text;

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
    /// <param name="Label">The button text, in the language of the conversation. Never shown for <see cref="SuggestedActionKind.EndProcess"/>: see <see cref="ButtonText"/>.</param>
    /// <param name="Reason">One sentence from the model saying why it helps.</param>
    /// <param name="Pid">For <see cref="SuggestedActionKind.EndProcess"/>: the process id.</param>
    /// <param name="CreateTime">For <see cref="SuggestedActionKind.EndProcess"/>: the process start time as a FILETIME.</param>
    /// <param name="ProcessName">For <see cref="SuggestedActionKind.EndProcess"/>: the image name the model saw.</param>
    public sealed record SuggestedAction(SuggestedActionKind Kind, string Label, string Reason,
                                         int? Pid = null, long? CreateTime = null, string? ProcessName = null)
    {
        /// <summary>Longest process name an end-process button shows, so the PID always stays visible.</summary>
        private const int MaxShownName = 40;

        /// <summary>
        /// The text the button shows. For <see cref="SuggestedActionKind.EndProcess"/> it is always
        /// <see cref="EndProcessText"/>, whatever <see cref="Label"/> says; other kinds show the label.
        /// </summary>
        public string ButtonText => Kind == SuggestedActionKind.EndProcess ? EndProcessText(ProcessName, Pid) : Label;

        /// <summary>
        /// The end-process button text, built only from the target a click checks again, e.g.
        /// "End chrome.exe (PID 1234)". The model's own label is never used here: text it read in a
        /// process name, a report or a sensor label could otherwise dress the one destructive
        /// button up as something harmless.
        /// </summary>
        public static string EndProcessText(string? processName, int? pid)
        {
            var name = new StringBuilder();
            foreach (char c in (processName ?? "").Trim())
            {
                if (name.Length == MaxShownName) break;
                name.Append(char.IsControl(c) ? ' ' : c);
            }
            string? id = pid is int p ? "PID " + p.ToString(CultureInfo.InvariantCulture) : null;

            if (name.Length == 0) return id == null ? "End process" : "End " + id;
            return id == null ? "End " + name : "End " + name + " (" + id + ")";
        }
    }
}
