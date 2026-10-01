using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using Kil0bitSystemMonitor.Ai;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

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
    }
}
