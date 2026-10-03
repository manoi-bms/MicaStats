using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>One line of the pane's Changes view: what happened to it, its <c>+</c> or <c>−</c>, and its text.</summary>
    public sealed record AiChangeRow(DiffKind Kind, string Glyph, string Text);

    /// <summary>
    /// The AI pane (MicaPad AI spec 3.2). It draws an <see cref="AiPaneView"/> and raises an event
    /// for each thing the user asks for; it knows nothing about the editor, the request or the
    /// note. The window opens and closes it: <see cref="Show"/> only draws.
    /// </summary>
    public sealed partial class AiPane : UserControl
    {
        /// <summary>The brushes behind an added and a removed line of the Changes view, in the pane's resources.</summary>
        internal const string AddedTintKey = "AiPane.AddedTint";
        internal const string RemovedTintKey = "AiPane.RemovedTint";

        private readonly DispatcherTimer _redraw;
        private readonly Stopwatch _sinceDraw = new();
        private AiPaneView? _view;
        private bool _drawn;
        private bool _drawnMarkdown;
        private string? _comparedOriginal;
        private string? _comparedResult;

        /// <summary>Builds the pane hidden, empty and dark.</summary>
        public AiPane()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            _redraw = new DispatcherTimer(DispatcherPriority.Background);
            _redraw.Tick += (_, _) => DrawNow();
            // A redraw still waiting is dropped with the window: no timer ticks for a pane that is gone. The next view draws as usual.
            Unloaded += (_, _) => _redraw.Stop();
            ApplyTheme(dark: true);
        }

        /// <summary>Replace selection was clicked.</summary>
        public event Action? ReplaceRequested;

        /// <summary>Insert below was clicked.</summary>
        public event Action? InsertRequested;

        /// <summary>Copy was clicked.</summary>
        public event Action? CopyRequested;

        /// <summary>Stop was clicked.</summary>
        public event Action? StopRequested;

        /// <summary>Try again was clicked.</summary>
        public event Action? RetryRequested;

        /// <summary>The user asked to close the pane.</summary>
        public event Action? CloseRequested;

        /// <summary>Enter in the instruction box: the instruction, trimmed and never empty.</summary>
        public event Action<string>? InstructionEntered;

        /// <summary>The shortest time between two redraws of a result that is streaming in.</summary>
        internal TimeSpan RedrawInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>True while the line diff is shown in place of the result.</summary>
        internal bool ShowingChanges => ChangesToggle.IsChecked == true;

        /// <summary>
        /// Draws <paramref name="view"/>. The title, the buttons and the status change at once. The
        /// result text redraws at most every <see cref="RedrawInterval"/> while one reply streams
        /// in, and at once when it ends or when the view belongs to another request. Showing a
        /// view never opens or closes the pane.
        /// </summary>
        public void Show(AiPaneView view)
        {
            bool streaming = Continues(view);
            _view = view;

            TitleText.Text = view.Title;
            SourceText.Text = view.SourceLine;

            // The next question starts with an empty box.
            if (!view.AskForInstruction && InstructionBox.Visibility == Visibility.Visible) InstructionBox.Clear();
            InstructionBox.Visibility = When(view.AskForInstruction);

            // The four actions need a result: none is offered while one streams in, or while the
            // pane still waits for the instruction that asks for it.
            bool noResultYet = view.Running || view.AskForInstruction;
            StopButton.Visibility = When(view.Running);
            ReplaceButton.Visibility = When(!noResultYet && view.ShowReplace);
            InsertButton.Visibility = When(!noResultYet);
            CopyButton.Visibility = When(!noResultYet);
            RetryButton.Visibility = When(!noResultYet);
            ReplaceButton.IsEnabled = view.CanReplace;
            InsertButton.IsEnabled = view.CanInsert;
            CopyButton.IsEnabled = view.CanCopy;
            RetryButton.IsEnabled = view.CanRetry;

            StatusText.Text = view.Status;
            StatusText.Visibility = When(view.Status.Length > 0);

            ChangesToggle.Visibility = When(view.CanShowChanges);
            if (!view.CanShowChanges) ChangesToggle.IsChecked = false;
            ShowResultOrChanges();

            if (streaming) ScheduleDraw();
            else DrawNow();
        }

        /// <summary>
        /// Empties the pane: nothing of the last request stays in it, shown or not (its result,
        /// its Changes view, a typed instruction), and a redraw still waiting is dropped. For
        /// text that must not be kept: a credential was stored from the note it came from. The
        /// next view draws as usual.
        /// </summary>
        public void Clear()
        {
            _redraw.Stop();
            _view = null;
            _drawn = false;
            _comparedOriginal = null;
            _comparedResult = null;
            ChangesToggle.IsChecked = false;
            ChangesToggle.Visibility = Visibility.Collapsed;
            ChangesList.ItemsSource = null;
            ChangesSummary.Text = "";
            InstructionBox.Clear();
            TitleText.Text = "";
            SourceText.Text = "";
            StatusText.Text = "";
            StatusText.Visibility = Visibility.Collapsed;
            ResultBox.ShowPlain("");
        }

        /// <summary>Puts the keyboard in the instruction box, with what it holds selected.</summary>
        public void FocusInstruction()
        {
            InstructionBox.Focus();
            InstructionBox.SelectAll();
        }

        /// <summary>
        /// Paints the pane for the pad's dark or light theme: the <c>Pad.*</c> brushes its XAML
        /// reads, the tints of the Changes view (the ones the history compare uses), its ModernWpf
        /// controls and the answer box.
        /// </summary>
        public void ApplyTheme(bool dark)
        {
            PadPalette palette = dark ? PadPalette.Dark : PadPalette.Light;
            PadThemeApplier.ApplyResources(Resources, palette);
            Resources[AddedTintKey] = PadThemeApplier.ToBrush(DiffPreview.TintOf(DiffKind.Added, palette) ?? palette.DiffAdded);
            Resources[RemovedTintKey] = PadThemeApplier.ToBrush(DiffPreview.TintOf(DiffKind.Removed, palette) ?? palette.DiffRemoved);
            ModernWpf.ThemeManager.SetRequestedTheme(this, dark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            ResultBox.ApplyTheme(dark);
        }

        /// <summary>
        /// A key pressed in the instruction box. Enter runs the instruction, or does nothing when
        /// the box is empty; either way it adds no line. Shift+Enter and every other key are left
        /// to the box. True when the key was used up.
        /// </summary>
        internal bool HandleInstructionKey(Key key, ModifierKeys modifiers)
        {
            if (key != Key.Enter || (modifiers & ModifierKeys.Shift) != 0) return false;
            string instruction = InstructionBox.Text.Trim();
            if (instruction.Length > 0) InstructionEntered?.Invoke(instruction);
            return true;
        }

        private static Visibility When(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// True when <paramref name="view"/> is the reply on screen with more text added: one
        /// stream going on, which may wait for the next redraw. Anything else (the first view, a
        /// finished one, another request) draws at once.
        /// </summary>
        private bool Continues(AiPaneView view) =>
            view.Running && _drawn && _drawnMarkdown == view.Markdown
            && view.Result.StartsWith(ResultBox.Shown, StringComparison.Ordinal);

        /// <summary>Draws now when the interval has passed since the last draw; otherwise once, when it has.</summary>
        private void ScheduleDraw()
        {
            if (_redraw.IsEnabled) return;
            TimeSpan since = _sinceDraw.Elapsed;
            if (since >= RedrawInterval)
            {
                DrawNow();
                return;
            }
            _redraw.Interval = RedrawInterval - since;
            _redraw.Start();
        }

        /// <summary>
        /// Draws the latest view's result, cancelling a redraw that was waiting. Text that is on
        /// screen already is left alone, so a refresh does not drop a selection in it.
        /// </summary>
        private void DrawNow()
        {
            _redraw.Stop();
            if (_view is not { } view) return;
            if (_drawn && _drawnMarkdown == view.Markdown && string.Equals(ResultBox.Shown, view.Result, StringComparison.Ordinal)) return;

            if (view.Markdown) ResultBox.ShowMarkdown(view.Result);
            else ResultBox.ShowPlain(view.Result);
            _drawn = true;
            _drawnMarkdown = view.Markdown;
            _sinceDraw.Restart();
        }

        /// <summary>Shows the result, or the line diff in its place while Changes is on.</summary>
        private void ShowResultOrChanges()
        {
            bool changes = ShowingChanges;
            ResultBox.Visibility = When(!changes);
            ChangesSummary.Visibility = When(changes);
            ChangesList.Visibility = When(changes);
            if (changes) Compare();
        }

        /// <summary>
        /// Lists the original against the result line by line, as History's compare does. Run again
        /// only when either text changed: the window refreshes the view on every keystroke.
        /// </summary>
        private void Compare()
        {
            if (_view is not { } view) return;
            if (string.Equals(_comparedOriginal, view.Original, StringComparison.Ordinal)
                && string.Equals(_comparedResult, view.Result, StringComparison.Ordinal)) return;
            _comparedOriginal = view.Original;
            _comparedResult = view.Result;

            DiffOutcome outcome = HistoryDiff.Compare(view.Original, view.Result);
            ChangesSummary.Text = outcome.Summary;   // the counts, "No changes" or "Too large to compare"
            ChangesList.ItemsSource = outcome.Rows.Select(row => new AiChangeRow(row.Kind, DiffPreview.GlyphOf(row.Kind), row.Text)).ToList();
        }

        private void OnChangesToggled(object sender, RoutedEventArgs e)
        {
            ShowResultOrChanges();
            ResultScroller.ScrollToHome();
        }

        private void OnInstructionKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleInstructionKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }

        private void OnReplaceClick(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke();

        private void OnInsertClick(object sender, RoutedEventArgs e) => InsertRequested?.Invoke();

        private void OnCopyClick(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();

        private void OnStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

        private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

        private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
