using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>How one Ask turn shows a streamed answer: its Markdown source, rendering, chips, dots and footer.</summary>
    public class AiAskTurnViewTests
    {
        private static string Rendered(AskTurnView turn)
        {
            var document = turn.Answer.Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd();
        }

        [Fact]
        public void Line_breaks_before_the_answer_starts_are_not_shown() => UiThread.Run(() =>
        {
            // vLLM-served reasoning models (e.g. with a reasoning parser) start the content with "\n\n".
            var turn = new AskTurnView("q");
            turn.AppendText("\n");
            turn.AppendText(" \n ");
            turn.AppendText("\n\npong");
            Assert.Equal("pong", turn.RawText);
        });

        [Fact]
        public void Line_breaks_inside_the_answer_are_kept() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("first");
            turn.AppendText("\n\n");
            turn.AppendText("second");
            Assert.Equal("first\n\nsecond", turn.RawText);
        });

        [Fact]
        public void The_dots_stay_through_tools_and_blank_text_and_go_with_the_first_text() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            Assert.Equal(Visibility.Visible, turn.Typing.Visibility);
            // Off screen the dots do not animate: nothing ticks the renderer for a turn nobody sees.
            Assert.All(((System.Windows.Controls.Panel)turn.Typing).Children.Cast<UIElement>(), d => Assert.False(d.HasAnimatedProperties));

            turn.AddTool("get_live_status", null);
            turn.AppendText("\n\n");
            Assert.Equal(Visibility.Visible, turn.Typing.Visibility);
            Assert.Equal(Visibility.Collapsed, turn.Answer.Visibility);

            turn.AppendText("Fine.");
            Assert.Equal(Visibility.Collapsed, turn.Typing.Visibility);
            Assert.Equal(Visibility.Visible, turn.Answer.Visibility);
        });

        [Fact]
        public void A_note_or_the_end_hides_the_dots() => UiThread.Run(() =>
        {
            var noted = new AskTurnView("q");
            noted.ShowNote("Limited mode.");
            Assert.Equal(Visibility.Collapsed, noted.Typing.Visibility);
            Assert.Equal(Visibility.Visible, noted.Note.Visibility);

            var ended = new AskTurnView("q");
            ended.Complete(DateTime.Now, TimeSpan.Zero);
            Assert.Equal(Visibility.Collapsed, ended.Typing.Visibility);
            Assert.Equal(Visibility.Collapsed, ended.Footer.Visibility);
        });

        [Theory]
        [InlineData("get_live_status", "Reading live status…")]
        [InlineData("get_top_processes", "Checking top processes…")]
        [InlineData("get_history", "Looking at history…")]
        [InlineData("list_slowdown_reports", "Listing slowdown reports…")]
        [InlineData("get_slowdown_report", "Reading a slowdown report…")]
        [InlineData("list_alerts", "Checking alerts…")]
        [InlineData("get_hardware", "Reading hardware info…")]
        [InlineData("get_battery", "Checking the battery…")]
        [InlineData("get_boot_summary", "Checking startup times…")]
        [InlineData("search_notes", "Searching notes…")]
        [InlineData("get_note", "Reading a note…")]
        [InlineData("something_new", "Using something_new…")]
        public void Every_tool_has_its_activity_line(string tool, string line)
        {
            Assert.Equal(line, AskTurnView.ActivityFor(tool));
        }

        [Fact]
        public void An_unknown_tool_name_is_cut_to_40_characters_and_cannot_break_the_line()
        {
            string longName = new string('x', 500);
            Assert.Equal("Using " + new string('x', 40) + "…", AskTurnView.ActivityFor(longName));

            // A line break or another control character from the model must not make a second line.
            Assert.Equal("Using a b c…", AskTurnView.ActivityFor("a\nb\tc"));
            Assert.Equal("Using a tool…", AskTurnView.ActivityFor("  "));

            // A pair of surrogates is never cut in half.
            string emoji = "a" + string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 30));
            string cut = AskTurnView.ActivityFor(emoji);
            Assert.False(char.IsHighSurrogate(cut[^2]));
            Assert.True(cut.Length <= "Using ".Length + 40 + 1);
        }

        [Fact]
        public void Line_and_paragraph_separators_in_a_tool_name_become_spaces()
        {
            string name = "a" + (char)0x2028 + "b" + (char)0x2029 + "c";

            string line = AskTurnView.ActivityFor(name);

            Assert.Equal("Using a b c…", line);
            Assert.DoesNotContain((char)0x2028, line);
            Assert.DoesNotContain((char)0x2029, line);
        }

        [Fact]
        public void A_new_turn_says_thinking_beside_the_dots() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");

            Assert.Equal("Thinking…", turn.ActivityText.Text);
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);
            Assert.Equal(Visibility.Visible, turn.Typing.Visibility);
            Assert.Same(turn.Activity, ((FrameworkElement)turn.Typing).Parent);   // the dots and the text are one row
            Assert.Same(turn.Activity, turn.ActivityText.Parent);
        });

        [Fact]
        public void A_tool_line_replaces_thinking_text_hides_it_and_a_later_tool_shows_it_again_under_the_answer() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            var content = (System.Windows.Controls.Panel)turn.Activity.Parent;

            turn.AddTool("get_live_status", null);
            turn.ShowActivity(AskTurnView.ActivityFor("get_live_status"));
            Assert.Equal("Reading live status…", turn.ActivityText.Text);
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);
            // Before any text the row sits where the dots always sat: under the chips, above the answer.
            Assert.True(content.Children.IndexOf(turn.Tools) < content.Children.IndexOf(turn.Activity));
            Assert.True(content.Children.IndexOf(turn.Activity) < content.Children.IndexOf(turn.Answer));

            turn.AppendText("The CPU is fine.");
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);
            Assert.Equal(Visibility.Collapsed, turn.Typing.Visibility);

            turn.AddTool("get_history", null);
            turn.ShowActivity(AskTurnView.ActivityFor("get_history"));
            Assert.Equal(Visibility.Visible, turn.Activity.Visibility);
            Assert.Equal(Visibility.Visible, turn.Typing.Visibility);
            Assert.Equal("Looking at history…", turn.ActivityText.Text);
            // Now the answer text is above it.
            int answer = content.Children.IndexOf(turn.Answer);
            int activity = content.Children.IndexOf(turn.Activity);
            Assert.True(answer < activity, "the activity must sit below the answer text");
            Assert.True(activity < content.Children.IndexOf(turn.Note));
            Assert.True(activity < content.Children.IndexOf(turn.Footer));

            turn.AppendText(" More.");
            Assert.Equal(Visibility.Collapsed, turn.Activity.Visibility);
            Assert.Equal(2, turn.ToolChips.Count);   // the chips stay: they are the record
        });

        [Fact]
        public void A_note_or_the_end_hides_the_activity_and_nothing_brings_it_back_after_the_end() => UiThread.Run(() =>
        {
            var noted = new AskTurnView("q");
            noted.ShowActivity("Reading a note…");
            noted.ShowNote("Limited mode.");
            Assert.Equal(Visibility.Collapsed, noted.Activity.Visibility);

            var ended = new AskTurnView("q");
            ended.AppendText("Done.");
            ended.ShowActivity("Checking alerts…");
            ended.Complete(DateTime.Now, TimeSpan.FromSeconds(2));
            Assert.Equal(Visibility.Collapsed, ended.Activity.Visibility);
            Assert.Equal(Visibility.Collapsed, ended.Typing.Visibility);

            ended.ShowActivity("Using late_tool…");   // a straggling update after the end
            Assert.Equal(Visibility.Collapsed, ended.Activity.Visibility);
            Assert.Equal(Visibility.Collapsed, ended.Typing.Visibility);
        });

        [Fact]
        public void A_tool_name_from_the_model_is_shown_as_plain_text_never_rendered() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");

            turn.ShowActivity(AskTurnView.ActivityFor("**bold** [x](https://example.com)"));

            Assert.Equal("Using **bold** [x](https://example.com)…", turn.ActivityText.Text);
            Assert.Single(turn.ActivityText.Inlines);
            Assert.IsType<Run>(turn.ActivityText.Inlines.FirstInline);
            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));
        });

        [Fact]
        public void Each_tool_gets_one_chip_with_a_friendly_label() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AddTool("get_history", "{\"metric\":\"cpu\"}");
            turn.AddTool("get_history", "{}");
            turn.AddTool("get_boot_summary", "");
            turn.AddTool("get_future_tool", null);

            Assert.Equal(new[] { "Looked at history", "Checked startup times", "get_future_tool" },
                new[] { turn.ToolChips[0].Label.Text, turn.ToolChips[1].Label.Text, turn.ToolChips[2].Label.Text });
            Assert.Equal("get_history {\"metric\":\"cpu\"}\nget_history", turn.ToolChips[0].Element.ToolTip);
            Assert.Equal(new[] { "get_history {\"metric\":\"cpu\"}", "get_history" }, turn.ToolChips[0].Calls);
        });

        [Theory]
        [InlineData("get_live_status", "Read live status")]
        [InlineData("get_top_processes", "Checked top processes")]
        [InlineData("get_history", "Looked at history")]
        [InlineData("list_slowdown_reports", "Listed slowdown reports")]
        [InlineData("get_slowdown_report", "Read a slowdown report")]
        [InlineData("list_alerts", "Checked alerts")]
        [InlineData("get_hardware", "Read hardware info")]
        [InlineData("get_battery", "Checked the battery")]
        [InlineData("get_boot_summary", "Checked startup times")]
        [InlineData("search_notes", "Searched notes")]
        [InlineData("get_note", "Read a note")]
        [InlineData("something_new", "something_new")]
        public void Every_tool_has_its_chip_label(string tool, string label)
        {
            Assert.Equal(label, AskTurnView.ToolLabel(tool));
        }

        [Fact]
        public void A_streaming_answer_renders_at_most_once_per_interval_and_at_once_when_complete() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q") { RenderInterval = TimeSpan.FromSeconds(30) };

            turn.AppendText("**first**");
            Assert.Equal("first", Rendered(turn));
            Assert.Equal(new Thickness(0), turn.Answer.Document.PagePadding);
            // Building the box from its template must not indent the answer past the chips.
            Assert.True(turn.Answer.ApplyTemplate());
            Assert.Equal(new Thickness(0), turn.Answer.Document.PagePadding);

            turn.AppendText(" and more");
            Assert.Equal("**first** and more", turn.RawText);
            Assert.Equal("first", Rendered(turn));

            turn.Complete(DateTime.Now, TimeSpan.Zero);
            Assert.Equal("first and more", Rendered(turn));
            Assert.Equal(new Thickness(0), turn.Answer.Document.PagePadding);   // a document handed to a templated box too
        });

        [Fact]
        public void The_footer_shows_the_finish_time_and_the_duration_in_ASCII_digits_whatever_the_culture() => UiThread.Run(() =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                var turn = new AskTurnView("q");
                turn.AppendText("Done.");
                turn.Complete(new DateTime(2026, 10, 1, 14, 5, 0), TimeSpan.FromSeconds(4.2));

                Assert.Equal(Visibility.Visible, turn.Footer.Visibility);
                Assert.Equal("14:05 · 4 s", turn.TimeText.Text);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        });

        [Fact]
        public void Copy_hands_the_markdown_source_to_the_clipboard_seam() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("1. **Close** `chrome.exe`\n2. Restart");
            turn.Complete(DateTime.Now, TimeSpan.Zero);

            var copied = new List<string>();
            var previous = AskTurnView.SetClipboard;
            AskTurnView.SetClipboard = copied.Add;
            try
            {
                turn.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            finally
            {
                AskTurnView.SetClipboard = previous;
            }

            Assert.Equal(new[] { "1. **Close** `chrome.exe`\n2. Restart" }, copied);
        });

        [Fact]
        public void The_question_is_selectable_text_in_its_bubble() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("\u0E17\u0E33\u0E44\u0E21\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32?");
            Assert.True(turn.Question.IsReadOnly);
            Assert.Equal("\u0E17\u0E33\u0E44\u0E21\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32?", turn.Question.Text);
            Assert.True(turn.Answer.IsReadOnly);
            Assert.True(turn.Answer.IsDocumentEnabled);
            Assert.True(turn.Answer.IsTabStop);   // keyboard users can reach the answer to select and copy it
        });

        [Fact]
        public void A_render_failure_shows_the_raw_text_and_is_reported_once() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var turn = new AskTurnView("q")
            {
                RenderInterval = TimeSpan.FromSeconds(30),
                BuildDocument = _ => throw new InvalidOperationException("broken"),
                Warn = warnings.Add,
            };

            turn.AppendText("**bold**\nline two");   // renders at once: the first failure
            turn.AppendText(" and more");
            turn.Complete(DateTime.Now, TimeSpan.Zero);             // renders again: no second report

            Assert.Equal("**bold**\nline two and more", Rendered(turn).Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal(Visibility.Visible, turn.Answer.Visibility);
            Assert.Equal(Visibility.Visible, turn.Footer.Visibility);
            var warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
        });

        [Fact]
        public void With_plain_links_a_table_cell_link_is_text_and_without_them_it_stays_a_link() => UiThread.Run(() =>
        {
            const string table = "| Name |\n|---|\n| [site](https://example.com/a) |";

            var plain = new AskTurnView("q") { PlainLinks = true };
            plain.AppendText(table);
            plain.Complete(DateTime.Now, TimeSpan.Zero);
            Assert.Empty(ChatDocument.All<Hyperlink>(plain.Answer.Document));
            Assert.Contains("https://example.com/a", Rendered(plain), StringComparison.Ordinal);

            var open = new AskTurnView("q");
            open.AppendText(table);
            open.Complete(DateTime.Now, TimeSpan.Zero);
            Assert.Single(ChatDocument.All<Hyperlink>(open.Answer.Document));
        });

        [Fact]
        public void A_note_tool_after_a_table_with_a_link_turns_the_link_into_text() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("| Name |\n|---|\n| [site](https://example.com/a) |");
            Assert.Single(ChatDocument.All<Hyperlink>(turn.Answer.Document));

            turn.AddTool("search_notes", null);

            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));
        });

        [Fact]
        public void The_answers_code_Copy_button_is_enabled_and_uses_the_one_clipboard_hook() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("```sh\nls -la\n```");
            var copy = Assert.Single(ChatDocument.All<Button>(turn.Answer.Document));
            Assert.True(turn.Answer.IsDocumentEnabled);
            Assert.True(copy.IsEnabled);

            var copied = new List<string>();
            var previous = AskTurnView.SetClipboard;
            AskTurnView.SetClipboard = copied.Add;
            try
            {
                Assert.Same(AskTurnView.SetClipboard, ChatClipboard.SetText);
                copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            finally
            {
                AskTurnView.SetClipboard = previous;
            }

            Assert.Equal(new[] { "ls -la" }, copied);
        });

        [Fact]
        public void A_mouse_reaches_the_Copy_button_of_a_code_block_in_the_Ask_answer() => UiThread.Run(() =>
        {
            // The turn's own box sits in its transcript; a box built the way the turn builds it is shown in the test window.
            var answer = new AnswerBox { Style = ChatStyles.Get("ChatAnswer") };
            AskThemeApplier.ApplyResources(answer.Resources, AskPalette.Dark);   // the Ask window supplies these
            answer.Show(ChatDocument.Build(ChatMarkdown.Parse("```cs\nint x;\n```"), ChatRender.Default));
            var copy = Assert.Single(ChatDocument.All<Button>(answer.Document));

            Assert.True(PadAnswerBoxTests.MouseReaches(answer, copy));
        });

        // ---- Mermaid diagrams: the real adapter over a renderer whose draws the test ends ----------

        private const string Flow = ChatDiagramFakes.Flow;

        /// <summary>A turn that renders every piece of text at once, drawing through <see cref="ChatDiagrams"/> over a fake renderer.</summary>
        private static (AskTurnView Turn, ChatDiagrams Diagrams, FakeRenderer Renderer) DiagramTurn()
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            var turn = new AskTurnView("q") { Diagrams = diagrams, RenderInterval = TimeSpan.Zero };
            return (turn, diagrams, renderer);
        }

        private static List<string> Lines(AskTurnView turn) => ChatDocument.All<TextBlock>(turn.Answer.Document).Select(t => t.Text).ToList();

        private static Image? Picture(AskTurnView turn) => ChatDocument.All<Image>(turn.Answer.Document).SingleOrDefault();

        /// <summary>Counts how often the turn builds its document from here on.</summary>
        private static Func<int> CountBuilds(AskTurnView turn)
        {
            int builds = 0;
            var build = turn.BuildDocument;
            turn.BuildDocument = raw =>
            {
                builds++;
                return build(raw);
            };
            return () => builds;
        }

        private static void RaiseClick(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        [Fact]
        public void A_mermaid_block_streamed_in_pieces_is_asked_for_only_when_its_closing_fence_arrives() => UiThread.Run(() =>
        {
            var (turn, _, renderer) = DiagramTurn();
            string[] pieces = { "Here ", "it is:\n\n", "```mer", "maid\n", "flowchart LR\n", "  a --", "> b", "\n``", "`", "\n\nDone." };

            var draws = new List<int>();
            foreach (string piece in pieces)
            {
                turn.AppendText(piece);
                draws.Add(renderer.Calls.Count);
            }

            // While the fence is open the block is code; the ninth piece closes it.
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1 }, draws);
            Assert.Equal(Flow, renderer.Calls[0].Request.Source);
            Assert.Null(renderer.Calls[0].Request.KrokiServer);
            Assert.Contains("Drawing the diagram…", Lines(turn));
            Assert.Null(Picture(turn));
        });

        [Fact]
        public Task When_the_draw_ends_the_turn_shows_the_picture_without_any_new_text() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText("Look:\n\n" + ChatDiagramFakes.Block() + "\n\nThat is all.");
            Assert.Contains("Drawing the diagram…", Lines(turn));
            string raw = turn.RawText;

            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(width: 100, height: 50), turn);

            Assert.Equal(raw, turn.RawText);
            var picture = Picture(turn);
            Assert.NotNull(picture);
            Assert.Equal(100, picture!.MaxWidth);
            Assert.DoesNotContain("Drawing the diagram…", Lines(turn));
            Assert.Contains("That is all.", Rendered(turn), StringComparison.Ordinal);
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public Task Text_that_streams_after_the_picture_keeps_it_and_asks_for_nothing_more() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block() + "\n\n");
            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), turn);
            var first = Picture(turn)!.Source;
            turn.LastRedrawCost = TimeSpan.Zero;   // whatever the redraws so far cost: each piece of text below is rendered at once
            var builds = CountBuilds(turn);

            for (int i = 0; i < 25; i++)
            {
                turn.AppendText("more ");
                // Every rebuild finds the picture already there: the same bitmap, never "Drawing…" again.
                Assert.Same(first, Picture(turn)!.Source);
                Assert.DoesNotContain("Drawing the diagram…", Lines(turn));
            }
            Assert.Equal(25, builds());
            turn.Complete(DateTime.Now, TimeSpan.Zero);

            Assert.Same(first, Picture(turn)!.Source);
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public Task A_theme_change_draws_the_turn_again_in_the_new_theme_and_a_theme_it_already_has_draws_nothing() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block());
            Assert.True(renderer.Calls[0].Request.Dark);   // a turn is dark until it is told
            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), turn);
            var dark = Picture(turn)!.Source;

            turn.ApplyTheme(false);

            Assert.Equal(2, renderer.Calls.Count);
            Assert.False(renderer.Calls[1].Request.Dark);
            Assert.Null(Picture(turn));   // the dark picture is not shown on a light answer
            Assert.Contains("Drawing the diagram…", Lines(turn));

            turn.ApplyTheme(false);
            Assert.Equal(2, renderer.Calls.Count);

            await FinishAndRedraw(diagrams, renderer, 1, DiagramFakes.Picture(), turn);
            Assert.NotNull(Picture(turn));
            Assert.NotSame(dark, Picture(turn)!.Source);

            turn.ApplyTheme(true);
            Assert.Equal(2, renderer.Calls.Count);   // the dark picture is still kept
            Assert.Same(dark, Picture(turn)!.Source);
        });

        [Fact]
        public void A_theme_given_before_the_first_text_renders_nothing_and_is_used_by_the_first_render() => UiThread.Run(() =>
        {
            var (turn, _, renderer) = DiagramTurn();
            var build = CountBuilds(turn);

            turn.ApplyTheme(false);

            Assert.Equal(0, build());
            Assert.Equal(Visibility.Collapsed, turn.Answer.Visibility);

            turn.AppendText(ChatDiagramFakes.Block());
            Assert.False(Assert.Single(renderer.Calls).Request.Dark);
        });

        [Fact]
        public Task A_draw_that_fails_is_shown_once_and_never_asked_for_again_however_often_the_turn_renders() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            var builds = CountBuilds(turn);
            turn.AppendText(ChatDiagramFakes.Block() + "\n\n");
            Assert.Equal(1, builds());

            // A failure the engine does not keep (a timeout): only the adapter stands between it and draw, fail, redraw, draw.
            await FinishAndRedraw(diagrams, renderer, 0, DiagramResult.Failure(DiagramText.TookTooLong, lasting: false), turn);

            Assert.Equal(2, builds());   // one redraw, for the end of the draw
            Assert.Contains("This diagram could not be drawn: " + DiagramText.TookTooLong, Lines(turn));
            Assert.Null(Picture(turn));

            await Task.Delay(150);       // left alone, it does not go round again
            Assert.Equal(2, builds());
            Assert.Single(renderer.Calls);

            turn.LastRedrawCost = TimeSpan.Zero;   // whatever the redraws so far cost: each piece of text below is rendered at once
            for (int i = 0; i < 50; i++) turn.AppendText("more ");
            Assert.Equal(52, builds());
            turn.ApplyTheme(false);
            turn.ApplyTheme(true);
            turn.Complete(DateTime.Now, TimeSpan.Zero);

            Assert.Equal(2, renderer.Calls.Count);   // the dark failure once, and the light theme's own draw
            Assert.True(renderer.Calls[0].Request.Dark);
            Assert.False(renderer.Calls[1].Request.Dark);
            Assert.Contains("This diagram could not be drawn: " + DiagramText.TookTooLong, Lines(turn));
        });

        [Fact]
        public void A_theme_change_builds_nothing_for_an_answer_without_a_diagram_and_the_theme_is_kept_for_the_next_render() => UiThread.Run(() =>
        {
            var (turn, _, renderer) = DiagramTurn();
            turn.AppendText("| a | b |\n|---|---|\n| 1 | 2 |\n\n```cs\nint x;\n```\n\n```mermaid\nflowchart LR");   // the fence is still open: nothing was asked for
            var builds = CountBuilds(turn);
            var shown = turn.Answer.Document;

            turn.ApplyTheme(false);
            turn.ApplyTheme(true);
            turn.ApplyTheme(false);

            // Before diagrams a theme switch was a swap of brushes. For an answer that asked for no picture it still is.
            Assert.Equal(0, builds());
            Assert.Same(shown, turn.Answer.Document);
            Assert.Empty(renderer.Calls);

            turn.AppendText("\n  a --> b\n```");   // the theme was recorded: the diagram that closes now is drawn for it
            Assert.False(Assert.Single(renderer.Calls).Request.Dark);
            int before = builds();

            turn.ApplyTheme(true);                 // and now there is a picture to ask for again
            Assert.Equal(before + 1, builds());
            Assert.Equal(2, renderer.Calls.Count);
            Assert.True(renderer.Calls[1].Request.Dark);
        });

        [Fact]
        public Task A_Clear_while_a_turn_waits_for_its_picture_tells_the_turn_once_and_it_asks_again_once() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block());
            var builds = CountBuilds(turn);

            diagrams.Clear();   // MicaPad stored a credential: the pictures kept for every answer are forgotten, this turn's draw too
            renderer.Calls[0].Done.TrySetResult(DiagramFakes.Picture());   // the draw ends; not through Finish, so the fake engine keeps nothing
            await ChatDiagramFakes.Until(() => builds() > 0, "the turn to be told");

            Assert.Equal(1, builds());                         // told once
            Assert.Equal(2, renderer.Calls.Count);             // the cleared draw's picture was not kept: the turn asked again, once
            Assert.Equal(0, diagrams.PicturesKept);
            Assert.Contains("Drawing the diagram…", Lines(turn));

            await Task.Delay(100);                             // and nothing more by itself
            Assert.Equal(1, builds());
            Assert.Equal(2, renderer.Calls.Count);

            await FinishAndRedraw(diagrams, renderer, 1, DiagramFakes.Picture(), turn);
            Assert.NotNull(Picture(turn));
            Assert.Equal(2, builds());
            Assert.Equal(2, renderer.Calls.Count);
        });

        private static Button? TryAgain(AskTurnView turn) =>
            ChatDocument.All<Button>(turn.Answer.Document).SingleOrDefault(b => Equals(b.Content, "Try again"));

        [Fact]
        public Task Try_again_starts_exactly_one_new_draw_and_a_second_failure_offers_it_again_and_draws_nothing_by_itself() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            var passing = DiagramResult.Failure(DiagramText.TookTooLong, lasting: false);   // the engine was still starting
            turn.AppendText(ChatDiagramFakes.Block() + "\n\nDone.");
            turn.Complete(DateTime.Now, TimeSpan.Zero);
            await FinishAndRedraw(diagrams, renderer, 0, passing, turn);
            Assert.Contains("This diagram could not be drawn: " + DiagramText.TookTooLong, Lines(turn));
            var builds = CountBuilds(turn);

            RaiseClick(TryAgain(turn)!);
            await Redrawn(turn);                                     // the press asks for the redraw; the turn's timer makes it

            Assert.Equal(2, renderer.Calls.Count);                   // one press, one draw
            Assert.Equal(1, builds());
            Assert.Contains("Drawing the diagram…", Lines(turn));    // the finished answer drew itself again
            Assert.Null(TryAgain(turn));

            await FinishAndRedraw(diagrams, renderer, 1, passing, turn);

            Assert.NotNull(TryAgain(turn));                          // it failed again: the button is back
            Assert.Equal(2, builds());
            await Task.Delay(150);                                   // and left alone, nothing is drawn
            Assert.Equal(2, renderer.Calls.Count);
            Assert.Equal(2, builds());

            RaiseClick(TryAgain(turn)!);
            await Redrawn(turn);
            await FinishAndRedraw(diagrams, renderer, 2, DiagramFakes.Picture(), turn);

            Assert.Equal(3, renderer.Calls.Count);
            Assert.NotNull(Picture(turn));                           // the engine was up this time
            Assert.Null(TryAgain(turn));
        });

        [Fact]
        public Task A_failure_that_would_come_again_has_no_Try_again_in_a_turn() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block());

            await FinishAndRedraw(diagrams, renderer, 0, DiagramResult.Failure("Parse error on line 2", lasting: true), turn);

            Assert.Contains("This diagram could not be drawn: Parse error on line 2", Lines(turn));
            Assert.Null(TryAgain(turn));
        });

        [Fact]
        public Task A_turn_the_window_dropped_is_not_rendered_again_and_asks_for_no_picture() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block());
            var builds = CountBuilds(turn);
            var shown = turn.Answer.Document;

            turn.Release();
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

            Assert.Null(turn.PendingRedraw);   // told of the picture, the turn asked for no redraw: none comes later either
            Assert.Equal(0, builds());
            Assert.Same(shown, turn.Answer.Document);

            turn.ApplyTheme(false);
            Assert.Equal(0, builds());

            // A stream that was cancelled may still end the turn: it renders as text and code, and draws nothing.
            turn.AppendText("\n\n" + ChatDiagramFakes.Block("pie\n  \"a\" : 1"));
            turn.Complete(DateTime.Now, TimeSpan.Zero);
            Assert.Single(renderer.Calls);
            Assert.Empty(ChatDocument.All<Image>(turn.Answer.Document));
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference WaitingTurn(ChatDiagrams diagrams)
        {
            var turn = new AskTurnView("q") { Diagrams = diagrams, RenderInterval = TimeSpan.Zero };
            turn.AppendText(ChatDiagramFakes.Block());
            return new WeakReference(turn);
        }

        [Fact]
        public Task A_draw_still_running_does_not_keep_its_turn_alive() => UiThread.RunAsync(async () =>
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };

            WeakReference turn = WaitingTurn(diagrams);
            Assert.Single(renderer.Calls);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(turn.IsAlive);   // the adapter holds the turn's redraw, and the redraw holds the turn only weakly

            // The draw ends for a turn that is gone: nothing is thrown, and the picture is there for whoever asks next.
            renderer.Finish(0, DiagramFakes.Picture());
            await ChatDiagramFakes.Until(() => diagrams.Get(Flow, true, null).Status == ChatDiagramStatus.Drawn, "the picture");
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public Task The_Source_choice_of_a_diagram_survives_the_next_render_and_belongs_to_its_turn() => UiThread.RunAsync(async () =>
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            var one = new AskTurnView("q") { Diagrams = diagrams, RenderInterval = TimeSpan.Zero };
            var two = new AskTurnView("q") { Diagrams = diagrams, RenderInterval = TimeSpan.Zero };
            one.AppendText(ChatDiagramFakes.Block() + "\n\n");
            two.AppendText(ChatDiagramFakes.Block() + "\n\n");
            Assert.Single(renderer.Calls);   // the same diagram in two turns is one draw

            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), one, two);
            Assert.NotNull(Picture(one));
            Assert.NotNull(Picture(two));    // and both turns are told

            RaiseClick(ChatDocument.All<Button>(one.Answer.Document).Single(b => Equals(b.Content, "Source")));
            Assert.Equal(Visibility.Collapsed, Picture(one)!.Visibility);
            var clicked = one.Answer.Document;
            one.LastRedrawCost = two.LastRedrawCost = TimeSpan.Zero;   // whatever the redraws so far cost: the text below is rendered at once

            one.AppendText("more");          // a new document, with new buttons
            two.AppendText("more");

            Assert.NotSame(clicked, one.Answer.Document);
            Assert.Equal(Visibility.Collapsed, Picture(one)!.Visibility);
            Assert.Equal(Visibility.Visible, ChatDocument.All<System.Windows.Controls.TextBox>(one.Answer.Document).Single().Visibility);
            Assert.Equal(Visibility.Visible, Picture(two)!.Visibility);
        });

        [Fact]
        public Task With_plain_links_a_diagram_is_still_drawn_and_no_link_is_left_beside_it_or_in_its_error() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.PlainLinks = true;
            turn.AppendText("See [site](https://example.com/a).\n\n" + ChatDiagramFakes.Block() + "\n\n" + ChatDiagramFakes.Block("pie"));

            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), turn);
            await FinishAndRedraw(diagrams, renderer, 1, DiagramResult.Failure("No diagram: see [help](https://evil.example/h)", lasting: true), turn);

            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));
            Assert.Single(ChatDocument.All<Image>(turn.Answer.Document));
            Assert.Contains("https://example.com/a", Rendered(turn), StringComparison.Ordinal);
            Assert.Contains("This diagram could not be drawn: No diagram: see [help](https://evil.example/h)", Lines(turn));
        });

        [Fact]
        public void A_turn_without_a_source_of_pictures_shows_mermaid_as_code() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");   // ChatDiagrams.Current is null in tests

            turn.AppendText(ChatDiagramFakes.Block());

            Assert.Null(turn.Diagrams);
            Assert.Empty(ChatDocument.All<Image>(turn.Answer.Document));
            Assert.Equal("Copy code", Assert.Single(ChatDocument.All<Button>(turn.Answer.Document)).ToolTip);
        });

        [Fact]
        public void A_mouse_reaches_the_Source_toggle_and_the_Copy_button_of_a_diagram_in_the_Ask_answer() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var answer = new AnswerBox { Style = ChatStyles.Get("ChatAnswer") };
            AskThemeApplier.ApplyResources(answer.Resources, AskPalette.Dark);
            answer.Show(ChatDocument.Build(ChatMarkdown.Parse(ChatDiagramFakes.Block()), new ChatRender { Diagrams = diagrams }));
            var buttons = ChatDocument.All<Button>(answer.Document);

            Assert.True(PadAnswerBoxTests.MouseReaches(answer, buttons.Single(b => Equals(b.Content, "Source"))));
            Assert.True(PadAnswerBoxTests.MouseReaches(answer, buttons.Single(b => Equals(b.Content, "Copy"))));
        });

        // ---- keeping up while an answer streams (AI chat UI spec 1.5) ------------------------------

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        /// <summary>Waits, with the dispatcher free, until no redraw of these turns is waiting for its timer.</summary>
        private static Task Redrawn(params AskTurnView[] turns) =>
            ChatDiagramFakes.Until(() => turns.All(turn => turn.PendingRedraw is null), "the redraw that waited");

        /// <summary>
        /// Ends a draw, and waits for the redraw each of these turns asked for: a turn that is told
        /// of a picture does not draw it then, its timer does.
        /// </summary>
        private static async Task FinishAndRedraw(ChatDiagrams diagrams, FakeRenderer renderer, int index, DiagramResult result, params AskTurnView[] turns)
        {
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, index, result);
            await Redrawn(turns);
        }

        /// <summary>
        /// A turn that shows one diagram, "being drawn" until the test says otherwise. The redraw
        /// it handed over is <c>Gets[0].WhenDone</c>: calling it is a picture arriving, as the
        /// adapter tells it (inline, on the UI thread).
        /// </summary>
        private static (AskTurnView Turn, FakeChatDiagrams Diagrams, Func<int> Builds) WaitingForAPicture(Func<bool> pointerHeld)
        {
            var diagrams = new FakeChatDiagrams();
            var turn = new AskTurnView("q") { Diagrams = diagrams, PointerHeld = pointerHeld };
            turn.AppendText(ChatDiagramFakes.Block());
            return (turn, diagrams, CountBuilds(turn));
        }

        [Fact]
        public void After_a_redraw_that_cost_300_ms_the_next_streamed_text_waits_1200_ms() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q") { PointerHeld = () => false };
            var clock = Stopwatch.StartNew();
            turn.AppendText("one");                               // the first text draws at once
            turn.LastRedrawCost = Ms(300);

            turn.AppendText(" two");

            Assert.Equal("one", Rendered(turn));                  // not at once, though the plain 100 ms may have passed
            RedrawWaits.AssertWaits(turn.PendingRedraw, Ms(1200), clock);
        });

        [Fact]
        public Task A_tick_while_the_pointer_is_held_draws_nothing_and_waits_the_plain_interval_and_the_next_tick_draws() => UiThread.RunAsync(async () =>
        {
            bool held = true;
            int asked = 0;
            var turn = new AskTurnView("q") { RenderInterval = Ms(20), PointerHeld = () => { asked++; return held; } };
            turn.AppendText("one");
            turn.LastRedrawCost = Ms(15);                         // the next redraw is due 60 ms after this one
            turn.AppendText(" two");

            await ChatDiagramFakes.Until(() => asked > 0, "the redraw timer");

            Assert.Equal("one", Rendered(turn));                  // a click that began on a button in it is not lost
            Assert.Equal(Ms(20), turn.PendingRedraw);             // the timer runs again: for the plain interval, not the paced one

            held = false;
            await Redrawn(turn);
            Assert.Equal("one two", Rendered(turn));
        });

        [Fact]
        public Task Text_that_is_due_at_once_is_not_drawn_while_the_pointer_is_held() => UiThread.RunAsync(async () =>
        {
            bool held = true;
            var turn = new AskTurnView("q") { RenderInterval = TimeSpan.Zero, PointerHeld = () => held };
            turn.AppendText("one");

            turn.AppendText(" two");                              // nothing to wait for but the pointer

            Assert.Equal("one", Rendered(turn));
            Assert.Equal(TimeSpan.Zero, turn.PendingRedraw);

            held = false;
            await Redrawn(turn);
            Assert.Equal("one two", Rendered(turn));
        });

        [Fact]
        public void The_first_text_draws_at_once_while_the_pointer_is_held_and_whatever_a_redraw_cost() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q") { RenderInterval = TimeSpan.FromSeconds(30), PointerHeld = () => true };
            turn.LastRedrawCost = TimeSpan.FromSeconds(1);

            turn.AppendText("one");

            Assert.Equal("one", Rendered(turn));
            Assert.Null(turn.PendingRedraw);
        });

        [Fact]
        public void The_end_of_the_answer_draws_at_once_while_the_pointer_is_held_and_whatever_the_last_redraw_cost() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q") { RenderInterval = TimeSpan.FromSeconds(30), PointerHeld = () => true };
            turn.AppendText("one");
            turn.LastRedrawCost = TimeSpan.FromSeconds(1);
            turn.AppendText(" two");                              // a redraw of the stream: it waits
            Assert.NotNull(turn.PendingRedraw);

            turn.Complete(DateTime.Now, TimeSpan.Zero);

            Assert.Equal("one two", Rendered(turn));
            Assert.Null(turn.PendingRedraw);
            Assert.Equal(Visibility.Visible, turn.Footer.Visibility);
        });

        /// <summary>A turn that shows a link and a diagram's picture, after a costly redraw and under a held pointer.</summary>
        private static (AskTurnView Turn, FakeChatDiagrams Diagrams, Func<int> Builds) HeldTurnWithALinkAndAPicture()
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var turn = new AskTurnView("q") { Diagrams = diagrams, RenderInterval = TimeSpan.FromSeconds(30), PointerHeld = () => true };
            turn.AppendText("[site](https://example.com/a)\n\n" + ChatDiagramFakes.Block());
            turn.LastRedrawCost = TimeSpan.FromSeconds(1);
            return (turn, diagrams, CountBuilds(turn));
        }

        [Fact]
        public void A_theme_change_draws_at_once_while_the_pointer_is_held_and_whatever_the_last_redraw_cost() => UiThread.Run(() =>
        {
            var (turn, diagrams, builds) = HeldTurnWithALinkAndAPicture();

            turn.ApplyTheme(false);

            Assert.Equal(1, builds());
            Assert.False(diagrams.Gets[^1].Dark);                 // the picture is asked for in the new theme, now
            Assert.Null(turn.PendingRedraw);
        });

        [Fact]
        public void Links_are_turned_into_text_at_once_while_the_pointer_is_held_and_whatever_the_last_redraw_cost() => UiThread.Run(() =>
        {
            var (turn, _, builds) = HeldTurnWithALinkAndAPicture();
            Assert.Single(ChatDocument.All<Hyperlink>(turn.Answer.Document));

            turn.ShowLinksAsText();                               // the conversation read notes: a link on screen goes, now

            Assert.Equal(1, builds());
            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));
            Assert.Null(turn.PendingRedraw);
        });

        /// <summary>
        /// Text that streamed before a note tool ran may hold a link, and from that moment note
        /// text may have steered the answer. The link must not stay clickable until the next
        /// redraw of the stream, nor for as long as the mouse button is held over the answer:
        /// that held button is the click this rule exists to stop. No dispatcher pass is needed.
        /// </summary>
        [Theory]
        [InlineData("search_notes")]
        [InlineData("get_note")]
        public void A_note_tool_used_mid_answer_leaves_no_link_on_screen_at_once_while_the_pointer_is_held_and_whatever_the_last_redraw_cost(string tool) => UiThread.Run(() =>
        {
            var (turn, _, builds) = HeldTurnWithALinkAndAPicture();
            turn.AppendText("\n\nAnd [more](https://example.com/b)");   // a redraw of the stream: it waits for its timer
            Assert.NotNull(turn.PendingRedraw);
            Assert.Single(ChatDocument.All<Hyperlink>(turn.Answer.Document));

            turn.AddTool(tool, "{\"query\":\"vpn\"}");

            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));
            Assert.Equal(1, builds());                            // rendered then and there, with the text that was waiting
            Assert.Contains("https://example.com/b", Rendered(turn), StringComparison.Ordinal);
            Assert.Null(turn.PendingRedraw);                      // and no redraw is left to wait for
        });

        /// <summary>
        /// Defence in depth beside <see cref="AskTurnView.ShowLinksAsText"/>, which renders by
        /// itself. Whoever only sets <see cref="AskTurnView.PlainLinks"/> and then asks for a
        /// redraw the way the stream does must not leave a link clickable while that redraw waits
        /// for its pace or for the pointer: what is on screen was built for the other value.
        /// </summary>
        [Theory]
        [InlineData("text")]       // more of the answer streams in
        [InlineData("picture")]    // a diagram's picture arrives, or Try again is pressed
        public void A_redraw_asked_for_after_PlainLinks_changed_is_made_at_once_whatever_it_would_wait_for(string asked) => UiThread.Run(() =>
        {
            var (turn, diagrams, builds) = HeldTurnWithALinkAndAPicture();
            turn.PlainLinks = true;                               // set directly: nothing renders for that
            Assert.Single(ChatDocument.All<Hyperlink>(turn.Answer.Document));
            Assert.Equal(0, builds());

            if (asked == "text") turn.AppendText("\n\nMore.");
            else diagrams.Gets[^1].WhenDone!();

            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));   // then and there: no dispatcher pass, no timer
            Assert.Equal(1, builds());
            Assert.Null(turn.PendingRedraw);
        });

        [Fact]
        public Task A_redraw_that_waits_under_a_held_pointer_is_made_at_its_next_tick_once_PlainLinks_changed() => UiThread.RunAsync(async () =>
        {
            var (turn, _, builds) = HeldTurnWithALinkAndAPicture();   // the pointer stays held to the end
            turn.RenderInterval = Ms(20);
            turn.LastRedrawCost = TimeSpan.Zero;
            turn.AppendText("\n\nMore.");                         // waits, and under the held pointer would wait again at every tick
            Assert.NotNull(turn.PendingRedraw);

            turn.PlainLinks = true;
            await ChatDiagramFakes.Until(() => builds() > 0, "the tick that finds the links out of date");

            Assert.Empty(ChatDocument.All<Hyperlink>(turn.Answer.Document));
            Assert.Null(turn.PendingRedraw);
        });

        [Fact]
        public void A_PointerHeld_that_throws_counts_as_not_held_and_is_reported_once_by_its_type() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var turn = new AskTurnView("q")
            {
                RenderInterval = TimeSpan.Zero,
                Warn = warnings.Add,
                PointerHeld = () => throw new InvalidOperationException("the answer says hunter2"),
            };

            turn.AppendText("one");
            turn.AppendText(" two");
            turn.AppendText(" three");

            Assert.Equal("one two three", Rendered(turn));        // each drawn at once: nothing held it back
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
        });

        [Fact]
        public void A_redraw_stores_what_it_cost_once_the_dispatcher_reaches_Loaded() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");

            turn.AppendText("| a | b |\n|---|---|\n| 1 | 2 |");

            Assert.Equal(TimeSpan.Zero, turn.LastRedrawCost);     // not when the build returns: its layout is still to come
            RedrawWaits.ToLoaded();
            Assert.True(turn.LastRedrawCost > TimeSpan.Zero, "the cost of the redraw is stored");
        });

        [Fact]
        public void A_turn_released_before_its_redraw_was_measured_stores_no_cost() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("text");

            turn.Release();
            RedrawWaits.ToLoaded();

            Assert.Equal(TimeSpan.Zero, turn.LastRedrawCost);
        });

        [Fact]
        public void A_render_of_a_released_turn_stores_no_cost() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("text");
            turn.LastRedrawCost = TimeSpan.FromHours(1);          // nothing is waited for now, and this value is no cost a redraw has
            turn.Release();

            turn.Complete(DateTime.Now, TimeSpan.Zero);           // a stream that was cancelled still ends its turn, and renders it
            RedrawWaits.ToLoaded();

            Assert.Equal("text", Rendered(turn));
            Assert.Equal(TimeSpan.FromHours(1), turn.LastRedrawCost);
        });

        [Fact]
        public Task A_picture_that_arrives_is_drawn_by_the_timer_and_several_arrivals_give_one_redraw() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, builds) = WaitingForAPicture(() => false);
            Action arrive = diagrams.Gets[0].WhenDone!;

            arrive();                                             // as the adapter tells its waiters: inline, several in a row
            arrive();
            arrive();

            Assert.Equal(0, builds());                            // not inside whatever ended the draw
            Assert.NotNull(turn.PendingRedraw);
            await Redrawn(turn);
            Assert.Equal(1, builds());
            Assert.Same(arrive, diagrams.Gets[^1].WhenDone);      // the same delegate at every build: the adapter tells a turn once

            await Task.Delay(60);                                 // and nothing more by itself
            Assert.Equal(1, builds());
            Assert.Null(turn.PendingRedraw);
        });

        [Fact]
        public void A_picture_that_arrives_is_drawn_no_sooner_than_the_last_redraw_allows() => UiThread.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var (turn, diagrams, builds) = WaitingForAPicture(() => false);
            turn.LastRedrawCost = Ms(300);

            diagrams.Gets[0].WhenDone!();

            Assert.Equal(0, builds());
            RedrawWaits.AssertWaits(turn.PendingRedraw, Ms(1200), clock);
        });

        [Fact]
        public Task A_picture_that_arrives_waits_while_the_pointer_is_held() => UiThread.RunAsync(async () =>
        {
            bool held = true;
            int asked = 0;
            var (turn, diagrams, builds) = WaitingForAPicture(() => { asked++; return held; });
            turn.RenderInterval = Ms(20);

            diagrams.Gets[0].WhenDone!();
            await ChatDiagramFakes.Until(() => asked > 0, "the redraw timer");

            Assert.Equal(0, builds());
            Assert.Equal(Ms(20), turn.PendingRedraw);

            held = false;
            await Redrawn(turn);
            Assert.Equal(1, builds());
        });

        [Fact]
        public Task A_picture_that_arrives_after_the_answer_ended_is_drawn_without_any_new_text() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, builds) = WaitingForAPicture(() => false);
            turn.Complete(DateTime.Now, TimeSpan.Zero);           // no other redraw is due any more
            int before = builds();
            diagrams.Answer = (_, _) => ChatDiagramFakes.Drawn();

            diagrams.Gets[0].WhenDone!();

            Assert.Equal(before, builds());
            await Redrawn(turn);
            Assert.Equal(before + 1, builds());
            Assert.NotNull(Picture(turn));
        });

        [Fact]
        public Task Release_stops_a_redraw_that_waits_and_nothing_starts_one_for_a_released_turn() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, builds) = WaitingForAPicture(() => true);   // held: the redraw would wait, and wait again
            turn.RenderInterval = Ms(10);
            Action arrive = diagrams.Gets[0].WhenDone!;
            arrive();
            Assert.NotNull(turn.PendingRedraw);

            turn.Release();
            Assert.Null(turn.PendingRedraw);                      // the timer is stopped

            turn.AppendText(" more");                             // text a cancelled stream still hands over
            Assert.Null(turn.PendingRedraw);
            arrive();                                             // a picture that arrives late
            Assert.Null(turn.PendingRedraw);

            await Task.Delay(60);
            Assert.Equal(0, builds());
            Assert.Null(turn.PendingRedraw);                      // and the held pointer did not start it again
        });

        [Fact]
        public Task Try_again_under_a_held_pointer_is_drawn_when_the_button_is_released_and_needs_no_new_text() => UiThread.RunAsync(async () =>
        {
            bool held = true;
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Failed("It took too long", canRetry: true) };
            var turn = new AskTurnView("q") { Diagrams = diagrams, RenderInterval = Ms(20), PointerHeld = () => held };
            turn.AppendText(ChatDiagramFakes.Block());
            turn.Complete(DateTime.Now, TimeSpan.Zero);
            var builds = CountBuilds(turn);
            int asked = diagrams.Gets.Count;

            RaiseClick(TryAgain(turn)!);                          // the click itself comes with the pointer over the answer

            Assert.Single(diagrams.Forgotten);
            Assert.Equal(0, builds());
            Assert.NotNull(turn.PendingRedraw);                   // asked for, though the text is the same: the timer will draw it
            await Task.Delay(80);                                 // some ticks, all under the held button
            Assert.Equal(0, builds());
            Assert.NotNull(turn.PendingRedraw);

            held = false;                                         // the button is released
            await Redrawn(turn);
            Assert.Equal(1, builds());                            // the same text, built again
            Assert.Equal(asked + 1, diagrams.Gets.Count);         // and that build asks for the diagram, which starts its draw
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference TurnWithARedrawWaiting()
        {
            var diagrams = new FakeChatDiagrams();
            var turn = new AskTurnView("q") { Diagrams = diagrams, PointerHeld = () => false };
            turn.AppendText(ChatDiagramFakes.Block());
            turn.LastRedrawCost = Ms(500);                        // the next redraw waits two seconds: its timer runs through the collections
            turn.Complete(DateTime.Now, TimeSpan.Zero);           // a redraw whose cost is not stored yet
            diagrams.Gets[0].WhenDone!();
            Assert.NotNull(turn.PendingRedraw);
            return new WeakReference(turn);
        }

        [Fact]
        public void A_redraw_that_waits_and_a_cost_not_yet_stored_do_not_keep_their_turn_alive() => UiThread.Run(() =>
        {
            WeakReference turn = TurnWithARedrawWaiting();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(turn.IsAlive);   // neither the timer that runs nor the callback that waits holds the turn
        });
    }
}
