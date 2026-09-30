using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.History;
using Microsoft.Extensions.AI;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Ask MicaStats window, built for real on the UI test thread but never shown. Most tests
    /// feed it a scripted stream of assistant updates; the last one runs a real AiAssistant over a
    /// fake model. No provider, network or %APPDATA% is involved.
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

            public AskWindow Build() => new(
                () => Setups.Dequeue(),
                () => SettingsOpened++,
                action =>
                {
                    Ran.Add(action);
                    return "Ended chrome.exe.";
                });
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
            Assert.Equal("The CPU is fine.", turn.Answer.Text);
            Assert.Equal(Visibility.Collapsed, turn.Details.Visibility);
            Assert.Equal("", window.QuestionBox.Text);
            Assert.True(window.SendButton.IsEnabled);
            Assert.False(window.StopButton.IsEnabled);
            Assert.Equal(new[] { "How is the CPU?" }, h.Questions);
        });

        [Fact]
        public void Tools_used_are_listed_on_the_details_line() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_live_status"),
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_top_processes", ToolArgs: "{\"by\":\"cpu\",\"count\":5}"),
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is the busiest."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "What is busy?");

            var turn = Assert.Single(window.Turns);
            Assert.Equal(Visibility.Visible, turn.Details.Visibility);
            Assert.Equal("Details: get_live_status; get_top_processes {\"by\":\"cpu\",\"count\":5}", turn.Details.Text);
            Assert.Equal("Chrome is the busiest.", turn.Answer.Text);
        });

        [Fact]
        public void A_suggestion_runs_only_when_clicked_and_goes_through_the_runner() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is using most of the CPU."),
                new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: EndChrome),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Why is it slow?");

            var button = Assert.Single(window.Turns[0].ActionButtons);
            Assert.Equal("End chrome.exe", button.Content);
            Assert.Empty(h.Ran);
            Assert.Equal("Suggestions do nothing until you click them.", window.StatusText.Text);

            Click(button);

            Assert.Equal(new[] { EndChrome }, h.Ran);
            Assert.Equal("Ended chrome.exe.", window.StatusText.Text);
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

            Click(window.NewButton);

            Assert.Empty(window.Turns);
            Assert.Empty(window.TranscriptPanel.Children);
            Assert.Equal(Visibility.Visible, window.EmptyText.Visibility);

            Click(oldButton);

            Assert.Empty(h.Ran);
            Assert.StartsWith("That suggestion belongs to a conversation that was cleared", window.StatusText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void A_setup_problem_offers_settings_and_keeps_the_question() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(new AskSetup(null, "Add an API key in Settings > AI."));

            Send(window, "Hello?");

            Assert.Empty(window.Turns);
            Assert.Equal("Add an API key in Settings > AI.", window.StatusText.Text);
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
            Assert.Equal(failure, window.Turns[0].Note.Text);
            Assert.Equal(Visibility.Visible, window.RetryButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.SettingsButton.Visibility);
            Assert.Equal("Is the disk full?", window.QuestionBox.Text);

            Click(window.RetryButton);
            UiPump.Wait(window.Pending!);

            Assert.Equal(2, window.Turns.Count);
            Assert.Equal("The disk has 40 GB free.", window.Turns[1].Answer.Text);
            Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
            Assert.Equal("", window.QuestionBox.Text);
            Assert.Equal(new[] { "Is the disk full?", "Is the disk full?" }, h.Questions);
        });

        [Fact]
        public void Stop_cancels_the_answer_and_keeps_the_question() => WithWindow((window, h) =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => Hang(reached, ct), null));

            window.QuestionBox.Text = "Why is it slow?";
            Click(window.SendButton);
            UiPump.Wait(reached.Task);

            Assert.Equal("Partial", window.Turns[0].Answer.Text);
            Assert.True(window.StopButton.IsEnabled);
            Assert.False(window.SendButton.IsEnabled);

            Click(window.StopButton);
            UiPump.Wait(window.Pending!);

            Assert.Equal("Stopped.", window.Turns[0].Note.Text);
            Assert.Equal("Why is it slow?", window.QuestionBox.Text);
            Assert.True(window.SendButton.IsEnabled);
            Assert.False(window.StopButton.IsEnabled);
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
            Assert.Equal("Limited mode: this model cannot use tools.", turn.Note.Text);
            Assert.Equal("Memory looks fine.", turn.Answer.Text);
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
            Assert.Equal("It hosts Windows services.", window.Turns[0].Answer.Text);
        });

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
                Assert.Equal("CPU is fine.", turn.Answer.Text);
                Assert.True(client.Calls >= 1);
                Assert.Equal(1, usage.UsedToday);
                Assert.Equal("", window.QuestionBox.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
