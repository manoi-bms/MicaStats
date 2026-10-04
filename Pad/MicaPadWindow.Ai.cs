using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;
using static Kil0bitSystemMonitor.Pad.EditorMenus;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's AI actions on text (MicaPad AI spec 3 and 5): the editor's AI menu and
    /// Ctrl+Shift+A, one request at a time shown in the AI pane, the anchors that follow the
    /// source text while the note is edited, and Replace selection, Insert below and Copy.
    ///
    /// <para>
    /// Which buttons the pane offers, and why not, is <see cref="AiSession"/>'s to say; this file
    /// feeds it the stream and the facts about the source text. Nothing is sent unless AI is on
    /// and the user runs an action, a note changes only on a click on Replace selection or Insert
    /// below, and nothing here logs note text, an instruction or a reply.
    /// </para>
    /// </summary>
    public partial class MicaPadWindow
    {
        /// <summary>What the status bar says when an AI action is asked for while AI is off.</summary>
        internal const string AiOffText = "Turn on Settings → MicaPad → AI to use AI here";

        private const string AiReadOnlyText = "This note is read-only";
        private const string AiNoTextText = "There is no text to work on";
        private const string AiNotReadyText = "AI is not available yet";
        private const string AiShowSourceText = "Show the note this came from to try again";
        private const string AiSourceGoneText = "The text this ran on is gone; select text and run the action again";
        private const string AiDiagramGoneText = "That diagram is no longer there";

        /// <summary>Tells when the history preview opens or closes: it makes the note read-only for the pane.</summary>
        private static readonly DependencyPropertyDescriptor s_previewVisibility =
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));

        /// <summary>
        /// One request and where its text came from. The anchors (a selection only) follow the
        /// source text through edits elsewhere in the note; they belong to <see cref="Document"/>.
        /// </summary>
        private sealed class AiRun
        {
            public AiRun(AiSession session, OpenNote note, TextDocument document, string? instruction)
            {
                Session = session;
                Note = note;
                Document = document;
                Instruction = instruction;
            }

            public AiSession Session { get; }

            /// <summary>The note the text came from: the only note the result may ever be put into.</summary>
            public OpenNote Note { get; }

            public TextDocument Document { get; }

            /// <summary>What the user typed for Ask AI; null for the other actions.</summary>
            public string? Instruction { get; }

            public TextAnchor? Start { get; set; }

            public TextAnchor? End { get; set; }

            /// <summary>
            /// After Replace selection: the place the result went to, with whatever is typed right
            /// at its edges. An undo makes it read as the original again, and a redo as the result
            /// (<see cref="ReplacedWith"/>) once more; null when nothing was replaced.
            /// </summary>
            public (TextAnchor Start, TextAnchor End)? Replaced { get; set; }

            /// <summary>The text Replace selection wrote, line endings and all; null when nothing was replaced.</summary>
            public string? ReplacedWith { get; set; }

            /// <summary>True while a Replace selection is undone: its place holds the original again, and a redo would bring the result back.</summary>
            public bool Undone { get; set; }

            public CancellationTokenSource Cancel { get; } = new();

            /// <summary>The facts the pane was last drawn with.</summary>
            public AiSourceFacts? Drawn { get; set; }

            /// <summary>True while the pane shows a status that holds for one drawing only ("Inserted below"): the next edit draws it again.</summary>
            public bool Noticed { get; set; }

            /// <summary>
            /// For the fix of a diagram block: the fence around its source when the window last
            /// read it, "" when none stood around it; null before the first read. The pane is
            /// drawn with it between the edits of one update group. Nothing is decided on it:
            /// Replace selection and Try again read the fence at the click.
            /// </summary>
            public string? FenceRead { get; set; }

            /// <summary>True from an edit of the source note until its update group ends, when the fence is read again, once.</summary>
            public bool FenceReadPending { get; set; }
        }

        private AiRun? _ai;
        private Action<string>? _aiCopy;

        /// <summary>
        /// Whether Settings → MicaPad → AI is on: read from the app's settings each time it is
        /// asked, and off while there are none. Tests replace it.
        /// </summary>
        internal Func<bool> AiEnabled { get; set; } = () => AiOnIn(App.ConfigService?.Config);

        /// <summary>
        /// True only when <paramref name="config"/> is there and its "Use AI in MicaPad" is on.
        /// No settings means off, and no other switch counts (not the Ask MicaStats one).
        /// </summary>
        internal static bool AiOnIn(Models.AppConfig? config) => config?.PadAiEnabled == true;

        /// <summary>
        /// The runner for one request, or null before the settings load: the app's own, with the
        /// provider, the key and the daily count shared with Ask MicaStats. Tests replace it.
        /// </summary>
        internal Func<PadAiRunner?> AiRunnerFactory { get; set; } = App.CreatePadAiRunner;

        /// <summary>
        /// Where the text of a request goes, in a word or two, for the pane's source line and the
        /// notes status: the provider of Settings → AI as <see cref="PadAiPrivacy.Destination"/>
        /// names it, or "" before the settings load. Tests replace it.
        /// </summary>
        internal Func<string> AiDestination { get; set; } = DestinationInSettings;

        private static string DestinationInSettings()
        {
            var config = App.ConfigService?.Config;
            return config == null ? "" : PadAiPrivacy.Destination(config.AiProvider, config.AiCompatibleBaseUrl);
        }

        /// <summary>
        /// The destination as the settings have it now. It is only shown, never decided on: one
        /// that cannot be read is not named, and the failure is logged.
        /// </summary>
        private string AiDestinationNow()
        {
            string destination = "";
            GuardAi("Reading where AI text goes", () => destination = AiDestination() ?? "");
            return destination;
        }

        /// <summary>
        /// The model a request goes to, for the pane's source line and its "Waiting for …" line:
        /// the model of the provider of Settings → AI as <see cref="PadAiPrivacy.Model"/> names
        /// it, or "" before the settings load. Tests replace it.
        /// </summary>
        internal Func<string> AiModel { get; set; } = ModelInSettings;

        private static string ModelInSettings() => AiModelIn(App.ConfigService?.Config);

        /// <summary>The model <paramref name="config"/> names for its provider; no settings name none.</summary>
        internal static string AiModelIn(Models.AppConfig? config) =>
            config == null ? "" : PadAiPrivacy.Model(config.AiProvider, config.AiClaudeModel, config.AiCompatibleModel);

        /// <summary>
        /// The model as the settings have it now. It is only shown, never decided on: one that
        /// cannot be read is not named, and the failure is logged.
        /// </summary>
        private string AiModelNow()
        {
            string model = "";
            GuardAi("Reading the AI model's name", () => model = AiModel() ?? "");
            return model;
        }

        /// <summary>Puts a result on the clipboard: the pad's own clipboard helper unless a test replaces it.</summary>
        internal Action<string> AiCopy
        {
            get => _aiCopy ?? SetClipboardText;
            set => _aiCopy = value;
        }

        /// <summary>Opens Settings on the MicaPad section (Set up AI…). Tests replace it.</summary>
        internal Action OpenPadSettings { get; set; } = () => App.ShowSettingsSection("MicaPad");

        /// <summary>Where the one line per action is logged: its id, the characters in the request and in the reply, and the outcome. Tests replace it.</summary>
        internal Action<string> AiLog { get; set; } = message => DiagnosticsLog.Log("pad", message);

        /// <summary>Whether the keyboard is in the AI pane. Tests set it: a window that is never shown has no keyboard focus.</summary>
        internal Func<bool>? AiPaneHasFocus { get; set; }

        /// <summary>Draws a view in the AI pane. Tests replace it, to see that a failure while drawing never escapes into typing.</summary>
        internal Action<AiPaneView>? AiDraw { get; set; }

        /// <summary>The request the AI pane shows, or null while the pane is closed.</summary>
        internal AiSession? AiSessionNow => _ai?.Session;

        /// <summary>
        /// How many times this window read the fence around a fix's source. Each read copies and
        /// classifies the whole note, so the tests count them: once per batch of edits, not per edit.
        /// </summary>
        internal int AiFenceReads { get; private set; }

        /// <summary>The pane's buttons, and the two things that end or change a request from outside: a note closing and its text changing.</summary>
        private void ConfigureAi()
        {
            AiPanel.ReplaceRequested += () => GuardAi("Replacing the selection with an AI result", ReplaceWithAiResult);
            AiPanel.InsertRequested += () => GuardAi("Inserting an AI result", InsertAiResult);
            AiPanel.CopyRequested += () => GuardAi("Copying an AI result", CopyAiResult);
            AiPanel.StopRequested += () => GuardAi("Stopping an AI request", StopAi);
            AiPanel.RetryRequested += () => GuardAi("Trying an AI action again", RetryAi);
            AiPanel.CloseRequested += () => GuardAi("Closing the AI pane", () => CloseAi(focusEditor: true));
            AiPanel.InstructionEntered += instruction => GuardAi("Asking AI", () => EnterAiInstruction(instruction));
            SearchPanel.Ask = AskNotesAsync;
            SearchPanel.CopyAnswer = answer => GuardAi("Copying an answer", () => CopyNotesAnswer(answer));
            SearchPanel.Warn = message => Warn(message);   // what the pane reports goes where the window's own warnings go
            _workspace.NoteClosing += OnAiNoteClosing;
            _workspace.NoteTextChanged += OnAiNoteTextChanged;
            s_previewVisibility.AddValueChanged(PreviewPanel, OnAiPreviewChanged);
        }

        /// <summary>
        /// Runs a step of an AI action so a failure is logged and never thrown into MicaStats, which
        /// has no dispatcher exception handler. False when it threw.
        /// </summary>
        private bool GuardAi(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                WarnAi(what, ex);
                return false;
            }
        }

        /// <summary>Logs a failed step by the exception's type only: a message could quote the note.</summary>
        private void WarnAi(string what, Exception ex)
        {
            try
            {
                Warn(what + " failed (" + ex.GetType().Name + ")");
            }
            catch (Exception)
            {
                // Logging is best effort; it must not throw either.
            }
        }

        /// <summary>The close path: the request is cancelled and the workspace stops reaching this window. Safe to call twice.</summary>
        private void DetachAi()
        {
            _workspace.NoteClosing -= OnAiNoteClosing;
            _workspace.NoteTextChanged -= OnAiNoteTextChanged;
            s_previewVisibility.RemoveValueChanged(PreviewPanel, OnAiPreviewChanged);
            CancelAi(_ai);
            _ai = null;
            StopNotesAnswer();   // an answer from notes ends with the window too
        }

        /// <summary>
        /// Cancels a request. A callback on its token that throws is logged here, never thrown
        /// into a click, a key or the window closing.
        /// </summary>
        private void CancelAi(AiRun? run)
        {
            if (run == null) return;
            try
            {
                run.Cancel.Cancel();
            }
            catch (Exception ex)
            {
                WarnAi("Cancelling an AI request", ex);
            }
        }

        // ---- the menu and the shortcut ---------------------------------------------------------

        /// <summary>
        /// The editor menu's AI submenu: the actions while AI is on, Set up AI… while it is off.
        /// Draw as diagram only in a note shown as Markdown, where a fenced block is drawn; there,
        /// with the caret in a diagram or math block that shows an error, Fix diagram too.
        /// </summary>
        private System.Windows.Controls.MenuItem BuildAiMenu()
        {
            bool on = AiOnInMenus();
            bool markdown = ReferenceEquals(_resolved.Effective, PadLanguages.Markdown);
            return AiMenu(Editor, on, RunAiFromMenu, () => OpenPadSettings(), on && markdown ? FixDiagramAtCaret() : null, diagrams: markdown);
        }

        /// <summary>
        /// What a menu names its AI entries by: the actions while AI is on, Set up AI… while it is
        /// off or cannot be read. The editor's AI menu and a diagram's error box ask it as they
        /// open. It only names entries: every action asks again at its own gate.
        /// </summary>
        private bool AiOnInMenus() => !AiIsOff();

        /// <summary>
        /// What Fix diagram does when the caret is in a diagram or math block that shows an error,
        /// or null (no entry): this editor draws no diagrams, the caret is in no such block, or the
        /// block shows no error a fix could cure. A failure while asking is logged and means none.
        ///
        /// <para>
        /// The menu is built before the click, so the entry holds no line numbers: it keeps the
        /// block's closing fence line, which follows the block through edits, and asks the board
        /// again at the click, as the error box does. The fix then runs on the block where it is
        /// by then; a block that is gone says so, and one that draws by then asks for nothing.
        /// </para>
        /// </summary>
        private Action? FixDiagramAtCaret()
        {
            Action? fix = null;
            GuardAi("Looking for a failing diagram", () =>
            {
                if (_language.DiagramBoard is not { } board || !ReferenceEquals(board.Document, Editor.Document)) return;
                if (board.FailureAt(Editor.TextArea.Caret.Line) is not { } failing) return;
                DocumentLine closing = board.Document.GetLineByNumber(failing.CloseLine);
                fix = () =>
                {
                    DiagramFailure? now = ReferenceEquals(board.Document, Editor.Document) ? board.FailureOf(closing) : null;
                    if (now != null) _ = FixDiagramAsync(now.OpenLine, now.CloseLine, now.Kind, now.Message);
                    else if (board.BlockClosedBy(closing) == null) ShowStatus(AiDiagramGoneText);
                };
            });
            return fix;
        }

        private void RunAiFromMenu(PadAiAction action)
        {
            if (ReferenceEquals(action, PadAiAction.Ask)) AskAi();
            else _ = RunAiAsync(action);
        }

        /// <summary>
        /// Ctrl+Shift+A: opens the AI pane on Ask AI for the selection (or the whole note), with the
        /// keyboard in its instruction box. Pressed with the keyboard in the open pane, it closes
        /// the pane. While AI is off it opens nothing and says how to turn it on.
        /// </summary>
        internal void ToggleAi()
        {
            // Closing is always allowed, with AI on or off.
            if (AiPanel.Visibility == Visibility.Visible && (AiPaneHasFocus?.Invoke() ?? AiPanel.IsKeyboardFocusWithin))
            {
                CloseAi(focusEditor: true);
                return;
            }
            if (AiIsOff())
            {
                ShowStatus(AiOffText);
                return;
            }
            AskAi();
        }

        /// <summary>Opens the pane on Ask AI, waiting for its instruction. Nothing is sent until one is entered.</summary>
        private void AskAi()
        {
            AiRun? before = _ai;
            _ = RunAiAsync(PadAiAction.Ask);   // ends at once: it only asks for the instruction
            // Only when this call opened the question: a refusal (AI off, no text) leaves the keyboard where it is.
            if (!ReferenceEquals(_ai, before) && _ai is { Session.AwaitingInstruction: true }) AiPanel.FocusInstruction();
        }

        // ---- running an action -----------------------------------------------------------------

        /// <summary>
        /// True unless Settings → MicaPad → AI is on. A setting that cannot be read counts as off:
        /// without the user's consent nothing is built, counted or sent.
        /// </summary>
        private bool AiIsOff()
        {
            bool on = false;
            GuardAi("Reading the AI setting", () => on = AiEnabled());
            return !on;
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the selection, or on the whole note when nothing is
        /// selected and the action allows it, and shows it in the AI pane. The task completes when
        /// the request ends (at once when nothing was sent) and never faults.
        /// </summary>
        internal Task RunAiAsync(PadAiAction action, string? instruction = null) => RunAiAsync(action, instruction, null);

        /// <summary>
        /// With <paramref name="again"/>, the earlier request whose text this one takes: its
        /// selection where it is now, or its whole note. Never what is selected or shown at the click.
        /// With <paramref name="select"/> (Fix with AI), a step that selects the text to run on
        /// first and gives back the action as it then runs (a fix learns its block's fence from
        /// the note); null from it means there is nothing to run on, and nothing starts. It runs
        /// after the gate, never before it: while AI is off the note is not read and the selection
        /// stays.
        /// </summary>
        private async Task RunAiAsync(PadAiAction action, string? instruction, AiRun? again, Func<PadAiAction, PadAiAction?>? select = null)
        {
            // The consent gate. Every way in ends here (the menu, Ctrl+Shift+A, an instruction
            // entered in the pane, Try again, a direct call), so it does not rest on any caller:
            // while AI is off no session is made, no runner is built, nothing is counted or sent.
            if (AiIsOff())
            {
                ShowStatus(AiOffText);
                return;
            }

            AiRun? run = null;
            if (!GuardAi("Starting an AI action", () =>
                {
                    PadAiAction? ready = select == null ? action : select(action);
                    if (ready != null) run = BeginAi(ready, instruction, again);
                })) return;
            if (run != null) await StreamAiAsync(run);
        }

        /// <summary>
        /// Fix with AI (part 2, spec 2.2): asks for a rewrite of the source of the diagram or math
        /// block whose fences are on <paramref name="openLine"/> and <paramref name="closeLine"/>
        /// (1-based), naming its <paramref name="kind"/> (the fence word) and the renderer's
        /// <paramref name="message"/>. The error box and the AI menu's Fix diagram call it.
        ///
        /// <para>
        /// It is an action like any other: it goes through <c>RunAiAsync</c> and so through its
        /// gate, and it never reaches the runner by another way. Once the gate has passed, the
        /// block's source is selected (<see cref="SelectDiagramSource"/>), and the action then
        /// runs on that selection as a rewrite does: the pane, Changes, Replace selection on that
        /// range alone, the limits, the masking and the log are part 1's. What a fix adds is the
        /// session's: a reply in a code fence is unwrapped, one that would close the block's fence
        /// cannot replace, and Insert below is not offered.
        /// </para>
        /// </summary>
        internal Task FixDiagramAsync(int openLine, int closeLine, string kind, string message) =>
            RunAiAsync(PadAiAction.FixDiagram(kind, message), null, null, _ => SelectDiagramSource(openLine, closeLine, kind, message));

        /// <summary>
        /// Selects the source of the block between the two fence lines, so the user sees what will
        /// be sent and replaced: the lines strictly between the fences (after the type line, in the
        /// kroki form), never a fence. A fold that hides any of it is opened first. The lines are
        /// read again here, as the note is now: they came from a click, and the note may have been
        /// edited since. Gives back the fix as it runs, built from what the note says now: the
        /// fence its block opens with, and the ids of the credentials in its source, none of
        /// which may leave in the renderer's <paramref name="message"/> however it quotes them.
        /// Null, with the reason in the status bar and the selection and the folds left alone,
        /// when there is nothing to run on: the lines no longer hold a diagram's fence pair, the
        /// block is empty, or the note is read-only.
        /// </summary>
        private PadAiAction? SelectDiagramSource(int openLine, int closeLine, string kind, string message)
        {
            if (_shown == null) return null;
            if (AiReadOnly)
            {
                ShowStatus(AiReadOnlyText);
                return null;
            }

            TextDocument document = Editor.Document;
            var lines = new string[document.LineCount];
            foreach (DocumentLine line in document.Lines) lines[line.LineNumber - 1] = document.GetText(line);
            if (DiagramBlocks.SourceLines(lines, openLine, closeLine) is not { } source)
            {
                ShowStatus(AiDiagramGoneText);
                return null;
            }

            int start = 0, end = 0;
            if (source.Last >= source.First)
            {
                start = document.GetLineByNumber(source.First).Offset;
                end = document.GetLineByNumber(source.Last).EndOffset;   // without the last line's break
            }
            string sourceText = document.GetText(start, end - start);
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                ShowStatus(AiNoTextText);
                return null;
            }

            ShowFolded(document.GetLineByNumber(closeLine), start, end);
            Editor.Select(start, end - start);
            // SourceLines found the pair, so the opening line is a fence; the action's own default is three backticks.
            string blockFence = FenceTracker.DelimiterOf(lines[openLine - 1]) is { } fence ? new string(fence.Char, fence.Length) : "```";
            return PadAiAction.FixDiagram(kind, message, blockFence, SecretTokens.Find(sourceText).Select(pill => pill.Id).Distinct());
        }

        /// <summary>
        /// Opens every fold that hides text between <paramref name="start"/> and
        /// <paramref name="end"/>, the source of the block <paramref name="closing"/> closes: what
        /// is about to be sent and replaced must be in sight. AvalonEdit opens a fold the caret
        /// moves into, but the selection's caret lands at the very end of Hide code's fold, which
        /// is not inside it. Hide code is undone through the board, so its button follows.
        /// </summary>
        private void ShowFolded(DocumentLine closing, int start, int end)
        {
            if (_language.DiagramBoard is { } board && ReferenceEquals(board.Document, Editor.Document) && board.IsCodeHidden(closing))
                board.SetCodeHidden(closing, false);
            if (_language.Folding?.Manager is not { } manager) return;
            foreach (ICSharpCode.AvalonEdit.Folding.FoldingSection fold in manager.AllFoldings)
                if (fold.IsFolded && fold.StartOffset < end && fold.EndOffset > start) fold.IsFolded = false;
        }

        /// <summary>Enter in the pane's instruction box: Ask AI runs on the text the pane names.</summary>
        private void EnterAiInstruction(string instruction)
        {
            if (_ai is { Session.AwaitingInstruction: true }) RerunAi(PadAiAction.Ask, instruction);
        }

        /// <summary>
        /// Try again, and the instruction entered for Ask AI: the request goes out again on the
        /// text the pane names and on nothing else. That is the earlier request's selection where
        /// it is now, or its whole note; what is selected or shown at the click does not count. So
        /// it runs only while the source note is shown and, for a selection, while its text is
        /// still there. Otherwise nothing is sent and the status bar says why: another note's
        /// text, or a whole note in place of a selection, never goes out under the pane's
        /// "Selection, 412 characters".
        /// </summary>
        private void RerunAi(PadAiAction action, string? instruction)
        {
            if (_ai is not { } run) return;
            if (AiIsOff())
            {
                ShowStatus(AiOffText);
                return;
            }
            if (!AiSourceShown(run))
            {
                ShowStatus(AiShowSourceText);
                return;
            }
            if (run.Session.FromSelection && SourceRange(run) == null)
            {
                ShowStatus(AiSourceGoneText);
                return;
            }
            // BeginAi asks both again where it reads the text. The fix of a diagram block also
            // reads its block's fence again, once the gate has passed.
            _ = RunAiAsync(action, instruction, run, action.BlockFence == null ? null : fix => FixWithFenceNow(fix, run));
        }

        /// <summary>
        /// Try again for the fix of a diagram block: the fix with the fence its block opens with
        /// as the note reads now. The fences may have been edited since the fix was asked for
        /// (four backticks shortened to three), and the new reply must be checked against what is
        /// there. Null, with the reason in the status bar, when no block stands around the source
        /// any more: there is no diagram left to fix.
        /// </summary>
        private PadAiAction? FixWithFenceNow(PadAiAction fix, AiRun run)
        {
            if (EnclosingFence(run) is { } fence) return fix with { BlockFence = fence };
            ShowStatus(AiDiagramGoneText);
            return null;
        }

        /// <summary>
        /// The run of characters the fenced block around the request's source opens with, as the
        /// note reads now (three backticks, four tildes, <c>$$</c>). Null when the source stands in
        /// no block any more: its place is gone, a fence was typed into it, or the fences around
        /// it were deleted. Read from the whole text, as the fences are when a fix is asked for.
        /// </summary>
        private string? EnclosingFence(AiRun run)
        {
            AiFenceReads++;
            if (run.Start is not { IsDeleted: false } start || run.End is not { IsDeleted: false } end || end.Offset < start.Offset) return null;

            TextDocument document = run.Document;
            var lines = new string[document.LineCount];
            foreach (DocumentLine line in document.Lines) lines[line.LineNumber - 1] = document.GetText(line);
            MdFence[] kinds = FenceTracker.Classify(lines);

            int first = document.GetLineByOffset(start.Offset).LineNumber, last = document.GetLineByOffset(end.Offset).LineNumber;
            for (int n = first; n <= last; n++)
                if (kinds[n - 1] != MdFence.Inside) return null;

            // Upward through the block's lines: the delimiter above them is the one that opened it.
            int open = first - 1;
            while (open >= 1 && kinds[open - 1] == MdFence.Inside) open--;
            if (open < 1 || kinds[open - 1] != MdFence.Delimiter) return null;
            return FenceTracker.DelimiterOf(lines[open - 1]) is { } fence ? new string(fence.Char, fence.Length) : null;
        }

        /// <summary>
        /// For the fix of a diagram block whose result is in: the fence around its source, "" when
        /// none stands around it any more. The session checks the result against it. Null for
        /// every other request: not a fix, still running, or its source not shown.
        ///
        /// <para>
        /// With <paramref name="now"/> the note is read: at the click on Replace selection, which
        /// is decided on it, and at a drawing of the pane that no edit asked for. Without it the
        /// answer is the last one read. Reading copies and classifies the whole note, and an edit
        /// may be one of thousands in an update group (Replace All), so an edit only draws with
        /// what was read last and <see cref="ReadFenceAfterUpdate"/> reads once when its group ends.
        /// </para>
        /// </summary>
        private string? FixFence(AiRun run, bool now)
        {
            if (run.Session.Action.BlockFence == null || !run.Session.Finished || !AiSourceShown(run)) return null;
            if (now || run.FenceRead == null) run.FenceRead = EnclosingFence(run) ?? "";
            return run.FenceRead;
        }

        /// <summary>
        /// An edit of the source note may have changed the fence around a fix's source: the fence
        /// is read again, and the pane drawn if that changes what it says, once the update group
        /// the edit belongs to has ended. One read for the group, however many edits it holds; a
        /// single edit is a group of its own, so typing on a fence line is told at once.
        /// </summary>
        private void ReadFenceAfterUpdate(AiRun run)
        {
            if (run.FenceReadPending || run.Session.Action.BlockFence == null || !run.Session.Finished) return;
            TextDocument document = run.Document;
            if (!document.IsInUpdate) return;   // no edit of this document is under way: what was read still holds

            run.FenceReadPending = true;
            EventHandler? finished = null;
            finished = (_, _) =>
            {
                document.UpdateFinished -= finished;
                run.FenceReadPending = false;
                if (!ReferenceEquals(_ai, run)) return;
                // Raised as the edit ends, so it is guarded like the edit itself.
                GuardAi("Following an edit of the AI source text", () =>
                {
                    AiSourceFacts facts = AiFacts(run);
                    if (facts != run.Drawn) DrawAi(run, facts);
                });
            };
            document.UpdateFinished += finished;
        }

        /// <summary>
        /// Everything before the request: the refusals, the source text, the session, its anchors
        /// and the pane. Returns the run to stream, or null when there is nothing to send: a
        /// refusal, or Ask AI still waiting for its instruction. Without <paramref name="again"/>
        /// the source is the selection, or the whole note when nothing is selected and the action
        /// allows it.
        ///
        /// <para>
        /// With <paramref name="again"/> (Try again, an entered instruction) the source is that
        /// earlier request's, and it is worked out here, where the text is read, not taken from
        /// the caller: the note must be the one shown, in the document the anchors live in, and
        /// a selection's range must still be there. So no caller, and nothing that happens
        /// between a click and this point, can have another note's text read under it.
        /// </para>
        /// </summary>
        private AiRun? BeginAi(PadAiAction action, string? instruction, AiRun? again)
        {
            if (_shown is not { } note) return null;
            if (again != null && !AiSourceShown(again))
            {
                ShowStatus(AiShowSourceText);
                return null;
            }
            if (again == null && Editor.TextArea.Selection is RectangleSelection)
            {
                ShowStatus(RectangleRefused);
                return null;
            }
            if (action.Kind == PadAiKind.Rewrite && AiReadOnly)
            {
                ShowStatus(AiReadOnlyText);
                return null;
            }

            TextDocument document = Editor.Document;
            bool fromSelection;
            int start = 0, length;
            if (again != null)
            {
                fromSelection = again.Session.FromSelection;
                if (!fromSelection) length = document.TextLength;
                else if (SourceRange(again) is { } range) (start, length) = range;
                else
                {
                    ShowStatus(AiSourceGoneText);
                    return null;
                }
            }
            else
            {
                fromSelection = Editor.SelectionLength > 0;
                if (fromSelection) (start, length) = (Editor.SelectionStart, Editor.SelectionLength);
                else length = action.NeedsSelection ? 0 : document.TextLength;
            }
            if (fromSelection) (start, length) = WholeMarkers(document, start, length);

            string source = document.GetText(start, length);
            if (string.IsNullOrWhiteSpace(source))
            {
                ShowStatus(AiNoTextText);
                return null;
            }

            CancelAi(_ai);   // a new action ends the one still running
            var session = new AiSession(action, source, fromSelection, instruction, AiDestinationNow(), AiModelNow());
            var run = new AiRun(session, note, document, instruction);
            if (fromSelection)
            {
                // Text typed right before or right after the source stays outside it.
                run.Start = Anchor(document, start, AnchorMovementType.AfterInsertion);
                run.End = Anchor(document, start + length, AnchorMovementType.BeforeInsertion);
            }
            _ai = run;
            OpenAiPane();
            RefreshAi();

            if (session.Refusal != null)
            {
                LogAi(session, 0, 0, "refused");
                return null;
            }
            return session.AwaitingInstruction ? null : run;
        }

        /// <summary>One request, from the runner to the end of its stream. The pane follows every update; a run that was replaced draws nothing.</summary>
        private async Task StreamAiAsync(AiRun run)
        {
            AiSession session = run.Session;
            CancellationToken token = run.Cancel.Token;
            int request = 0, back = 0;
            bool failed = false, cutShort = false;
            try
            {
                // Asked again, right before the request: the setting can be turned off while the
                // pane waits for an instruction, or between the click and here. Off: no runner is
                // built and nothing is sent; the pane says why.
                if (AiIsOff())
                {
                    failed = true;
                    session.Fail(AiOffText);
                }
                else if (AiRunnerFactory() is not { } runner)
                {
                    failed = true;
                    session.Fail(AiNotReadyText);
                }
                else
                {
                    session.Start();
                    request = session.UserMessage.Length;
                    ShowAi(run);
                    // The runner never throws and ends with Done, after a cancel too.
                    await foreach (PadAiUpdate update in runner.RunAsync(session.UserMessage, token))
                    {
                        switch (update.Kind)
                        {
                            case PadAiUpdateKind.Text:
                                if (!string.IsNullOrEmpty(update.Text))
                                {
                                    session.Append(update.Text);
                                    back += update.Text.Length;
                                }
                                break;
                            case PadAiUpdateKind.Error:
                                failed = true;
                                session.Fail(update.Text ?? "The AI request failed.");
                                break;
                            case PadAiUpdateKind.CutShort:
                                cutShort = true;
                                session.MarkCutShort();
                                break;
                        }
                        ShowAi(run);
                    }
                    session.Complete(stopped: token.IsCancellationRequested);
                }
            }
            catch (Exception ex)
            {
                // The request path never throws into the window: the pane says what went wrong.
                failed = true;
                WarnAi("An AI action", ex);
                CancelAi(run);
                GuardAi("Ending a failed AI action", () => session.Fail(AiErrorText.Describe(ex)));
            }

            GuardAi("Showing an AI result", () => ShowAi(run));
            LogAi(session, request, back, failed ? "failed" : token.IsCancellationRequested ? "stopped" : cutShort ? "cut short" : "ok");
        }

        /// <summary>
        /// The action's id, how many characters its request held and how many came back, and how
        /// it ended. Never the text. "In the request", not "sent": the runner may refuse a request
        /// handed to it (no key, the daily limit) without sending anything.
        /// </summary>
        private void LogAi(AiSession session, int request, int back, string outcome)
        {
            try
            {
                AiLog("AI " + session.Action.Id + ": " + request.ToString(CultureInfo.InvariantCulture) + " chars in the request, "
                      + back.ToString(CultureInfo.InvariantCulture) + " chars back, " + outcome);
            }
            catch (Exception)
            {
                // Logging is best effort; it must not end the request path with an exception.
            }
        }

        private static TextAnchor Anchor(TextDocument document, int offset, AnchorMovementType movement)
        {
            TextAnchor anchor = document.CreateAnchor(offset);
            anchor.MovementType = movement;
            anchor.SurviveDeletion = true;
            return anchor;
        }

        /// <summary>
        /// A range that starts or ends inside a credential marker grows to take that marker
        /// whole. Half a marker is not a marker, so it would not be masked, and part of the
        /// credential's id would be sent.
        /// </summary>
        private static (int Start, int Length) WholeMarkers(TextDocument document, int start, int length)
        {
            int end = start + length;
            if (MarkerAcross(document, start) is { } first) start = first.Start;
            if (MarkerAcross(document, end) is { } last) end = last.End;
            return (start, end - start);
        }

        /// <summary>The credential marker that <paramref name="offset"/> lies inside (not at an edge of), or null.</summary>
        private static (int Start, int End)? MarkerAcross(TextDocument document, int offset)
        {
            // A marker around the offset begins and ends within one marker's length of it.
            int from = Math.Max(0, offset - ReferenceLength);
            int to = Math.Min(document.TextLength, offset + ReferenceLength);
            foreach (SecretReference marker in SecretTokens.Find(document.GetText(from, to - from)))
            {
                int start = from + marker.Offset, end = start + marker.Length;
                if (start < offset && offset < end) return (start, end);
            }
            return null;
        }

        // ---- the facts and the pane ------------------------------------------------------------

        /// <summary>True while the note must not be edited from the pane: a read-only editor, or the history preview covering it.</summary>
        private bool AiReadOnly => Editor.IsReadOnly || PreviewPanel.Visibility == Visibility.Visible;

        /// <summary>
        /// True while the editor shows the request's own note, in the very document its anchors
        /// live in. Replace selection and Insert below act only then, so no other note is ever edited.
        /// </summary>
        private bool AiSourceShown(AiRun run) => ReferenceEquals(_shown, run.Note) && ReferenceEquals(Editor.Document, run.Document);

        /// <summary>
        /// What the window knows about the request's source text. With <paramref name="fenceNow"/>
        /// (everywhere but inside an edit) the fence around a fix's source is read from the note;
        /// without it, the one read last stands in (<see cref="FixFence"/>).
        /// </summary>
        private AiSourceFacts AiFacts(AiRun run, bool fenceNow = true) =>
            new(AiSourceShown(run), AiReadOnly, AiSourceUnchanged(run), FixFence(run, fenceNow));

        /// <summary>A selection is unchanged while the text between its anchors still equals what was sent; a whole note has nothing to replace.</summary>
        private static bool AiSourceUnchanged(AiRun run)
        {
            if (!run.Session.FromSelection) return true;
            if (run.Start is not { IsDeleted: false } start || run.End is not { IsDeleted: false } end) return false;
            string original = run.Session.Original;
            int length = end.Offset - start.Offset;   // negative once the source was deleted and text typed in its place
            return length == original.Length
                   && string.Equals(run.Document.GetText(start.Offset, length), original, StringComparison.Ordinal);
        }

        /// <summary>Where the request's selection is now, in the shown note; null for a whole note, another note, or text that is gone.</summary>
        private (int Start, int Length)? SourceRange(AiRun run)
        {
            if (!run.Session.FromSelection || !AiSourceShown(run)) return null;
            if (run.Start is not { IsDeleted: false } start || run.End is not { IsDeleted: false } end) return null;
            int length = end.Offset - start.Offset;
            return length > 0 ? (start.Offset, length) : null;
        }

        /// <summary>
        /// Draws the pane for the request it shows, with the facts as they are now.
        /// <paramref name="notice"/> is a status for this one drawing ("Inserted below"): it
        /// changes nothing the session decides, and the next drawing shows the session's own.
        /// </summary>
        private void RefreshAi(string? notice = null)
        {
            if (_ai is not { } run) return;
            DrawAi(run, AiFacts(run), notice);
        }

        /// <summary>Draws the pane for <paramref name="run"/>, the request it shows, with <paramref name="facts"/> the caller has just worked out.</summary>
        private void DrawAi(AiRun run, AiSourceFacts facts, string? notice = null)
        {
            run.Drawn = facts;
            run.Noticed = notice != null;
            AiPaneView view = run.Session.View(facts);
            (AiDraw ?? AiPanel.Show)(notice == null ? view : view with { Status = notice });
        }

        /// <summary>
        /// <see cref="RefreshAi"/> for callers outside an AI action (a tab shown, the history
        /// preview): a failure while drawing the pane is logged, never thrown into them.
        /// </summary>
        private void RedrawAi() => GuardAi("Drawing the AI pane", () => RefreshAi());

        /// <summary>Draws <paramref name="run"/>, unless a newer request or a close took the pane from it.</summary>
        private void ShowAi(AiRun run)
        {
            if (ReferenceEquals(_ai, run)) RefreshAi();
        }

        /// <summary>The AI pane takes the column History and Search notes share.</summary>
        private void OpenAiPane()
        {
            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                EndPreview();
                HistoryPanel.Visibility = Visibility.Collapsed;
            }
            SearchPanel.Visibility = Visibility.Collapsed;
            AiPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Closes the pane: its close button, Ctrl+Shift+A, History or Search notes taking its
        /// column, the source note closing. A request still running is cancelled, never left to
        /// run where nobody sees it.
        /// </summary>
        private void CloseAi(bool focusEditor)
        {
            CancelAi(_ai);
            _ai = null;
            if (AiPanel.Visibility != Visibility.Visible) return;
            AiPanel.Visibility = Visibility.Collapsed;
            if (focusEditor) Editor.Focus();
        }

        /// <summary>Stop: the request ends, and what came so far stays in the pane as a stopped reply.</summary>
        private void StopAi() => CancelAi(_ai);

        /// <summary>
        /// A credential was just stored from <paramref name="note"/>: its plain value is out of
        /// the note, so it must not stay in what AI holds of it. The AI pane's request for that
        /// note is closed (its original, its result and its Changes view may hold the value, and
        /// Insert below would put it back), a pane that was closed before is emptied of what it
        /// still held, and an answer from notes, which may quote it, is cleared. In every window
        /// of the workspace: a tab may have moved since its request.
        /// </summary>
        private void DropAiTextAfterStore(OpenNote note)
        {
            foreach (MicaPadWindow window in WindowsOf(_workspace).Append(this).Distinct())
            {
                window.GuardAi("Closing the AI pane after a credential was stored", () => window.DropAiOf(note));
                window.GuardAi("Clearing an answer after a credential was stored", window.SearchPanel.DropAnswer);
            }
            GuardAi("Clearing Ask MicaStats after a credential was stored", () => CredentialStored?.Invoke());
        }

        /// <summary>
        /// Closes the pane and empties it when its request ran on <paramref name="note"/>; a running
        /// request is cancelled. A pane that was closed before is emptied too, whatever the note:
        /// closed, it still holds its last result, unseen (the text, the Changes rows, the pictures
        /// kept for drawing it again), and with no request left nothing says which note that came
        /// from. Emptying a pane nobody sees loses nothing.
        /// </summary>
        private void DropAiOf(OpenNote note)
        {
            if (_ai is not { } run)
            {
                AiPanel.Clear();
                return;
            }
            if (!ReferenceEquals(run.Note, note)) return;
            CloseAi(focusEditor: false);
            AiPanel.Clear();
        }

        /// <summary>A tab is closing (the workspace's event): the pane goes with its source note. Guarded: a tab always closes.</summary>
        private void OnAiNoteClosing(OpenNote note)
        {
            if (_ai is not { } run || !ReferenceEquals(run.Note, note)) return;
            GuardAi("Closing the AI pane with its note", () => CloseAi(focusEditor: false));
        }

        /// <summary>
        /// The source note was edited. Raised inside the edit itself, so it is guarded: a failure
        /// while drawing the pane must never escape into typing.
        /// </summary>
        private void OnAiNoteTextChanged(OpenNote note)
        {
            if (_ai is not { } run || !ReferenceEquals(run.Note, note)) return;
            GuardAi("Following an edit of the AI source text", () => FollowSourceEdit(run));
        }

        /// <summary>
        /// The pane says at once whether the result can still replace the selection, offers
        /// Replace again when a Replace was undone, and says "Replaced the selection" again when
        /// it was redone. A status shown for one drawing ("Inserted below") goes with the next
        /// edit. Typing that changes none of this draws nothing.
        ///
        /// <para>
        /// This runs for every edit, thousands of times inside one Replace All, so it does not
        /// read the fence around a fix's source: it draws with the fence read last, and the fence
        /// is read again once, when the update group ends.
        /// </para>
        /// </summary>
        private void FollowSourceEdit(AiRun run)
        {
            bool turned = ReplaceWasUndoneOrRedone(run);
            ReadFenceAfterUpdate(run);
            AiSourceFacts facts = AiFacts(run, fenceNow: false);
            if (turned || run.Noticed || facts != run.Drawn) DrawAi(run, facts);
        }

        /// <summary>
        /// After Replace selection, tells its undo and its redo. Undone: the place the result
        /// went to reads as the original text again, so the session forgets it was applied and
        /// Replace selection is offered again. Redone: the place reads as the result once more,
        /// so the session is applied again ("Replaced the selection"), not a source whose text
        /// changed. Either way both pairs of anchors go back around the text that is there now.
        /// </summary>
        private static bool ReplaceWasUndoneOrRedone(AiRun run)
        {
            if (run.Replaced is not { } place || run.ReplacedWith is not { } result) return false;
            if (place.Start.IsDeleted || place.End.IsDeleted) return false;
            string becomes = run.Undone ? result : run.Session.Original;   // what the place reads as once the step is taken
            int start = place.Start.Offset, length = place.End.Offset - start;
            if (length != becomes.Length
                || !string.Equals(run.Document.GetText(start, length), becomes, StringComparison.Ordinal)) return false;

            run.Undone = !run.Undone;
            AnchorReplaced(run, start, length);
            if (run.Undone) run.Session.ClearApplied();
            else run.Session.MarkApplied(AiSession.Replaced);
            return true;
        }

        /// <summary>
        /// Puts the request's anchors around the text at <paramref name="start"/>, where a
        /// Replace selection put the result or an undo the original. One pair holds exactly that
        /// text (typing at its edges stays outside): the source of Replace selection, and what
        /// Insert below goes under. The other also takes what an undo or a redo puts back right
        /// at its edges, so the step can be told.
        /// </summary>
        private static void AnchorReplaced(AiRun run, int start, int length)
        {
            TextDocument document = run.Document;
            run.Start = Anchor(document, start, AnchorMovementType.AfterInsertion);
            run.End = Anchor(document, start + length, AnchorMovementType.BeforeInsertion);
            run.Replaced = (Anchor(document, start, AnchorMovementType.BeforeInsertion),
                            Anchor(document, start + length, AnchorMovementType.AfterInsertion));
        }

        private void OnAiPreviewChanged(object? sender, EventArgs e) => RedrawAi();

        // ---- applying a result -----------------------------------------------------------------

        /// <summary>
        /// Replace selection: the result takes the place of the source text, as one undo step, and
        /// is selected. Its line breaks are written as the note's own.
        /// </summary>
        private void ReplaceWithAiResult()
        {
            if (_ai is not { } run) return;
            // Asked again at the click: the note may have changed since the pane was drawn.
            if (!run.Session.View(AiFacts(run)).CanReplace
                || run.Start is not { IsDeleted: false } start || run.End is not { IsDeleted: false } end)
            {
                RefreshAi();
                return;
            }

            TextDocument document = run.Document;   // the editor's own: CanReplace says the source is shown
            string result = SelectionEdit.Normalize(run.Session.ResultForNote, TextLines.NewlineOf(document.Text));
            int offset = start.Offset;
            bool changes = !string.Equals(run.Session.Original, result, StringComparison.Ordinal);   // CanReplace: the range still holds the original
            ApplyEdit(Editor, SelectionEdit.Replace(offset, end.Offset - offset, result));

            // The anchors now hold the new text, so Insert below lands under it; every offset comes
            // from the text as it was written, carriage returns and all.
            AnchorReplaced(run, offset, result.Length);
            // A result equal to the source changed nothing, so there is no undo or redo to tell.
            if (!changes) run.Replaced = null;
            run.ReplacedWith = changes ? result : null;
            run.Undone = false;
            run.Session.MarkApplied(AiSession.Replaced);
            RefreshAi();
            Editor.Focus();   // so Ctrl+Z takes it back
        }

        /// <summary>
        /// Insert below: the result becomes a new paragraph, as one undo step, with its line
        /// breaks written as the note's own. It goes after the source's last line; for a whole
        /// note, at the end of the note. When a selection's place is gone (the note was reloaded
        /// or replaced whole, or the text was deleted), it goes after the caret's line: the
        /// collapsed anchors would put it under line one.
        /// </summary>
        private void InsertAiResult()
        {
            if (_ai is not { } run) return;
            if (!run.Session.View(AiFacts(run)).CanInsert)
            {
                RefreshAi();
                return;
            }

            string text = Editor.Document.Text;   // the source's own document: CanInsert says it is shown
            string newline = TextLines.NewlineOf(text);
            int end = !run.Session.FromSelection ? text.Length
                : SourceRange(run) is { } range ? range.Start + range.Length
                : TextLines.LineEnd(text, Editor.CaretOffset);
            ApplyEdit(Editor, SelectionEdit.InsertBelow(text, end, SelectionEdit.Normalize(run.Session.ResultForNote, newline), newline));
            // Said once, and nothing is marked: the source text is where it was, so Replace
            // selection stays under its usual rules, before and after an undo of this insert.
            RefreshAi(AiSession.Inserted);
            Editor.Focus();
        }

        private void CopyAiResult()
        {
            if (_ai is not { } run) return;
            string result = run.Session.ResultForNote;
            if (result.Length == 0) return;
            try
            {
                AiCopy(result);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Warn("Copying an AI result failed (" + ex.GetType().Name + ")");
                ShowStatus("Clipboard busy, try again");
                return;
            }
            ShowStatus("Copied");
        }

        /// <summary>
        /// Try again: the same action and instruction on the same text, in a new session (a
        /// session runs once). <see cref="RerunAi"/> refuses, and says why, when that text is not
        /// there to send.
        /// </summary>
        private void RetryAi()
        {
            if (_ai is not { } run) return;
            AiSession session = run.Session;
            if (session.Running || session.AwaitingInstruction || session.Refusal != null) return;   // nothing to try again
            RerunAi(session.Action, run.Instruction);
        }
    }
}
