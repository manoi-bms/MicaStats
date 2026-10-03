using System;
using System.Globalization;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>What the window knows about the source text right now.</summary>
    public readonly record struct AiSourceFacts(bool SourceShown, bool ReadOnly, bool SourceUnchanged);

    /// <summary>Everything the AI pane draws; immutable.</summary>
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
        string Original);

    /// <summary>
    /// The state of one AI request and the rules for which buttons the pane offers
    /// (MicaPad AI spec 3.2 to 3.4). No WPF; the window feeds it the stream and the facts.
    /// A session runs once: Try again builds a new <see cref="AiSession"/>.
    /// </summary>
    public sealed class AiSession
    {
        private readonly SecretMask _mask;
        private readonly StringBuilder _raw = new();
        private string? _failure;
        private string? _applied;
        private bool _stopped;
        private bool _cutShort;

        public AiSession(PadAiAction action, string sourceText, bool fromSelection, string? instruction = null)
        {
            Action = action;
            Original = sourceText ?? "";
            FromSelection = fromSelection;
            _mask = SecretMask.Of(Original);
            AwaitingInstruction = ReferenceEquals(action, PadAiAction.Ask) && string.IsNullOrWhiteSpace(instruction);
            Instruction = ReferenceEquals(action, PadAiAction.Ask)
                ? NotePassages.WithoutSecrets((instruction ?? "").Trim())
                : action.Instruction;
            Refusal = action.TooLong(Original.Length);
            UserMessage = PadAiPrompts.ForAction(Instruction, _mask.Text);
        }

        public PadAiAction Action { get; }
        public string Original { get; }
        public bool FromSelection { get; }
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
            Running = true;
        }

        public void Append(string piece) { if (!Finished) _raw.Append(piece); }
        public void Fail(string message) { if (Finished) return; _failure = message; Running = false; Finished = true; }
        public void MarkCutShort() { if (!Finished) _cutShort = true; }
        public void Complete(bool stopped) { _stopped = stopped; Running = false; Finished = true; }
        public void MarkApplied(string what) => _applied = what;

        private string CleanRaw => SelectionEdit.Clean(_raw.ToString());

        /// <summary>The cleaned result with each credential placeholder turned back into its pill.</summary>
        public string ResultForNote => _mask.Unmask(CleanRaw);

        public AiPaneView View(AiSourceFacts facts)
        {
            bool hasText = Refusal == null && ResultForNote.Length > 0;
            bool failed = _failure != null;
            bool clean = Finished && !failed && !_stopped && !_cutShort;
            bool showReplace = Action.Kind != PadAiKind.Read && FromSelection;
            string? credentialProblem = _mask.Problem(CleanRaw);

            bool canReplace = showReplace && Refusal == null && clean && _applied == null && hasText
                && facts.SourceShown && !facts.ReadOnly && facts.SourceUnchanged && credentialProblem == null;
            bool canInsert = Refusal == null && Finished && hasText && !failed && facts.SourceShown && !facts.ReadOnly;

            string status = "";
            if (Refusal != null) status = Refusal;
            else if (failed) status = _failure!;
            else if (_applied != null) status = _applied;
            else if (_stopped) status = "Stopped";
            else if (_cutShort) status = "Cut short at the length limit";
            else if (showReplace && clean && hasText && !canReplace)
            {
                if (!facts.SourceShown) status = "Show the note this came from to apply it";
                else if (facts.ReadOnly) status = "This note is read-only";
                else if (!facts.SourceUnchanged) status = "The text changed since the request; use Insert below or Copy";
                else if (credentialProblem != null) status = credentialProblem;
            }

            return new AiPaneView(
                Title: Action.Name,
                SourceLine: (FromSelection ? "Selection, " : "Whole note, ") + Count(Original.Length),
                AskForInstruction: AwaitingInstruction,
                Result: Refusal != null ? "" : ResultForNote,
                Markdown: Action.RendersMarkdown,
                Running: Running,
                Status: status,
                ShowReplace: showReplace,
                CanReplace: canReplace,
                CanInsert: canInsert,
                CanCopy: hasText,
                CanRetry: !Running && !AwaitingInstruction && Refusal == null,
                CanShowChanges: Action.Kind == PadAiKind.Rewrite && Refusal == null && Finished && hasText && !failed,
                Original: Original);
        }

        private static string Count(int n) =>
            n == 1 ? "1 character" : n.ToString("N0", CultureInfo.InvariantCulture) + " characters";
    }
}
