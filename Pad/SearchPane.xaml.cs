using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad.Search;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>One result: where it is and what it says.</summary>
    public sealed record SearchRow(string NoteId, string Title, bool Closed, int FirstLine, int LastLine, string FirstLineText, IReadOnlyList<SnippetRun> Snippet)
    {
        /// <summary>"closed · line 12" or "line 12".</summary>
        public string Where => (Closed ? "closed · " : "") + "line " + FirstLine;
    }

    /// <summary>
    /// The Search notes pane (search spec 1): searches 300 ms after typing stops, Enter at once; a
    /// newer search cancels the older one. The window supplies <see cref="Run"/> and opens what is
    /// chosen.
    /// </summary>
    public partial class SearchPane : UserControl
    {
        /// <summary>How long typing must pause before the query runs.</summary>
        public static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);

        private readonly DispatcherTimer _typing;
        private CancellationTokenSource? _running;

        /// <summary>Builds the pane hidden; <see cref="Open"/> reveals it.</summary>
        public SearchPane()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            _typing = new DispatcherTimer { Interval = TypingPause };
            _typing.Tick += (_, _) => { _typing.Stop(); _ = SearchNow(); };
        }

        /// <summary>The user picked a result.</summary>
        public event Action<SearchRow>? ResultChosen;

        /// <summary>The user asked to close the pane.</summary>
        public event Action? CloseRequested;

        /// <summary>Esc in the query box: back to the editor.</summary>
        public event Action? ReturnRequested;

        /// <summary>Runs a query: rows and the status line. Set by the window.</summary>
        public Func<string, CancellationToken, Task<(IReadOnlyList<SearchRow> Rows, string Status)>>? Run { get; set; }

        /// <summary>The rows currently listed.</summary>
        public IReadOnlyList<SearchRow> Rows { get; private set; } = Array.Empty<SearchRow>();

        /// <summary>Shows the pane with the query box focused; <paramref name="query"/>, when given, replaces the query.</summary>
        public void Open(string? query)
        {
            Visibility = Visibility.Visible;
            if (query != null) QueryBox.Text = query;
            QueryBox.Focus();
            QueryBox.SelectAll();
            if (QueryBox.Text.Trim().Length > 0) _ = SearchNow();
        }

        /// <summary>Searches now, cancelling a search still running.</summary>
        public async Task SearchNow()
        {
            _typing.Stop();
            _running?.Cancel();
            var mine = _running = new CancellationTokenSource();
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
                Show(rows, rows.Count == 0 ? status + " · No notes found" : status);
            }
            catch (OperationCanceledException)
            {
                // a newer search took over
            }
        }

        private void Show(IReadOnlyList<SearchRow> rows, string status)
        {
            Rows = rows;
            Results.ItemsSource = rows;
            StatusText.Text = status;
        }

        private void OnQueryChanged(object sender, TextChangedEventArgs e)
        {
            _typing.Stop();
            _typing.Start();
        }

        private void OnQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _ = SearchNow(); e.Handled = true; }
            else if (e.Key == Key.Down && Rows.Count > 0) { Results.SelectedIndex = 0; FocusSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ReturnRequested?.Invoke(); e.Handled = true; }
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
