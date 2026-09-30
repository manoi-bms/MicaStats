using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The find/replace bar. All matching is done by <see cref="FindReplaceEngine"/> over the
    /// document text; this control only keeps the highlights and the count in step with typing,
    /// refreshing 300 ms after the last change rather than on every keystroke.
    /// </summary>
    public partial class FindReplaceBar : UserControl
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly MatchHighlighter _highlighter = new();
        private readonly DispatcherTimer _refresh;
        private TextEditor? _editor;
        private Regex? _regex;
        private bool _timedOut;
        private IReadOnlyList<FindMatch> _matches = Array.Empty<FindMatch>();

        /// <summary>Raised just before Replace All rewrites the document, so the owner can snapshot first.</summary>
        public event Action? ReplacingAll;

        /// <summary>Creates the bar collapsed; <see cref="Open"/> shows it.</summary>
        public FindReplaceBar()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _refresh.Tick += (s, e) => Recompute();
        }

        /// <summary>True while the bar is showing.</summary>
        public bool IsOpen => Visibility == Visibility.Visible;

        /// <summary>Connects the bar to the editor it searches; the highlights follow tab switches.</summary>
        public void Attach(TextEditor editor)
        {
            _editor = editor;
            editor.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
            editor.DocumentChanged += (s, e) => ScheduleRefresh();
            editor.TextChanged += (s, e) => ScheduleRefresh();
        }

        /// <summary>Takes the find-match color of the MicaPad theme and repaints the matches.</summary>
        public void ApplyPalette(PadPalette palette)
        {
            _highlighter.Fill = PadThemeApplier.ToBrush(palette.FindMatch);
            _editor?.TextArea.TextView.InvalidateLayer(_highlighter.Layer);
        }

        /// <summary>The brush behind each match, for tests.</summary>
        internal Brush MatchFill => _highlighter.Fill;

        /// <summary>Shows the bar, seeded with a single-line selection, and focuses the find box.</summary>
        public void Open(bool replace)
        {
            Visibility = Visibility.Visible;
            var replaceVisibility = replace ? Visibility.Visible : Visibility.Collapsed;
            ReplaceBox.Visibility = replaceVisibility;
            ReplaceButtons.Visibility = replaceVisibility;

            if (_editor != null && _editor.SelectionLength > 0 && !_editor.SelectedText.Contains('\n'))
                FindBox.Text = _editor.SelectedText;

            FindBox.Focus();
            FindBox.SelectAll();
            Recompute();
        }

        /// <summary>Hides the bar, clears the highlights and returns focus to the editor.</summary>
        public void Close()
        {
            Visibility = Visibility.Collapsed;
            _refresh.Stop();
            _highlighter.Matches = Array.Empty<FindMatch>();
            _editor?.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            _editor?.Focus();
        }

        /// <summary>F3: the next match after the selection. With the bar closed, opens it instead.</summary>
        public void FindNext()
        {
            if (!EnsureSearch()) return;
            // The same pattern would time out again, and each attempt blocks the UI thread for the full timeout.
            if (_timedOut) { ShowTimedOut(); return; }
            Select(FindReplaceEngine.FindNext(_editor!.Document.Text, _regex!, _editor.SelectionStart + _editor.SelectionLength));
        }

        /// <summary>Shift+F3: the previous match before the selection.</summary>
        public void FindPrevious()
        {
            if (!EnsureSearch()) return;
            // The same pattern would time out again, and each attempt blocks the UI thread for the full timeout.
            if (_timedOut) { ShowTimedOut(); return; }
            Select(FindReplaceEngine.FindPrevious(_editor!.Document.Text, _regex!, _editor.SelectionStart));
        }

        /// <summary>Rebuilds the pattern, the match list, the count and the highlights now.</summary>
        internal void Recompute()
        {
            _refresh.Stop();
            if (_editor == null) return;

            if (FindReplaceEngine.TryBuild(FindBox.Text, Options, out _regex, out string? error))
            {
                _matches = FindReplaceEngine.FindAll(_editor.Document.Text, _regex!, out bool timedOut);
                _timedOut = timedOut;
                UpdateCount();
                if (timedOut) ShowTimedOut();
            }
            else
            {
                _timedOut = false;
                _matches = Array.Empty<FindMatch>();
                CountText.Text = error ?? "";
                CountText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.AlertRed");
            }

            _highlighter.Matches = _matches;
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }

        private void ShowTimedOut()
        {
            CountText.Text = FindReplaceEngine.TimedOutMessage;
            CountText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.AlertRed");
        }

        private FindOptions Options => new(CaseToggle.IsChecked == true, WordToggle.IsChecked == true, RegexToggle.IsChecked == true);

        private bool EnsureSearch()
        {
            if (!IsOpen)
            {
                Open(replace: false);
                return false;
            }
            if (_regex == null || _refresh.IsEnabled) Recompute();
            return _editor != null && _regex != null;
        }

        private void ScheduleRefresh()
        {
            if (!IsOpen) return;
            _refresh.Stop();
            _refresh.Start();
        }

        private void UpdateCount()
        {
            CountText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.Muted");
            if (_editor == null) return;
            if (_matches.Count == 0)
            {
                CountText.Text = FindBox.Text.Length == 0 ? "" : "No results";
                return;
            }

            string total = _matches.Count.ToString("N0", Inv) + (_matches.Count >= FindReplaceEngine.MaxHighlights ? "+" : "");
            int index = _editor.SelectionLength > 0 ? FindReplaceEngine.IndexOf(_matches, _editor.SelectionStart) : -1;
            CountText.Text = index >= 0
                ? (index + 1).ToString("N0", Inv) + " of " + total
                : total + (_matches.Count == 1 ? " result" : " results");
        }

        private void Select(FindMatch? match)
        {
            if (_editor == null) return;
            if (match == null)
            {
                CountText.Text = "No results";
                return;
            }

            _editor.Select(match.Value.Offset, match.Value.Length);
            Reveal(match.Value);
            var location = _editor.Document.GetLocation(match.Value.Offset);
            _editor.ScrollTo(location.Line, location.Column);
            UpdateCount();
        }

        /// <summary>
        /// Opens every fold that hides part of a found match. AvalonEdit opens a fold only when the
        /// caret lands strictly inside it, and a selected match leaves the caret at its end, which can
        /// be exactly where a fold ends.
        /// </summary>
        private void Reveal(FindMatch match)
        {
            if (_editor!.TextArea.TextView.GetService(typeof(FoldingManager)) is not FoldingManager folds) return;
            int start = match.Offset;
            int end = match.Offset + match.Length;
            var hiding = folds.AllFoldings.Where(f => f.IsFolded && f.StartOffset < end && f.EndOffset > start).ToList();
            foreach (var section in hiding) section.IsFolded = false;
        }

        private void OnReplaceClick(object sender, RoutedEventArgs e)
        {
            if (!EnsureSearch()) return;
            if (_timedOut) { ShowTimedOut(); return; }

            var current = new FindMatch(_editor!.SelectionStart, _editor.SelectionLength);
            if (current.Length > 0)
            {
                string? replacement = FindReplaceEngine.ExpandAt(_editor.Document.Text, _regex!, current, ReplaceBox.Text, Options.UseRegex);
                if (replacement != null)
                {
                    _editor.Document.Replace(current.Offset, current.Length, replacement);
                    _editor.Select(current.Offset + replacement.Length, 0);
                }
            }

            Recompute();
            FindNext();
        }

        private void OnReplaceAllClick(object sender, RoutedEventArgs e)
        {
            if (!EnsureSearch()) return;
            if (_timedOut) { ShowTimedOut(); return; }

            ReplacingAll?.Invoke();
            var document = _editor!.Document;
            string text = document.Text;
            if (!FindReplaceEngine.TryPlanReplaceAll(text, _regex!, ReplaceBox.Text, Options.UseRegex,
                                                     out var edits, out int count, out string? error))
            {
                CountText.Text = error ?? "";
                CountText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.AlertRed");
                return;
            }

            if (count > 0)
            {
                // One update group: one undo step, however many matches. Match by match, from the
                // last, so bookmarks between the matches stay on their lines. Past the limit one
                // replacement from the first match to the last instead: thousands of separate edits
                // are slow, and bookmarks between those matches collapse then.
                using (document.RunUpdate())
                {
                    if (edits.Count > TextPieces.MaxPieces)
                    {
                        var all = TextPieces.Combine(text, edits);
                        document.Replace(all.Offset, all.Length, all.Text);
                    }
                    else
                    {
                        for (int i = edits.Count - 1; i >= 0; i--) document.Replace(edits[i].Offset, edits[i].Length, edits[i].Text);
                    }
                }
            }
            Recompute();
            CountText.Text = count.ToString("N0", Inv) + " replaced";
        }

        private void OnFindKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (Keyboard.Modifiers == ModifierKeys.Shift) FindPrevious();
            else FindNext();
            e.Handled = true;
        }

        private void OnReplaceKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            OnReplaceClick(sender, e);
            e.Handled = true;
        }

        private void OnFindTextChanged(object sender, TextChangedEventArgs e) => ScheduleRefresh();

        private void OnOptionChanged(object sender, RoutedEventArgs e) => Recompute();

        private void OnPreviousClick(object sender, RoutedEventArgs e) => FindPrevious();

        private void OnNextClick(object sender, RoutedEventArgs e) => FindNext();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
