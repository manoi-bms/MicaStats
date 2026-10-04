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

using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Size = System.Windows.Size;
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

            public AskWindow Build(Func<string?>? modelLabel = null, Func<(int Used, int Limit)?>? usage = null,
                                   Kil0bitSystemMonitor.Models.AppConfig? config = null) => new(
                () => Setups.Dequeue(),
                () => SettingsOpened++,
                action =>
                {
                    Ran.Add(action);
                    return "Ended chrome.exe.";
                },
                modelLabel, config, usage);
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
            Assert.Matches("^[0-9]{2}:[0-9]{2} · [0-9]+ (s|min [0-9]+ s)$", turn.TimeText.Text);
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
        public void The_activity_says_thinking_then_the_tool_then_goes_with_the_text() => WithWindow((window, h) =>
        {
            var toolSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => ToolThenText(toolSeen, gate.Task, ct), null));

            window.QuestionBox.Text = "Is it fine?";
            Click(window.SendButton);
            var turn = window.Turns[0];
            Assert.Equal("Thinking…", turn.ActivityText.Text);
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);

            UiPump.Wait(toolSeen.Task);
            Assert.Equal("Reading live status…", turn.ActivityText.Text);
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);

            gate.SetResult();
            UiPump.Wait(window.Pending!);
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);
        });

        [Fact]
        public void A_tool_used_after_text_shows_its_line_below_the_answer_and_the_end_hides_it() => WithWindow((window, h) =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => TextThenTool(reached, gate.Task, ct), null));

            window.QuestionBox.Text = "Is it fine?";
            Click(window.SendButton);
            var turn = window.Turns[0];
            UiPump.Wait(reached.Task);

            var content = (System.Windows.Controls.Panel)turn.Activity.Parent;
            Assert.Equal("Checking alerts…", turn.ActivityText.Text);
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);
            Assert.True(content.Children.IndexOf(turn.Answer) < content.Children.IndexOf(turn.Activity));

            gate.SetResult();
            UiPump.Wait(window.Pending!);
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);
        });

        private static async IAsyncEnumerable<AssistantUpdate> TextThenTool(
            TaskCompletionSource reached, Task gate, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new AssistantUpdate(AssistantUpdateKind.Text, "First look.");
            yield return new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "list_alerts");
            reached.TrySetResult();
            await gate.WaitAsync(ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        [Fact]
        public void A_stopped_answer_with_text_still_gets_its_footer_with_the_time_so_far() => WithWindow((window, h) =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => Hang(reached, ct), null));

            window.QuestionBox.Text = "Why?";
            Click(window.SendButton);
            UiPump.Wait(reached.Task);
            window.Stop();
            UiPump.Wait(window.Pending!);

            var turn = window.Turns[0];
            Assert.Equal(Visibility.Visible, turn.Footer.Visibility);
            Assert.Matches("^[0-9]{2}:[0-9]{2} · [0-9]+ (s|min [0-9]+ s)$", turn.TimeText.Text);
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);
        });

        // ---- the day's count in the hint ----

        private const string OldHint = "Enter to send · Shift+Enter for a new line";

        [Fact]
        public void The_hint_is_the_old_text_while_the_count_is_unknown() => UiThread.Run(() =>
        {
            var none = new Harness().Build();
            var unknown = new Harness().Build(usage: () => null);
            var broken = new Harness().Build(usage: () => throw new InvalidOperationException("no meter"));
            try
            {
                Assert.Equal(OldHint, none.HintText.Text);
                Assert.Equal(OldHint, unknown.HintText.Text);
                Assert.Equal(OldHint, broken.HintText.Text);
            }
            finally
            {
                none.Close();
                unknown.Close();
                broken.Close();
            }
        });

        [Fact]
        public void The_hint_ends_with_the_count_in_ASCII_digits_whatever_the_culture() => UiThread.Run(() =>
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
                var window = new Harness().Build(usage: () => (1234, 10000));
                try
                {
                    Assert.Equal(OldHint + " · 1234 of 10000 today", window.HintText.Text);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        });

        [Fact]
        public void The_count_is_read_again_after_each_answer_and_when_asked() => UiThread.Run(() =>
        {
            int used = 3;
            var h = new Harness();
            var window = h.Build(usage: () => (used, 100));
            try
            {
                Assert.Equal(OldHint + " · 3 of 100 today", window.HintText.Text);

                h.Setups.Enqueue(new AskSetup((conversation, question, ct) =>
                {
                    used = 4;   // the assistant counted the question
                    return Play(new[]
                    {
                        new AssistantUpdate(AssistantUpdateKind.Text, "Fine."),
                        new AssistantUpdate(AssistantUpdateKind.Done),
                    }, ct);
                }, null));
                Send(window, "Is it fine?");
                Assert.Equal(OldHint + " · 4 of 100 today", window.HintText.Text);

                used = 9;   // another feature asked something
                window.RefreshUsage();   // what ShowOrActivate does
                Assert.Equal(OldHint + " · 9 of 100 today", window.HintText.Text);
            }
            finally
            {
                window.Close();
            }
        });

        // ---- jump to the latest ----

        /// <summary>Gives the never-shown window a size and a layout pass, so the transcript has a viewport.</summary>
        private static void Lay(AskWindow window)
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();
        }

        private static AssistantUpdate[] LongAnswer() => new[]
        {
            new AssistantUpdate(AssistantUpdateKind.Text, string.Concat(Enumerable.Repeat("A line of the answer.\n\n", 60))),
            new AssistantUpdate(AssistantUpdateKind.Done),
        };

        [Fact]
        public void The_jump_button_is_hidden_at_the_end_shown_when_scrolled_up_and_a_click_follows_again() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(LongAnswer()));
            Send(window, "Tell me a lot.");
            Lay(window);
            Assert.True(window.TranscriptScroll.ScrollableHeight > 0, "the answer must be taller than the window for this test");
            Assert.Equal(Visibility.Collapsed, window.JumpButton.Visibility);   // following: at the end

            window.TranscriptScroll.ScrollToVerticalOffset(0);
            Lay(window);
            Assert.Equal(Visibility.Visible, window.JumpButton.Visibility);

            Click(window.JumpButton);
            Lay(window);
            Assert.Equal(window.TranscriptScroll.ScrollableHeight, window.TranscriptScroll.VerticalOffset);
            Assert.Equal(Visibility.Collapsed, window.JumpButton.Visibility);

            // Following again: more text keeps the end in view.
            h.Setups.Enqueue(h.Answer(LongAnswer()));
            Send(window, "And more.");
            Lay(window);
            Assert.Equal(window.TranscriptScroll.ScrollableHeight, window.TranscriptScroll.VerticalOffset);
            Assert.Equal(Visibility.Collapsed, window.JumpButton.Visibility);
        });

        [Fact]
        public void A_viewport_change_that_leaves_the_offset_at_the_end_follows_again_and_hides_the_button() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(LongAnswer()));
            Send(window, "Tell me a lot.");
            Lay(window);
            var scroll = window.TranscriptScroll;
            Assert.True(scroll.ScrollableHeight > 400, "the answer must be much taller than the window for this test");

            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight - 100);   // scrolled up, 100 from the end
            Lay(window);
            Assert.Equal(Visibility.Visible, window.JumpButton.Visibility);

            window.Height += 150;   // the viewport grows; the offset is clamped to the new end
            Lay(window);
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
            Assert.Equal(Visibility.Collapsed, window.JumpButton.Visibility);
        });

        [Fact]
        public void Growing_content_while_scrolled_up_and_not_at_the_end_does_not_follow() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(LongAnswer()));
            Send(window, "Tell me a lot.");
            Lay(window);
            window.TranscriptScroll.ScrollToVerticalOffset(0);
            Lay(window);
            Assert.Equal(Visibility.Visible, window.JumpButton.Visibility);

            window.Turns[0].AppendText(string.Concat(Enumerable.Repeat("\n\nMore of the answer.", 30)));
            window.Turns[0].RenderNow();
            Lay(window);

            Assert.Equal(0, window.TranscriptScroll.VerticalOffset);
            Assert.Equal(Visibility.Visible, window.JumpButton.Visibility);
        });

        [Fact]
        public void After_the_limited_mode_note_the_activity_shows_thinking_until_text_arrives_and_stays_hidden_after() => WithWindow((window, h) =>
        {
            var noted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var texted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => NoteThenText(noted, gate.Task, texted, end.Task, ct), null));

            window.QuestionBox.Text = "Is it fine?";
            Click(window.SendButton);
            var turn = window.Turns[0];
            UiPump.Wait(noted.Task);
            Assert.Equal(Visibility.Visible, turn.Note.Visibility);
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);
            Assert.Equal("Thinking…", turn.ActivityText.Text);
            var content = (System.Windows.Controls.Panel)turn.Activity.Parent;
            Assert.True(content.Children.IndexOf(turn.Activity) < content.Children.IndexOf(turn.Answer));   // no text yet: above the answer
            Assert.True(content.Children.IndexOf(turn.Answer) < content.Children.IndexOf(turn.Note));

            gate.SetResult();
            UiPump.Wait(texted.Task);
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);

            end.SetResult();
            UiPump.Wait(window.Pending!);
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);
        });

        private static async IAsyncEnumerable<AssistantUpdate> NoteThenText(
            TaskCompletionSource noted, Task gate, TaskCompletionSource texted, Task end, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new AssistantUpdate(AssistantUpdateKind.LimitedMode);
            noted.TrySetResult();
            await gate.WaitAsync(ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Text, "Fine.");
            texted.TrySetResult();
            await end.WaitAsync(ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        [Fact]
        public void An_error_note_keeps_the_activity_hidden() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Error, "The provider is down."),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Send(window, "Hello?");
            Assert.Equal(Visibility.Collapsed, window.Turns[0].Activity.Visibility);
        });

        [Fact]
        public void The_jump_button_is_not_in_the_scrolled_content_takes_no_focus_and_has_a_name() => WithWindow((window, h) =>
        {
            Assert.False(window.JumpButton.Focusable);
            Assert.Equal("Jump to the latest", window.JumpButton.ToolTip);
            Assert.Equal("Jump to the latest", System.Windows.Automation.AutomationProperties.GetName(window.JumpButton));
            Assert.Equal(((char)0xE74B).ToString(), window.JumpButton.Content);
            Assert.False(window.TranscriptScroll.IsAncestorOf(window.JumpButton));
            // Over the transcript: the same grid cell, at its lower right.
            Assert.Equal(System.Windows.Controls.Grid.GetRow(window.TranscriptScroll), System.Windows.Controls.Grid.GetRow(window.JumpButton));
            Assert.Equal(HorizontalAlignment.Right, window.JumpButton.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Bottom, window.JumpButton.VerticalAlignment);
        });

        [Theory]
        [InlineData(true, 500, false)]    // following: at the end
        [InlineData(false, 500, true)]    // scrolled away from the end
        [InlineData(false, 0, false)]     // nothing to scroll
        [InlineData(true, 0, false)]
        public void The_jump_button_shows_only_when_not_following_and_there_is_something_to_scroll(bool follow, double scrollable, bool shown)
        {
            Assert.Equal(shown, AskWindow.ShowsJump(follow, scrollable));
        }

        // ---- the size ----

        [Fact]
        public void The_window_opens_at_the_configured_size_within_the_work_area() => UiThread.Run(() =>
        {
            var work = SystemParameters.WorkArea;
            var config = new Kil0bitSystemMonitor.Models.AppConfig { AskWidth = 700, AskHeight = 560 };
            var window = new Harness().Build(config: config);
            var big = new Harness().Build(config: new Kil0bitSystemMonitor.Models.AppConfig { AskWidth = 9000, AskHeight = 9000 });
            var normal = new Harness().Build(config: new Kil0bitSystemMonitor.Models.AppConfig());
            try
            {
                Assert.Equal(Math.Min(700, work.Width), window.Width);
                Assert.Equal(Math.Min(560, work.Height), window.Height);
                Assert.Equal(Math.Max(big.MinWidth, work.Width), big.Width);     // a saved size from a larger screen
                Assert.Equal(Math.Max(big.MinHeight, work.Height), big.Height);
                Assert.Equal(Math.Min(640, work.Width), normal.Width);
                Assert.Equal(Math.Min(720, work.Height), normal.Height);
                Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
            }
            finally
            {
                window.Close();
                big.Close();
                normal.Close();
            }
        });

        // ---- the size is fitted to the screen the window opens on, which need not be the primary one ----

        /// <summary>A window never shown, whose screen is the one a test names; the real screens are not read.</summary>
        private static void OnScreen(double askWidth, double askHeight, Func<Rect> workArea, Action<AskWindow> test) => UiThread.Run(() =>
        {
            var window = new Harness().Build(config: new Kil0bitSystemMonitor.Models.AppConfig { AskWidth = askWidth, AskHeight = askHeight });
            try
            {
                window.WorkAreaOfScreen = workArea;
                test(window);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void A_large_saved_size_is_fitted_to_the_smaller_screen_the_window_opens_on_and_centred_there() =>
            // A second screen left of the primary one, with 1,283 by 687 of work area (sizes no real screen has,
            // so the constructor's fit against the primary screen never gives the same).
            OnScreen(9000, 500, () => new Rect(-1283, 40, 1283, 687), window =>
            {
                window.FitToScreen();   // what runs once the window has its handle

                Assert.Equal(1283, window.Width);                 // as wide as that screen's work area, not the primary one's
                Assert.Equal(500, window.Height);
                Assert.Equal(-1283, window.Left);
                Assert.Equal(40 + (687 - 500) / 2.0, window.Top); // centred in that work area

                var both = new Harness().Build(config: new Kil0bitSystemMonitor.Models.AppConfig { AskWidth = 9000, AskHeight = 9000 });
                try
                {
                    both.WorkAreaOfScreen = () => new Rect(1920, 0, 1021, 731);
                    both.FitToScreen();
                    Assert.Equal((1021d, 731d), (both.Width, both.Height));
                    Assert.Equal((1920d, 0d), (both.Left, both.Top));
                }
                finally
                {
                    both.Close();
                }
            });

        [Fact]
        public void On_a_screen_larger_than_the_primary_one_the_configured_size_is_kept_and_centred_there() =>
            OnScreen(9000, 8000, () => new Rect(3000, -200, 10000, 9000), window =>
            {
                Assert.True(window.Width < 9000);                 // the constructor's first fit, against the primary work area
                Assert.True(window.Height < 8000);

                window.FitToScreen();

                Assert.Equal(9000, window.Width);
                Assert.Equal(8000, window.Height);
                Assert.Equal(3000 + (10000 - 9000) / 2.0, window.Left);
                Assert.Equal(-200 + (9000 - 8000) / 2.0, window.Top);
            });

        [Fact]
        public void A_size_that_already_fits_its_screen_is_left_where_WPF_centres_it() =>
            OnScreen(420, 420, () => new Rect(0, 0, 5000, 5000), window =>
            {
                window.FitToScreen();

                Assert.Equal((420d, 420d), (window.Width, window.Height));
                Assert.True(double.IsNaN(window.Left));           // not placed here: CenterScreen places it
                Assert.True(double.IsNaN(window.Top));
            });

        [Fact]
        public void A_screen_that_cannot_be_read_leaves_the_size_the_constructor_gave()
        {
            Func<Rect>[] unreadable =
            {
                () => throw new InvalidOperationException("no screen"),
                () => Rect.Empty,
                () => new Rect(0, 0, 0, 0),
                () => new Rect(0, 0, double.NaN, 700),
                () => new Rect(double.NaN, 0, 800, 700),
                () => new Rect(0, 0, double.PositiveInfinity, 700),
            };
            foreach (Func<Rect> screen in unreadable)
            {
                OnScreen(9000, 9000, screen, window =>
                {
                    (double width, double height) = (window.Width, window.Height);

                    window.FitToScreen();   // must not throw: it runs while the window is being opened

                    Assert.Equal((width, height), (window.Width, window.Height));
                    Assert.True(double.IsNaN(window.Left));
                    Assert.True(double.IsNaN(window.Top));
                });
            }
        }

        [Fact]
        public void The_window_is_fitted_to_its_screen_once_it_has_a_handle_and_asks_Windows_for_that_screen()
        {
            string code = System.Text.RegularExpressions.Regex.Replace(
                System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "Ai", "AskWindow.xaml.cs")), @"\s+", " ");

            // Tests never give the window a handle, so this is read: SourceInitialized runs the fit.
            Assert.Matches(@"SourceInitialized \+= \(s, e\) => \{[^}]*FitToScreen\(\);[^}]*\}", code);
            // The seam's default is the real lookup, by the window's own handle.
            Assert.Contains("WorkAreaOfScreen = WorkAreaOfOwnScreen;", code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_window_writes_its_size_to_the_config_when_it_closes() => UiThread.Run(() =>
        {
            var config = new Kil0bitSystemMonitor.Models.AppConfig();
            var window = new Harness().Build(config: config);
            window.Width = 730;
            window.Height = 510;

            window.Close();

            Assert.Equal(730, config.AskWidth);
            Assert.Equal(510, config.AskHeight);
        });

        [Fact]
        public void A_window_that_is_maximized_or_minimized_remembers_its_restored_size_not_the_big_one()
        {
            var restore = new Rect(10, 20, 700, 500);
            Assert.Equal((800d, 600d), AskWindow.SizeToRemember(WindowState.Normal, restore, 800, 600));
            Assert.Equal((700d, 500d), AskWindow.SizeToRemember(WindowState.Maximized, restore, 1920, 1040));
            Assert.Equal((700d, 500d), AskWindow.SizeToRemember(WindowState.Minimized, restore, 160, 28));
            // No restore size known (a window that was never shown): the window's own size.
            Assert.Equal((640d, 720d), AskWindow.SizeToRemember(WindowState.Maximized, Rect.Empty, 640, 720));
            Assert.Equal((640d, 720d), AskWindow.SizeToRemember(WindowState.Minimized, Rect.Empty, 640, 720));
            var nan = new Rect(double.NaN, double.NaN, double.NaN, double.NaN);
            Assert.Equal((640d, 720d), AskWindow.SizeToRemember(WindowState.Maximized, nan, 640, 720));
            Assert.Equal((640d, 720d), AskWindow.SizeToRemember(WindowState.Minimized, nan, 640, 720));
            Assert.Equal((640d, 720d), AskWindow.SizeToRemember(WindowState.Maximized, new Rect(0, 0, 0, 0), 640, 720));
        }

        [Fact]
        public void The_size_is_kept_between_the_minimum_and_the_work_area()
        {
            Assert.Equal((640d, 720d), AskWindow.FitSize(640, 720, 420, 420, 1920, 1040));
            Assert.Equal((1920d, 1040d), AskWindow.FitSize(5000, 5000, 420, 420, 1920, 1040));
            Assert.Equal((420d, 420d), AskWindow.FitSize(100, 100, 420, 420, 1920, 1040));
            Assert.Equal((420d, 420d), AskWindow.FitSize(640, 720, 420, 420, 400, 300));   // a work area below the minimum: the minimum wins
            Assert.Equal((420d, 420d), AskWindow.FitSize(double.NaN, double.NaN, 420, 420, 1920, 1040));
            Assert.Equal((1920d, 1040d), AskWindow.FitSize(double.PositiveInfinity, double.PositiveInfinity, 420, 420, 1920, 1040));
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

        // ---- text of a note stored as a credential ends a conversation that read notes -------------

        private const string ClearedForCredential =
            "This conversation was cleared: text from a note it had read was stored as a credential.";

        /// <summary>
        /// A setup whose answer rests on a note tool: the conversation is marked as the tool itself
        /// marks it, before anything is shown. Every conversation it is handed is recorded.
        /// </summary>
        private static AskSetup AnswerFromNotes(Harness h, List<AiConversation> seen, params AssistantUpdate[] updates) =>
            new((conversation, question, ct) =>
            {
                h.Questions.Add(question);
                seen.Add(conversation);
                conversation.MarkNotesRead("this PC");
                return Play(updates, ct);
            }, null);

        private static AskSetup PlainAnswer(Harness h, List<AiConversation> seen, string text) =>
            new((conversation, question, ct) =>
            {
                h.Questions.Add(question);
                seen.Add(conversation);
                return Play(AnswerWith(text), ct);
            }, null);

        [Fact]
        public void A_conversation_that_read_notes_is_cleared_when_a_credential_is_stored_and_the_status_says_why() => WithWindow((window, h) =>
        {
            var seen = new List<AiConversation>();
            h.Setups.Enqueue(AnswerFromNotes(h, seen,
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "read_note", ToolArgs: "{}"),
                new AssistantUpdate(AssistantUpdateKind.Text, "The vpn login is hunter2."),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Send(window, "What is the vpn login?");
            Assert.Equal("The vpn login is hunter2.", Assert.Single(window.Turns).RawText);
            Assert.Equal(Visibility.Collapsed, window.EmptyState.Visibility);

            window.ClearAfterCredentialStored();

            Assert.Empty(window.Turns);                           // nothing of it on screen
            Assert.Empty(window.TranscriptPanel.Children);
            Assert.Equal(Visibility.Visible, window.EmptyState.Visibility);
            Assert.Equal(ClearedForCredential, window.StatusText.Text);
            Assert.Equal(ClearedForCredential, AskWindow.ClearedForCredential);
            Assert.Equal(Visibility.Visible, window.StatusRow.Visibility);

            // And nothing of it in what the next question sends: that one gets a new conversation.
            h.Setups.Enqueue(PlainAnswer(h, seen, "Fine."));
            Send(window, "And now?");
            Assert.Equal(2, seen.Count);
            Assert.NotSame(seen[0], seen[1]);
            Assert.True(seen[0].NotesEverRead);
            Assert.False(seen[1].NotesEverRead);
            Assert.False(Assert.Single(window.Turns).PlainLinks);  // links are links again: this one read no notes
            Assert.Equal("", window.StatusText.Text);
        });

        [Fact]
        public void A_conversation_that_never_read_notes_is_left_alone_when_a_credential_is_stored() => WithWindow((window, h) =>
        {
            var seen = new List<AiConversation>();
            h.Setups.Enqueue(PlainAnswer(h, seen, "The CPU is at 12%."));
            Send(window, "How busy is it?");

            window.ClearAfterCredentialStored();
            window.ClearAfterCredentialStored();

            Assert.Equal("The CPU is at 12%.", Assert.Single(window.Turns).RawText);
            Assert.Equal(Visibility.Collapsed, window.EmptyState.Visibility);
            Assert.Equal("", window.StatusText.Text);

            h.Setups.Enqueue(PlainAnswer(h, seen, "Still 12%."));
            Send(window, "And now?");
            Assert.Same(seen[0], seen[1]);                        // the same conversation goes on
            Assert.Equal(2, window.Turns.Count);

            // A window that was never asked anything has nothing to clear either.
            var empty = new Harness().Build();
            try
            {
                empty.ClearAfterCredentialStored();
                Assert.Equal("", empty.StatusText.Text);
            }
            finally
            {
                empty.Close();
            }
        });

        private static async IAsyncEnumerable<AssistantUpdate> NotesThenHang(
            AiConversation conversation, TaskCompletionSource reached, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            conversation.MarkNotesRead("this PC");
            yield return new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "search_notes", ToolArgs: "{}");
            yield return new AssistantUpdate(AssistantUpdateKind.Text, "The vpn login is hunter");
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        [Fact]
        public void An_answer_from_notes_that_still_streams_is_cancelled_when_a_credential_is_stored() => WithWindow((window, h) =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelled = false;
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) =>
            {
                ct.Register(() => cancelled = true);
                return NotesThenHang(conversation, reached, ct);
            }, null));
            window.QuestionBox.Text = "What is the vpn login?";
            Click(window.SendButton);
            var answer = window.Pending!;
            UiPump.Wait(reached.Task);
            Assert.True(window.IsBusy);

            window.ClearAfterCredentialStored();
            UiPump.Wait(answer);

            Assert.True(cancelled);
            Assert.False(window.IsBusy);
            Assert.Empty(window.Turns);
            Assert.Empty(window.TranscriptPanel.Children);
            Assert.Equal(ClearedForCredential, window.StatusText.Text);   // not "Stopped": the answer is gone, not ended
            Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
            Assert.True(window.SendButton.IsEnabled);
        });

        /// <summary>An answer that is under way and has read nothing yet: its first note tool may be reading right now.</summary>
        private static async IAsyncEnumerable<AssistantUpdate> HangBeforeAnyNote(
            TaskCompletionSource reached, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        /// <summary>
        /// The gap both final reviewers found: a conversation is marked as having read notes only
        /// when a note tool hands its result over. A credential stored while the first note tool
        /// is still reading found the mark unset, left the conversation alone, and the tool then
        /// handed the old text to the model. So an answer under way is ended too, while Ask is
        /// allowed to read notes.
        /// </summary>
        [Theory]
        [InlineData(true, true)]      // Ask may read notes: the answer under way is ended
        [InlineData(false, false)]    // it may not: no note can be on its way, the answer goes on
        public void A_credential_stored_while_an_answer_is_under_way_ends_it_when_Ask_may_read_notes(bool notesInAsk, bool ended) => UiThread.Run(() =>
        {
            var harness = new Harness();
            var config = new Kil0bitSystemMonitor.Models.AppConfig { AiNotesInAsk = notesInAsk };
            var window = harness.Build(config: config);
            try
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool cancelled = false;
                harness.Setups.Enqueue(new AskSetup((conversation, question, ct) =>
                {
                    ct.Register(() => cancelled = true);
                    return HangBeforeAnyNote(reached, ct);
                }, null));
                window.QuestionBox.Text = "What is the vpn login?";
                Click(window.SendButton);
                var answer = window.Pending!;
                UiPump.Wait(reached.Task);
                Assert.True(window.IsBusy);

                window.ClearAfterCredentialStored();

                Assert.Equal(ended, cancelled);
                if (ended)
                {
                    UiPump.Wait(answer);
                    Assert.False(window.IsBusy);
                    Assert.Empty(window.Turns);
                    Assert.Equal(AskWindow.ClearedWhileAnswering, window.StatusText.Text);
                }
                else
                {
                    Assert.True(window.IsBusy);
                    Assert.Single(window.Turns);
                    Assert.Equal("", window.StatusText.Text);
                    window.Stop();
                    UiPump.Wait(answer);
                }
            }
            finally
            {
                window.Close();
            }
        });

        /// <summary>The Ask window forgets the pictures itself: it does not lean on a MicaPad pane having been cleared first.</summary>
        [Fact]
        public void Clearing_for_a_stored_credential_also_forgets_the_pictures_kept_for_answers() => WithWindow((window, h) =>
        {
            var diagrams = new CountingDiagrams();
            IChatDiagrams? before = ChatDiagrams.Current;
            try
            {
                ChatDiagrams.Current = diagrams;
                var seen = new List<AiConversation>();
                h.Setups.Enqueue(AnswerFromNotes(h, seen, AnswerWith("The vpn login is hunter2.")));
                Send(window, "What is the vpn login?");

                window.ClearAfterCredentialStored();

                Assert.Equal(1, diagrams.Cleared);
                Assert.Empty(window.Turns);
            }
            finally
            {
                ChatDiagrams.Current = before;
            }
        });

        private sealed class CountingDiagrams : IChatDiagrams
        {
            public int Cleared { get; private set; }

            public ChatDiagramState Get(string source, bool dark, Action? whenDone) => new(ChatDiagramStatus.Off);

            public void Forget(string source, bool dark)
            {
            }

            public void Clear() => Cleared++;
        }

        [Fact]
        public void The_hook_for_a_stored_credential_is_safe_with_no_window_and_from_another_thread() => WithWindow((window, h) =>
        {
            var seen = new List<AiConversation>();
            h.Setups.Enqueue(AnswerFromNotes(h, seen, AnswerWith("The vpn login is hunter2.")));
            Send(window, "What is the vpn login?");

            AskWindow.ClearAfterCredentialStored(null);           // no Ask window is open
            AskWindow.ClearCurrentAfterCredentialStored();        // none was opened by the app in a test
            Assert.Single(window.Turns);

            // MicaPad raises it on the UI thread, but nothing here may depend on that.
            UiPump.Wait(Task.Run(() => AskWindow.ClearAfterCredentialStored(window)));
            UiPump.Wait(window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background).Task);

            Assert.Empty(window.Turns);
            Assert.Equal(ClearedForCredential, window.StatusText.Text);

            // On the UI thread it is done before the call comes back.
            h.Setups.Enqueue(AnswerFromNotes(h, seen, AnswerWith("It is hunter2.")));
            Send(window, "Again?");
            Assert.Single(window.Turns);
            AskWindow.ClearAfterCredentialStored(window);
            Assert.Empty(window.Turns);
        });

        [Fact]
        public void The_app_hands_a_stored_credential_to_the_Ask_window()
        {
            string ai = System.Text.RegularExpressions.Regex.Replace(
                System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "App.Ai.cs")), @"\s+", " ");

            Assert.Contains("Kil0bitSystemMonitor.Pad.MicaPadWindow.CredentialStored = Kil0bitSystemMonitor.Ai.AskWindow.ClearCurrentAfterCredentialStored;",
                            ai, StringComparison.Ordinal);
        }

        // ---- Mermaid diagrams in answers ------------------------------------------------------------

        private static AssistantUpdate[] AnswerWith(string text) => new[]
        {
            new AssistantUpdate(AssistantUpdateKind.Text, text),
            new AssistantUpdate(AssistantUpdateKind.Done),
        };

        /// <summary>
        /// Runs a test over a window whose turns draw through <paramref name="diagrams"/>, as the
        /// app's own would. The app-wide adapter is replaced inside the <c>try</c> and put back
        /// first of all in the <c>finally</c>, before closing the window or anything else that can
        /// throw, so no other test ever finds it replaced.
        /// </summary>
        private static Task WithDiagrams(IChatDiagrams diagrams, Func<Harness, AskWindow> build, Func<AskWindow, Harness, Task> test) =>
            UiThread.RunAsync(async () =>
            {
                var before = ChatDiagrams.Current;
                AskWindow? window = null;
                bool closed = false;
                try
                {
                    ChatDiagrams.Current = diagrams;   // what the app sets at startup; a turn takes it when it is made
                    var h = new Harness();
                    window = build(h);
                    window.Closed += (s, e) => closed = true;
                    await test(window, h);
                }
                finally
                {
                    ChatDiagrams.Current = before;
                    if (window != null && !closed) window.Close();
                }
            });

        [Fact]
        public Task A_turn_draws_in_the_windows_theme_and_a_theme_change_draws_every_turn_that_has_a_diagram_again()
        {
            const string pie = "pie\n  \"a\" : 1";
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var config = new Kil0bitSystemMonitor.Models.AppConfig { AskTheme = "Light" };
            return WithDiagrams(diagrams, h => new AskWindow(() => h.Setups.Dequeue(), () => { }, _ => "", null, config), (window, h) =>
            {
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                h.Setups.Enqueue(h.Answer(AnswerWith("| a | b |\n|---|---|\n| 1 | 2 |")));
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block(pie))));
                Send(window, "One?");
                Send(window, "Two?");
                Send(window, "Three?");

                Assert.NotEmpty(diagrams.Gets);
                Assert.All(diagrams.Gets, g => Assert.False(g.Dark));   // a new turn has the window's theme before it first renders
                Assert.Single(ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document));
                Assert.Single(ChatDocument.All<System.Windows.Controls.Image>(window.Turns[2].Answer.Document));
                var table = window.Turns[1].Answer.Document;
                diagrams.Gets.Clear();

                config.AskTheme = "Dark";

                // A diagram is a bitmap drawn for one theme: a turn that has one builds its document again.
                Assert.Equal(new[] { (ChatDiagramFakes.Flow, true), (pie, true) }, diagrams.Gets.Select(g => (g.Source, g.Dark)));
                var menu = ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document).Single().ContextMenu!;
                Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Same(table, window.Turns[1].Answer.Document);    // the turn with only a table is repainted, not built again
                diagrams.Gets.Clear();

                window.ToggleTheme();

                Assert.Equal(new[] { (ChatDiagramFakes.Flow, false), (pie, false) }, diagrams.Gets.Select(g => (g.Source, g.Dark)));
                menu = ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document).Single().ContextMenu!;
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Same(table, window.Turns[1].Answer.Document);
                return Task.CompletedTask;
            });
        }

        [Fact]
        public Task New_conversation_drops_its_turns_and_a_picture_that_arrives_later_redraws_none_of_them()
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            return WithDiagrams(diagrams, h => h.Build(), async (window, h) =>
            {
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                Send(window, "One?");
                var turn = Assert.Single(window.Turns);
                Assert.Single(renderer.Calls);
                var shown = turn.Answer.Document;

                Click(window.NewButton);
                await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

                Assert.Null(turn.PendingRedraw);            // told of the picture, the dropped turn asked for no redraw
                Assert.Same(shown, turn.Answer.Document);   // the dropped turn was not built again
                Assert.Empty(window.Turns);

                // The next conversation still draws: the picture is kept, so it is there at once.
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                Send(window, "Two?");
                Assert.Single(ChatDocument.All<System.Windows.Controls.Image>(window.Turns[0].Answer.Document));
                Assert.Single(renderer.Calls);
            });
        }

        [Fact]
        public Task A_closed_window_is_not_drawn_again_when_a_picture_arrives()
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            return WithDiagrams(diagrams, h => h.Build(), async (window, h) =>
            {
                h.Setups.Enqueue(h.Answer(AnswerWith(ChatDiagramFakes.Block())));
                Send(window, "One?");
                var turn = Assert.Single(window.Turns);
                var shown = turn.Answer.Document;

                window.Close();
                await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

                Assert.Null(turn.PendingRedraw);            // no redraw waits for the closed window's turn either
                Assert.Same(shown, turn.Answer.Document);
            });
        }
    }
}
