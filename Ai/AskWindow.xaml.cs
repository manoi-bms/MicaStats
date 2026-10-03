using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms in scope, which has its own KeyEventArgs and Button.
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Ask MicaStats: a conversation about this PC, shown as a chat.
    ///
    /// <para>
    /// Deliberately thin. Providers, the tool loop, limits and the limited-mode fallback live in
    /// <see cref="AiAssistant"/>; this window renders the updates it streams and never acts on its
    /// own. A suggestion runs only when its button is clicked, through the runner it was given
    /// (<see cref="SuggestedActionRunner.Run(SuggestedAction)"/> in the app).
    /// </para>
    ///
    /// <para>
    /// Everything it needs arrives as delegates, so a test builds it over a scripted stream with
    /// no provider, network or running app. The question stays in the box after any failure.
    /// </para>
    /// </summary>
    public partial class AskWindow : Window
    {
        private const string LimitedModeNote =
            "Limited mode: this model could not use MicaStats' tools, so the answer rests on a short summary of the PC right now.";

        /// <summary>How close to the bottom the transcript must be for new content to keep it there.</summary>
        private const double FollowDistance = 40;

        /// <summary>The starter questions on the empty screen; a click asks one at once.</summary>
        internal static readonly string[] PromptQuestions =
        {
            "Why was my PC slow earlier?",
            "What is using the most memory?",
            "Is my CPU running too hot?",
            "\u0E0A\u0E48\u0E27\u0E07\u0E19\u0E35\u0E49\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32\u0E40\u0E1E\u0E23\u0E32\u0E30\u0E2D\u0E30\u0E44\u0E23",
        };

        private static AskWindow? s_current;

        private readonly Func<AskSetup> _setup;
        private readonly Action _openSettings;
        private readonly Func<SuggestedAction, string> _runAction;
        private readonly Func<string?>? _modelLabel;
        private readonly AppConfig _config;
        private AskPalette _palette = AskPalette.Dark;
        private AiConversation _conversation = new();
        private readonly List<AskTurnView> _turns = new();
        private CancellationTokenSource? _cts;

        /// <summary>Raised by New conversation; a suggestion from an earlier conversation no longer runs.</summary>
        private int _generation;

        /// <summary>True while the transcript is at (or near) its end, so growing content keeps it there.</summary>
        private bool _follow = true;

        /// <summary>Builds the window over its collaborators; the app passes the live ones.</summary>
        /// <param name="setup">Builds what one Send needs, or says why it cannot.</param>
        /// <param name="openSettings">Opens Settings on the AI section.</param>
        /// <param name="runAction">Runs a clicked suggestion and returns the sentence to show.</param>
        /// <param name="modelLabel">Names the model in use for the header, read on open and on each Send; null hides the line.</param>
        /// <param name="config">The live config: <see cref="AppConfig.AskTheme"/> is read and written here. Null uses a private default config (tests).</param>
        internal AskWindow(Func<AskSetup> setup, Action openSettings, Func<SuggestedAction, string> runAction,
                           Func<string?>? modelLabel = null, AppConfig? config = null)
        {
            InitializeComponent();
            _setup = setup;
            _openSettings = openSettings;
            _runAction = runAction;
            _modelLabel = modelLabel;
            _config = config ?? new AppConfig();

            AskMenus.Install(QuestionBox, editable: true);
            ApplyTheme();
            _config.PropertyChanged += OnConfigChanged;
            SourceInitialized += (s, e) => PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);

            foreach (string question in PromptQuestions)
            {
                var chip = new Button { Style = (Style)FindResource("ChatPromptChip"), Content = question };
                chip.Click += (s, e) => Ask(question);
                PromptPanel.Children.Add(chip);
            }

            // Closing the window cancels an answer in progress, like Stop.
            Closed += (s, e) =>
            {
                _cts?.Cancel();
                _config.PropertyChanged -= OnConfigChanged;
                if (ReferenceEquals(s_current, this)) s_current = null;
            };
            UpdateButtons();
            RefreshModelLabel();
        }

        /// <summary>The palette the window is painted with.</summary>
        internal AskPalette Palette => _palette;

        /// <summary>Flips the Ask theme in the config; the window repaints from the change notice.</summary>
        internal void ToggleTheme() => _config.AskTheme = _palette.IsDark ? PadThemes.Light : PadThemes.Dark;

        private void OnThemeButtonClick(object sender, RoutedEventArgs e) => ToggleTheme();

        private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(AppConfig.AskTheme)) return;
            if (Dispatcher.CheckAccess()) ApplyTheme();
            else Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        /// <summary>
        /// Paints the window in the theme the config names: the Ask.* brushes the XAML and the turns
        /// read, the ModernWpf controls, the theme button and the title bar. Only this window changes.
        /// </summary>
        private void ApplyTheme()
        {
            _palette = AskPalette.For(_config.AskTheme);
            AskThemeApplier.ApplyResources(Resources, _palette);
            ModernWpf.ThemeManager.SetRequestedTheme(this, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            // Sun (E706) offers the light theme, moon (E708) the dark one.
            ThemeButton.Content = _palette.IsDark ? "\uE706" : "\uE708";
            ThemeButton.ToolTip = _palette.IsDark ? "Switch to light theme" : "Switch to dark theme";
            AskMenus.Retheme(this, _palette);
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
        }

        /// <summary>The open window, or null.</summary>
        public static AskWindow? Current => s_current;

        /// <summary>
        /// Shows the window, creating it on first use, and brings it forward. With a
        /// <paramref name="question"/> (an Explain button), asks it at once.
        /// </summary>
        public static AskWindow ShowOrActivate(string? question = null)
        {
            var window = s_current;
            if (window == null)
            {
                window = new AskWindow(App.CreateAskSetup, () => App.ShowSettingsSection("AI"), SuggestedActionRunner.Run,
                    App.AskModelLabel, App.ConfigService?.Config);
                s_current = window;
                window.Show();
            }
            else
            {
                window.RefreshModelLabel();
            }

            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            if (question != null) window.Ask(question);
            else window.QuestionBox.Focus();
            return window;
        }

        /// <summary>Closes the window if it is open: the assistant was switched off.</summary>
        internal static void CloseIfOpen() => s_current?.Close();

        /// <summary>The turns shown, oldest first.</summary>
        internal IReadOnlyList<AskTurnView> Turns => _turns;

        /// <summary>The starter-question buttons of the empty screen.</summary>
        internal IReadOnlyList<Button> PromptChips => PromptPanel.Children.OfType<Button>().ToList();

        /// <summary>The latest Send, for tests to wait on.</summary>
        internal Task? Pending { get; private set; }

        /// <summary>True while an answer is streaming.</summary>
        internal bool IsBusy => _cts != null;

        /// <summary>Puts <paramref name="question"/> in the box and sends it, unless an answer is still streaming.</summary>
        internal Task Ask(string question)
        {
            QuestionBox.Text = question;
            if (IsBusy)
            {
                SetStatus("Your question is in the box. Press Send when this answer has finished, or Stop it first.");
                return Task.CompletedTask;
            }
            return StartSend();
        }

        /// <summary>Cancels the answer in progress; the question stays in the box.</summary>
        internal void Stop() => _cts?.Cancel();

        /// <summary>Starts over: clears the transcript and the conversation, and disarms old suggestions.</summary>
        internal void NewConversation()
        {
            _cts?.Cancel();
            _generation++;
            // A new instance, not Clear(): an orphaned stream rolls back its own conversation.
            _conversation = new AiConversation();
            _turns.Clear();
            TranscriptPanel.Children.Clear();
            EmptyState.Visibility = Visibility.Visible;
            _follow = true;
            HideProblem();
            SetStatus("");
        }

        /// <summary>True when the transcript is within <see cref="FollowDistance"/> of its end.</summary>
        internal static bool IsNearEnd(double verticalOffset, double scrollableHeight) =>
            scrollableHeight - verticalOffset <= FollowDistance;

        private Task StartSend()
        {
            Pending = SendAsync();
            return Pending;
        }

        private async Task SendAsync()
        {
            string question = QuestionBox.Text.Trim();
            if (question.Length == 0 || IsBusy) return;
            HideProblem();
            RefreshModelLabel();

            AskSetup setup;
            try
            {
                setup = _setup();
            }
            catch (Exception ex)
            {
                ShowProblem(AiErrorText.Describe(ex), offerSettings: true, offerRetry: false);
                return;
            }

            if (setup.Ask is not { } ask)
            {
                setup.Resource?.Dispose();
                ShowProblem(setup.Problem ?? "The assistant is not set up yet. Open Settings > AI.", offerSettings: true, offerRetry: false);
                return;
            }

            AskTurnView turn = AddTurn(question);
            int generation = _generation;
            var cts = new CancellationTokenSource();
            _cts = cts;
            UpdateButtons();
            SetStatus("");
            bool failed = false;
            AiConversation conversation = _conversation;

            try
            {
                await foreach (AssistantUpdate update in ask(conversation, question, cts.Token))
                {
                    // A note tool handed notes to the model: said by the tool itself, before its
                    // result reaches the model, so every answer text after it is shown this way.
                    if (conversation.NotesRead) turn.ShowLinksAsText();

                    switch (update.Kind)
                    {
                        case AssistantUpdateKind.Text:
                            if (!string.IsNullOrEmpty(update.Text)) turn.AppendText(update.Text);
                            break;
                        case AssistantUpdateKind.ToolUsed:
                            if (!string.IsNullOrEmpty(update.ToolName)) turn.AddTool(update.ToolName, update.ToolArgs);
                            break;
                        case AssistantUpdateKind.Suggestion:
                            if (update.Suggestion is { } action)
                                turn.AddAction(action, () => RunSuggestion(action, generation));
                            break;
                        case AssistantUpdateKind.LimitedMode:
                            turn.ShowNote(update.Text ?? LimitedModeNote);
                            break;
                        case AssistantUpdateKind.Error:
                            failed = true;
                            string message = update.Text ?? "The assistant could not answer.";
                            turn.ShowNote(message);
                            ShowProblem(message, offerSettings: message.Contains("Settings > AI", StringComparison.Ordinal), offerRetry: true);
                            break;
                        case AssistantUpdateKind.Done:
                            break;
                    }
                }

                // The assistant does not throw on cancellation: it drops the exchange from the
                // conversation and ends with Done, so a stop is recognised here.
                if (cts.IsCancellationRequested && !failed)
                {
                    failed = true;
                    ShowStopped(turn, generation);
                }
            }
            catch (OperationCanceledException)
            {
                failed = true;
                ShowStopped(turn, generation);
            }
            catch (Exception ex)
            {
                failed = true;
                string message = AiErrorText.Describe(ex);
                turn.ShowNote(message);
                if (generation == _generation)
                    ShowProblem(message, offerSettings: message.Contains("Settings > AI", StringComparison.Ordinal), offerRetry: true);
            }
            finally
            {
                _cts = null;
                cts.Dispose();
                setup.Resource?.Dispose();
                if (conversation.NotesRead) turn.ShowLinksAsText();
                turn.Complete(DateTime.Now);
                UpdateButtons();
            }

            if (failed || generation != _generation) return;

            // Only the question that was answered is cleared; anything typed meanwhile stays.
            if (QuestionBox.Text.Trim() == question) QuestionBox.Clear();
            if (turn.RawText.Length == 0 && turn.ActionButtons.Count == 0)
                turn.ShowNote("The model sent back no text. Try asking again.");
            SetStatus(turn.ActionButtons.Count > 0 ? "Suggestions do nothing until you click them." : "");
        }

        private void ShowStopped(AskTurnView turn, int generation)
        {
            turn.ShowNote("Stopped.");
            if (generation == _generation) SetStatus("Stopped. Your question is still in the box.");
        }

        /// <summary>
        /// Adds a turn for <paramref name="question"/>; sending always shows the end of the transcript.
        /// Once a note tool was used in this conversation, every later turn shows its links as
        /// text too: the conversation keeps that tool's result and sends it with each later
        /// question, so note text can steer those answers as well. The conversation says so itself
        /// (<see cref="AiConversation.NotesRead"/>); a turn that saw a note tool's chip counts too.
        /// New conversation starts clean.
        /// </summary>
        private AskTurnView AddTurn(string question)
        {
            var turn = new AskTurnView(question) { PlainLinks = _conversation.NotesRead || _turns.Any(t => t.PlainLinks) };
            _turns.Add(turn);
            TranscriptPanel.Children.Add(turn.Root);
            EmptyState.Visibility = Visibility.Collapsed;
            _follow = true;
            TranscriptScroll.ScrollToEnd();
            return turn;
        }

        private void RunSuggestion(SuggestedAction action, int generation)
        {
            if (generation != _generation)
            {
                SetStatus("That suggestion belongs to a conversation that was cleared, so it does nothing now.");
                return;
            }

            try
            {
                SetStatus(_runAction(action));
            }
            catch (Exception ex)
            {
                SetStatus("That did not work: " + ex.Message);
            }
        }

        /// <summary>Reads the model label again: settings may have changed since the last look.</summary>
        private void RefreshModelLabel()
        {
            string? label = null;
            try
            {
                label = _modelLabel?.Invoke();
            }
            catch (Exception ex)
            {
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", "Reading the model label failed (" + ex.GetType().Name + ")");
            }
            ModelText.Text = label ?? "";
            ModelText.Visibility = string.IsNullOrWhiteSpace(label) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
            UpdateStatusRow();
        }

        private void ShowProblem(string message, bool offerSettings, bool offerRetry)
        {
            StatusText.Text = message;
            SettingsButton.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = offerRetry ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatusRow();
        }

        private void HideProblem()
        {
            SettingsButton.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            UpdateStatusRow();
        }

        /// <summary>The status line takes no room while it has nothing to say.</summary>
        private void UpdateStatusRow()
        {
            bool empty = StatusText.Text.Length == 0
                         && SettingsButton.Visibility != Visibility.Visible
                         && RetryButton.Visibility != Visibility.Visible;
            StatusRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Send while idle, Stop in the same spot while an answer streams.</summary>
        private void UpdateButtons()
        {
            bool busy = IsBusy;
            SendButton.IsEnabled = !busy;
            StopButton.IsEnabled = busy;
            SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// New content keeps the end in view only if the reader was already there: reading an
        /// earlier answer is never interrupted. A scroll by the reader decides which it is.
        /// </summary>
        private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                _follow = IsNearEnd(TranscriptScroll.VerticalOffset, TranscriptScroll.ScrollableHeight);
            else if (_follow)
                TranscriptScroll.ScrollToEnd();
        }

        private void OnSend(object sender, RoutedEventArgs e) => StartSend();

        private void OnStop(object sender, RoutedEventArgs e) => Stop();

        private void OnRetry(object sender, RoutedEventArgs e) => StartSend();

        private void OnNewConversation(object sender, RoutedEventArgs e) => NewConversation();

        private void OnOpenSettings(object sender, RoutedEventArgs e) => _openSettings();

        /// <summary>A click anywhere in the composer puts the caret in the question box.</summary>
        private void OnComposerMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!QuestionBox.IsKeyboardFocusWithin) QuestionBox.Focus();
        }

        /// <summary>Enter sends, Shift+Enter adds a line, Escape stops an answer in progress.</summary>
        private void OnQuestionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                StartSend();
            }
            else if (e.Key == Key.Escape && IsBusy)
            {
                e.Handled = true;
                Stop();
            }
        }
    }
}
