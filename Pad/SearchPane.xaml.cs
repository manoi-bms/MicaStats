using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// One result: where it is and what it says. <see cref="Source"/> is its number, 1 to 8, while
    /// an answer drawn from it is shown: the <c>[n]</c> the answer cites it by.
    /// </summary>
    public sealed record SearchRow(string NoteId, string Title, bool Closed, int FirstLine, int LastLine, string FirstLineText,
                                   IReadOnlyList<SnippetRun> Snippet, int? Source = null)
    {
        /// <summary>"closed · line 12" or "line 12".</summary>
        public string Where => (Closed ? "closed · " : "") + "line " + FirstLine;
    }

    /// <summary>
    /// What the window gives back for an Ask: the rows, the status, and the answer stream or the
    /// sentence shown instead. The status line never claims an answer that did not come:
    /// <see cref="Status"/> is the search status alone, shown with a sentence instead of an
    /// answer and after an answer that failed, was stopped or was cut short;
    /// <see cref="Answering"/> is shown while the answer streams in, and <see cref="Answered"/>
    /// only once it ended cleanly. Without them the search status stays.
    /// </summary>
    public sealed record AskStart(IReadOnlyList<SearchRow> Rows, string Status, IAsyncEnumerable<PadAiUpdate>? Answer, string? Instead,
                                  string? Answering = null, string? Answered = null);

    /// <summary>
    /// The Search notes pane (search spec 1): searches 300 ms after typing stops, Enter at once; a
    /// newer search cancels the older one. The window supplies <see cref="Run"/> and opens what is
    /// chosen.
    ///
    /// <para>
    /// Ask (MicaPad AI spec 4): the Ask button and Ctrl+Enter run <see cref="Ask"/>, which searches
    /// and answers the question from the passages found. The answer streams in above the rows,
    /// whose first ones carry their source numbers. A new search or question cancels a running
    /// answer and clears the old one. The pane sends nothing itself: the window's
    /// <see cref="Ask"/> decides whether a request is made.
    /// </para>
    /// </summary>
    public partial class SearchPane : UserControl
    {
        /// <summary>How long typing must pause before the query runs.</summary>
        public static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);

        /// <summary>The line under an answer that ended at the output cap.</summary>
        internal const string CutShortText = "Cut short at the length limit";

        /// <summary>The line under an answer the user stopped.</summary>
        internal const string StoppedText = "Stopped";

        /// <summary>Shown in place of an answer when <see cref="Ask"/> itself failed.</summary>
        internal const string AskFailedText = "The question could not be answered";

        private readonly DispatcherTimer _typing;
        private readonly DispatcherTimer _redraw;
        private readonly Stopwatch _sinceDraw = new();
        private readonly StringBuilder _answer = new();
        private CancellationTokenSource? _running;

        /// <summary>Builds the pane hidden; <see cref="Open"/> reveals it.</summary>
        public SearchPane()
        {
            // The timers first: hiding the pane, below, already stops one of them.
            _typing = new DispatcherTimer { Interval = TypingPause };
            _typing.Tick += (_, _) => { _typing.Stop(); _ = SearchNow(); };
            _redraw = new DispatcherTimer(DispatcherPriority.Background);
            _redraw.Tick += (_, _) => DrawAnswer();
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        /// <summary>The user picked a result.</summary>
        public event Action<SearchRow>? ResultChosen;

        /// <summary>The user asked to close the pane.</summary>
        public event Action? CloseRequested;

        /// <summary>Esc in the query box: back to the editor.</summary>
        public event Action? ReturnRequested;

        /// <summary>Runs a query: rows and the status line. Set by the window.</summary>
        public Func<string, CancellationToken, Task<(IReadOnlyList<SearchRow> Rows, string Status)>>? Run { get; set; }

        /// <summary>
        /// Runs a question: the search, then the answer from the passages found, or the sentence
        /// to show in its place. Set by the window, which alone decides whether anything is sent.
        /// </summary>
        public Func<string, CancellationToken, Task<AskStart>>? Ask { get; set; }

        /// <summary>Puts the answer on the clipboard. Set by the window to the pad's clipboard helper; tests replace it.</summary>
        public Action<string>? CopyAnswer { get; set; }

        /// <summary>The rows currently listed.</summary>
        public IReadOnlyList<SearchRow> Rows { get; private set; } = Array.Empty<SearchRow>();

        /// <summary>The shortest time between two redraws of an answer that is streaming in.</summary>
        internal TimeSpan RedrawInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Where a failed question is reported, by the exception's type only: a message could quote
        /// the question or a note. Tests replace it so nothing reaches the real log.
        /// </summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("pad", message);

        /// <summary>Shows the pane with the query box focused; <paramref name="query"/>, when given, replaces the query.</summary>
        public void Open(string? query)
        {
            Visibility = Visibility.Visible;
            if (query != null) QueryBox.Text = query;
            QueryBox.Focus();
            QueryBox.SelectAll();
            if (QueryBox.Text.Trim().Length > 0) _ = SearchNow();
        }

        /// <summary>Searches now, cancelling a search or an answer still running and taking the old answer away.</summary>
        public async Task SearchNow()
        {
            _typing.Stop();
            CancelRunning();
            var mine = _running = new CancellationTokenSource();
            ClearAnswer();
            string query = QueryBox.Text;

            if (query.Trim().Length == 0 || Run == null)
            {
                Show(Array.Empty<SearchRow>(), "");
                return;
            }

            try
            {
                var (rows, status) = await Run(query, mine.Token);
                if (!ReferenceEquals(mine, _running)) return;
                ShowFound(rows, status);
            }
            catch (OperationCanceledException)
            {
                // a newer search took over
            }
        }

        /// <summary>
        /// What the Ask button and Ctrl+Enter run: <see cref="Ask"/> for the query, its rows and
        /// status, then its answer streaming in above them (or the sentence given instead). An
        /// empty query does nothing. A search or an answer still running is cancelled and the old
        /// answer cleared first. The answer is read, which is what starts its request, only while
        /// this question still owns the pane: one that a newer search overtook, or whose pane
        /// closed while it searched, is never read. The task completes when the answer ends, and
        /// never faults.
        /// </summary>
        internal async Task AskNowAsync()
        {
            string query = QueryBox.Text;
            if (query.Trim().Length == 0 || Ask is not { } ask) return;

            _typing.Stop();
            CancelRunning();
            var mine = _running = new CancellationTokenSource();
            ClearAnswer();

            AskStart start;
            try
            {
                start = await ask(query, mine.Token);
            }
            catch (OperationCanceledException)
            {
                return;   // a newer search or question took over, or the pane closed
            }
            catch (Exception ex)
            {
                Report("Asking the notes", ex);
                if (ReferenceEquals(mine, _running)) ShowInstead(AskFailedText);
                return;
            }
            // Overtaken or closed while it searched: the rows on screen belong to someone else now.
            if (!ReferenceEquals(mine, _running) || mine.IsCancellationRequested) return;

            ShowFound(start.Rows, start.Status);
            if (start.Instead != null) ShowInstead(start.Instead);
            else if (start.Answer != null) await StreamAnswerAsync(start, start.Answer, mine);
        }

        /// <summary>
        /// The pane or its window goes away: what is running is cancelled, and a search still
        /// waiting for typing to pause is dropped. What came of an answer so far stays, marked
        /// "Stopped". Never throws: it is called from the window's close path.
        /// </summary>
        internal void StopAnswer()
        {
            _typing.Stop();
            CancelRunning();
        }

        /// <summary>
        /// Cancels the search or the answer that is running. A callback on its token that throws
        /// is reported by its type here, never thrown into a click, a key or the window closing
        /// (MicaStats has no dispatcher exception handler). The token is cancelled all the same.
        /// </summary>
        private void CancelRunning()
        {
            try
            {
                _running?.Cancel();
            }
            catch (Exception ex)
            {
                Report("Cancelling a search or an answer", ex);
            }
        }

        /// <summary>Paints the answer for the pad's dark or light theme. The rest of the pane reads the window's <c>Pad.*</c> brushes.</summary>
        public void ApplyTheme(bool dark) => AnswerBox.ApplyTheme(dark);

        /// <summary>
        /// A key pressed in the query box. Enter searches; Ctrl+Enter asks; Down goes to the
        /// results; Esc returns to the editor. True when the key was used up.
        /// </summary>
        internal bool HandleQueryKey(Key key, ModifierKeys modifiers)
        {
            if (key == Key.Enter)
            {
                if (modifiers == ModifierKeys.Control) _ = AskNowAsync();
                else _ = SearchNow();
                return true;
            }
            if (key == Key.Down && Rows.Count > 0)
            {
                Results.SelectedIndex = 0;
                FocusSelected();
                return true;
            }
            if (key == Key.Escape)
            {
                ReturnRequested?.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>
        /// A pane that is closed, or whose column History or the AI pane took, reads no answer:
        /// the request is cancelled, never left to run where nobody sees it.
        /// </summary>
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == VisibilityProperty && Visibility != Visibility.Visible) StopAnswer();
        }

        private static Visibility When(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

        private void Show(IReadOnlyList<SearchRow> rows, string status)
        {
            Rows = rows;
            Results.ItemsSource = rows;
            StatusText.Text = status;
        }

        /// <summary>Lists what a query found; with nothing found, the status says so.</summary>
        private void ShowFound(IReadOnlyList<SearchRow> rows, string status) => Show(rows, FoundStatus(rows, status));

        /// <summary>The status line for what a query found: the search status, and with nothing found, that too.</summary>
        private static string FoundStatus(IReadOnlyList<SearchRow> rows, string status) =>
            rows.Count == 0 ? status + " · No notes found" : status;

        // ---- the answer ----------------------------------------------------------------------

        /// <summary>Takes the answer off the pane: a new search or question starts without the old one.</summary>
        private void ClearAnswer()
        {
            _redraw.Stop();
            _sinceDraw.Reset();
            _answer.Clear();
            AnswerPanel.Visibility = Visibility.Collapsed;
            AnswerStop.Visibility = Visibility.Collapsed;
            AnswerCopy.Visibility = Visibility.Collapsed;
            AnswerNote.Text = "";
            AnswerNote.Visibility = Visibility.Collapsed;
            if (AnswerBox.Shown.Length > 0) AnswerBox.ShowPlain("");
        }

        /// <summary>Shows a sentence where the answer would be, exactly as it is: AI is off, nothing matched.</summary>
        private void ShowInstead(string sentence)
        {
            AnswerBox.ShowPlain(sentence);
            AnswerPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Reads <paramref name="answer"/> into the answer box as Markdown until it ends, is
        /// stopped, or a newer search or question takes the pane (which then draws nothing more
        /// for this one). Never throws: a stream that fails ends the answer with its error.
        ///
        /// <para>
        /// The status line claims no answer that did not come. While this reads it says
        /// <see cref="AskStart.Answering"/>; it says <see cref="AskStart.Answered"/> only after a
        /// Done with no error, no stop and no cut-short before it. Otherwise it goes back to the
        /// search status, and the line under the answer says what happened.
        /// </para>
        /// </summary>
        private async Task StreamAnswerAsync(AskStart start, IAsyncEnumerable<PadAiUpdate> answer, CancellationTokenSource mine)
        {
            string found = FoundStatus(start.Rows, start.Status);
            StatusText.Text = start.Answering ?? found;
            AnswerStop.Visibility = Visibility.Visible;
            AnswerPanel.Visibility = Visibility.Visible;

            string? ending = null;
            bool done = false;
            try
            {
                await foreach (PadAiUpdate update in answer.WithCancellation(mine.Token))
                {
                    if (!ReferenceEquals(mine, _running)) return;
                    if (mine.IsCancellationRequested) break;   // Stop: nothing more is read
                    switch (update.Kind)
                    {
                        case PadAiUpdateKind.Text:
                            if (!string.IsNullOrEmpty(update.Text))
                            {
                                _answer.Append(update.Text);
                                ScheduleDraw();
                            }
                            break;
                        case PadAiUpdateKind.Error:
                            ending = update.Text ?? "The AI request failed.";
                            break;
                        case PadAiUpdateKind.CutShort:
                            ending = CutShortText;
                            break;
                        case PadAiUpdateKind.Done:
                            done = true;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (mine.IsCancellationRequested)
            {
                // stopped, or overtaken: told below
            }
            catch (Exception ex)
            {
                Report("Reading an answer", ex);
                ending = AiErrorText.Describe(ex);
            }

            if (!ReferenceEquals(mine, _running)) return;
            DrawAnswer();   // the last text, at once
            ending ??= mine.IsCancellationRequested ? StoppedText : null;
            StatusText.Text = done && ending == null ? start.Answered ?? found : found;
            AnswerNote.Text = ending ?? "";
            AnswerNote.Visibility = When(ending != null);
            AnswerStop.Visibility = Visibility.Collapsed;
            AnswerCopy.Visibility = When(_answer.Length > 0);
        }

        /// <summary>Draws now when the interval has passed since the last draw (or nothing was drawn yet); otherwise once, when it has.</summary>
        private void ScheduleDraw()
        {
            if (_redraw.IsEnabled) return;
            TimeSpan since = _sinceDraw.Elapsed;
            if (!_sinceDraw.IsRunning || since >= RedrawInterval)
            {
                DrawAnswer();
                return;
            }
            _redraw.Interval = RedrawInterval - since;
            _redraw.Start();
        }

        /// <summary>Draws the answer so far, cancelling a redraw that was waiting. Text already on screen is left alone.</summary>
        private void DrawAnswer()
        {
            _redraw.Stop();
            string raw = _answer.ToString();
            if (!string.Equals(AnswerBox.Shown, raw, StringComparison.Ordinal)) AnswerBox.ShowMarkdown(raw);
            _sinceDraw.Restart();
        }

        /// <summary>Reports a failure once it happened, by its type only; reporting never throws into the pane.</summary>
        private void Report(string what, Exception ex)
        {
            try
            {
                Warn(what + " failed (" + ex.GetType().Name + ")");
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        // ---- events --------------------------------------------------------------------------

        private void OnQueryChanged(object sender, TextChangedEventArgs e)
        {
            _typing.Stop();
            _typing.Start();
        }

        private void OnQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleQueryKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }

        private void OnAskClick(object sender, RoutedEventArgs e) => _ = AskNowAsync();

        private void OnAnswerStopClick(object sender, RoutedEventArgs e) => CancelRunning();

        /// <summary>Copy: the answer as the model wrote it, never the line under it.</summary>
        private void OnAnswerCopyClick(object sender, RoutedEventArgs e)
        {
            if (_answer.Length > 0) CopyAnswer?.Invoke(_answer.ToString());
        }

        private void OnResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Results.SelectedItem is SearchRow row) { ResultChosen?.Invoke(row); e.Handled = true; }
            else if (e.Key == Key.Escape) { QueryBox.Focus(); e.Handled = true; }
        }

        private void OnResultsClick(object sender, MouseButtonEventArgs e)
        {
            // Only a click on a row: the scroll bar and the space below the rows open nothing.
            if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: SearchRow row })
                ResultChosen?.Invoke(row);
        }

        private void FocusSelected()
        {
            Results.UpdateLayout();
            (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListBoxItem)?.Focus();
        }

        private void OnSnippetLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock block || block.DataContext is not SearchRow row) return;
            block.Inlines.Clear();
            foreach (var run in row.Snippet)
            {
                // Qualified: Run is also this pane's query property.
                var text = new System.Windows.Documents.Run(run.Text);
                block.Inlines.Add(run.Bold ? new Bold(text) : (Inline)text);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
