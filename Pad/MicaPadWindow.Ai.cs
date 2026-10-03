using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
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

            public CancellationTokenSource Cancel { get; } = new();

            /// <summary>The facts the pane was last drawn with.</summary>
            public AiSourceFacts? Drawn { get; set; }
        }

        private AiRun? _ai;
        private Action<string>? _aiCopy;

        /// <summary>Whether Settings → MicaPad → AI is on. Tests replace it.</summary>
        internal Func<bool> AiEnabled { get; set; } = () => App.ConfigService?.Config.PadAiEnabled == true;

        /// <summary>The runner for one request, or null before the settings load. Tests replace it.</summary>
        internal Func<PadAiRunner?> AiRunnerFactory { get; set; } = () => App.CreatePadAiRunner();

        /// <summary>Puts a result on the clipboard: the pad's own clipboard helper unless a test replaces it.</summary>
        internal Action<string> AiCopy
        {
            get => _aiCopy ?? SetClipboardText;
            set => _aiCopy = value;
        }

        /// <summary>Opens Settings on the MicaPad section (Set up AI…). Tests replace it.</summary>
        internal Action OpenPadSettings { get; set; } = () => App.ShowSettingsSection("MicaPad");

        /// <summary>Where the one line per action is logged: its id, the character counts and the outcome. Tests replace it.</summary>
        internal Action<string> AiLog { get; set; } = message => DiagnosticsLog.Log("pad", message);

        /// <summary>Whether the keyboard is in the AI pane. Tests set it: a window that is never shown has no keyboard focus.</summary>
        internal Func<bool>? AiPaneHasFocus { get; set; }

        /// <summary>The request the AI pane shows, or null while the pane is closed.</summary>
        internal AiSession? AiSessionNow => _ai?.Session;

        /// <summary>The pane's buttons, and the two things that end or change a request from outside: a note closing and its text changing.</summary>
        private void ConfigureAi()
        {
            AiPanel.ReplaceRequested += () => GuardAi("Replacing the selection with an AI result", ReplaceWithAiResult);
            AiPanel.InsertRequested += () => GuardAi("Inserting an AI result", InsertAiResult);
            AiPanel.CopyRequested += () => GuardAi("Copying an AI result", CopyAiResult);
            AiPanel.StopRequested += () => GuardAi("Stopping an AI request", StopAi);
            AiPanel.RetryRequested += () => GuardAi("Trying an AI action again", RetryAi);
            AiPanel.CloseRequested += () => GuardAi("Closing the AI pane", () => CloseAi(focusEditor: true));
            AiPanel.InstructionEntered += instruction => GuardAi("Asking AI", () => RerunAi(PadAiAction.Ask, instruction));
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
            _ai?.Cancel.Cancel();
            _ai = null;
        }

        // ---- the menu and the shortcut ---------------------------------------------------------

        /// <summary>The editor menu's AI submenu: the actions while AI is on, Set up AI… while it is off.</summary>
        private System.Windows.Controls.MenuItem BuildAiMenu() =>
            AiMenu(Editor, !AiIsOff(), RunAiFromMenu, () => OpenPadSettings());

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

        private async Task RunAiAsync(PadAiAction action, string? instruction, (int Start, int Length)? range)
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
            if (!GuardAi("Starting an AI action", () => run = BeginAi(action, instruction, range))) return;
            if (run != null) await StreamAiAsync(run);
        }

        /// <summary>
        /// Try again, and an instruction entered in the pane: the current selection when there is
        /// one; with nothing selected, the text the pane's request came from, where it is now. So
        /// a click in the note on the way to the pane does not turn "Selection, 412 characters"
        /// into the whole note.
        /// </summary>
        private void RerunAi(PadAiAction action, string? instruction)
        {
            (int Start, int Length)? range = Editor.SelectionLength == 0 && _ai is { } run ? SourceRange(run) : null;
            _ = RunAiAsync(action, instruction, range);
        }

        /// <summary>
        /// Everything before the request: the refusals, the source text, the session, its anchors
        /// and the pane. Returns the run to stream, or null when there is nothing to send: a
        /// refusal, or Ask AI still waiting for its instruction.
        /// </summary>
        private AiRun? BeginAi(PadAiAction action, string? instruction, (int Start, int Length)? range)
        {
            if (_shown is not { } note) return null;
            if (Editor.TextArea.Selection is RectangleSelection)
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
            bool selected = Editor.SelectionLength > 0;
            bool fromSelection = selected || range != null;
            int start = selected ? Editor.SelectionStart : range?.Start ?? 0;
            int length = selected ? Editor.SelectionLength
                : range?.Length ?? (action.NeedsSelection ? 0 : document.TextLength);
            string source = document.GetText(start, length);
            if (string.IsNullOrWhiteSpace(source))
            {
                ShowStatus(AiNoTextText);
                return null;
            }

            _ai?.Cancel.Cancel();   // a new action ends the one still running
            var session = new AiSession(action, source, fromSelection, instruction);
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
            int sent = 0, back = 0;
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
                    sent = session.UserMessage.Length;
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
                GuardAi("Ending a failed AI action", () =>
                {
                    run.Cancel.Cancel();
                    session.Fail(AiErrorText.Describe(ex));
                });
            }

            GuardAi("Showing an AI result", () => ShowAi(run));
            LogAi(session, sent, back, failed ? "failed" : token.IsCancellationRequested ? "stopped" : cutShort ? "cut short" : "ok");
        }

        /// <summary>The action's id, how many characters went out and came back, and how it ended. Never the text.</summary>
        private void LogAi(AiSession session, int sent, int back, string outcome)
        {
            try
            {
                AiLog("AI " + session.Action.Id + ": " + sent.ToString(CultureInfo.InvariantCulture) + " chars sent, "
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

        // ---- the facts and the pane ------------------------------------------------------------

        /// <summary>True while the note must not be edited from the pane: a read-only editor, or the history preview covering it.</summary>
        private bool AiReadOnly => Editor.IsReadOnly || PreviewPanel.Visibility == Visibility.Visible;

        /// <summary>
        /// True while the editor shows the request's own note, in the very document its anchors
        /// live in. Replace selection and Insert below act only then, so no other note is ever edited.
        /// </summary>
        private bool AiSourceShown(AiRun run) => ReferenceEquals(_shown, run.Note) && ReferenceEquals(Editor.Document, run.Document);

        private AiSourceFacts AiFacts(AiRun run) => new(AiSourceShown(run), AiReadOnly, AiSourceUnchanged(run));

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

        /// <summary>Draws the pane for the request it shows, with the facts as they are now.</summary>
        private void RefreshAi()
        {
            if (_ai is not { } run) return;
            AiSourceFacts facts = AiFacts(run);
            run.Drawn = facts;
            AiPanel.Show(run.Session.View(facts));
        }

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
            _ai?.Cancel.Cancel();
            _ai = null;
            if (AiPanel.Visibility != Visibility.Visible) return;
            AiPanel.Visibility = Visibility.Collapsed;
            if (focusEditor) Editor.Focus();
        }

        /// <summary>Stop: the request ends, and what came so far stays in the pane as a stopped reply.</summary>
        private void StopAi() => _ai?.Cancel.Cancel();

        private void OnAiNoteClosing(OpenNote note)
        {
            if (_ai is { } run && ReferenceEquals(run.Note, note)) CloseAi(focusEditor: false);
        }

        /// <summary>
        /// The source note was edited: the pane says at once whether the result can still replace
        /// the selection. Typing that changes none of the facts draws nothing.
        /// </summary>
        private void OnAiNoteTextChanged(OpenNote note)
        {
            if (_ai is not { } run || !ReferenceEquals(run.Note, note)) return;
            if (AiFacts(run) != run.Drawn) RefreshAi();
        }

        private void OnAiPreviewChanged(object? sender, EventArgs e) => RefreshAi();

        // ---- applying a result -----------------------------------------------------------------

        /// <summary>Replace selection: the result takes the place of the source text, as one undo step, and is selected.</summary>
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

            string result = run.Session.ResultForNote;
            int offset = start.Offset;
            ApplyEdit(Editor, SelectionEdit.Replace(offset, end.Offset - offset, result));
            // The anchors now hold the new text, so Insert below lands under it.
            run.Start = Anchor(run.Document, offset, AnchorMovementType.AfterInsertion);
            run.End = Anchor(run.Document, offset + result.Length, AnchorMovementType.BeforeInsertion);
            run.Session.MarkApplied("Replaced the selection");
            RefreshAi();
            Editor.Focus();   // so Ctrl+Z takes it back
        }

        /// <summary>Insert below: the result becomes a new paragraph after the source's last line (the end of the note for a whole note), as one undo step.</summary>
        private void InsertAiResult()
        {
            if (_ai is not { } run) return;
            if (!run.Session.View(AiFacts(run)).CanInsert)
            {
                RefreshAi();
                return;
            }

            string text = Editor.Document.Text;   // the source's own document: CanInsert says it is shown
            int end = run.End is { IsDeleted: false } anchor ? anchor.Offset : text.Length;
            ApplyEdit(Editor, SelectionEdit.InsertBelow(text, end, run.Session.ResultForNote, TextLines.NewlineOf(text)));
            run.Session.MarkApplied("Inserted below");
            RefreshAi();
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

        /// <summary>Try again: the same action and instruction in a new session (a session runs once).</summary>
        private void RetryAi()
        {
            if (_ai is not { } run || !run.Session.View(AiFacts(run)).CanRetry) return;
            RerunAi(run.Session.Action, run.Instruction);
        }
    }
}
