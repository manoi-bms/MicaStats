using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Format menu's edits: wrapping, links, line prefixes and code blocks, each toggling where it makes sense.</summary>
    public class MarkdownFormatterTests
    {
        private static string Apply(string text, TextEdit edit) => text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Text);

        private static string Selected(string text, TextEdit edit) => Apply(text, edit).Substring(edit.SelectionStart, edit.SelectionLength);

        [Fact]
        public void Wrap_surrounds_the_selection_and_keeps_it_selected()
        {
            var edit = MarkdownFormatter.Wrap("hello world", 0, 5, "**");
            Assert.Equal("**hello** world", Apply("hello world", edit));
            Assert.Equal("hello", Selected("hello world", edit));
        }

        [Fact]
        public void Wrap_with_nothing_selected_puts_the_caret_between_the_markers()
        {
            var edit = MarkdownFormatter.Wrap("ab", 1, 0, "`");
            Assert.Equal("a``b", Apply("ab", edit));
            Assert.Equal(2, edit.SelectionStart);
            Assert.Equal(0, edit.SelectionLength);
        }

        [Fact]
        public void Wrap_again_unwraps_a_selection_that_includes_the_markers()
        {
            var edit = MarkdownFormatter.Wrap("**hello** x", 0, 9, "**");
            Assert.Equal("hello x", Apply("**hello** x", edit));
            Assert.Equal("hello", Selected("**hello** x", edit));
        }

        [Fact]
        public void Wrap_again_unwraps_markers_just_outside_the_selection()
        {
            var edit = MarkdownFormatter.Wrap("**hello** x", 2, 5, "**");
            Assert.Equal("hello x", Apply("**hello** x", edit));
            Assert.Equal("hello", Selected("**hello** x", edit));
        }

        [Theory]
        [InlineData("line one\nline two", 9, "**line one**\nline two", "line one")]          // Home, Shift+Down
        [InlineData("line one\r\nline two", 10, "**line one**\r\nline two", "line one")]
        [InlineData("line one\r\n", 10, "**line one**\r\n", "line one")]
        [InlineData("one\n\ntwo", 5, "**one**\n\ntwo", "one")]                              // a line and an empty line
        public void Wrap_leaves_a_trailing_line_break_outside(string text, int length, string expected, string selected)
        {
            var edit = MarkdownFormatter.Wrap(text, 0, length, "**");
            Assert.Equal(expected, Apply(text, edit));
            Assert.Equal(selected, Selected(text, edit));
        }

        [Theory]
        [InlineData("one\ntwo", 7, "**one**\n**two**")]
        [InlineData("one\r\ntwo\r\nthree", 10, "**one**\r\n**two**\r\nthree")]
        [InlineData("one\n\ntwo", 8, "**one**\n\n**two**")]
        [InlineData("one\n  \ntwo", 10, "**one**\n  \n**two**")]
        [InlineData("  one\n  two", 11, "  **one**\n  **two**")]
        [InlineData("**one**\ntwo", 11, "**one**\n**two**")]
        public void Wrap_across_lines_wraps_each_line_on_its_own(string text, int length, string expected)
        {
            var edit = MarkdownFormatter.Wrap(text, 0, length, "**");
            Assert.Equal(expected, Apply(text, edit));
            string block = expected.Substring(0, expected.Length - (text.Length - length)).TrimEnd('\r', '\n');
            Assert.Equal(block, Selected(text, edit));
        }

        [Fact]
        public void Wrap_across_lines_takes_just_the_selected_part_of_each_line()
        {
            string text = "hello world\nnext line";
            var edit = MarkdownFormatter.Wrap(text, 6, 10, "`");   // "world\nnext"
            Assert.Equal("hello `world`\n`next` line", Apply(text, edit));
            Assert.Equal("`world`\n`next`", Selected(text, edit));
        }

        [Theory]
        [InlineData("**one**\n**two**\n", "one\ntwo\n")]
        [InlineData("**one**\r\n\r\n**two**", "one\r\n\r\ntwo")]
        [InlineData("~~one~~\n~~two~~", "one\ntwo")]
        public void Wrap_across_lines_again_unwraps_each_line(string text, string expected)
        {
            string marker = text.StartsWith("~", System.StringComparison.Ordinal) ? "~~" : "**";
            var edit = MarkdownFormatter.Wrap(text, 0, text.Length, marker);
            Assert.Equal(expected, Apply(text, edit));
            Assert.Equal(expected.TrimEnd('\r', '\n'), Selected(text, edit));
        }

        [Fact]
        public void Italic_across_bold_lines_adds_italics_to_each()
        {
            string text = "**one**\n**two**";
            var edit = MarkdownFormatter.Wrap(text, 0, text.Length, "*");
            Assert.Equal("***one***\n***two***", Apply(text, edit));
        }

        [Fact]
        public void Link_wraps_the_text_and_selects_the_address()
        {
            var edit = MarkdownFormatter.Link("see docs", 4, 4);
            Assert.Equal("see [docs](url)", Apply("see docs", edit));
            Assert.Equal("url", Selected("see docs", edit));

            var empty = MarkdownFormatter.Link("", 0, 0);
            Assert.Equal("[](url)", Apply("", empty));
            Assert.Equal("url", Selected("", empty));
        }

        [Theory]
        [InlineData("a\nb", LinePrefix.Bullet, "- a\n- b")]
        [InlineData("- a\n- b", LinePrefix.Bullet, "a\nb")]
        [InlineData("x\n\ny", LinePrefix.Numbered, "1. x\n\n2. y")]
        [InlineData("- x\n- y", LinePrefix.Numbered, "1. x\n2. y")]
        [InlineData("1. x\n2. y", LinePrefix.Numbered, "x\ny")]
        [InlineData("buy milk", LinePrefix.Task, "- [ ] buy milk")]
        [InlineData("- [x] done", LinePrefix.Task, "done")]
        [InlineData("- item", LinePrefix.Task, "- [ ] item")]
        [InlineData("# Title", LinePrefix.Heading2, "## Title")]
        [InlineData("## Title", LinePrefix.Heading2, "Title")]
        [InlineData("Title", LinePrefix.Heading1, "# Title")]
        [InlineData("a", LinePrefix.Quote, "> a")]
        [InlineData("> a", LinePrefix.Quote, "a")]
        [InlineData("", LinePrefix.Bullet, "- ")]
        [InlineData("  a", LinePrefix.Bullet, "  - a")]
        public void Prefix_toggles_on_every_selected_line(string text, LinePrefix kind, string expected)
        {
            var edit = MarkdownFormatter.Prefix(text, 0, text.Length, kind);
            Assert.Equal(expected, Apply(text, edit));
            Assert.Equal(expected, Selected(text, edit));
        }

        [Fact]
        public void Prefix_takes_the_whole_line_under_a_caret()
        {
            string text = "first\nsecond\nthird";
            var edit = MarkdownFormatter.Prefix(text, 9, 0, LinePrefix.Bullet);   // caret inside "second"
            Assert.Equal("first\n- second\nthird", Apply(text, edit));
        }

        [Fact]
        public void Prefix_keeps_crlf_and_ignores_a_line_the_selection_only_touches()
        {
            string text = "one\r\ntwo\r\nthree";
            var edit = MarkdownFormatter.Prefix(text, 0, "one\r\ntwo\r\n".Length, LinePrefix.Bullet);
            Assert.Equal("- one\r\n- two\r\nthree", Apply(text, edit));
        }

        [Fact]
        public void Prefix_keeps_mixed_line_endings_as_they_are()
        {
            string text = "a\r\nb\nc";
            Assert.Equal("- a\r\n- b\n- c", Apply(text, MarkdownFormatter.Prefix(text, 0, text.Length, LinePrefix.Bullet)));
        }

        [Fact]
        public void Prefix_quote_does_not_nest_on_lines_that_already_have_quotes()
        {
            string text = "> a\nb";
            var edit = MarkdownFormatter.Prefix(text, 0, text.Length, LinePrefix.Quote);
            Assert.Equal("> a\n> b", Apply(text, edit));
        }

        [Fact]
        public void Prefix_quote_removes_when_all_lines_have_it()
        {
            string text = "> a\n> b";
            var edit = MarkdownFormatter.Prefix(text, 0, text.Length, LinePrefix.Quote);
            Assert.Equal("a\nb", Apply(text, edit));
        }

        [Fact]
        public void Wrap_italic_on_italic_unwraps()
        {
            var edit = MarkdownFormatter.Wrap("*hello*", 0, 7, "*");
            Assert.Equal("hello", Apply("*hello*", edit));
        }

        [Fact]
        public void Wrap_italic_on_bold_adds_italics()
        {
            var edit = MarkdownFormatter.Wrap("**hello**", 0, 9, "*");
            Assert.Equal("***hello***", Apply("**hello**", edit));
        }

        [Fact]
        public void Wrap_italic_on_hello_inside_bold_adds_italics()
        {
            var edit = MarkdownFormatter.Wrap("**hello**", 2, 5, "*");
            Assert.Equal("***hello***", Apply("**hello**", edit));
        }

        [Fact]
        public void Code_block_fences_the_selected_lines_with_the_notes_line_ending()
        {
            string crlf = "a\r\nb";
            var edit = MarkdownFormatter.CodeBlock(crlf, 0, crlf.Length);
            Assert.Equal("```\r\na\r\nb\r\n```", Apply(crlf, edit));
            Assert.Equal("a\r\nb", Selected(crlf, edit));

            string lf = "x\ny";
            Assert.Equal("```\nx\n```\ny", Apply(lf, MarkdownFormatter.CodeBlock(lf, 0, 1)));

            Assert.Equal("```\r\nsolo\r\n```", Apply("solo", MarkdownFormatter.CodeBlock("solo", 0, 0)));
        }
    }
}
