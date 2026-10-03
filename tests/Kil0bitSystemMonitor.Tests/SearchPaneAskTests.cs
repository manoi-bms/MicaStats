using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Size = System.Windows.Size;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Search notes pane's Ask (MicaPad AI spec 4) on the shared UI thread: the pane alone,
    /// never shown, over a fake search and a fake answer the test feeds one update at a time.
    /// </summary>
    public class SearchPaneAskTests
    {
        private const string CutShort = "Cut short at the length limit";
        private const string Stopped = "Stopped";

        private static readonly PadAiUpdate Done = new(PadAiUpdateKind.Done);

        private static PadAiUpdate Text(string text) => new(PadAiUpdateKind.Text, text);

        private static SearchRow Row(string title, int? source) =>
            new("id-" + title, title, false, 3, 5, "first line", new[] { new SnippetRun("about the vpn", false) }, source);

        /// <summary>A pane, its fake search and Ask, and what they were asked.</summary>
        private sealed class Fake
        {
            public Fake()
            {
                Pane.Run = (query, token) =>
                {
                    Searched.Add(query);
                    return Task.FromResult<(IReadOnlyList<SearchRow> Rows, string Status)>((Found, "Words"));
                };
                Pane.Ask = (query, token) =>
                {
                    Asked.Add(query);
                    Tokens.Add(token);
                    if (WhenCancelled is { } whenCancelled) token.Register(whenCancelled);
                    var answer = Channel.CreateUnbounded<PadAiUpdate>();
                    Answers.Add(answer);
                    return Task.FromResult(new AskStart(Found, Status, Instead == null ? Read(answer, token) : null, Instead, Answering, Answered));
                };
                Pane.CopyAnswer = Copied.Add;
                Pane.Warn = Warned.Add;   // nothing reaches the real log
            }

            public SearchPane Pane { get; } = new();

            public List<string> Searched { get; } = new();

            public List<string> Asked { get; } = new();

            /// <summary>The token of each Ask, in order.</summary>
            public List<CancellationToken> Tokens { get; } = new();

            /// <summary>The answer of each Ask, in order; the test writes its updates.</summary>
            public List<Channel<PadAiUpdate>> Answers { get; } = new();

            public List<string> Copied { get; } = new();

            public List<string> Warned { get; } = new();

            /// <summary>The rows the next search or Ask finds: two sources and one more hit.</summary>
            public IReadOnlyList<SearchRow> Found { get; set; } = new[] { Row("Network", 1), Row("Office", 2), Row("Lunch", null) };

            /// <summary>The search status: the status line with no answer, and after one that did not end well.</summary>
            public string Status { get; set; } = "Words";

            /// <summary>The status line while the answer streams in.</summary>
            public string Answering { get; set; } = "Words · Answering from 2 passages";

            /// <summary>The status line once the answer ended cleanly.</summary>
            public string Answered { get; set; } = "Words · Answered from 2 passages";

            /// <summary>Registered on the token of each Ask made while it is set, as a callback on a request's token is; a test makes it throw.</summary>
            public Action? WhenCancelled { get; set; }

            /// <summary>When set, the next Ask gives this sentence and no answer.</summary>
            public string? Instead { get; set; }

            /// <summary>How many updates the pane has handled: it came back for the one after.</summary>
            public int Handled { get; private set; }

            /// <summary>Hands the pane the next update of the latest answer.</summary>
            public void Feed(PadAiUpdate update) => Assert.True(Answers[^1].Writer.TryWrite(update));

            /// <summary>Ends the latest answer as the runner does: with Done.</summary>
            public void End()
            {
                Feed(Done);
                Answers[^1].Writer.Complete();
            }

            private async IAsyncEnumerable<PadAiUpdate> Read(Channel<PadAiUpdate> answer, [EnumeratorCancellation] CancellationToken token)
            {
                await foreach (PadAiUpdate update in answer.Reader.ReadAllAsync(token))
                {
                    yield return update;
                    Handled++;
                }
            }
        }

        /// <summary>Runs an async test on the UI thread over a new pane; a test that hangs fails after a minute.</summary>
        private static async Task OnUi(Func<Fake, Task> test)
        {
            Task body = UiThread.RunAsync(async () =>
            {
                var f = new Fake();
                try
                {
                    await test(f);
                }
                finally
                {
                    // Nothing runs on once the test is over: no answer is read, and no typing timer
                    // is left to tick on the UI thread the other tests share.
                    f.Pane.StopAnswer();
                }
            });
            await body.WaitAsync(TimeSpan.FromSeconds(60));
        }

        /// <summary>Lets the dispatcher run until <paramref name="done"/>, for at most ten seconds.</summary>
        private static async Task Until(Func<bool> done, string what)
        {
            var waited = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for " + what);
                await Task.Delay(10);
            }
        }

        /// <summary>Waits until the pane has handled <paramref name="count"/> updates in all.</summary>
        private static Task Handled(Fake f, int count) => Until(() => f.Handled >= count, count + " updates to be handled");

        /// <summary>Types <paramref name="query"/> and asks, as Ctrl+Enter does. The task ends when the answer does.</summary>
        private static Task Ask(Fake f, string query = "vpn")
        {
            f.Pane.QueryBox.Text = query;
            return f.Pane.AskNowAsync();
        }

        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static string Rendered(SearchPane pane)
        {
            var document = pane.AnswerBox.Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        /// <summary>
        /// Not shown: lays the pane out by hand, 300 wide in a 600 high window, with the ModernWpf
        /// theme the MicaPad window gives it (its scroll bars need one to draw).
        /// </summary>
        private static void LayOut(SearchPane pane)
        {
            ModernWpf.ThemeManager.SetRequestedTheme(pane, ModernWpf.ElementTheme.Dark);
            pane.Visibility = Visibility.Visible;
            pane.Measure(new Size(300, 600));
            pane.Arrange(new Rect(0, 0, 300, 600));
            pane.UpdateLayout();
        }

        /// <summary>The element called <paramref name="name"/> inside a laid-out result row.</summary>
        private static FrameworkElement InRow(SearchPane pane, int index, string name)
        {
            var row = Assert.IsType<ListBoxItem>(pane.Results.ItemContainerGenerator.ContainerFromIndex(index));
            return Assert.Single(Descendants(row).OfType<FrameworkElement>(), e => e.Name == name);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject below in Descendants(child)) yield return below;
            }
        }

        // ---- the keys and the button -----------------------------------------------------------

        [Fact]
        public Task Ctrl_Enter_and_the_Ask_button_ask_and_Enter_still_only_searches() => OnUi(async f =>
        {
            f.Instead = "nothing to stream";
            f.Pane.QueryBox.Text = "vpn";

            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.None));
            await Until(() => f.Searched.Count == 1, "the search");
            Assert.Empty(f.Asked);                                // Enter is the search it always was
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);

            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control));
            Assert.Equal(new[] { "vpn" }, f.Asked);

            Click(f.Pane.AskButton);
            Assert.Equal(new[] { "vpn", "vpn" }, f.Asked);
            Assert.Equal(new[] { "vpn" }, f.Searched);            // Ask runs its own search: the window's, not Run
        });

        // ---- one request for one question (each counts against the daily limit) ----------------------

        [Fact]
        public Task A_held_Ctrl_Enter_asks_once_however_long_it_is_held() => OnUi(async f =>
        {
            f.Pane.QueryBox.Text = "vpn";

            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control));                    // the key goes down
            for (int i = 0; i < 5; i++)
                Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control, repeat: true));  // and repeats: used up, asking nothing

            Assert.Equal(new[] { "vpn" }, f.Asked);
            Assert.False(f.Tokens[0].IsCancellationRequested);    // nor is the first question started over

            f.Feed(Text("A quick answer."));
            f.End();
            await Until(() => f.Pane.AnswerCopy.Visibility == Visibility.Visible, "the answer to end");
            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control, repeat: true));      // still held after a quick answer
            Assert.Equal(new[] { "vpn" }, f.Asked);

            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control));                    // let go and pressed again: a new question
            Assert.Equal(new[] { "vpn", "vpn" }, f.Asked);
        });

        [Fact]
        public Task A_held_Enter_still_searches() => OnUi(async f =>
        {
            f.Pane.QueryBox.Text = "vpn";

            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.None, repeat: true));

            await Until(() => f.Searched.Count == 1, "the search");   // a search sends no counted request
            Assert.Empty(f.Asked);
        });

        [Fact]
        public Task The_question_being_answered_is_not_asked_again_until_its_answer_ends() => OnUi(async f =>
        {
            Task first = Ask(f);
            f.Feed(Text("Use the"));
            await Handled(f, 1);

            Click(f.Pane.AskButton);                              // a double click on Ask
            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control));
            await f.Pane.AskNowAsync();

            Assert.Equal(new[] { "vpn" }, f.Asked);
            Assert.False(f.Tokens[0].IsCancellationRequested);    // the answer under way is left alone
            Assert.Equal("Use the", f.Pane.AnswerBox.Shown);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerStop.Visibility);

            f.Feed(Text(" office wifi."));
            f.End();
            await first;
            Assert.Equal("Use the office wifi.", f.Pane.AnswerBox.Shown);

            Task again = Ask(f);                                  // once it has ended, it can be asked again
            Assert.Equal(new[] { "vpn", "vpn" }, f.Asked);
            f.End();
            await again;
        });

        [Fact]
        public Task The_question_still_being_searched_for_is_not_asked_again() => OnUi(async f =>
        {
            var slow = new TaskCompletionSource<AskStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            int asked = 0;
            f.Pane.Ask = (_, _) =>
            {
                asked++;
                return slow.Task;
            };
            Task first = Ask(f);

            await Ask(f);                                         // again, while the first still searches
            Click(f.Pane.AskButton);

            Assert.Equal(1, asked);
            slow.SetResult(new AskStart(f.Found, "Words", null, "a sentence"));
            await first;
            Assert.Equal("a sentence", f.Pane.AnswerBox.Shown);   // and the first went on to its end
        });

        [Fact]
        public Task A_question_that_was_stopped_or_overtaken_can_be_asked_again_at_once() => OnUi(async f =>
        {
            Task first = Ask(f);
            f.Feed(Text("Use the"));
            await Handled(f, 1);
            Click(f.Pane.AnswerStop);

            Task second = Ask(f);                                 // before the stopped one has wound up
            Assert.Equal(new[] { "vpn", "vpn" }, f.Asked);
            await first;

            await f.Pane.SearchNow();                             // a search takes the pane from the second
            Task third = Ask(f);
            Assert.Equal(new[] { "vpn", "vpn", "vpn" }, f.Asked);
            await second;

            f.Feed(Text("The answer."));
            f.End();
            await third;
            Assert.Equal("The answer.", f.Pane.AnswerBox.Shown);
        });

        [Fact]
        public void The_Ask_button_sits_beside_the_query_box_and_names_its_shortcut() => UiThread.Run(() =>
        {
            var pane = new SearchPane();
            LayOut(pane);

            Assert.Equal("Ask", pane.AskButton.Content);
            Assert.Equal("Answer from your notes (Ctrl+Enter)", pane.AskButton.ToolTip);
            System.Windows.Point box = pane.QueryBox.TranslatePoint(new System.Windows.Point(0, 0), pane);
            System.Windows.Point button = pane.AskButton.TranslatePoint(new System.Windows.Point(0, 0), pane);
            Assert.True(pane.QueryBox.ActualWidth > 150, "the box keeps most of the line: " + pane.QueryBox.ActualWidth);
            Assert.True(button.X >= box.X + pane.QueryBox.ActualWidth, "the button is right of the box");
            Assert.True(pane.AskButton.ActualWidth > 0 && button.X + pane.AskButton.ActualWidth <= 300, "the button is inside the pane");
            Assert.InRange(button.Y + pane.AskButton.ActualHeight / 2, box.Y, box.Y + pane.QueryBox.ActualHeight);   // on the same line

            Assert.Equal(Visibility.Collapsed, pane.AnswerPanel.Visibility);   // collapsed until used
        });

        [Fact]
        public Task Keys_other_than_Enter_keep_their_meaning_in_the_query_box() => OnUi(f =>
        {
            int returned = 0;
            f.Pane.ReturnRequested += () => returned++;
            f.Pane.QueryBox.Text = "vpn";

            Assert.False(f.Pane.HandleQueryKey(Key.A, ModifierKeys.None));
            Assert.False(f.Pane.HandleQueryKey(Key.A, ModifierKeys.Control));
            Assert.True(f.Pane.HandleQueryKey(Key.Escape, ModifierKeys.None));

            Assert.Equal(1, returned);
            Assert.Empty(f.Asked);
            Assert.Empty(f.Searched);
            return Task.CompletedTask;
        });

        [Fact]
        public Task An_empty_query_does_nothing() => OnUi(async f =>
        {
            Task first = Ask(f);
            f.Feed(Text("An answer."));
            f.End();
            await first;

            f.Pane.QueryBox.Text = "   ";
            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.Control));
            Click(f.Pane.AskButton);
            await f.Pane.AskNowAsync();

            Assert.Equal(new[] { "vpn" }, f.Asked);
            Assert.Empty(f.Searched);
            Assert.False(f.Tokens[0].IsCancellationRequested);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);   // the answer on screen is left alone
            Assert.Equal("An answer.", f.Pane.AnswerBox.Shown);
            Assert.Equal(3, f.Pane.Rows.Count);
        });

        // ---- the rows and the status -------------------------------------------------------------

        [Fact]
        public Task Rows_with_a_source_show_its_number_in_a_badge_before_the_title() => OnUi(async f =>
        {
            f.Instead = "nothing to stream";
            await Ask(f);
            LayOut(f.Pane);

            Assert.Equal(f.Found, f.Pane.Rows);
            Assert.Equal("Words", f.Pane.StatusText.Text);        // a sentence instead of an answer: the search status alone
            for (int i = 0; i < 2; i++)
            {
                FrameworkElement badge = InRow(f.Pane, i, "SourceBadge");
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                             Assert.IsType<TextBlock>(InRow(f.Pane, i, "SourceNumber")).Text);
                FrameworkElement title = InRow(f.Pane, i, "RowTitle");
                Assert.True(badge.ActualWidth > 0, "the badge is drawn");
                Assert.True(badge.TranslatePoint(new System.Windows.Point(badge.ActualWidth, 0), f.Pane).X
                            <= title.TranslatePoint(new System.Windows.Point(0, 0), f.Pane).X, "the badge is before the title");
            }
            Assert.Equal(Visibility.Collapsed, InRow(f.Pane, 2, "SourceBadge").Visibility);   // a hit that is not a source
        });

        [Fact]
        public Task Rows_of_a_plain_search_have_no_badge() => OnUi(async f =>
        {
            f.Found = new[] { Row("Network", null) };
            f.Pane.QueryBox.Text = "vpn";
            await f.Pane.SearchNow();
            LayOut(f.Pane);

            Assert.Equal(Visibility.Collapsed, InRow(f.Pane, 0, "SourceBadge").Visibility);
        });

        [Fact]
        public Task With_no_rows_the_status_says_so_as_a_search_does() => OnUi(async f =>
        {
            f.Found = Array.Empty<SearchRow>();
            f.Status = "Words";
            f.Instead = NotesQuestion.NoSources;

            await Ask(f);

            Assert.Equal("Words · No notes found", f.Pane.StatusText.Text);
            Assert.Equal(NotesQuestion.NoSources, f.Pane.AnswerBox.Shown);
        });

        // ---- a sentence in place of an answer ------------------------------------------------------

        [Fact]
        public Task A_sentence_given_instead_is_shown_as_plain_text_and_nothing_streams() => OnUi(async f =>
        {
            f.Instead = "Turn on **Settings** → MicaPad → AI";

            await Ask(f);

            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("Turn on **Settings** → MicaPad → AI", f.Pane.AnswerBox.Shown);
            Assert.Equal("Turn on **Settings** → MicaPad → AI", Rendered(f.Pane));   // as it is: no Markdown
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerCopy.Visibility);        // there is no answer to copy
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerNote.Visibility);
            Assert.Equal(f.Found, f.Pane.Rows);

            Click(f.Pane.AnswerCopy);                             // even a forced click copies nothing
            Assert.Empty(f.Copied);
        });

        // ---- an answer that streams ----------------------------------------------------------------

        [Fact]
        public Task An_answer_streams_in_as_Markdown_with_Stop_and_ends_with_Copy() => OnUi(async f =>
        {
            Task ask = Ask(f);

            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerStop.Visibility);
            Assert.Equal("Stop", f.Pane.AnswerStop.Content);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerCopy.Visibility);
            Assert.Equal(f.Found, f.Pane.Rows);                   // the rows and the status are there before the answer
            Assert.Equal("Words · Answering from 2 passages", f.Pane.StatusText.Text);   // not "Answered": nothing has come yet

            f.Feed(Text("Use the **office wifi**"));
            await Handled(f, 1);
            Assert.Equal("Use the **office wifi**", f.Pane.AnswerBox.Shown);
            Assert.Equal("Use the office wifi", Rendered(f.Pane));
            Assert.Equal("Words · Answering from 2 passages", f.Pane.StatusText.Text);

            f.Feed(Text(" [1]."));
            f.End();
            await ask;

            Assert.Equal("Words · Answered from 2 passages", f.Pane.StatusText.Text);    // only after a clean end
            Assert.Equal("Use the **office wifi** [1].", f.Pane.AnswerBox.Shown);
            Assert.Equal("Use the office wifi [1].", Rendered(f.Pane));
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerCopy.Visibility);
            Assert.Equal("Copy", f.Pane.AnswerCopy.Content);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerNote.Visibility);
            Assert.False(f.Tokens[0].IsCancellationRequested);
        });

        [Fact]
        public Task A_link_in_an_answer_is_text_with_its_address_in_sight_and_cannot_be_clicked() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("It is in the vault [2](https://evil.example/c?d=the+passage)."));   // dressed as a citation
            f.End();
            await ask;

            Assert.Equal("It is in the vault 2 (https://evil.example/c?d=the+passage).", Rendered(f.Pane));
            Assert.Empty(AiAskWindowTests.Descendants<Hyperlink>(f.Pane.AnswerBox.Document));
        });

        [Fact]
        public Task An_error_shows_its_message_under_the_partial_text() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            f.Feed(new PadAiUpdate(PadAiUpdateKind.Error, AiErrorText.Busy));
            f.End();
            await ask;

            Assert.Equal("Use the", f.Pane.AnswerBox.Shown);
            Assert.Equal(AiErrorText.Busy, f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerNote.Visibility);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);        // the status claims no answer: the line under the text says why
        });

        [Fact]
        public Task An_error_before_any_text_shows_its_message_and_offers_no_Copy() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(new PadAiUpdate(PadAiUpdateKind.Error, "Add an API key in Settings > AI."));
            f.End();
            await ask;

            Assert.Equal("", f.Pane.AnswerBox.Shown);
            Assert.Equal("Add an API key in Settings > AI.", f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerCopy.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        [Fact]
        public Task An_answer_cut_short_says_so_under_the_text() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the office wi"));
            f.Feed(new PadAiUpdate(PadAiUpdateKind.CutShort));
            f.End();
            await ask;

            Assert.Equal("Use the office wi", f.Pane.AnswerBox.Shown);
            Assert.Equal(CutShort, f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerNote.Visibility);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerCopy.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);        // half an answer is not "Answered"
        });

        [Theory]
        [InlineData(null)]        // a clean end, and not one piece of text
        [InlineData("")]
        [InlineData(" \n\n ")]    // or nothing but white space
        public Task A_reply_with_no_text_is_not_an_answer(string? reply) => OnUi(async f =>
        {
            Task ask = Ask(f);
            if (reply != null) f.Feed(Text(reply));
            f.End();
            await ask;

            Assert.Equal("No answer came back", f.Pane.AnswerNote.Text);
            Assert.Equal(SearchPane.NoAnswerText, f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerNote.Visibility);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);        // nothing was answered: no "Answered from 2 passages"
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerCopy.Visibility);   // and there is nothing to copy
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
        });

        [Fact]
        public Task A_new_search_takes_the_Answered_status_away_at_once_not_when_its_result_arrives() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("An answer."));
            f.End();
            await ask;
            Assert.Equal("Words · Answered from 2 passages", f.Pane.StatusText.Text);
            var slow = new TaskCompletionSource<(IReadOnlyList<SearchRow> Rows, string Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Pane.Run = (_, _) => slow.Task;

            Task search = f.Pane.SearchNow();                     // the answer is cleared, and the search is still out

            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);        // the status no longer speaks of an answer that is gone
            slow.SetResult((new[] { Row("Later", null) }, "Meaning + words"));
            await search;
            Assert.Equal("Meaning + words", f.Pane.StatusText.Text);
        });

        [Fact]
        public Task A_new_question_takes_the_Answering_and_Answered_status_away_at_once() => OnUi(async f =>
        {
            Task first = Ask(f, "vpn");
            f.Feed(Text("The old answer"));
            await Handled(f, 1);
            Assert.Equal("Words · Answering from 2 passages", f.Pane.StatusText.Text);
            var slow = new TaskCompletionSource<AskStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Pane.Ask = (_, _) => slow.Task;

            Task second = Ask(f, "wifi");                         // its search is still out

            Assert.Equal("Words", f.Pane.StatusText.Text);
            await first;
            Assert.Equal("Words", f.Pane.StatusText.Text);        // and the old one's end does not bring it back
            slow.SetResult(new AskStart(f.Found, "Words", null, "a sentence"));
            await second;
        });

        [Fact]
        public Task An_answer_that_ends_without_Done_is_not_called_answered() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            f.Answers[0].Writer.Complete();                       // the stream just ends: the runner always says Done first
            await ask;

            Assert.Equal("Use the", f.Pane.AnswerBox.Shown);
            Assert.Equal("Words", f.Pane.StatusText.Text);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
        });

        [Fact]
        public Task Stop_cancels_the_request_keeps_the_partial_text_and_says_Stopped() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            await Handled(f, 1);

            Click(f.Pane.AnswerStop);
            await ask;

            Assert.True(f.Tokens[0].IsCancellationRequested);
            Assert.Equal("Use the", f.Pane.AnswerBox.Shown);
            Assert.Equal(Stopped, f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerNote.Visibility);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerCopy.Visibility);
            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        // ---- a cancel that throws ------------------------------------------------------------------

        /// <summary>An Ask whose token has a callback that throws when it is cancelled, with its answer under way.</summary>
        private static async Task<Task> AskWithACancelThatThrows(Fake f)
        {
            f.WhenCancelled = () => throw new InvalidOperationException("boom");
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            await Handled(f, 1);
            return ask;
        }

        private static void AssertTheCancelWasReportedByTypeOnly(Fake f)
        {
            Assert.True(f.Tokens[0].IsCancellationRequested);     // cancelled all the same
            string warning = Assert.Single(f.Warned);
            Assert.Contains("failed (AggregateException)", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("boom", warning, StringComparison.Ordinal);
        }

        [Fact]
        public Task A_cancel_that_throws_never_escapes_the_Stop_button() => OnUi(async f =>
        {
            Task ask = await AskWithACancelThatThrows(f);

            Click(f.Pane.AnswerStop);                             // a click handler: an exception here would take MicaStats down
            await ask;

            AssertTheCancelWasReportedByTypeOnly(f);
            Assert.Equal(Stopped, f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
        });

        [Fact]
        public Task A_cancel_that_throws_never_escapes_StopAnswer_or_hiding_the_pane() => OnUi(async f =>
        {
            f.Pane.Visibility = Visibility.Visible;
            Task ask = await AskWithACancelThatThrows(f);

            f.Pane.Visibility = Visibility.Collapsed;             // the window's close path ends here too
            f.Pane.StopAnswer();
            await ask;

            AssertTheCancelWasReportedByTypeOnly(f);
        });

        [Fact]
        public Task A_cancel_that_throws_never_stops_a_new_search() => OnUi(async f =>
        {
            Task ask = await AskWithACancelThatThrows(f);

            await f.Pane.SearchNow();
            await ask;

            AssertTheCancelWasReportedByTypeOnly(f);
            Assert.Equal(new[] { "vpn" }, f.Searched);            // the search ran all the same
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
        });

        [Fact]
        public Task A_cancel_that_throws_never_stops_a_new_question() => OnUi(async f =>
        {
            Task first = await AskWithACancelThatThrows(f);
            f.WhenCancelled = null;

            Task second = Ask(f, "wifi");
            await first;
            f.Feed(Text("The new answer"));
            f.End();
            await second;

            AssertTheCancelWasReportedByTypeOnly(f);
            Assert.Equal(new[] { "vpn", "wifi" }, f.Asked);
            Assert.Equal("The new answer", f.Pane.AnswerBox.Shown);
        });

        [Fact]
        public Task Copy_passes_the_raw_answer_and_never_the_line_under_it() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the **office wifi** [1]."));
            f.Feed(new PadAiUpdate(PadAiUpdateKind.CutShort));
            f.End();
            await ask;

            Click(f.Pane.AnswerCopy);

            Assert.Equal(new[] { "Use the **office wifi** [1]." }, f.Copied);
        });

        [Fact]
        public Task An_answer_that_throws_ends_with_its_error_and_never_faults_the_pane() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            var boom = new InvalidOperationException("boom");
            f.Answers[0].Writer.Complete(boom);

            await ask;                                            // it did not throw

            Assert.Equal("Use the", f.Pane.AnswerBox.Shown);
            Assert.Equal(AiErrorText.Describe(boom), f.Pane.AnswerNote.Text);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
            Assert.Equal("Words", f.Pane.StatusText.Text);
            Assert.Equal(new[] { "Reading an answer failed (InvalidOperationException)" }, f.Warned);   // the type only
        });

        [Fact]
        public Task An_Ask_that_throws_says_so_and_never_faults_the_pane() => OnUi(async f =>
        {
            f.Pane.Ask = (_, _) => Task.FromException<AskStart>(new InvalidOperationException("boom"));

            await Ask(f);

            Assert.Equal(Visibility.Visible, f.Pane.AnswerPanel.Visibility);
            Assert.Equal(SearchPane.AskFailedText, f.Pane.AnswerBox.Shown);
            Assert.DoesNotContain("boom", f.Pane.AnswerBox.Shown, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
            Assert.Equal(new[] { "Asking the notes failed (InvalidOperationException)" }, f.Warned);    // the type only
        });

        // ---- the redraw rule -------------------------------------------------------------------------

        [Fact]
        public Task A_streaming_answer_redraws_at_most_once_per_interval_and_at_once_when_it_ends() => OnUi(async f =>
        {
            f.Pane.RedrawInterval = TimeSpan.FromSeconds(30);
            Task ask = Ask(f);

            f.Feed(Text("one"));
            await Handled(f, 1);
            Assert.Equal("one", f.Pane.AnswerBox.Shown);          // the first text draws at once

            f.Feed(Text(" two"));
            await Handled(f, 2);
            Assert.Equal("one", f.Pane.AnswerBox.Shown);          // inside the interval: it waits

            f.End();
            await ask;
            Assert.Equal("one two", f.Pane.AnswerBox.Shown);      // the end draws at once
            Assert.Equal("one two", Rendered(f.Pane));
        });

        [Fact]
        public Task The_redraw_that_waited_draws_the_latest_text() => OnUi(async f =>
        {
            f.Pane.RedrawInterval = TimeSpan.FromMilliseconds(50);
            Task ask = Ask(f);
            f.Feed(Text("one"));
            f.Feed(Text(" two"));
            f.Feed(Text(" three"));

            await Until(() => f.Pane.AnswerBox.Shown == "one two three", "the redraw");

            Assert.Equal(Visibility.Visible, f.Pane.AnswerStop.Visibility);   // drawn by the timer: the answer still runs
            f.End();
            await ask;
        });

        [Fact]
        public void The_default_redraw_interval_is_100_ms() => UiThread.Run(() =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100), new SearchPane().RedrawInterval);
        });

        // ---- what ends an answer -----------------------------------------------------------------------

        [Fact]
        public Task A_new_search_cancels_a_running_answer_and_collapses_it() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            await Handled(f, 1);

            await f.Pane.SearchNow();
            await ask;

            Assert.True(f.Tokens[0].IsCancellationRequested);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("", f.Pane.AnswerBox.Shown);             // the old answer is cleared, not only hidden
            Assert.Equal("", f.Pane.AnswerNote.Text);             // and its end draws nothing: no "Stopped"
            Assert.Equal(new[] { "vpn" }, f.Searched);
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        [Fact]
        public Task A_new_search_collapses_a_finished_answer() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("Use the office wifi."));
            f.End();
            await ask;

            Assert.True(f.Pane.HandleQueryKey(Key.Enter, ModifierKeys.None));

            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("", f.Pane.AnswerBox.Shown);
            Click(f.Pane.AnswerCopy);                             // nothing is left to copy
            Assert.Empty(f.Copied);
        });

        [Fact]
        public Task A_new_question_cancels_the_running_answer_and_clears_the_old_one() => OnUi(async f =>
        {
            Task first = Ask(f, "vpn");
            f.Feed(Text("The old answer"));
            await Handled(f, 1);

            Task second = Ask(f, "wifi");

            Assert.True(f.Tokens[0].IsCancellationRequested);
            Assert.False(f.Tokens[1].IsCancellationRequested);
            Assert.Equal("", f.Pane.AnswerBox.Shown);
            await first;
            Assert.Equal(Visibility.Visible, f.Pane.AnswerStop.Visibility);   // the first one's end left the new answer alone
            Assert.Equal("", f.Pane.AnswerNote.Text);

            f.Feed(Text("The new answer"));
            f.End();
            await second;

            Assert.Equal(new[] { "vpn", "wifi" }, f.Asked);
            Assert.Equal("The new answer", f.Pane.AnswerBox.Shown);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerNote.Visibility);
        });

        [Fact]
        public Task An_Ask_that_a_newer_search_overtook_shows_nothing() => OnUi(async f =>
        {
            var slow = new TaskCompletionSource<AskStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Pane.Ask = (_, _) => slow.Task;
            Task ask = Ask(f);

            await f.Pane.SearchNow();                             // Enter while the question's search still runs
            slow.SetResult(new AskStart(new[] { Row("Late", 1) }, "Late", null, "late"));
            await ask;

            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal(f.Found, f.Pane.Rows);                   // the search's rows, not the overtaken question's
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        /// <summary>An answer that must never be read: reading it is what starts the request.</summary>
        private sealed class NeverRead : IAsyncEnumerable<PadAiUpdate>
        {
            public int Reads { get; private set; }

            public IAsyncEnumerator<PadAiUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                Reads++;
                throw new InvalidOperationException("An overtaken answer was read");
            }
        }

        [Fact]
        public Task An_answer_that_a_newer_search_overtook_is_never_read() => OnUi(async f =>
        {
            var slow = new TaskCompletionSource<AskStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new NeverRead();
            f.Pane.Ask = (_, _) => slow.Task;
            Task ask = Ask(f);

            await f.Pane.SearchNow();                             // Enter while the question's search still runs
            slow.SetResult(new AskStart(new[] { Row("Late", 1) }, "Late", answer, null, "Late · Answering", "Late · Answered"));
            await ask;

            Assert.Equal(0, answer.Reads);                        // so no request was made for a question nobody waits for
            Assert.Empty(f.Warned);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal(f.Found, f.Pane.Rows);
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        [Fact]
        public Task An_answer_whose_question_was_stopped_while_it_searched_is_never_read() => OnUi(async f =>
        {
            var slow = new TaskCompletionSource<AskStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new NeverRead();
            f.Pane.Visibility = Visibility.Visible;
            f.Pane.Ask = (_, _) => slow.Task;
            Task ask = Ask(f);

            f.Pane.Visibility = Visibility.Collapsed;             // the pane was closed while the question's search still ran
            slow.SetResult(new AskStart(new[] { Row("Late", 1) }, "Late", answer, null, "Late · Answering", "Late · Answered"));
            await ask;

            Assert.Equal(0, answer.Reads);
            Assert.Empty(f.Warned);
            Assert.Empty(f.Pane.Rows);                            // nothing of it is shown
        });

        // ---- an answer that must not stay (a credential was stored) ---------------------------------

        [Fact]
        public Task DropAnswer_takes_a_finished_answer_and_its_status_off_the_pane_and_keeps_the_rows() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("The login is hunter2 [1]."));
            f.End();
            await ask;
            Assert.Equal("Words · Answered from 2 passages", f.Pane.StatusText.Text);

            f.Pane.DropAnswer();

            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("", f.Pane.AnswerBox.Shown);
            Assert.Equal("Words", f.Pane.StatusText.Text);
            Assert.Equal(f.Found, f.Pane.Rows);
            Click(f.Pane.AnswerCopy);
            Assert.Empty(f.Copied);
        });

        [Fact]
        public Task DropAnswer_cancels_an_answer_that_streams_in_and_its_end_draws_nothing() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text("The login is"));
            await Handled(f, 1);

            f.Pane.DropAnswer();
            await ask;

            Assert.True(f.Tokens[0].IsCancellationRequested);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
            Assert.Equal("", f.Pane.AnswerBox.Shown);
            Assert.Equal("", f.Pane.AnswerNote.Text);             // not "Stopped": nothing of it is left to stop
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        [Fact]
        public Task DropAnswer_while_the_question_still_searches_means_its_answer_is_never_read() => OnUi(async f =>
        {
            var slow = new TaskCompletionSource<AskStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new NeverRead();
            f.Pane.Ask = (_, _) => slow.Task;
            Task ask = Ask(f);

            f.Pane.DropAnswer();
            slow.SetResult(new AskStart(new[] { Row("Late", 1) }, "Late", answer, null, "Late · Answering", "Late · Answered"));
            await ask;

            Assert.Equal(0, answer.Reads);                        // so the passages found before the store are never sent
            Assert.Empty(f.Pane.Rows);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerPanel.Visibility);
        });

        [Fact]
        public Task DropAnswer_leaves_a_plain_search_alone() => OnUi(async f =>
        {
            var slow = new TaskCompletionSource<(IReadOnlyList<SearchRow> Rows, string Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Pane.Run = (_, _) => slow.Task;
            f.Pane.QueryBox.Text = "vpn";
            Task search = f.Pane.SearchNow();

            f.Pane.DropAnswer();                                  // there is no answer: nothing to drop
            slow.SetResult((f.Found, "Words"));
            await search;

            Assert.Equal(f.Found, f.Pane.Rows);
            Assert.Equal("Words", f.Pane.StatusText.Text);
        });

        [Fact]
        public Task Hiding_the_pane_drops_a_search_still_waiting_for_typing_to_pause() => OnUi(async f =>
        {
            f.Pane.Visibility = Visibility.Visible;
            f.Pane.QueryBox.Text = "vpn";                         // the search would run 300 ms from now

            f.Pane.Visibility = Visibility.Collapsed;
            await Task.Delay(SearchPane.TypingPause + TimeSpan.FromMilliseconds(300));

            Assert.Empty(f.Searched);                             // a closed pane searches nothing
        });

        [Fact]
        public Task Hiding_the_pane_stops_a_running_answer() => OnUi(async f =>
        {
            f.Pane.Visibility = Visibility.Visible;
            Task ask = Ask(f);
            f.Feed(Text("Use the"));
            await Handled(f, 1);

            f.Pane.Visibility = Visibility.Collapsed;             // closed, or History or the AI pane took its column
            await ask;

            Assert.True(f.Tokens[0].IsCancellationRequested);
            Assert.Equal(Visibility.Collapsed, f.Pane.AnswerStop.Visibility);
        });

        // ---- the theme -----------------------------------------------------------------------------------

        [Fact]
        public void The_answer_box_follows_the_theme_the_pane_is_given() => UiThread.Run(() =>
        {
            var pane = new SearchPane();
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(pane.AnswerBox.Foreground));   // dark until it is told

            pane.ApplyTheme(false);
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(pane.AnswerBox.Foreground));

            pane.ApplyTheme(true);
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(pane.AnswerBox.Foreground));
        });

        [Fact]
        public Task A_long_answer_scrolls_inside_its_panel_and_leaves_the_results_in_view() => OnUi(async f =>
        {
            Task ask = Ask(f);
            f.Feed(Text(string.Join("\n\n", Enumerable.Range(1, 80).Select(i => "Paragraph " + i + " of a long answer."))));
            f.End();
            await ask;

            LayOut(f.Pane);

            Assert.True(f.Pane.AnswerScroller.ScrollableHeight > 0, "the answer scrolls");
            Assert.True(f.Pane.AnswerPanel.ActualHeight < 400, "the answer leaves room: " + f.Pane.AnswerPanel.ActualHeight);
            Assert.True(f.Pane.AnswerBox.ActualWidth is > 180 and < 300, "the answer wraps to the pane: " + f.Pane.AnswerBox.ActualWidth);
            Assert.True(f.Pane.Results.ActualHeight > 100, "the results are in view: " + f.Pane.Results.ActualHeight);
            System.Windows.Point copy = f.Pane.AnswerCopy.TranslatePoint(new System.Windows.Point(f.Pane.AnswerCopy.ActualWidth, f.Pane.AnswerCopy.ActualHeight), f.Pane);
            Assert.True(f.Pane.AnswerCopy.ActualWidth > 0 && copy.X <= 300 && copy.Y <= 600, "Copy is inside the pane");
        });

        [Fact]
        public void The_pane_xaml_has_no_literal_colors_and_reads_only_keys_the_pad_palette_has()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "Pad", "SearchPane.xaml"));

            Assert.Empty(new Regex("\"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\"").Matches(xaml).Select(m => m.Value));

            var known = PadPalette.Dark.Resources().Select(r => r.Key).ToHashSet();
            var used = new Regex(@"Resource (Pad\.[A-Za-z]+)\}").Matches(xaml).Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.True(used.Count >= 5, "expected the pane to read its colors from Pad.* keys, found " + used.Count);
            Assert.All(used, key => Assert.Contains(key, known));
        }
    }
}
