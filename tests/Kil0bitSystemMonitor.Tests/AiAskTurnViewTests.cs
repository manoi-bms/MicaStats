using System;
using System.Collections.Generic;
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
            ended.Complete(DateTime.Now);
            Assert.Equal(Visibility.Collapsed, ended.Typing.Visibility);
            Assert.Equal(Visibility.Collapsed, ended.Footer.Visibility);
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

            turn.Complete(DateTime.Now);
            Assert.Equal("first and more", Rendered(turn));
            Assert.Equal(new Thickness(0), turn.Answer.Document.PagePadding);   // a document handed to a templated box too
        });

        [Fact]
        public void The_footer_shows_the_finish_time_in_24_hour_digits_whatever_the_culture() => UiThread.Run(() =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                var turn = new AskTurnView("q");
                turn.AppendText("Done.");
                turn.Complete(new DateTime(2026, 10, 1, 14, 5, 0));

                Assert.Equal(Visibility.Visible, turn.Footer.Visibility);
                Assert.Equal("14:05", turn.TimeText.Text);
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
            turn.Complete(DateTime.Now);

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
            turn.Complete(DateTime.Now);             // renders again: no second report

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
            plain.Complete(DateTime.Now);
            Assert.Empty(ChatDocument.All<Hyperlink>(plain.Answer.Document));
            Assert.Contains("https://example.com/a", Rendered(plain), StringComparison.Ordinal);

            var open = new AskTurnView("q");
            open.AppendText(table);
            open.Complete(DateTime.Now);
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

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture(width: 100, height: 50));

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
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            var first = Picture(turn)!.Source;

            for (int i = 0; i < 25; i++)
            {
                turn.AppendText("more ");
                // Every rebuild finds the picture already there: the same bitmap, never "Drawing…" again.
                Assert.Same(first, Picture(turn)!.Source);
                Assert.DoesNotContain("Drawing the diagram…", Lines(turn));
            }
            turn.Complete(DateTime.Now);

            Assert.Same(first, Picture(turn)!.Source);
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public Task A_theme_change_draws_the_turn_again_in_the_new_theme_and_a_theme_it_already_has_draws_nothing() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block());
            Assert.True(renderer.Calls[0].Request.Dark);   // a turn is dark until it is told
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            var dark = Picture(turn)!.Source;

            turn.ApplyTheme(false);

            Assert.Equal(2, renderer.Calls.Count);
            Assert.False(renderer.Calls[1].Request.Dark);
            Assert.Null(Picture(turn));   // the dark picture is not shown on a light answer
            Assert.Contains("Drawing the diagram…", Lines(turn));

            turn.ApplyTheme(false);
            Assert.Equal(2, renderer.Calls.Count);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramFakes.Picture());
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
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramResult.Failure(DiagramText.TookTooLong, lasting: false));

            Assert.Equal(2, builds());   // one redraw, for the end of the draw
            Assert.Contains("This diagram could not be drawn: " + DiagramText.TookTooLong, Lines(turn));
            Assert.Null(Picture(turn));

            await Task.Delay(150);       // left alone, it does not go round again
            Assert.Equal(2, builds());
            Assert.Single(renderer.Calls);

            for (int i = 0; i < 50; i++) turn.AppendText("more ");
            turn.ApplyTheme(false);
            turn.ApplyTheme(true);
            turn.Complete(DateTime.Now);

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

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramFakes.Picture());
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
            turn.Complete(DateTime.Now);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, passing);
            Assert.Contains("This diagram could not be drawn: " + DiagramText.TookTooLong, Lines(turn));
            var builds = CountBuilds(turn);

            RaiseClick(TryAgain(turn)!);

            Assert.Equal(2, renderer.Calls.Count);                   // one press, one draw
            Assert.Equal(1, builds());
            Assert.Contains("Drawing the diagram…", Lines(turn));    // the finished answer drew itself again
            Assert.Null(TryAgain(turn));

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, passing);

            Assert.NotNull(TryAgain(turn));                          // it failed again: the button is back
            Assert.Equal(2, builds());
            await Task.Delay(150);                                   // and left alone, nothing is drawn
            Assert.Equal(2, renderer.Calls.Count);
            Assert.Equal(2, builds());

            RaiseClick(TryAgain(turn)!);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 2, DiagramFakes.Picture());

            Assert.Equal(3, renderer.Calls.Count);
            Assert.NotNull(Picture(turn));                           // the engine was up this time
            Assert.Null(TryAgain(turn));
        });

        [Fact]
        public Task A_failure_that_would_come_again_has_no_Try_again_in_a_turn() => UiThread.RunAsync(async () =>
        {
            var (turn, diagrams, renderer) = DiagramTurn();
            turn.AppendText(ChatDiagramFakes.Block());

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramResult.Failure("Parse error on line 2", lasting: true));

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

            Assert.Equal(0, builds());
            Assert.Same(shown, turn.Answer.Document);

            turn.ApplyTheme(false);
            Assert.Equal(0, builds());

            // A stream that was cancelled may still end the turn: it renders as text and code, and draws nothing.
            turn.AppendText("\n\n" + ChatDiagramFakes.Block("pie\n  \"a\" : 1"));
            turn.Complete(DateTime.Now);
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

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            Assert.NotNull(Picture(one));
            Assert.NotNull(Picture(two));    // and both turns are told

            RaiseClick(ChatDocument.All<Button>(one.Answer.Document).Single(b => Equals(b.Content, "Source")));
            Assert.Equal(Visibility.Collapsed, Picture(one)!.Visibility);

            one.AppendText("more");          // a new document, with new buttons
            two.AppendText("more");

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

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramResult.Failure("No diagram: see [help](https://evil.example/h)", lasting: true));

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
    }
}
