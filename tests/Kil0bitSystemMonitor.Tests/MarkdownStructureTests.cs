using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>What a line is in its document (spec 2): fences and $$ blocks, tables, setext headings, front matter, callouts, abbreviations.</summary>
    public class MarkdownStructureTests
    {
        private static MarkdownStructure Scan(string text) => MarkdownStructure.Scan(text.Split('\n'));

        [Fact]
        public void Dollar_lines_open_and_close_math_blocks_like_fences()
        {
            var s = Scan("a\n$$\nx^2\n$$\nb\n$$ not a block");

            Assert.Equal(new[] { MdFence.None, MdFence.Delimiter, MdFence.Inside, MdFence.Delimiter, MdFence.None, MdFence.None }, s.Fences);
            Assert.Equal(2, s.Openings[3]);
            Assert.Equal(4, s.Closings[1]);
            Assert.Equal(2, s.BlockOpenings[2]);
            Assert.Equal(0, s.BlockOpenings[4]);
        }

        [Fact]
        public void Inside_lines_know_their_opening_line()
        {
            var s = Scan("```cs\na\nb\n```\n~~~\nc");

            Assert.Equal(new[] { 0, 1, 1, 0, 0, 5 }, s.BlockOpenings);
        }

        [Fact]
        public void Tables_have_a_header_a_delimiter_and_rows()
        {
            var s = Scan("intro\n| a | b |\n|:--|--:|\n| 1 | 2 |\n3 | 4\n\nafter | x");

            Assert.Equal(
                new[] { MdTableRole.None, MdTableRole.Header, MdTableRole.Delimiter, MdTableRole.Row, MdTableRole.Row, MdTableRole.None, MdTableRole.None },
                s.Facts.Select(f => f.Table));
        }

        [Theory]
        [InlineData("a | b\nc | d")]
        [InlineData("a | b\n---")]
        [InlineData("a | b | c\n|---|---|")]
        [InlineData("```\na | b\n|---|---|\n```")]
        [InlineData("> a | b\n> |---|---|")]
        public void Not_every_pipe_line_is_a_table(string text) =>
            Assert.All(Scan(text).Facts, f => Assert.Equal(MdTableRole.None, f.Table));

        [Fact]
        public void A_dash_line_under_text_is_a_heading_and_after_a_blank_line_a_rule()
        {
            var s = Scan("Title\n===\ntext\n---\n\n---\n- item\n---");

            Assert.Equal(1, s.Facts[0].SetextLevel);
            Assert.True(s.Facts[1].SetextUnderline);
            Assert.Equal(2, s.Facts[2].SetextLevel);
            Assert.True(s.Facts[3].SetextUnderline);
            Assert.False(s.Facts[5].SetextUnderline);   // after a blank line: a rule
            Assert.Equal(0, s.Facts[6].SetextLevel);    // a list item is no heading text
            Assert.False(s.Facts[7].SetextUnderline);
        }

        [Fact]
        public void Front_matter_is_the_first_dash_block_within_200_lines()
        {
            var s = Scan("---\ntitle: x\n---\n# H");
            Assert.Equal(new[] { true, true, true, false }, s.Facts.Select(f => f.FrontMatter));

            Assert.All(Scan("---\ntitle: x\n# H").Facts, f => Assert.False(f.FrontMatter));
            Assert.All(Scan("text\n---\na\n---").Facts, f => Assert.False(f.FrontMatter));

            string far = "---\n" + string.Join("\n", Enumerable.Repeat("k: v", 250)) + "\n---";
            Assert.All(Scan(far).Facts, f => Assert.False(f.FrontMatter));
            Assert.True(Scan("---\n" + string.Join("\n", Enumerable.Repeat("k: v", 198)) + "\n...").Facts[0].FrontMatter);
            Assert.False(Scan("---\n" + string.Join("\n", Enumerable.Repeat("k: v", 199)) + "\n...").Facts[0].FrontMatter);
        }

        [Fact]
        public void A_callout_class_marks_the_quote_above_it()
        {
            var s = Scan("> one\n> two\n{.is-warning}\nafter\n{.is-info}");

            Assert.Equal(new[] { MdCallout.Warning, MdCallout.Warning, MdCallout.None, MdCallout.None, MdCallout.None }, s.Facts.Select(f => f.Callout));
            Assert.True(s.Facts[2].CalloutClass);
            Assert.False(s.Facts[4].CalloutClass);   // not under a quote
        }

        [Fact]
        public void A_dollar_block_closes_only_on_exactly_two_dollars()
        {
            var s = Scan("$$\nx\n$$$\ny\n$$  \nz");

            Assert.Equal(new[] { MdFence.Delimiter, MdFence.Inside, MdFence.Inside, MdFence.Inside, MdFence.Delimiter, MdFence.None }, s.Fences);
        }

        [Fact]
        public void A_callout_class_line_is_never_heading_text()
        {
            var s = Scan("> q\n{.is-info}\n---");

            Assert.True(s.Facts[1].CalloutClass);
            Assert.Equal(0, s.Facts[1].SetextLevel);
        }

        [Fact]
        public void Abbreviation_terms_are_collected_outside_fences()
        {
            var s = Scan("*[HTML]: Hyper Text Markup Language\n*[W3C]: World Wide Web Consortium\n```\n*[NO]: x\n```");

            Assert.Equal(new[] { "HTML", "W3C" }, s.Abbreviations.OrderBy(t => t));
        }

        [Fact]
        public void Plain_facts_are_not_structural()
        {
            Assert.False(new MdLineFacts(MdFence.None).IsStructural);
            Assert.True(new MdLineFacts(MdFence.Inside).IsStructural);
            Assert.True(new MdLineFacts(MdFence.None, Table: MdTableRole.Row).IsStructural);
        }

        [Fact]
        public void Table_cells_split_on_unescaped_pipes()
        {
            Assert.Equal(new[] { "a", "b" }, TableCells.Split("| a | b |"));
            Assert.Equal(new[] { "a", "b" }, TableCells.Split("a|b"));
            Assert.Equal(new[] { @"x \| y", "z" }, TableCells.Split(@"| x \| y | z |"));
            Assert.Equal(new[] { 0, 4, 8 }, TableCells.Pipes("| a | b |"));
            Assert.Equal(new[] { 0, 9 }, TableCells.Pipes(@"| a \| b |"));
        }

        [Fact]
        public void Callout_colors_follow_the_kind()
        {
            var palette = PadPalette.Dark;
            Assert.Equal(palette.MdCalloutInfo, MarkdownStyles.CalloutColor(MdCallout.Info, palette));
            Assert.Equal(palette.MdCalloutSuccess, MarkdownStyles.CalloutColor(MdCallout.Success, palette));
            Assert.Equal(palette.MdCalloutWarning, MarkdownStyles.CalloutColor(MdCallout.Warning, palette));
            Assert.Equal(palette.MdCalloutDanger, MarkdownStyles.CalloutColor(MdCallout.Danger, palette));
            Assert.Equal(0x26, MarkdownStyles.CalloutTintAlpha);
        }

        [Fact]
        public void The_cache_gives_facts_and_follows_a_new_table() => UiThread.Run(() =>
        {
            var document = new TextDocument("| a | b |\nnext");
            var cache = new MarkdownDocumentCache();
            int changes = 0;
            cache.StructureChanged += () => changes++;
            Assert.Equal(MdTableRole.None, cache.FactsOf(document, 1).Table);

            document.Insert(document.GetLineByNumber(1).EndOffset, "\n|---|---|");

            Assert.Equal(MdTableRole.Header, cache.FactsOf(document, 1).Table);
            Assert.Equal(MdTableRole.Delimiter, cache.FactsOf(document, 2).Table);
            Assert.Equal(1, changes);
        });

        [Fact]
        public void Typing_inside_a_paragraph_a_fence_or_a_table_cell_does_not_rescan() => UiThread.Run(() =>
        {
            var document = new TextDocument("# T\nsome text\n| a | b |\n|---|---|\n| 1 | 2 |\n```\ncode\n```");
            var cache = new MarkdownDocumentCache();
            cache.FactsOf(document, 1);
            int before = cache.Recomputes;

            document.Insert(document.GetLineByNumber(2).EndOffset, " more");
            document.Insert(document.GetLineByNumber(5).Offset + 3, "0");    // the cell "1" becomes "10"
            document.Insert(document.GetLineByNumber(7).EndOffset, "();");

            Assert.Equal(before, cache.Recomputes);
            Assert.Equal(MdTableRole.Row, cache.FactsOf(document, 5).Table);
        });

        [Fact]
        public void Typing_a_pipe_rescans() => UiThread.Run(() =>
        {
            var document = new TextDocument("a\nb");
            var cache = new MarkdownDocumentCache();
            cache.FactsOf(document, 1);
            int before = cache.Recomputes;

            document.Insert(1, " |");

            Assert.Equal(before + 1, cache.Recomputes);
        });

        [Fact]
        public void Editing_the_delimiter_row_turns_the_table_off_and_on() => UiThread.Run(() =>
        {
            var document = new TextDocument("| a | b |\n|---|---|\n| 1 | 2 |");
            var cache = new MarkdownDocumentCache();
            Assert.Equal(MdTableRole.Header, cache.FactsOf(document, 1).Table);

            document.Insert(document.GetLineByNumber(2).Offset + 2, "x");
            Assert.Equal(MdTableRole.None, cache.FactsOf(document, 1).Table);

            document.Remove(document.GetLineByNumber(2).Offset + 2, 1);
            Assert.Equal(MdTableRole.Header, cache.FactsOf(document, 1).Table);
            Assert.Equal(MdTableRole.Delimiter, cache.FactsOf(document, 2).Table);
        });

        [Fact]
        public void A_backslash_before_a_header_pipe_is_noticed() => UiThread.Run(() =>
        {
            var document = new TextDocument("| a | b |\n|---|---|");
            var cache = new MarkdownDocumentCache();
            Assert.Equal(MdTableRole.Header, cache.FactsOf(document, 1).Table);

            document.Insert(document.GetLineByNumber(1).Offset + 4, "\\");   // | a | b | -> | a \| b |

            Assert.Equal(MdTableRole.None, cache.FactsOf(document, 1).Table);
        });

        [Fact]
        public void Many_edits_in_one_update_rescan_once() => UiThread.Run(() =>
        {
            var document = new TextDocument("a\nb");
            var cache = new MarkdownDocumentCache();
            cache.FactsOf(document, 1);
            int before = cache.Recomputes;

            document.BeginUpdate();
            for (int i = 0; i < 100; i++) document.Insert(1, "|");
            document.EndUpdate();

            Assert.Equal(before + 1, cache.Recomputes);
        });

        [Fact]
        public void Editing_a_definition_term_updates_the_abbreviations() => UiThread.Run(() =>
        {
            var document = new TextDocument("*[HTML]: x\nHTML here");
            var cache = new MarkdownDocumentCache();
            Assert.Contains("HTML", cache.AbbreviationsOf(document));

            document.Insert(6, "X");   // *[HTMLX]: x

            Assert.Equal(new[] { "HTMLX" }, cache.AbbreviationsOf(document));
        });
    }
}
