using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Ai;

// UseWindowsForms puts System.Windows.Forms in scope, which has its own KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Ask MicaStats: a conversation about this PC.
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

        private static AskWindow? s_current;

        private readonly Func<AskSetup> _setup;
        private readonly Action _openSettings;
        private readonly Func<SuggestedAction, string> _runAction;
        private AiConversation _conversation = new();
        private readonly List<AskTurnView> _turns = new();
        private CancellationTokenSource? _cts;

        /// <summary>Raised by New conversation; a suggestion from an earlier conversation no longer runs.</summary>
        private int _generation;

        /// <summary>Builds the window over its collaborators; the app passes the live ones.</summary>
        /// <param name="setup">Builds what one Send needs, or says why it cannot.</param>
        /// <param name="openSettings">Opens Settings on the AI section.</param>
        /// <param name="runAction">Runs a clicked suggestion and returns the sentence to show.</param>
        internal AskWindow(Func<AskSetup> setup, Action openSettings, Func<SuggestedAction, string> runAction)
        {
            InitializeComponent();
            _setup = setup;
            _openSettings = openSettings;
            _runAction = runAction;

            // Closing the window cancels an answer in progress, like Stop.
            Closed += (s, e) =>
            {
                _cts?.Cancel();
                if (ReferenceEquals(s_current, this)) s_current = null;
            };
            UpdateButtons();
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
                window = new AskWindow(App.CreateAskSetup, () => App.ShowSettingsSection("AI"), SuggestedActionRunner.Run);
                s_current = window;
                window.Show();
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
                StatusText.Text = "Your question is in the box. Press Send when this answer has finished, or Stop it first.";
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
            EmptyText.Visibility = Visibility.Visible;
            HideProblem();
            StatusText.Text = "";
        }

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
            StatusText.Text = "Thinking\u2026";
            bool failed = false;
            AiConversation conversation = _conversation;

            try
            {
                await foreach (AssistantUpdate update in ask(conversation, question, cts.Token))
                {
                    switch (update.Kind)
                    {
                        case AssistantUpdateKind.Text:
                            if (!string.IsNullOrEmpty(update.Text)) turn.AppendText(update.Text);
                            StatusText.Text = "";
                            break;
                        case AssistantUpdateKind.ToolUsed:
                            if (!string.IsNullOrEmpty(update.ToolName))
                            {
                                turn.AddTool(update.ToolName, update.ToolArgs);
                                StatusText.Text = "Looking up " + update.ToolName + "\u2026";
                            }
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
                    TranscriptScroll.ScrollToEnd();
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
                UpdateButtons();
            }

            if (failed || generation != _generation) return;

            // Only the question that was answered is cleared; anything typed meanwhile stays.
            if (QuestionBox.Text.Trim() == question) QuestionBox.Clear();
            if (turn.Answer.Text.Length == 0 && turn.ActionButtons.Count == 0)
                turn.ShowNote("The model sent back no text. Try asking again.");
            StatusText.Text = turn.ActionButtons.Count > 0 ? "Suggestions do nothing until you click them." : "";
        }

        private void ShowStopped(AskTurnView turn, int generation)
        {
            turn.ShowNote("Stopped.");
            if (generation == _generation) StatusText.Text = "Stopped. Your question is still in the box.";
        }

        private AskTurnView AddTurn(string question)
        {
            var turn = new AskTurnView(question);
            _turns.Add(turn);
            TranscriptPanel.Children.Add(turn.Root);
            EmptyText.Visibility = Visibility.Collapsed;
            TranscriptScroll.ScrollToEnd();
            return turn;
        }

        private void RunSuggestion(SuggestedAction action, int generation)
        {
            if (generation != _generation)
            {
                StatusText.Text = "That suggestion belongs to a conversation that was cleared, so it does nothing now.";
                return;
            }

            try
            {
                StatusText.Text = _runAction(action);
            }
            catch (Exception ex)
            {
                StatusText.Text = "That did not work: " + ex.Message;
            }
        }

        private void ShowProblem(string message, bool offerSettings, bool offerRetry)
        {
            StatusText.Text = message;
            SettingsButton.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = offerRetry ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideProblem()
        {
            SettingsButton.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
        }

        private void UpdateButtons()
        {
            SendButton.IsEnabled = !IsBusy;
            StopButton.IsEnabled = IsBusy;
        }

        private void OnSend(object sender, RoutedEventArgs e) => StartSend();

        private void OnStop(object sender, RoutedEventArgs e) => Stop();

        private void OnRetry(object sender, RoutedEventArgs e) => StartSend();

        private void OnNewConversation(object sender, RoutedEventArgs e) => NewConversation();

        private void OnOpenSettings(object sender, RoutedEventArgs e) => _openSettings();

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
