using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Markdown line model: which runs of a line get which style, and which lines are fenced.</summary>
    public class MarkdownTokenizerTests
    {
        private static List<(string Text, MdStyle Style)> Runs(string line, MdFence fence = MdFence.None) =>
            MarkdownLineTokenizer.Tokenize(line, fence).Spans.Select(s => (line.Substring(s.Start, s.Length), s.Style)).ToList();

        private static List<string> TextsOf(string line, MdStyle style) =>
            Runs(line).Where(r => r.Style == style).Select(r => r.Text).ToList();

        [Theory]
        [InlineData("# Title", MdStyle.Heading1)]
        [InlineData("## Two", MdStyle.Heading2)]
        [InlineData("   ###### Six", MdStyle.Heading6)]
        [InlineData("#", MdStyle.Heading1)]
        public void Headings_style_the_whole_line_and_dim_the_hashes(string line, MdStyle expected)
        {
            var result = MarkdownLineTokenizer.Tokenize(line, MdFence.None);
            Assert.Equal(MdBlock.Heading, result.Block);
            Assert.Equal((line, expected), Runs(line)[0]);
            Assert.Contains(Runs(line), r => r.Style == MdStyle.Marker && r.Text.Trim('#').Length == 0);
        }

        [Theory]
        [InlineData("####### seven")]
        [InlineData("#nospace")]
        [InlineData("    # indented four")]
        public void Not_every_hash_is_a_heading(string line)
        {
            Assert.NotEqual(MdBlock.Heading, MarkdownLineTokenizer.Tokenize(line, MdFence.None).Block);
        }

        [Theory]
        [InlineData("a **bold** b", "bold", MdStyle.Bold)]
        [InlineData("__bold__", "bold", MdStyle.Bold)]
        [InlineData("an *it* here", "it", MdStyle.Italic)]
        [InlineData("_it_", "it", MdStyle.Italic)]
        [InlineData("***both***", "both", MdStyle.BoldItalic)]
        [InlineData("~~gone~~", "gone", MdStyle.Strike)]
        [InlineData("use `co*de*` here", "co*de*", MdStyle.Code)]
        [InlineData("``a`b``", "a`b", MdStyle.Code)]
        [InlineData("**สวัสดี** ครับ", "สวัสดี", MdStyle.Bold)]
        public void Inline_styles_cover_the_text_between_markers(string line, string text, MdStyle style)
        {
            Assert.Contains((text, style), Runs(line));
        }

        [Fact]
        public void Markers_are_dimmed_not_hidden()
        {
            var runs = Runs("a **bold** b");
            Assert.Equal(2, runs.Count(r => r.Text == "**" && r.Style == MdStyle.Marker));
        }

        [Theory]
        [InlineData("snake_case_name")]
        [InlineData("a * b * c")]
        [InlineData("**unclosed")]
        [InlineData("~one~")]
        [InlineData(@"\*not italic\*")]
        [InlineData("2 * 3 * 4")]
        public void Some_marker_characters_are_just_text(string line)
        {
            Assert.DoesNotContain(Runs(line), r => r.Style is MdStyle.Bold or MdStyle.Italic or MdStyle.BoldItalic or MdStyle.Strike);
        }

        [Fact]
        public void Code_hides_emphasis_inside_it()
        {
            Assert.Empty(TextsOf("use `co*de*` here", MdStyle.Italic));
        }

        [Fact]
        public void Emphasis_nests()
        {
            Assert.Equal(new[] { "bold _it_ bold" }, TextsOf("**bold _it_ bold**", MdStyle.Bold));
            Assert.Equal(new[] { "it" }, TextsOf("**bold _it_ bold**", MdStyle.Italic));

            Assert.Equal(new[] { "a **b** c" }, TextsOf("*a **b** c*", MdStyle.Italic));
            Assert.Equal(new[] { "b" }, TextsOf("*a **b** c*", MdStyle.Bold));

            Assert.Equal(new[] { "bold *italic*" }, TextsOf("**bold *italic***", MdStyle.Bold));
            Assert.Equal(new[] { "italic" }, TextsOf("**bold *italic***", MdStyle.Italic));
        }

        [Fact]
        public void Links_color_the_text_and_dim_the_address()
        {
            var runs = Runs("see [docs](https://x.y/a_b*c) now");
            Assert.Contains(("[", MdStyle.Marker), runs);
            Assert.Contains(("docs", MdStyle.LinkText), runs);
            Assert.Contains(("](https://x.y/a_b*c)", MdStyle.Marker), runs);
            Assert.DoesNotContain(runs, r => r.Style is MdStyle.Italic or MdStyle.Bold);
        }

        [Fact]
        public void Bullets_numbers_and_tasks()
        {
            var bullet = MarkdownLineTokenizer.Tokenize("  - item", MdFence.None);
            Assert.Equal(MdBlock.Bullet, bullet.Block);
            Assert.Contains(("-", MdStyle.ListMarker), Runs("  - item"));
            Assert.Equal(2, MarkdownLineTokenizer.BulletOffset("  - item"));
            Assert.Equal(0, MarkdownLineTokenizer.BulletOffset("* item"));

            Assert.Equal(MdBlock.Numbered, MarkdownLineTokenizer.Tokenize("12. twelfth", MdFence.None).Block);
            Assert.Contains(("12.", MdStyle.ListMarker), Runs("12. twelfth"));
            Assert.Equal(-1, MarkdownLineTokenizer.BulletOffset("12. twelfth"));

            Assert.Equal(MdBlock.Task, MarkdownLineTokenizer.Tokenize("- [ ] todo", MdFence.None).Block);
            Assert.Contains(("[ ]", MdStyle.Marker), Runs("- [ ] todo"));
            Assert.DoesNotContain(Runs("- [ ] todo"), r => r.Style == MdStyle.TaskDone);
            Assert.Contains(("done", MdStyle.TaskDone), Runs("- [x] done"));
            Assert.Equal(0, MarkdownLineTokenizer.BulletOffset("- [x] done"));
        }

        [Fact]
        public void Quotes_mute_the_text_and_keep_inline_styles()
        {
            var runs = Runs("> quote **b**");
            Assert.Equal(MdBlock.Quote, MarkdownLineTokenizer.Tokenize("> quote **b**", MdFence.None).Block);
            Assert.Contains((">", MdStyle.Marker), runs);
            Assert.Contains(("quote **b**", MdStyle.QuoteText), runs);
            Assert.Contains(("b", MdStyle.Bold), runs);
        }

        [Theory]
        [InlineData("---")]
        [InlineData("* * *")]
        [InlineData("___")]
        [InlineData("  - - -  ")]
        public void Rules_are_not_bullets(string line)
        {
            Assert.Equal(MdBlock.Rule, MarkdownLineTokenizer.Tokenize(line, MdFence.None).Block);
            Assert.Equal(-1, MarkdownLineTokenizer.BulletOffset(line));
        }

        [Fact]
        public void Fenced_lines_are_code_and_nothing_else()
        {
            Assert.Equal(new[] { ("# not a heading **x**", MdStyle.CodeBlock) }, Runs("# not a heading **x**", MdFence.Inside));
            Assert.Equal(new[] { ("```cs", MdStyle.Marker) }, Runs("```cs", MdFence.Delimiter));
            Assert.Equal(MdBlock.Fence, MarkdownLineTokenizer.BlockOf("anything", MdFence.Inside));
        }

        [Fact]
        public void A_very_long_line_gets_no_inline_formatting()
        {
            string line = "**x** " + new string('a', MarkdownLineTokenizer.MaxInlineLength);
            Assert.Empty(TextsOf(line, MdStyle.Bold));

            string heading = "# " + new string('a', MarkdownLineTokenizer.MaxInlineLength);
            Assert.Equal(MdBlock.Heading, MarkdownLineTokenizer.Tokenize(heading, MdFence.None).Block);
        }

        [Fact]
        public void Empty_and_plain_lines_have_no_spans()
        {
            Assert.Empty(MarkdownLineTokenizer.Tokenize("", MdFence.None).Spans);
            Assert.Empty(MarkdownLineTokenizer.Tokenize("just words", MdFence.None).Spans);
        }

        [Fact]
        public void Fences_open_close_and_contain()
        {
            var kinds = FenceTracker.Classify(new[] { "text", "```cs", "code", "```", "after" });
            Assert.Equal(new[] { MdFence.None, MdFence.Delimiter, MdFence.Inside, MdFence.Delimiter, MdFence.None }, kinds);
        }

        [Fact]
        public void A_fence_closes_only_with_its_own_kind_and_length()
        {
            Assert.Equal(new[] { MdFence.Delimiter, MdFence.Inside, MdFence.Inside, MdFence.Delimiter },
                         FenceTracker.Classify(new[] { "````", "```", "~~~~", "`````" }));
            Assert.Equal(new[] { MdFence.Delimiter, MdFence.Inside, MdFence.Delimiter },
                         FenceTracker.Classify(new[] { "~~~", "x", "~~~~" }));
        }

        [Fact]
        public void An_unclosed_fence_runs_to_the_end()
        {
            Assert.Equal(new[] { MdFence.None, MdFence.Delimiter, MdFence.Inside, MdFence.Inside },
                         FenceTracker.Classify(new[] { "a", "```", "b", "c" }));
        }

        [Theory]
        [InlineData("```a`b")]
        [InlineData("    ```")]
        [InlineData("``")]
        public void Not_every_backtick_line_opens_a_fence(string line)
        {
            Assert.Equal(new[] { MdFence.None, MdFence.None }, FenceTracker.Classify(new[] { line, "x" }));
        }

        [Fact]
        public void Heading_sizes_and_looks_follow_the_spec()
        {
            Assert.Equal(new[] { 1.6, 1.4, 1.25, 1.15, 1.05, 1.0 }, MarkdownStyles.HeadingSizes);

            var dark = PadPalette.Dark;
            var h2 = MarkdownStyles.LookOf(MdStyle.Heading2, dark);
            Assert.Equal(1.4, h2.SizeFactor);
            Assert.Equal(MdWeight.SemiBold, h2.Weight);
            Assert.Equal(dark.MdHeading, h2.Foreground);

            Assert.Equal(dark.MdMarker, MarkdownStyles.LookOf(MdStyle.Marker, dark).Foreground);
            Assert.Equal(dark.MdCodeBackground, MarkdownStyles.LookOf(MdStyle.Code, dark).Background);
            Assert.True(MarkdownStyles.LookOf(MdStyle.TaskDone, dark).Strike);
            Assert.True(MarkdownStyles.LookOf(MdStyle.BoldItalic, dark).Italic);
            Assert.Equal(MdWeight.Bold, MarkdownStyles.LookOf(MdStyle.BoldItalic, dark).Weight);
            Assert.Equal(PadPalette.Light.MdLink, MarkdownStyles.LookOf(MdStyle.LinkText, PadPalette.Light).Foreground);
        }
    }
}
