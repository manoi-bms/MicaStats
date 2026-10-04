using System;
using System.Globalization;
using System.Text;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>What the window knows about the source text right now.</summary>
    /// <param name="BlockFence">
    /// For the fix of a diagram block: the run of characters the block around the source opens
    /// with as the note reads now, or "" when no block stands around it any more. Null when the
    /// window did not look (another action, a fix still running, a source that is not shown).
    /// </param>
    public readonly record struct AiSourceFacts(bool SourceShown, bool ReadOnly, bool SourceUnchanged, string? BlockFence = null);

    /// <summary>
    /// Everything the AI pane draws; immutable. <c>ShowInsert</c> is false only for the fix of a
    /// diagram block: Insert below is then not offered at all (it would land inside the block),
    /// as <c>ShowReplace</c> is false for a result with nothing to replace. <c>Activity</c> says
    /// what a running request is doing ("Waiting for …", "Writing…") and <c>Info</c> how long one
    /// that finished whole took ("Finished in 4 s"); each is "" when there is nothing to say.
    /// <c>SourceFirst</c> asks the pane to start a result that is shown rendered with Source on
    /// (Ask AI on a note that is not Markdown, whose answer is often code); the user's toggle
    /// decides from then on.
    /// </summary>
    public sealed record AiPaneView(
        string Title,
        string SourceLine,
        bool AskForInstruction,
        string Result,
        bool Markdown,
        bool Running,
        string Status,
        bool ShowReplace, bool CanReplace,
        bool CanInsert, bool CanCopy, bool CanRetry,
        bool CanShowChanges,
        string Original,
        bool ShowInsert = true,
        string Activity = "",
        string Info = "",
        bool SourceFirst = false);

    /// <summary>
    /// The state of one AI request and the rules for which buttons the pane offers
    /// (MicaPad AI spec 3.2 to 3.4). No WPF; the window feeds it the stream and the facts.
    /// A session runs once: Try again builds a new <see cref="AiSession"/>.
    /// </summary>
    public sealed class AiSession
    {
        /// <summary>The status once Replace selection put the result in the note.</summary>
        public const string Replaced = "Replaced the selection";

        /// <summary>The status right after Insert below, for one drawing of the pane: an insert changes none of the rules.</summary>
        public const string Inserted = "Inserted below";

        /// <summary>Why the result of a fix may not replace its block's source: a line of it would close the block's fence.</summary>
        public const string HoldsFence = "The result holds a code fence, so it cannot replace the diagram's source";

        private const string NotShownText = "Show the note this came from to apply it";
        private const string ReadOnlyText = "This note is read-only";
        private const string ChangedText = "The text changed since the request; use Insert below or Copy";

        private const string WaitingForText = "Waiting for ";
        private const string WaitingForTheModelText = "Waiting for the model…";
        private const string WritingText = "Writing…";
        private const string FinishedInText = "Finished in ";

        private readonly SecretMask _mask;
        private readonly StringBuilder _raw = new();
        private readonly Func<DateTime> _utcNow;
        private DateTime? _startedAt;
        private DateTime? _endedAt;
        private string? _failure;
        private string? _applied;
        private bool _stopped;
        private bool _cutShort;

        /// <param name="destination">
        /// Where the text goes, in a word or two ("api.anthropic.com", "this PC"), for the source
        /// line; "" names none. The window reads it from the settings (<see cref="PadAiPrivacy.Destination"/>).
        /// </param>
        /// <param name="model">
        /// The model the request goes to ("claude-sonnet-5-5"), for the source line and the
        /// "Waiting for …" line; "" names none. The window reads it from the settings
        /// (<see cref="PadAiPrivacy.Model"/>). It is only shown: nothing is decided on it.
        /// </param>
        /// <param name="utcNow">
        /// The clock the request is timed with, read when it starts and when it ends; null is the
        /// PC's own. Tests hand in one they move by hand.
        /// </param>
        /// <param name="sourceFirst">
        /// True when the result is to be shown as its text first, though it is one the pane
        /// renders: the window says so for Ask AI on a note it does not show as Markdown. It is
        /// only how the pane starts; what goes into the note is the text either way.
        /// </param>
        /// <param name="budget">
        /// The limits this request runs within, sized for the model it goes to
        /// (<see cref="AiBudget"/>); null is the standard limits, those of a model whose context
        /// window is not known. The window reads it once, where it builds the session. It decides
        /// whether the source text is refused as too long, and the window sizes the request with
        /// this same object (<see cref="Budget"/>).
        /// </param>
        public AiSession(PadAiAction action, string sourceText, bool fromSelection, string? instruction = null, string destination = "",
                         string model = "", Func<DateTime>? utcNow = null, bool sourceFirst = false, AiBudget? budget = null)
        {
            Action = action;
            Original = sourceText ?? "";
            FromSelection = fromSelection;
            Destination = destination ?? "";
            Model = (model ?? "").Trim();
            SourceFirst = sourceFirst;
            Budget = budget ?? AiBudget.Standard;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _mask = SecretMask.Of(Original);
            // The text as it was selected, before a credential becomes its placeholder: where it was always measured.
            Refusal = action.TooLong(Original, Budget);
            // A refusal wins: text that cannot be sent is not asked an instruction for, which could never run.
            AwaitingInstruction = Refusal == null && ReferenceEquals(action, PadAiAction.Ask) && string.IsNullOrWhiteSpace(instruction);
            Instruction = ReferenceEquals(action, PadAiAction.Ask)
                ? NotePassages.WithoutSecretParts((instruction ?? "").Trim())   // typed or pasted: part of a reference is cleaned too
                : action.Instruction;
            UserMessage = PadAiPrompts.ForAction(Instruction, _mask.Text);
        }

        public PadAiAction Action { get; }
        public string Original { get; }
        public bool FromSelection { get; }

        /// <summary>Where the text goes, as the source line says it; "" when none is named.</summary>
        public string Destination { get; }

        /// <summary>The model the request goes to, as the source line names it; "" when none is named.</summary>
        public string Model { get; }

        /// <summary>True when the pane is to start this request's rendered result with Source on.</summary>
        public bool SourceFirst { get; }

        /// <summary>
        /// The limits this request was built with, never null: what refused or accepted its source
        /// text, and what its request is sized by (the output limit asked for, the length a reply
        /// is kept to). One object for both, so the two can never be of different models.
        /// </summary>
        public AiBudget Budget { get; }

        public string Instruction { get; }
        public string? Refusal { get; }
        public bool AwaitingInstruction { get; }
        public string UserMessage { get; }
        public bool Running { get; private set; }
        public bool Finished { get; private set; }

        public void Start()
        {
            if (Running || Finished) throw new InvalidOperationException("An AI session runs once");
            if (Refusal != null) throw new InvalidOperationException("This request is refused and cannot start");
            if (AwaitingInstruction) throw new InvalidOperationException("This request still needs an instruction");
            _startedAt = _utcNow();
            Running = true;
        }

        public void Append(string piece) { if (!Finished) _raw.Append(piece); }
        public void Fail(string message) { if (Finished) return; _failure = message; End(); }
        public void MarkCutShort() { if (!Finished) _cutShort = true; }
        public void Complete(bool stopped) { _stopped = stopped; End(); }

        /// <summary>
        /// The request is over, however it ended. The clock is read once, at its first end: the
        /// window calls <see cref="Complete"/> when the stream closes, after a <see cref="Fail"/> too.
        /// </summary>
        private void End()
        {
            if (!Finished) _endedAt = _utcNow();
            Running = false;
            Finished = true;
        }

        /// <summary>
        /// Replace selection put the result in the note (or a redo put it back): there is nothing
        /// left to replace, and the status says <paramref name="what"/> happened. Only a Replace
        /// is marked: Insert below leaves the source text where it is, so it turns nothing off.
        /// </summary>
        public void MarkApplied(string what) => _applied = what;

        /// <summary>The edit was undone and the source text is back as it was sent: Replace selection may be offered again.</summary>
        public void ClearApplied() => _applied = null;

        /// <summary>
        /// The reply without the blank lines around it. The fix of a diagram block that came back
        /// in a code fence, against its instruction, loses that one enclosing fence too: its
        /// result goes between the block's own fences (<see cref="PadAiAction.BlockFence"/>).
        /// While the reply streams, the fence shows until its closing line has arrived.
        /// </summary>
        private string CleanRaw
        {
            get
            {
                string clean = SelectionEdit.Clean(_raw.ToString());
                return Action.BlockFence == null ? clean : SelectionEdit.Clean(SelectionEdit.Unfenced(clean));
            }
        }

        /// <summary>The cleaned result with each credential placeholder turned back into its pill.</summary>
        public string ResultForNote => _mask.Unmask(CleanRaw);

        /// <summary>
        /// True when a line of <paramref name="result"/> would close a block opened by
        /// <paramref name="fence"/>: put between that block's fences, it would end the block
        /// early, and the note's own closing fence would then open one that never closes. A line
        /// of the other fence character, or of fewer of them, is text of the block and closes nothing.
        /// </summary>
        private static bool ClosesBlock(string result, string fence)
        {
            int start = 0;
            for (int i = 0; i <= result.Length; i++)
            {
                if (i < result.Length && result[i] != '\n' && result[i] != '\r') continue;
                if (FenceTracker.Closes(result.Substring(start, i - start), fence[0], fence.Length)) return true;
                start = i + 1;   // a CRLF gives an empty line in between, which closes nothing
            }
            return false;
        }

        /// <summary>
        /// True when a line of <paramref name="result"/> would open a fenced block: where no block
        /// stands around the source any more, such a line would start one that never closes.
        /// </summary>
        private static bool OpensBlock(string result)
        {
            foreach (string line in result.Split('\n', '\r'))   // a CRLF gives an empty line in between, which opens nothing
                if (FenceTracker.DelimiterOf(line) != null) return true;
            return false;
        }

        public AiPaneView View(AiSourceFacts facts)
        {
            string result = Refusal != null ? "" : ResultForNote;
            bool hasText = result.Length > 0;
            bool failed = _failure != null;
            bool clean = Finished && !failed && !_stopped && !_cutShort;
            bool showReplace = Action.Kind != PadAiKind.Read && FromSelection;
            string? credentialProblem = _mask.Problem(CleanRaw);
            // A fix goes inside its block: nothing in it may close the block, and Insert below, which
            // would land under the old source inside the block, is not offered at all. The fence it is
            // checked against is the one the block opens with now, when the window looked: the note
            // may have been edited since the fix was asked for (four backticks shortened to three).
            bool fix = Action.BlockFence != null;
            string? fence = fix ? facts.BlockFence ?? Action.BlockFence : null;
            bool closesBlock = fence != null && (fence.Length > 0 ? ClosesBlock(result, fence) : OpensBlock(result));

            bool canReplace = showReplace && Refusal == null && clean && _applied == null && hasText
                && facts.SourceShown && !facts.ReadOnly && facts.SourceUnchanged && credentialProblem == null && !closesBlock;
            bool canInsert = !fix && Refusal == null && Finished && hasText && !failed && facts.SourceShown && !facts.ReadOnly;

            string status = "";
            if (Refusal != null) status = Refusal;
            else if (failed) status = _failure!;
            else if (_applied != null) status = _applied;
            else if (_stopped) status = "Stopped";
            else if (_cutShort) status = "Cut short at the length limit";
            else if (clean && hasText)
            {
                // A whole result that cannot be put into the note: the status says why its buttons are off.
                // The first two reasons hold for every action (Insert below and Try again go with them);
                // the others are Replace selection's own.
                if (!facts.SourceShown) status = NotShownText;
                else if (facts.ReadOnly) status = ReadOnlyText;
                else if (showReplace && !facts.SourceUnchanged) status = ChangedText;
                else if (showReplace && credentialProblem != null) status = credentialProblem;
                else if (showReplace && closesBlock) status = HoldsFence;
            }

            // What is going on while it runs: nothing to show yet, or the reply coming in.
            string activity = !Running ? ""
                : hasText ? WritingText
                : Model.Length > 0 ? WaitingForText + Model + "…"
                : WaitingForTheModelText;
            // How long it took, for a request that finished whole and for no other: one that
            // was stopped, failed, was cut short or was refused says that in its status.
            string info = Refusal == null && clean && _startedAt is { } startedAt && _endedAt is { } endedAt
                ? FinishedInText + ChatDuration.Text(endedAt - startedAt)
                : "";

            return new AiPaneView(
                Title: Action.Name,
                // What it runs on, where that goes and to which model:
                // "Selection, 412 characters · to api.anthropic.com (claude-sonnet-5-5)".
                SourceLine: (FromSelection ? "Selection, " : "Whole note, ") + Count(Original.Length)
                            + (Destination.Length > 0 ? " · to " + Destination : "")
                            + (Model.Length > 0 ? " (" + Model + ")" : ""),
                AskForInstruction: AwaitingInstruction,
                Result: result,
                Markdown: Action.RendersMarkdown,
                Running: Running,
                Status: status,
                ShowReplace: showReplace,
                CanReplace: canReplace,
                CanInsert: canInsert,
                CanCopy: hasText,
                // Only on the note it came from: on another note, that note's text would be sent in its place.
                CanRetry: !Running && !AwaitingInstruction && Refusal == null && facts.SourceShown,
                CanShowChanges: Action.Kind == PadAiKind.Rewrite && Refusal == null && Finished && hasText && !failed,
                Original: Original,
                ShowInsert: !fix,
                Activity: activity,
                Info: info,
                SourceFirst: SourceFirst);
        }

        private static string Count(int n) =>
            n == 1 ? "1 character" : n.ToString("N0", CultureInfo.InvariantCulture) + " characters";
    }
}
