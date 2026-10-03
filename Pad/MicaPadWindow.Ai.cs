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
            /// at its edges. An undo makes it read as the original again; null when nothing was replaced.
            /// </summary>
            public (TextAnchor Start, TextAnchor End)? Replaced { get; set; }

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

        /// <summary>Where the one line per action is logged: its id, the characters in the request and in the reply, and the outcome. Tests replace it.</summary>
        internal Action<string> AiLog { get; set; } = message => DiagnosticsLog.Log("pad", message);

        /// <summary>Whether the keyboard is in the AI pane. Tests set it: a window that is never shown has no keyboard focus.</summary>
        internal Func<bool>? AiPaneHasFocus { get; set; }

        /// <summary>Draws a view in the AI pane. Tests replace it, to see that a failure while drawing never escapes into typing.</summary>
        internal Action<AiPaneView>? AiDraw { get; set; }

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
        /// Stops an answer from notes as the window hides or closes. Guarded for the same reason
        /// as <see cref="CancelAi"/>: a cancel that throws must not come out of closing.
        /// </summary>
        private void StopNotesAnswer() => GuardAi("Stopping an answer from notes", SearchPanel.StopAnswer);

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

        /// <summary>
        /// With <paramref name="again"/>, the earlier request whose text this one takes: its
        /// selection where it is now, or its whole note. Never what is selected or shown at the click.
        /// </summary>
        private async Task RunAiAsync(PadAiAction action, string? instruction, AiRun? again)
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
            if (!GuardAi("Starting an AI action", () => run = BeginAi(action, instruction, again))) return;
            if (run != null) await StreamAiAsync(run);
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
            _ = RunAiAsync(action, instruction, run);   // BeginAi asks both again where it reads the text
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
            (AiDraw ?? AiPanel.Show)(run.Session.View(facts));
        }

        /// <summary>
        /// <see cref="RefreshAi"/> for callers outside an AI action (a tab shown, the history
        /// preview): a failure while drawing the pane is logged, never thrown into them.
        /// </summary>
        private void RedrawAi() => GuardAi("Drawing the AI pane", RefreshAi);

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
        /// The pane says at once whether the result can still replace the selection, and offers
        /// Replace again when a Replace was undone. Typing that changes none of this draws nothing.
        /// </summary>
        private void FollowSourceEdit(AiRun run)
        {
            bool undone = ReplaceWasUndone(run);
            if (undone || AiFacts(run) != run.Drawn) RefreshAi();
        }

        /// <summary>
        /// After Replace selection, tells its undo: the place the result went to reads as the
        /// original text again. The anchors go back around that text and the session forgets it
        /// was applied, so Replace selection is offered again.
        /// </summary>
        private static bool ReplaceWasUndone(AiRun run)
        {
            if (run.Replaced is not { } place || place.Start.IsDeleted || place.End.IsDeleted) return false;
            string original = run.Session.Original;
            int start = place.Start.Offset, length = place.End.Offset - start;
            if (length != original.Length
                || !string.Equals(run.Document.GetText(start, length), original, StringComparison.Ordinal)) return false;

            run.Replaced = null;
            run.Start = Anchor(run.Document, start, AnchorMovementType.AfterInsertion);
            run.End = Anchor(run.Document, start + length, AnchorMovementType.BeforeInsertion);
            run.Session.ClearApplied();
            return true;
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

            // Every offset below comes from the text as it was written, carriage returns and all.
            int after = offset + result.Length;
            // The anchors now hold the new text, so Insert below lands under it.
            run.Start = Anchor(document, offset, AnchorMovementType.AfterInsertion);
            run.End = Anchor(document, after, AnchorMovementType.BeforeInsertion);
            // And a second pair that also takes what an undo puts back right at its edges; a
            // result equal to the source changed nothing, so there is no undo to tell.
            run.Replaced = changes
                ? (Anchor(document, offset, AnchorMovementType.BeforeInsertion), Anchor(document, after, AnchorMovementType.AfterInsertion))
                : null;
            run.Session.MarkApplied("Replaced the selection");
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

        // ---- ask your notes (MicaPad AI spec 4) ------------------------------------------------

        /// <summary>
        /// The Search pane's Ask: the search as usual, then the question and the first passages
        /// found (at most eight) for the model to answer from. The rows are built in hit order,
        /// so row i is source i + 1, the <c>[n]</c> the answer cites.
        ///
        /// <para>
        /// While AI is off this is the normal search and a sentence saying how to turn AI on:
        /// no runner is built, nothing is counted or sent. With nothing found there is no request
        /// either. The request itself starts only when the pane reads the answer
        /// (<see cref="AnswerFromNotesAsync"/>). Cancelling throws, as the search does.
        /// </para>
        ///
        /// <para>
        /// The passages are those of the notes as they are now. An edit reaches the index two
        /// seconds after it, so the feeder is flushed and the index waited for before the search:
        /// text deleted a moment ago is not found, and so is not sent.
        /// </para>
        /// </summary>
        private async Task<AskStart> AskNotesAsync(string query, CancellationToken token)
        {
            // The consent gate: asked before anything else.
            bool off = AiIsOff();

            // Every edit still waiting for its two seconds goes to the index now, as when the pane opens.
            SearchFeeder?.FlushPending();

            // Off, or search not ready yet: the normal search, and the sentence that says why
            // there is no answer (for search not ready, the search's own status says it).
            if (off || SearchService is not { } service)
            {
                (IReadOnlyList<SearchRow> found, string how) = await RunSearchAsync(query, token);
                return new AskStart(found, how, null, off ? NotesQuestion.AiOff : how);
            }

            // The index has taken those edits in before it is searched. This waits for the notes'
            // words only, never for the embedding server.
            await service.Indexer.WhenApplied().WaitAsync(token);

            // Off the UI thread, as RunSearchAsync does; the rows are built back here.
            SearchOutcome outcome = await Task.Run(() => service.Search.SearchAsync(query, token), token);
            string status = SearchStatusText.For(outcome, service.Settings());
            IReadOnlyList<Passage> sources = NotesQuestion.Sources(outcome.Hits);

            // The search as usual, with a sentence in place of the answer: no row is a numbered source.
            AskStart Instead(string sentence, string why)
            {
                LogAsk(sources.Count, 0, why);
                return new AskStart(NoteRows(outcome.Hits, query, numbered: 0), status, null, sentence);
            }

            if (sources.Count == 0) return Instead(NotesQuestion.NoSources, "no sources");

            // Asked again once the search is back, before a runner is built: the search takes a
            // while (an embedding server, a reranker), and the setting may have been turned off.
            if (AiIsOff()) return Instead(NotesQuestion.AiOff, "AI off");

            PadAiRunner? runner;
            try
            {
                runner = AiRunnerFactory();
            }
            catch (Exception ex)
            {
                WarnAi("Building the AI runner for a question", ex);
                return Instead(AiErrorText.Describe(ex), "failed");
            }
            if (runner == null) return Instead(AiNotReadyText, "not available");

            // The status claims an answer only once one came: "Answering" while it streams,
            // "Answered" after a clean end, the search status alone after anything else.
            string message = NotesQuestion.Message(query, sources);
            return new AskStart(NoteRows(outcome.Hits, query, numbered: sources.Count), status,
                                AnswerFromNotesAsync(runner, message, sources.Count, token), null,
                                Answering: status + " · " + NotesQuestion.Answering(sources.Count),
                                Answered: status + " · " + NotesQuestion.Status(sources.Count));
        }

        /// <summary>A question's rows, in hit order; the first <paramref name="numbered"/> carry their source numbers, 1 up.</summary>
        private List<SearchRow> NoteRows(IReadOnlyList<Passage> hits, string query, int numbered)
        {
            var open = _workspace.Open.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            return hits.Select((p, i) => new SearchRow(
                p.NoteId, p.Title, !open.Contains(p.NoteId), p.FirstLine, p.LastLine, p.FirstLineText,
                SearchSnippet.Make(p.Body, query), i < numbered ? i + 1 : null)).ToList();
        }

        /// <summary>
        /// The answer the pane reads: one request, which starts with the first read and is logged
        /// when it ends. <paramref name="message"/> is free of credentials already (the passages
        /// by the index, the question by <see cref="NotesQuestion.Message"/>).
        /// </summary>
        private async IAsyncEnumerable<PadAiUpdate> AnswerFromNotesAsync(PadAiRunner runner, string message, int sources,
            [EnumeratorCancellation] CancellationToken token)
        {
            // Asked once more, right before the request: this runs when the pane starts to read
            // the answer, which is after AskNotesAsync returned. Off: nothing is counted or sent,
            // and the pane shows why.
            if (AiIsOff())
            {
                LogAsk(sources, 0, "AI off");
                yield return new PadAiUpdate(PadAiUpdateKind.Error, NotesQuestion.AiOff);
                yield return new PadAiUpdate(PadAiUpdateKind.Done);
                yield break;
            }

            string outcome = "stopped";   // unless it runs to its end: the pane reads no further once it is stopped
            try
            {
                bool failed = false, cutShort = false;
                // The runner never throws and ends with Done, after a cancel too.
                await foreach (PadAiUpdate update in runner.RunAsync(message, token))
                {
                    if (update.Kind == PadAiUpdateKind.Error) failed = true;
                    else if (update.Kind == PadAiUpdateKind.CutShort) cutShort = true;
                    else if (update.Kind == PadAiUpdateKind.Done)
                        outcome = failed ? "failed" : token.IsCancellationRequested ? "stopped" : cutShort ? "cut short" : "ok";
                    yield return update;
                }
            }
            finally
            {
                LogAsk(sources, message.Length, outcome);
            }
        }

        /// <summary>
        /// One line per question: how many passages, how many characters its request held and how
        /// it ended. Never the question or the answer. "In the request", not "sent", as
        /// <see cref="LogAi"/> says: the runner may refuse a request handed to it (no key, the
        /// daily limit) without sending anything.
        /// </summary>
        private void LogAsk(int sources, int request, string outcome)
        {
            try
            {
                AiLog("AI ask-notes: " + sources.ToString(CultureInfo.InvariantCulture) + " sources, "
                      + request.ToString(CultureInfo.InvariantCulture) + " chars in the request, " + outcome);
            }
            catch (Exception)
            {
                // Logging is best effort; it must not end the request path with an exception.
            }
        }

        /// <summary>Copy under an answer: the answer as the model wrote it goes to the clipboard, and the status bar says so.</summary>
        private void CopyNotesAnswer(string answer)
        {
            if (answer.Length == 0) return;
            try
            {
                AiCopy(answer);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Warn("Copying an answer failed (" + ex.GetType().Name + ")");
                ShowStatus("Clipboard busy, try again");
                return;
            }
            ShowStatus("Copied");
        }
    }
}
