using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.History;
using Microsoft.Extensions.AI;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using List = System.Windows.Documents.List;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Ask MicaStats window, built for real on the UI test thread but never shown. Most tests
    /// feed it a scripted stream of assistant updates; a few run a real AiAssistant over a fake
    /// model. No provider, network, clipboard, browser or %APPDATA% is involved.
    /// </summary>
    public class AiAskWindowTests
    {
        private static readonly SuggestedAction EndChrome = new(
            SuggestedActionKind.EndProcess, "End chrome.exe", "It has used most of the CPU for ten minutes", 1234, 555L, "chrome.exe");

        /// <summary>Scripted setups, the questions asked, and what the runner was handed.</summary>
        private sealed class Harness
        {
            public readonly Queue<AskSetup> Setups = new();
            public readonly List<string> Questions = new();
            public readonly List<SuggestedAction> Ran = new();
            public int SettingsOpened;

            public AskSetup Answer(params AssistantUpdate[] updates) =>
                new((conversation, question, ct) =>
                {
                    Questions.Add(question);
                    return Play(updates, ct);
                }, null);

            public AskWindow Build(Func<string?>? modelLabel = null) => new(
                () => Setups.Dequeue(),
                () => SettingsOpened++,
                action =>
                {
                    Ran.Add(action);
                    return "Ended chrome.exe.";
                },
                modelLabel);
        }

        private static void WithWindow(Action<AskWindow, Harness> test) => UiThread.Run(() =>
        {
            var harness = new Harness();
            var window = harness.Build();
            try
            {
                test(window, harness);
            }
            finally
            {
                window.Close();
            }
        });

        private static void Click(UIElement element) =>
            element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static void Send(AskWindow window, string question)
        {
            window.QuestionBox.Text = question;
            Click(window.SendButton);
            UiPump.Wait(window.Pending!);
        }

        private static async IAsyncEnumerable<AssistantUpdate> Play(
            AssistantUpdate[] updates, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        private static async IAsyncEnumerable<AssistantUpdate> Hang(
            TaskCompletionSource reached, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new AssistantUpdate(AssistantUpdateKind.Text, "Partial");
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        /// <summary>Every element of type <typeparamref name="T"/> under <paramref name="root"/> in the logical tree.</summary>
        internal static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            var found = new List<T>();
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject element) continue;
                if (element is T match) found.Add(match);
                found.AddRange(Descendants<T>(element));
            }
            return found;
        }

        [Fact]
        public void A_streamed_answer_fills_one_turn_and_clears_the_box() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "The CPU "),
                new AssistantUpdate(AssistantUpdateKind.Text, "is fine."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "How is the CPU?");

            var turn = Assert.Single(window.Turns);
            Assert.Equal("How is the CPU?", turn.Question.Text);
            Assert.Equal("The CPU is fine.", turn.RawText);
            Assert.Empty(turn.ToolChips);
            Assert.Equal(Visibility.Collapsed, turn.Tools.Visibility);
            Assert.Equal(Visibility.Visible, turn.Answer.Visibility);
            Assert.Equal(Visibility.Visible, turn.Footer.Visibility);
            Assert.Matches("^[0-9]{2}:[0-9]{2}$", turn.TimeText.Text);
            Assert.Equal("", window.QuestionBox.Text);
            Assert.True(window.SendButton.IsEnabled);
            Assert.False(window.StopButton.IsEnabled);
            Assert.Equal(new[] { "How is the CPU?" }, h.Questions);
        });

        [Fact]
        public void The_answer_is_rendered_from_its_markdown() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "## Why\n\n- **Chrome** uses 40%\n"),
                new AssistantUpdate(AssistantUpdateKind.Text, "- Teams uses 12%"),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Why is it slow?");

            var document = window.Turns[0].Answer.Document;
            var heading = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            Assert.Equal(16, heading.FontSize);
            var list = Assert.IsType<List>(document.Blocks.LastBlock);
            Assert.Equal(2, list.ListItems.Count);
            var bold = Assert.Single(Descendants<Run>(list), r => r.Text == "Chrome");
            Assert.Equal(FontWeights.SemiBold, bold.FontWeight);
            Assert.DoesNotContain(Descendants<Run>(document), r => r.Text.Contains("**", StringComparison.Ordinal));
        });

        [Fact]
        public void Tools_used_show_as_chips_with_their_calls_in_the_tooltip() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_live_status"),
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_top_processes", ToolArgs: "{\"by\":\"cpu\",\"count\":5}"),
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_top_processes", ToolArgs: "{\"by\":\"memory\"}"),
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is the busiest."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "What is busy?");

            var turn = Assert.Single(window.Turns);
            Assert.Equal(Visibility.Visible, turn.Tools.Visibility);
            Assert.Equal(new[] { "Read live status", "Checked top processes" }, turn.ToolChips.Select(c => c.Label.Text));
            Assert.Equal("get_live_status", turn.ToolChips[0].Element.ToolTip);
            Assert.Equal("get_top_processes {\"by\":\"cpu\",\"count\":5}\nget_top_processes {\"by\":\"memory\"}",
                turn.ToolChips[1].Element.ToolTip);
            Assert.Equal(2, turn.Tools.Children.Count);
            Assert.Equal("Chrome is the busiest.", turn.RawText);
        });

        [Fact]
        public void The_typing_indicator_shows_from_send_until_the_first_text() => WithWindow((window, h) =>
        {
            var toolSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => ToolThenText(toolSeen, gate.Task, ct), null));

            window.QuestionBox.Text = "Is it fine?";
            Click(window.SendButton);
            var turn = window.Turns[0];
            Assert.Equal(Visibility.Visible, turn.Typing.Visibility);
            Assert.Equal(Visibility.Collapsed, turn.Answer.Visibility);

            UiPump.Wait(toolSeen.Task);
            Assert.Single(turn.ToolChips);
            Assert.Equal(Visibility.Visible, turn.Typing.Visibility);

            gate.SetResult();
            UiPump.Wait(window.Pending!);
            Assert.Equal(Visibility.Collapsed, turn.Typing.Visibility);
            Assert.Equal("All fine.", turn.RawText);
        });

        private static async IAsyncEnumerable<AssistantUpdate> ToolThenText(
            TaskCompletionSource toolSeen, Task gate, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_live_status");
            toolSeen.TrySetResult();
            await gate.WaitAsync(ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Text, "All fine.");
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        [Fact]
        public void A_suggestion_runs_only_when_clicked_and_goes_through_the_runner() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is using most of the CPU."),
                new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: EndChrome),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Why is it slow?");

            var button = Assert.Single(window.Turns[0].ActionButtons);
            Assert.Equal("End chrome.exe (PID 1234)", button.Content);
            Assert.Same(button, Assert.Single(window.Turns[0].Actions.Children));
            Assert.Empty(h.Ran);
            Assert.Equal("Suggestions do nothing until you click them.", window.StatusText.Text);
            Assert.Equal(Visibility.Visible, window.StatusRow.Visibility);

            Click(button);

            Assert.Equal(new[] { EndChrome }, h.Ran);
            Assert.Equal("Ended chrome.exe.", window.StatusText.Text);
        });

        /// <summary>
        /// Text the model read (a process name, a report, a sensor label) could ask it to label the
        /// one destructive button as something harmless; the button must say what it ends.
        /// </summary>
        [Fact]
        public void An_end_process_button_names_the_process_and_pid_whatever_the_label_says() => WithWindow((window, h) =>
        {
            var disguised = new SuggestedAction(SuggestedActionKind.EndProcess, "Open Diagnostics",
                "Opens the saved reports", 1234, 555L, "chrome.exe");
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: disguised),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Why is it slow?");

            var button = Assert.Single(window.Turns[0].ActionButtons);
            Assert.Equal("End chrome.exe (PID 1234)", button.Content);
        });

        [Fact]
        public void New_conversation_clears_the_transcript_and_disarms_old_suggestions() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is using most of the CPU."),
                new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: EndChrome),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Send(window, "Why is it slow?");
            var oldButton = window.Turns[0].ActionButtons[0];
            Assert.Equal(Visibility.Collapsed, window.EmptyState.Visibility);

            Click(window.NewButton);

            Assert.Empty(window.Turns);
            Assert.Empty(window.TranscriptPanel.Children);
            Assert.Equal(Visibility.Visible, window.EmptyState.Visibility);

            Click(oldButton);

            Assert.Empty(h.Ran);
            Assert.StartsWith("That suggestion belongs to a conversation that was cleared", window.StatusText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void The_empty_screen_offers_four_starter_questions_that_ask_at_once() => WithWindow((window, h) =>
        {
            Assert.Equal(Visibility.Visible, window.EmptyState.Visibility);
            Assert.Equal(new[]
            {
                "Why was my PC slow earlier?",
                "What is using the most memory?",
                "Is my CPU running too hot?",
                "\u0E0A\u0E48\u0E27\u0E07\u0E19\u0E35\u0E49\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32\u0E40\u0E1E\u0E23\u0E32\u0E30\u0E2D\u0E30\u0E44\u0E23",
            }, window.PromptChips.Select(b => (string)b.Content));

            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome, at 3 GB."),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Click(window.PromptChips[1]);
            UiPump.Wait(window.Pending!);

            Assert.Equal(new[] { "What is using the most memory?" }, h.Questions);
            Assert.Equal("What is using the most memory?", Assert.Single(window.Turns).Question.Text);
            Assert.Equal(Visibility.Collapsed, window.EmptyState.Visibility);
        });

        [Fact]
        public void A_setup_problem_offers_settings_and_keeps_the_question() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(new AskSetup(null, "Add an API key in Settings > AI."));

            Send(window, "Hello?");

            Assert.Empty(window.Turns);
            Assert.Equal("Add an API key in Settings > AI.", window.StatusText.Text);
            Assert.Equal(Visibility.Visible, window.StatusRow.Visibility);
            Assert.Equal(Visibility.Visible, window.SettingsButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
            Assert.Equal("Hello?", window.QuestionBox.Text);

            Click(window.SettingsButton);

            Assert.Equal(1, h.SettingsOpened);
        });

        [Fact]
        public void An_error_offers_retry_and_keeps_the_question_until_an_answer_arrives() => WithWindow((window, h) =>
        {
            const string failure = "MicaStats could not reach the provider. Check the connection and try again.";
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Error, failure),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "The disk has 40 GB free."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Is the disk full?");

            Assert.Equal(failure, window.StatusText.Text);
            Assert.Equal(failure, window.Turns[0].NoteText.Text);
            Assert.Equal(Visibility.Visible, window.Turns[0].Note.Visibility);
            Assert.Equal(Visibility.Collapsed, window.Turns[0].Typing.Visibility);
            Assert.Equal(Visibility.Visible, window.RetryButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.SettingsButton.Visibility);
            Assert.Equal("Is the disk full?", window.QuestionBox.Text);

            Click(window.RetryButton);
            UiPump.Wait(window.Pending!);

            Assert.Equal(2, window.Turns.Count);
            Assert.Equal("The disk has 40 GB free.", window.Turns[1].RawText);
            Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StatusRow.Visibility);
            Assert.Equal("", window.QuestionBox.Text);
            Assert.Equal(new[] { "Is the disk full?", "Is the disk full?" }, h.Questions);
        });

        [Fact]
        public void Stop_cancels_the_answer_and_keeps_the_question() => WithWindow((window, h) =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => Hang(reached, ct), null));
            Assert.Equal(Visibility.Visible, window.SendButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StopButton.Visibility);

            window.QuestionBox.Text = "Why is it slow?";
            Click(window.SendButton);
            UiPump.Wait(reached.Task);

            Assert.Equal("Partial", window.Turns[0].RawText);
            Assert.True(window.StopButton.IsEnabled);
            Assert.False(window.SendButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.StopButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.SendButton.Visibility);

            Click(window.StopButton);
            UiPump.Wait(window.Pending!);

            Assert.Equal("Stopped.", window.Turns[0].NoteText.Text);
            Assert.Equal("Why is it slow?", window.QuestionBox.Text);
            Assert.True(window.SendButton.IsEnabled);
            Assert.False(window.StopButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.SendButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StopButton.Visibility);
        });

        [Fact]
        public void Limited_mode_is_marked_on_the_answer() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.LimitedMode, "Limited mode: this model cannot use tools."),
                new AssistantUpdate(AssistantUpdateKind.Text, "Memory looks fine."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Memory?");

            var turn = window.Turns[0];
            Assert.Equal(Visibility.Visible, turn.Note.Visibility);
            Assert.Equal("Limited mode: this model cannot use tools.", turn.NoteText.Text);
            Assert.Equal("Memory looks fine.", turn.RawText);
        });

        [Fact]
        public void Explain_asks_its_question_at_once() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "It hosts Windows services."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            UiPump.Wait(window.Ask("What is svchost.exe (PID 1234) doing?"));

            Assert.Equal(new[] { "What is svchost.exe (PID 1234) doing?" }, h.Questions);
            Assert.Equal("What is svchost.exe (PID 1234) doing?", window.Turns[0].Question.Text);
            Assert.Equal("It hosts Windows services.", window.Turns[0].RawText);
        });

        [Fact]
        public void Copy_hands_the_answers_markdown_to_the_clipboard_seam() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "**Chrome** uses *40%*.\n\n- Close it"),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Send(window, "What is busy?");
            var turn = window.Turns[0];
            Assert.Equal(Visibility.Visible, turn.Footer.Visibility);

            var copied = new List<string>();
            var previous = AskTurnView.SetClipboard;
            AskTurnView.SetClipboard = copied.Add;
            try
            {
                Click(turn.CopyButton);
            }
            finally
            {
                AskTurnView.SetClipboard = previous;
            }

            Assert.Equal(new[] { "**Chrome** uses *40%*.\n\n- Close it" }, copied);
        });

        [Fact]
        public void The_model_label_shows_under_the_title_and_follows_the_settings() => UiThread.Run(() =>
        {
            var harness = new Harness();
            string? label = "Claude \u00B7 claude-haiku-4-5";
            var window = harness.Build(() => label);
            try
            {
                Assert.Equal(Visibility.Visible, window.ModelText.Visibility);
                Assert.Equal("Claude \u00B7 claude-haiku-4-5", window.ModelText.Text);

                label = "llama3.2 \u00B7 localhost";
                harness.Setups.Enqueue(harness.Answer(new AssistantUpdate(AssistantUpdateKind.Done)));
                Send(window, "Hi?");
                Assert.Equal("llama3.2 \u00B7 localhost", window.ModelText.Text);

                label = null;
                harness.Setups.Enqueue(harness.Answer(new AssistantUpdate(AssistantUpdateKind.Done)));
                Send(window, "Hi again?");
                Assert.Equal(Visibility.Collapsed, window.ModelText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void Without_a_model_label_the_line_is_hidden() => WithWindow((window, h) =>
        {
            Assert.Equal(Visibility.Collapsed, window.ModelText.Visibility);
        });

        [Fact]
        public void A_link_in_an_answer_opens_only_through_the_seam_and_only_for_a_safe_scheme() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text,
                    "See [the docs](https://example.com/docs), [calc](file:///C:/Windows/System32/calc.exe) or https://example.org/x."),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Send(window, "Where can I read more?");

            var links = Descendants<Hyperlink>(window.Turns[0].Answer.Document);
            Assert.Equal(2, links.Count);
            Assert.Contains(Descendants<Run>(window.Turns[0].Answer.Document), r => r.Text.Contains("calc", StringComparison.Ordinal));

            var opened = new List<Uri>();
            var previous = ChatDocument.OpenLink;
            ChatDocument.OpenLink = opened.Add;
            try
            {
                foreach (var link in links) link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                ChatDocument.Open(new Uri("file:///C:/Windows/System32/calc.exe"));
                ChatDocument.Open(new Uri("ms-settings:display"));
            }
            finally
            {
                ChatDocument.OpenLink = previous;
            }

            Assert.Equal(new[] { new Uri("https://example.com/docs"), new Uri("https://example.org/x") }, opened);
        });

        [Theory]
        [InlineData(960, 1000, true)]
        [InlineData(1000, 1000, true)]
        [InlineData(900, 1000, false)]
        [InlineData(0, 0, true)]
        public void New_content_follows_only_a_reader_near_the_end(double offset, double scrollable, bool follows)
        {
            Assert.Equal(follows, AskWindow.IsNearEnd(offset, scrollable));
        }

        /// <summary>A model that answers in two streamed chunks and never asks for a tool.</summary>
        private sealed class TwoChunkClient : IChatClient
        {
            public int Calls;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "CPU is fine.")));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Calls++;
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "CPU is ");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "fine.");
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

            public void Dispose() { }
        }

        [Fact]
        public void A_real_assistant_streams_its_answer_into_the_window() => UiThread.Run(() =>
        {
            using var env = new AiTestEnv();
            var store = new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow);
            var tools = new MicaTools(
                new OfflineMicaData(store, env.PathOf("reports"), () => env.Clock.UtcNow),
                new Redactor(@"C:\Users\tester", "tester", "TESTPC"));
            var usage = new UsageMeter(env.PathOf("ai-usage.json"),
                () => new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Local));
            var client = new TwoChunkClient();
            var assistant = new AiAssistant(client, false, tools, usage, new AiAssistantOptions());
            var window = new AskWindow(() => new AskSetup(assistant.AskAsync, null), () => { }, _ => "");
            try
            {
                Send(window, "How is the CPU?");

                var turn = Assert.Single(window.Turns);
                Assert.Equal("CPU is fine.", turn.RawText);
                Assert.True(client.Calls >= 1);
                Assert.Equal(1, usage.UsedToday);
                Assert.Equal("", window.QuestionBox.Text);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void An_exception_that_points_to_settings_offers_open_settings() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => Throw(), null));

            Send(window, "Hello?");

            Assert.Contains("Settings > AI", window.StatusText.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, window.SettingsButton.Visibility);
            Assert.Equal(Visibility.Visible, window.RetryButton.Visibility);
            Assert.Equal("Hello?", window.QuestionBox.Text);
        });

        private static async IAsyncEnumerable<AssistantUpdate> Throw()
        {
            await Task.Yield();
            if (DateTime.Now.Year > 0) throw new System.Net.Http.HttpRequestException("down");
            yield break;
        }

        /// <summary>A window over a real AiAssistant and a scripted model; <paramref name="seen"/> collects each question's conversation.</summary>
        private static AskWindow RealWindow(ScriptedChatClient client, AiTestEnv env, List<AiConversation> seen)
        {
            var store = new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow);
            var tools = new MicaTools(
                new OfflineMicaData(store, env.PathOf("reports"), () => env.Clock.UtcNow),
                new Redactor(@"C:\Users\tester", "tester", "TESTPC"));
            var usage = new UsageMeter(env.PathOf("ai-usage.json"),
                () => new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Local));
            var assistant = new AiAssistant(client, false, tools, usage, new AiAssistantOptions());
            return new AskWindow(
                () => new AskSetup((conversation, question, ct) =>
                {
                    seen.Add(conversation);
                    return assistant.AskAsync(conversation, question, ct);
                }, null),
                () => { }, _ => "");
        }

        private static Task ModelReached(ScriptedChatClient client) => Task.Run(async () =>
        {
            while (client.Requests.Count == 0) await Task.Delay(10);
        });

        [Fact]
        public void Stop_over_a_real_assistant_keeps_the_question_and_drops_the_exchange() => UiThread.Run(() =>
        {
            using var env = new AiTestEnv();
            var client = new ScriptedChatClient().Hang();
            var seen = new List<AiConversation>();
            var window = RealWindow(client, env, seen);
            try
            {
                window.QuestionBox.Text = "Why is it slow?";
                Click(window.SendButton);
                UiPump.Wait(ModelReached(client));

                Click(window.StopButton);
                UiPump.Wait(window.Pending!);

                Assert.Equal("Why is it slow?", window.QuestionBox.Text);
                Assert.Equal("Stopped.", window.Turns[0].NoteText.Text);
                Assert.StartsWith("Stopped.", window.StatusText.Text, StringComparison.Ordinal);
                Assert.Empty(seen[0].Messages);
                Assert.True(window.SendButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void New_conversation_mid_answer_shows_no_error_and_the_next_question_works() => UiThread.Run(() =>
        {
            using var env = new AiTestEnv();
            var client = new ScriptedChatClient().Hang();
            var seen = new List<AiConversation>();
            var window = RealWindow(client, env, seen);
            try
            {
                window.QuestionBox.Text = "First?";
                Click(window.SendButton);
                var first = window.Pending!;
                UiPump.Wait(ModelReached(client));

                Click(window.NewButton);
                UiPump.Wait(first);

                Assert.Empty(window.Turns);
                Assert.Equal("", window.StatusText.Text);
                Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
                Assert.Empty(seen[0].Messages);

                Send(window, "Second?");

                Assert.Equal("Done.", window.Turns[0].RawText);
                Assert.Equal(2, seen.Count);
                Assert.NotSame(seen[0], seen[1]);
                Assert.Equal("", window.StatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });

        // ---- Mermaid diagrams in answers ------------------------------------------------------------

        private static AssistantUpdate[] AnswerWith(string text) => new[]
        {
            new AssistantUpdate(AssistantUpdateKind.Text, text),
            new AssistantUpdate(AssistantUpdateKind.Done),
        };

        [Fact]
        public void A_turn_draws_in_the_windows_theme_and_a_theme_change_draws_every_turn_again() => UiThread.Run(() =>
        {
            const string pie = "pie\n  \"a\" : 1";
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var before = ChatDiagrams.Current;
            ChatDiagrams.Current = diagrams;   // what the app sets at startup; a turn takes it when it is made
            var h = new Harness();
            var config = new Kil0bitSystemMonitor.Models.AppConfig { AskTheme = "Light" };
            var window = new AskWindow(() => h.Setups.Dequeue(), () => { }, _ => "", null, config);
            try
            {
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block(pie))));
                Send(window, "One?");
                Send(window, "Two?");

                Assert.NotEmpty(diagrams.Gets);
                Assert.All(diagrams.Gets, g => Assert.False(g.Dark));   // a new turn has the window's theme before it first renders
                Assert.All(window.Turns, t => Assert.Single(ChatDocument.All<System.Windows.Controls.Image>(t.Answer.Document)));
                diagrams.Gets.Clear();

                config.AskTheme = "Dark";

                // A diagram is a bitmap drawn for one theme: every turn builds its document again.
                Assert.Equal(new[] { (ChatDiagramFakes.Flow, true), (pie, true) }, diagrams.Gets.Select(g => (g.Source, g.Dark)));
                var menu = ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document).Single().ContextMenu!;
                Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                diagrams.Gets.Clear();

                window.ToggleTheme();

                Assert.Equal(new[] { (ChatDiagramFakes.Flow, false), (pie, false) }, diagrams.Gets.Select(g => (g.Source, g.Dark)));
                menu = ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document).Single().ContextMenu!;
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
            }
            finally
            {
                window.Close();
                ChatDiagrams.Current = before;
            }
        });

        [Fact]
        public Task New_conversation_drops_its_turns_and_a_picture_that_arrives_later_redraws_none_of_them() => UiThread.RunAsync(async () =>
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            var before = ChatDiagrams.Current;
            ChatDiagrams.Current = diagrams;
            var h = new Harness();
            var window = h.Build();
            try
            {
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                Send(window, "One?");
                var turn = Assert.Single(window.Turns);
                Assert.Single(renderer.Calls);
                var shown = turn.Answer.Document;

                Click(window.NewButton);
                await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

                Assert.Same(shown, turn.Answer.Document);   // the dropped turn was not built again
                Assert.Empty(window.Turns);

                // The next conversation still draws: the picture is kept, so it is there at once.
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                Send(window, "Two?");
                Assert.Single(ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document));
                Assert.Single(renderer.Calls);
            }
            finally
            {
                window.Close();
                ChatDiagrams.Current = before;
            }
        });

        [Fact]
        public Task A_closed_window_is_not_drawn_again_when_a_picture_arrives() => UiThread.RunAsync(async () =>
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            var before = ChatDiagrams.Current;
            ChatDiagrams.Current = diagrams;
            var h = new Harness();
            var window = h.Build();
            bool closed = false;
            try
            {
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                Send(window, "One?");
                var turn = Assert.Single(window.Turns);
                var shown = turn.Answer.Document;

                window.Close();
                closed = true;
                await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

                Assert.Same(shown, turn.Answer.Document);
            }
            finally
            {
                if (!closed) window.Close();
                ChatDiagrams.Current = before;
            }
        });
    }
}
