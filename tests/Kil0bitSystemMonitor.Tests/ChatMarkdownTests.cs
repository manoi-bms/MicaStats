using System;
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Markdown an answer arrives in, parsed into blocks and styled runs for the Ask window.
    /// Pure: no WPF. Thai text is kept as \u escapes.
    /// </summary>
    public class ChatMarkdownTests
    {
        private static readonly ChatRun Break = new("\n");

        private static ChatRun Plain(string text) => new(text);

        private static ChatBlock Single(string markdown) => Assert.Single(ChatMarkdown.Parse(markdown));

        private static IReadOnlyList<ChatRun> Runs(string markdown) => Single(markdown).Runs;

        // ---- blocks --------------------------------------------------------------------------

        [Fact]
        public void Empty_or_blank_text_has_no_blocks()
        {
            Assert.Empty(ChatMarkdown.Parse(""));
            Assert.Empty(ChatMarkdown.Parse(null));
            Assert.Empty(ChatMarkdown.Parse("\n  \n\t\n"));
        }

        [Fact]
        public void A_line_of_text_is_a_paragraph()
        {
            var block = Single("The CPU is fine.");
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            Assert.Equal(new[] { Plain("The CPU is fine.") }, block.Runs);
        }

        [Fact]
        public void Double_dollar_lines_are_text_in_a_chat_reply_not_a_code_fence()
        {
            var blocks = ChatMarkdown.Parse("$$\nx^2\n$$");

            Assert.DoesNotContain(blocks, b => b.Kind == ChatBlockKind.Code);
            Assert.Contains(blocks.SelectMany(b => b.Runs), r => r.Text.Contains("$$"));
        }

        [Fact]
        public void A_single_newline_inside_a_paragraph_is_a_line_break()
        {
            Assert.Equal(new[] { Plain("one"), Break, Plain("two") }, Runs("one\ntwo"));
        }

        [Fact]
        public void A_blank_line_starts_a_new_paragraph()
        {
            var blocks = ChatMarkdown.Parse("one\n\n\ntwo");
            Assert.Equal(2, blocks.Count);
            Assert.All(blocks, b => Assert.Equal(ChatBlockKind.Paragraph, b.Kind));
            Assert.Equal(new[] { Plain("one") }, blocks[0].Runs);
            Assert.Equal(new[] { Plain("two") }, blocks[1].Runs);
        }

        [Fact]
        public void Crlf_lf_and_cr_line_endings_all_work()
        {
            var blocks = ChatMarkdown.Parse("one\r\ntwo\r\n\r\nthree\rfour\n\nfive");
            Assert.Equal(3, blocks.Count);
            Assert.Equal(new[] { Plain("one"), Break, Plain("two") }, blocks[0].Runs);
            Assert.Equal(new[] { Plain("three"), Break, Plain("four") }, blocks[1].Runs);
            Assert.Equal(new[] { Plain("five") }, blocks[2].Runs);
            Assert.DoesNotContain(blocks.SelectMany(b => b.Runs), r => r.Text.Contains('\r'));
        }

        [Fact]
        public void Headings_one_to_three_keep_their_level_and_four_to_six_render_as_three()
        {
            var blocks = ChatMarkdown.Parse("# One\n## Two\n### Three\n#### Four\n###### Six");
            Assert.All(blocks, b => Assert.Equal(ChatBlockKind.Heading, b.Kind));
            Assert.Equal(new[] { 1, 2, 3, 3, 3 }, blocks.Select(b => b.Level));
            Assert.Equal(new[] { "One", "Two", "Three", "Four", "Six" }, blocks.Select(b => b.Runs.Single().Text));
        }

        [Fact]
        public void A_heading_drops_its_closing_hashes_and_keeps_inline_styles()
        {
            Assert.Equal(new[] { Plain("Title") }, Runs("## Title ##"));
            Assert.Equal(new[] { Plain("Learn C#") }, Runs("# Learn C#"));
            Assert.Equal(new[] { Plain("The "), new ChatRun("CPU", Bold: true) }, Runs("### The **CPU**"));
        }

        [Fact]
        public void A_hash_without_a_space_is_not_a_heading()
        {
            var block = Single("#hashtag");
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            Assert.Equal(new[] { Plain("#hashtag") }, block.Runs);
        }

        [Fact]
        public void Dash_star_and_plus_make_bullet_items()
        {
            var blocks = ChatMarkdown.Parse("- one\n* two\n+ three");
            Assert.All(blocks, b => Assert.Equal(ChatBlockKind.Bullet, b.Kind));
            Assert.All(blocks, b => Assert.Equal(0, b.Depth));
            Assert.Equal(new[] { "one", "two", "three" }, blocks.Select(b => b.Runs.Single().Text));
        }

        [Fact]
        public void Indentation_sets_the_depth_of_nested_items()
        {
            var blocks = ChatMarkdown.Parse("- a\n  - b\n    - c\n  - d\n- e");
            Assert.Equal(new[] { 0, 1, 2, 1, 0 }, blocks.Select(b => b.Depth));
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }, blocks.Select(b => b.Runs.Single().Text));
        }

        [Fact]
        public void A_tab_indents_like_four_spaces()
        {
            var blocks = ChatMarkdown.Parse("- a\n\t- b\n    - c");
            Assert.Equal(new[] { 0, 1, 1 }, blocks.Select(b => b.Depth));
        }

        [Fact]
        public void A_numbered_item_keeps_its_number()
        {
            var blocks = ChatMarkdown.Parse("3. three\n4) four\n10. ten");
            Assert.All(blocks, b => Assert.Equal(ChatBlockKind.Numbered, b.Kind));
            Assert.Equal(new[] { 3, 4, 10 }, blocks.Select(b => b.Number));
            Assert.Equal(new[] { "three", "four", "ten" }, blocks.Select(b => b.Runs.Single().Text));
        }

        [Fact]
        public void Bullets_nest_under_numbered_items_and_the_numbering_carries_on()
        {
            var blocks = ChatMarkdown.Parse("1. **CPU**: busy\n   - Chrome 40%\n   - Teams 12%\n\n2. Memory\n");
            Assert.Equal(new[] { ChatBlockKind.Numbered, ChatBlockKind.Bullet, ChatBlockKind.Bullet, ChatBlockKind.Numbered },
                blocks.Select(b => b.Kind));
            Assert.Equal(new[] { 0, 1, 1, 0 }, blocks.Select(b => b.Depth));
            Assert.Equal(1, blocks[0].Number);
            Assert.Equal(2, blocks[3].Number);
            Assert.Equal(new[] { new ChatRun("CPU", Bold: true), Plain(": busy") }, blocks[0].Runs);
        }

        [Theory]
        [InlineData("\u0E51. \u0E02\u0E49\u0E2D\u0E41\u0E23\u0E01")]   // Thai numeral one: "1. first item"
        [InlineData("\u0663) \u0623\u0648\u0644")]                    // Arabic-Indic three
        [InlineData("1234567890. ten digits")]
        public void A_number_that_is_not_ascii_or_too_long_starts_no_list(string line)
        {
            var block = Single(line);
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            Assert.Equal(new[] { Plain(line) }, block.Runs);
        }

        [Fact]
        public void Item_zero_keeps_its_number()
        {
            var block = Single("0. zero");
            Assert.Equal(ChatBlockKind.Numbered, block.Kind);
            Assert.Equal(0, block.Number);
            Assert.Equal(new[] { Plain("zero") }, block.Runs);
        }

        [Fact]
        public void List_depth_stops_at_six_however_deep_the_indentation_goes()
        {
            string text = string.Join("\n", Enumerable.Range(0, 3000).Select(i => new string(' ', i) + "- a"));

            var blocks = ChatMarkdown.Parse(text);

            Assert.Equal(6, ChatMarkdown.MaxListDepth);
            Assert.Equal(3000, blocks.Count);
            Assert.All(blocks, b => Assert.Equal(ChatBlockKind.Bullet, b.Kind));
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 6, 6 }, blocks.Take(9).Select(b => b.Depth));
            Assert.Equal(6, blocks.Max(b => b.Depth));
        }

        [Fact]
        public void The_deepest_inline_nesting_a_line_can_hold_parses()
        {
            // 999 links inside one another fill a line to the styling limit; each level recurses once.
            string line = new string('[', 999) + "core" + string.Concat(Enumerable.Repeat("]()", 999));
            Assert.Equal(ChatMarkdown.MaxInlineLength, line.Length);

            var runs = Runs(line);

            Assert.Contains("core", string.Concat(runs.Select(r => r.Text)), StringComparison.Ordinal);
        }

        [Fact]
        public void A_list_interrupts_a_paragraph()
        {
            var blocks = ChatMarkdown.Parse("Two things:\n- a\n- b");
            Assert.Equal(new[] { ChatBlockKind.Paragraph, ChatBlockKind.Bullet, ChatBlockKind.Bullet }, blocks.Select(b => b.Kind));
        }

        [Fact]
        public void A_line_right_after_an_item_continues_it()
        {
            Assert.Equal(new[] { Plain("first"), Break, Plain("still first") }, Runs("- first\nstill first"));
        }

        [Fact]
        public void An_indented_paragraph_after_a_blank_line_stays_in_its_item()
        {
            var blocks = ChatMarkdown.Parse("1. Close Chrome.\n\n   It frees 2 GB.\n2. Restart.");
            Assert.Equal(2, blocks.Count);
            Assert.Equal(new[] { Plain("Close Chrome."), Break, Break, Plain("It frees 2 GB.") }, blocks[0].Runs);
            Assert.Equal(2, blocks[1].Number);
        }

        [Fact]
        public void A_paragraph_after_a_list_ends_it_and_a_later_list_starts_at_depth_zero()
        {
            var blocks = ChatMarkdown.Parse("- a\n    - b\n\nText\n\n    - c");
            Assert.Equal(new[] { ChatBlockKind.Bullet, ChatBlockKind.Bullet, ChatBlockKind.Paragraph, ChatBlockKind.Bullet },
                blocks.Select(b => b.Kind));
            Assert.Equal(0, blocks[3].Depth);
        }

        [Fact]
        public void A_task_item_is_a_bullet_that_keeps_its_box()
        {
            var block = Single("- [x] Updated the driver");
            Assert.Equal(ChatBlockKind.Bullet, block.Kind);
            Assert.Equal(new[] { Plain("[x] Updated the driver") }, block.Runs);
        }

        [Fact]
        public void Quote_lines_join_into_one_quote()
        {
            var block = Single("> The disk is **full**.\n> Free some space.");
            Assert.Equal(ChatBlockKind.Quote, block.Kind);
            Assert.Equal(new[] { Plain("The disk is "), new ChatRun("full", Bold: true), Plain("."), Break, Plain("Free some space.") },
                block.Runs);
        }

        [Fact]
        public void A_fenced_code_block_keeps_its_language_and_text_verbatim()
        {
            var blocks = ChatMarkdown.Parse("Run this:\n```powershell\nGet-Process | Sort CPU -Desc\n  **not bold** # not a heading\n```\nDone.");
            Assert.Equal(new[] { ChatBlockKind.Paragraph, ChatBlockKind.Code, ChatBlockKind.Paragraph }, blocks.Select(b => b.Kind));
            Assert.Equal("powershell", blocks[1].Language);
            Assert.Equal("Get-Process | Sort CPU -Desc\n  **not bold** # not a heading", blocks[1].Code);
            Assert.Empty(blocks[1].Runs);
            Assert.Equal(new[] { Plain("Done.") }, blocks[2].Runs);
        }

        [Fact]
        public void A_tilde_fence_works_and_a_code_block_may_have_no_language()
        {
            var block = Single("~~~\nline 1\n\nline 3\n~~~");
            Assert.Equal(ChatBlockKind.Code, block.Kind);
            Assert.Equal("", block.Language);
            Assert.Equal("line 1\n\nline 3", block.Code);
        }

        [Fact]
        public void An_unclosed_fence_runs_to_the_end()
        {
            var blocks = ChatMarkdown.Parse("Example:\n```js\nlet a = 1;\n\n# still code");
            Assert.Equal(2, blocks.Count);
            Assert.Equal(ChatBlockKind.Code, blocks[1].Kind);
            Assert.Equal("js", blocks[1].Language);
            Assert.Equal("let a = 1;\n\n# still code", blocks[1].Code);
        }

        [Fact]
        public void An_indented_fence_in_a_list_loses_that_indentation()
        {
            var blocks = ChatMarkdown.Parse("1. Run:\n   ```\n   ipconfig /all\n   ```\n2. Check.");
            Assert.Equal(new[] { ChatBlockKind.Numbered, ChatBlockKind.Code, ChatBlockKind.Numbered }, blocks.Select(b => b.Kind));
            Assert.Equal("ipconfig /all", blocks[1].Code);
            Assert.Equal(2, blocks[2].Number);
        }

        [Fact]
        public void Three_dashes_stars_or_underscores_make_a_rule()
        {
            var blocks = ChatMarkdown.Parse("above\n\n---\n***\n___\n- - -\nbelow");
            Assert.Equal(new[]
            {
                ChatBlockKind.Paragraph, ChatBlockKind.Rule, ChatBlockKind.Rule, ChatBlockKind.Rule, ChatBlockKind.Rule, ChatBlockKind.Paragraph,
            }, blocks.Select(b => b.Kind));
        }

        // ---- inline ----------------------------------------------------------------------------

        [Fact]
        public void Double_stars_and_double_underscores_are_bold()
        {
            Assert.Equal(new[] { Plain("a "), new ChatRun("b", Bold: true), Plain(" c") }, Runs("a **b** c"));
            Assert.Equal(new[] { new ChatRun("bold", Bold: true) }, Runs("__bold__"));
        }

        [Fact]
        public void Single_stars_and_underscores_are_italic()
        {
            Assert.Equal(new[] { Plain("a "), new ChatRun("b", Italic: true), Plain(" c") }, Runs("a *b* c"));
            Assert.Equal(new[] { new ChatRun("it", Italic: true) }, Runs("_it_"));
        }

        [Fact]
        public void Triple_markers_are_bold_and_italic()
        {
            Assert.Equal(new[] { new ChatRun("both", Bold: true, Italic: true) }, Runs("***both***"));
            Assert.Equal(new[] { new ChatRun("both", Bold: true, Italic: true) }, Runs("___both___"));
        }

        [Fact]
        public void Italic_can_sit_inside_bold()
        {
            Assert.Equal(new[] { new ChatRun("bold ", Bold: true), new ChatRun("italic", Bold: true, Italic: true) },
                Runs("**bold *italic***"));
        }

        [Fact]
        public void Backticks_make_inline_code_whose_content_is_literal()
        {
            Assert.Equal(new[] { Plain("Run "), new ChatRun("a*b*c", Code: true), Plain(" now") }, Runs("Run `a*b*c` now"));
            Assert.Equal(new[] { new ChatRun("a ` b", Code: true) }, Runs("`` a ` b ``"));
        }

        [Fact]
        public void Double_tildes_strike_through()
        {
            Assert.Equal(new[] { new ChatRun("old", Strike: true), Plain(" new") }, Runs("~~old~~ new"));
        }

        [Fact]
        public void A_markdown_link_carries_its_address()
        {
            Assert.Equal(new[] { Plain("See "), new ChatRun("the docs", Link: new Uri("https://example.com/docs")), Plain(".") },
                Runs("See [the docs](https://example.com/docs)."));
        }

        [Fact]
        public void A_link_keeps_the_styles_inside_its_text()
        {
            var uri = new Uri("https://example.com/");
            var runs = Runs("[**Bold** text](https://example.com/)");
            Assert.Equal(new[] { new ChatRun("Bold", Bold: true, Link: uri), new ChatRun(" text", Link: uri) }, runs);
        }

        [Fact]
        public void A_link_address_may_hold_balanced_parentheses_and_a_title()
        {
            var run = Runs("[Mercury](https://en.wikipedia.org/wiki/Mercury_(planet) \"The planet\")").Single();
            Assert.Equal("Mercury", run.Text);
            Assert.Equal(new Uri("https://en.wikipedia.org/wiki/Mercury_(planet)"), run.Link);
        }

        [Fact]
        public void Mailto_links_are_allowed()
        {
            var run = Runs("[mail me](mailto:help@example.com)").Single();
            Assert.Equal(new Uri("mailto:help@example.com"), run.Link);
        }

        [Fact]
        public void A_bare_address_becomes_a_link_without_the_sentence_punctuation()
        {
            Assert.Equal(new[] { Plain("Go to "), new ChatRun("https://example.com/x", Link: new Uri("https://example.com/x")), Plain(".") },
                Runs("Go to https://example.com/x."));
        }

        [Fact]
        public void Underscores_in_a_bare_address_are_not_italic()
        {
            var run = Runs("http://example.com/a_b_c").Single();
            Assert.Equal("http://example.com/a_b_c", run.Text);
            Assert.False(run.Italic);
            Assert.NotNull(run.Link);
        }

        [Fact]
        public void A_bold_bare_address_stops_before_the_closing_stars()
        {
            var run = Runs("**https://example.com/status**").Single();
            Assert.Equal(new ChatRun("https://example.com/status", Bold: true, Link: new Uri("https://example.com/status")), run);
        }

        [Theory]
        [InlineData("[calculator](file:///C:/Windows/System32/calc.exe)", "calculator")]
        [InlineData("[click](javascript:alert(1))", "click")]
        [InlineData("[settings](ms-settings:display)", "settings")]
        [InlineData("[report](slowdown-20260929.txt)", "report")]
        [InlineData("ftp://example.com/file and \\\\server\\share", "ftp://example.com/file and \\\\server\\share")]
        public void Unsafe_or_relative_addresses_stay_plain_text(string markdown, string shown)
        {
            var runs = Runs(markdown);
            Assert.All(runs, r => Assert.Null(r.Link));
            Assert.Equal(shown, string.Concat(runs.Select(r => r.Text)));
        }

        [Theory]
        [InlineData("**not closed")]
        [InlineData("*not closed")]
        [InlineData("`not closed")]
        [InlineData("~~not closed")]
        [InlineData("[text](no closing parenthesis")]
        [InlineData("[text] (see below)")]
        [InlineData("[1] and [x]")]
        [InlineData("2 * 3 * 4 = 24")]
        [InlineData("~5 GB free, ~~~ and ****")]
        public void Unclosed_markers_stay_literal(string markdown)
        {
            var run = Runs(markdown).Single();
            Assert.Equal(markdown, run.Text);
            Assert.False(run.Bold || run.Italic || run.Code || run.Strike);
            Assert.Null(run.Link);
        }

        [Fact]
        public void Snake_case_words_are_not_italic()
        {
            Assert.Equal(new[] { Plain("Check max_cpu_temp and get_top_processes now") },
                Runs("Check max_cpu_temp and get_top_processes now"));
        }

        [Fact]
        public void A_backslash_escapes_a_marker()
        {
            Assert.Equal(new[] { Plain("*not italic* and C:\\Users\\me") }, Runs("\\*not italic\\* and C:\\Users\\me"));
        }

        [Fact]
        public void Thai_text_is_untouched()
        {
            // "The CPU is **hot** today" in Thai: combining vowels and tone marks must survive as typed.
            const string thai = "\u0E0B\u0E35\u0E1E\u0E35\u0E22\u0E39\u0E23\u0E49\u0E2D\u0E19";
            const string today = "\u0E27\u0E31\u0E19\u0E19\u0E35\u0E49";
            Assert.Equal(new[] { Plain("CPU "), new ChatRun(thai, Bold: true), Plain(" " + today) },
                Runs("CPU **" + thai + "** " + today));

            const string question = "\u0E0A\u0E48\u0E27\u0E07\u0E19\u0E35\u0E49\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32";
            Assert.Equal(new[] { Plain(question) }, Runs(question));
        }

        [Fact]
        public void A_very_long_line_is_shown_plain()
        {
            string line = string.Concat(Enumerable.Repeat("*a ", ChatMarkdown.MaxInlineLength));
            Assert.Equal(new[] { Plain(line.TrimEnd()) }, Runs(line));
        }

        // ---- tables ----------------------------------------------------------------------------

        private static string Text(ChatCell cell) => string.Concat(cell.Runs.Select(r => r.Text));

        private static string[] Texts(IReadOnlyList<ChatCell> cells) => cells.Select(Text).ToArray();

        private static ChatTable TableOf(string markdown)
        {
            var block = Single(markdown);
            Assert.Equal(ChatBlockKind.Table, block.Kind);
            Assert.NotNull(block.Table);
            return block.Table!;
        }

        /// <summary>Every piece of text the blocks hold (runs, code, cells), joined, for "nothing was lost" checks.</summary>
        private static string AllText(IEnumerable<ChatBlock> blocks)
        {
            var all = new System.Text.StringBuilder();
            foreach (ChatBlock b in blocks)
            {
                foreach (ChatRun r in b.Runs) all.Append(r.Text).Append(' ');
                all.Append(b.Code).Append(' ');
                if (b.Table != null)
                {
                    foreach (ChatCell c in b.Table.Header) all.Append(Text(c)).Append(' ');
                    foreach (var row in b.Table.Rows)
                        foreach (ChatCell c in row) all.Append(Text(c)).Append(' ');
                }
            }
            return all.ToString();
        }

        [Fact]
        public void A_plain_table_has_header_aligns_and_rows()
        {
            var table = TableOf("| Name | CPU | Memory |\n| --- | --- | --- |\n| Chrome | 40% | 2 GB |\n| Teams | 12% | 1 GB |");

            Assert.Equal(new[] { "Name", "CPU", "Memory" }, Texts(table.Header));
            Assert.Equal(new[] { ChatAlign.Left, ChatAlign.Left, ChatAlign.Left }, table.Aligns);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { "Chrome", "40%", "2 GB" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { "Teams", "12%", "1 GB" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void The_delimiter_row_sets_each_columns_alignment()
        {
            var table = TableOf("| a | b | c | d |\n|:---|:---:|---:|---|\n| 1 | 2 | 3 | 4 |");
            Assert.Equal(new[] { ChatAlign.Left, ChatAlign.Center, ChatAlign.Right, ChatAlign.Left }, table.Aligns);
        }

        [Fact]
        public void Outer_pipes_are_optional()
        {
            var table = TableOf("a | b\n- | -\n1 | 2\n3 | 4");
            Assert.Equal(new[] { "a", "b" }, Texts(table.Header));
            Assert.Equal(new[] { "1", "2" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { "3", "4" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void A_short_row_is_padded_and_a_long_row_is_cut()
        {
            var table = TableOf("| a | b | c |\n|---|---|---|\n| 1 |\n| 1 | 2 | 3 | 4 | 5 |");
            Assert.Equal(new[] { "1", "", "" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { "1", "2", "3" }, Texts(table.Rows[1]));
            Assert.Empty(table.Rows[0][1].Runs);
        }

        [Fact]
        public void An_escaped_pipe_and_a_pipe_in_a_code_span_stay_in_their_cell()
        {
            var table = TableOf("| cmd | note |\n|---|---|\n| a \\| b | `x | y` |\n| ``p | q`` | r |");
            Assert.Equal(new[] { "a | b", "x | y" }, Texts(table.Rows[0]));
            Assert.True(table.Rows[0][1].Runs.Single().Code);
            Assert.Equal(new[] { "p | q", "r" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void Bold_code_and_a_link_in_a_cell_are_runs()
        {
            var table = TableOf("| a | b | c |\n|---|---|---|\n| **hot** | `top` | [docs](https://example.com/) |");
            var row = table.Rows[0];
            Assert.Equal(new[] { new ChatRun("hot", Bold: true) }, row[0].Runs);
            Assert.Equal(new[] { new ChatRun("top", Code: true) }, row[1].Runs);
            Assert.Equal(new[] { new ChatRun("docs", Link: new Uri("https://example.com/")) }, row[2].Runs);
        }

        [Fact]
        public void A_header_without_its_delimiter_is_a_paragraph_and_becomes_a_table_when_it_arrives()
        {
            const string header = "| Name | CPU |";
            var before = Single(header);
            Assert.Equal(ChatBlockKind.Paragraph, before.Kind);
            Assert.Equal(new[] { Plain(header) }, before.Runs);

            Assert.Equal(ChatBlockKind.Paragraph, Single(header + "\n").Kind);

            var table = TableOf(header + "\n|---|---|");
            Assert.Equal(new[] { "Name", "CPU" }, Texts(table.Header));
            Assert.Empty(table.Rows);
        }

        /// <summary>The characters that carry text: no whitespace and none of the table and inline markers, sorted.</summary>
        private static string Significant(string s) =>
            new string(s.Where(c => !char.IsWhiteSpace(c) && "|-:`*".IndexOf(c) < 0).OrderBy(c => c).ToArray());

        [Fact]
        public void Every_prefix_of_a_streaming_table_parses_and_keeps_every_character_of_text_once()
        {
            const string full = "Intro\n\n| Name | CPU |\n|:--|--:|\n| `a|b` | **40%** |\n| Teams | 12% |\n\nDone.";
            for (int n = 0; n <= full.Length; n++)
            {
                string prefix = full.Substring(0, n);
                var blocks = ChatMarkdown.Parse(prefix);   // must not throw
                // Each text character appears as often in the blocks as in the prefix: none lost, none doubled.
                Assert.Equal(Significant(prefix), Significant(AllText(blocks)));
            }
        }

        [Theory]
        [InlineData("| a | b |\n| --- |", "| --- |")]
        [InlineData("| a | b |\n| --- | :", "| --- | :")]
        [InlineData("| a | b |\n|", "|")]
        [InlineData("| a | b |\n| --- | --- | ---", "| --- | --- | ---")]
        public void A_half_delimiter_row_is_text_and_keeps_every_line(string markdown, string second)
        {
            var block = Single(markdown);
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            Assert.Equal(new[] { Plain("| a | b |"), Break, Plain(second) }, block.Runs);
        }

        [Fact]
        public void A_half_delimiter_row_that_is_a_table_so_far_keeps_its_header()
        {
            var table = TableOf("| a | b |\n| --- | -");
            Assert.Equal(new[] { "a", "b" }, Texts(table.Header));
            Assert.Equal(new[] { ChatAlign.Left, ChatAlign.Left }, table.Aligns);
            Assert.Empty(table.Rows);
        }

        [Theory]
        [InlineData("||", false, ChatAlign.Left)]
        [InlineData("|-", true, ChatAlign.Left)]
        [InlineData(":", false, ChatAlign.Left)]
        [InlineData("::", false, ChatAlign.Left)]
        [InlineData("-:", false, ChatAlign.Left)]
        [InlineData(":-:", false, ChatAlign.Left)]
        [InlineData("|:-:|", true, ChatAlign.Center)]
        [InlineData("|-:", true, ChatAlign.Right)]
        [InlineData("|::|", false, ChatAlign.Left)]
        [InlineData("|:|", false, ChatAlign.Left)]
        public void Odd_delimiter_rows_never_throw_and_only_one_with_a_pipe_and_a_dash_is_a_table(string delimiter, bool isTable, ChatAlign align)
        {
            var blocks = ChatMarkdown.Parse("| a |\n" + delimiter);

            if (isTable)
            {
                var table = Assert.Single(blocks).Table!;
                Assert.Equal(new[] { align }, table.Aligns);
                Assert.Equal(new[] { "a" }, Texts(table.Header));
            }
            else
            {
                Assert.DoesNotContain(blocks, b => b.Kind == ChatBlockKind.Table);
                Assert.Contains("a", AllText(blocks), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_header_and_delimiter_with_different_counts_are_not_a_table()
        {
            var blocks = ChatMarkdown.Parse("| a | b | c |\n|---|---|\n| 1 | 2 |");
            Assert.DoesNotContain(blocks, b => b.Kind == ChatBlockKind.Table);
            Assert.Contains("| 1 | 2 |", AllText(blocks), StringComparison.Ordinal);
        }

        [Fact]
        public void A_table_ends_at_a_blank_line_and_at_a_heading()
        {
            var blocks = ChatMarkdown.Parse("| a | b |\n|---|---|\n| 1 | 2 |\n\n| 3 | 4 |\n\n| c | d |\n|---|---|\n| 5 | 6 |\n## Next\nafter");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Paragraph, ChatBlockKind.Table, ChatBlockKind.Heading, ChatBlockKind.Paragraph },
                blocks.Select(b => b.Kind));
            Assert.Single(blocks[0].Table!.Rows);
            Assert.Equal(new[] { Plain("| 3 | 4 |") }, blocks[1].Runs);
            Assert.Single(blocks[2].Table!.Rows);
            Assert.Equal(new[] { Plain("Next") }, blocks[3].Runs);
        }

        [Fact]
        public void A_table_ends_at_a_fence_a_list_item_a_quote_and_a_rule()
        {
            const string table = "| a |\n|---|\n| 1 |\n";

            var fence = ChatMarkdown.Parse(table + "```js\ncode\n```");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Code }, fence.Select(b => b.Kind));
            Assert.Equal("code", fence[1].Code);

            var item = ChatMarkdown.Parse(table + "- item");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Bullet }, item.Select(b => b.Kind));
            Assert.Equal(new[] { Plain("item") }, item[1].Runs);

            var quote = ChatMarkdown.Parse(table + "> quote");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Quote }, quote.Select(b => b.Kind));
            Assert.Equal(new[] { Plain("quote") }, quote[1].Runs);

            var rule = ChatMarkdown.Parse(table + "---");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Rule }, rule.Select(b => b.Kind));

            Assert.All(new[] { fence, item, quote, rule }, blocks => Assert.Single(blocks[0].Table!.Rows));
        }

        [Fact]
        public void A_table_directly_before_a_fence_leaves_the_code_block_whole()
        {
            var open = ChatMarkdown.Parse("| a |\n|---|\n| 1 |\n```js\nlet x;");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Code }, open.Select(b => b.Kind));
            Assert.Equal("js", open[1].Language);
            Assert.Equal("let x;", open[1].Code);
            Assert.False(open[1].Closed);

            var closed = ChatMarkdown.Parse("| a |\n|---|\n| 1 |\n```js\nlet x;\n```");
            Assert.Equal("let x;", closed[1].Code);
            Assert.True(closed[1].Closed);
        }

        [Fact]
        public void A_whitespace_only_line_inside_a_table_ends_it()
        {
            var blocks = ChatMarkdown.Parse("| a |\n|---|\n| 1 |\n   \t\n| 2 |");
            Assert.Equal(new[] { ChatBlockKind.Table, ChatBlockKind.Paragraph }, blocks.Select(b => b.Kind));
            Assert.Single(blocks[0].Table!.Rows);
            Assert.Equal(new[] { Plain("| 2 |") }, blocks[1].Runs);
        }

        [Theory]
        [InlineData("| a | b |\r\n|:-:|---|\r\n| 1 | 2 |\r\n")]
        [InlineData("| a | b |\r|:-:|---|\r| 1 | 2 |")]
        [InlineData("| a | b |\n|:-:|---|\n| 1 | 2 |")]
        [InlineData("| a | b |\n|:-:|---|\n| 1 | 2 |\n")]
        public void A_table_at_the_end_of_the_text_works_with_any_line_ending_and_a_trailing_newline(string markdown)
        {
            var table = TableOf(markdown);
            Assert.Equal(new[] { "a", "b" }, Texts(table.Header));
            Assert.Equal(new[] { ChatAlign.Center, ChatAlign.Left }, table.Aligns);
            var row = Assert.Single(table.Rows);
            Assert.Equal(new[] { "1", "2" }, Texts(row));
        }

        [Fact]
        public void A_trailing_backslash_and_an_unclosed_backtick_in_a_cell_keep_their_text()
        {
            var table = TableOf("| a | b |\n|---|---|\n| x\\ | y |\n| p | q\\|\n| `u | v |");

            Assert.Equal(new[] { "x\\", "y" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { "p", "q|" }, Texts(table.Rows[1]));      // the escaped last pipe is a pipe in the cell
            Assert.Equal(new[] { "`u", "v" }, Texts(table.Rows[2]));      // an unclosed backtick is text, and the pipe still splits
        }

        [Fact]
        public void A_table_may_follow_a_paragraph_without_a_blank_line()
        {
            var blocks = ChatMarkdown.Parse("Results:\n| a | b |\n|---|---|\n| 1 | 2 |");
            Assert.Equal(new[] { ChatBlockKind.Paragraph, ChatBlockKind.Table }, blocks.Select(b => b.Kind));
            Assert.Equal(new[] { Plain("Results:") }, blocks[0].Runs);
        }

        [Fact]
        public void Inside_a_list_item_or_a_quote_there_are_no_tables()
        {
            // Each input is one block whose lines are all kept, joined by line breaks (a blank line, two).
            var cases = new (string Markdown, ChatBlockKind Kind, string[] Lines)[]
            {
                ("- | a | b |\n  |---|---|\n  | 1 | 2 |", ChatBlockKind.Bullet, new[] { "| a | b |", "|---|---|", "| 1 | 2 |" }),
                ("- item\n| a | b |\n|---|---|", ChatBlockKind.Bullet, new[] { "item", "| a | b |", "|---|---|" }),
                ("> | a | b |\n> |---|---|\n> | 1 | 2 |", ChatBlockKind.Quote, new[] { "| a | b |", "|---|---|", "| 1 | 2 |" }),
                ("> quote\n| a | b |\n|---|---|", ChatBlockKind.Quote, new[] { "quote", "| a | b |", "|---|---|" }),
            };
            foreach (var (markdown, kind, lines) in cases)
            {
                var block = Single(markdown);
                Assert.Equal(kind, block.Kind);
                Assert.Null(block.Table);
                var expected = new List<ChatRun>();
                foreach (string line in lines)
                {
                    if (expected.Count > 0) expected.Add(Break);
                    expected.Add(Plain(line));
                }
                Assert.Equal(expected, block.Runs);
            }

            // An item's indented paragraph after a blank line stays in the item, and so does its pipe table.
            var numbered = Single("1. item\n\n   | a | b |\n   |---|---|");
            Assert.Equal(ChatBlockKind.Numbered, numbered.Kind);
            Assert.Equal(new[] { Plain("item"), Break, Break, Plain("| a | b |"), Break, Plain("|---|---|") }, numbered.Runs);
        }

        [Fact]
        public void A_row_of_only_pipes_and_an_empty_header_cell_are_kept_as_empty_cells()
        {
            var table = TableOf("| | b |\n|---|---|\n|||\n| x | y |");
            Assert.Equal(new[] { "", "b" }, Texts(table.Header));
            Assert.Equal(new[] { "", "" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { "x", "y" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void Twelve_columns_and_a_hundred_rows_are_still_a_table()
        {
            Assert.Equal(12, ChatMarkdown.MaxTableColumns);
            Assert.Equal(100, ChatMarkdown.MaxTableRows);

            string head = "|" + string.Concat(Enumerable.Range(1, 12).Select(i => " h" + i + " |"));
            string delim = "|" + string.Concat(Enumerable.Repeat("---|", 12));
            string rows = string.Join("\n", Enumerable.Range(1, 100).Select(r => "| r" + r + " |"));

            var table = TableOf(head + "\n" + delim + "\n" + rows);

            Assert.Equal(12, table.Header.Count);
            Assert.Equal(100, table.Rows.Count);
            Assert.All(table.Rows, r => Assert.Equal(12, r.Count));
        }

        [Fact]
        public void Thirteen_columns_is_no_table_and_every_cell_is_still_there()
        {
            string head = "|" + string.Concat(Enumerable.Range(1, 13).Select(i => " h" + i + " |"));
            string delim = "|" + string.Concat(Enumerable.Repeat("---|", 13));
            string row = "|" + string.Concat(Enumerable.Range(1, 13).Select(i => " c" + i + " |"));
            string md = head + "\n" + delim + "\n" + row;

            var blocks = ChatMarkdown.Parse(md);

            // Exactly what the parser makes of these lines without table support: one paragraph of three lines.
            var block = Assert.Single(blocks);
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            Assert.Equal(new[] { Plain(head), Break, Plain(delim), Break, Plain(row) }, block.Runs);
            string all = AllText(blocks);
            for (int i = 1; i <= 13; i++)
            {
                Assert.Contains(" h" + i + " ", all, StringComparison.Ordinal);
                Assert.Contains(" c" + i + " ", all, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_hundred_and_first_row_is_no_table_and_every_row_is_still_there()
        {
            string[] lines = new[] { "| a | b |", "|---|---|" }
                .Concat(Enumerable.Range(1, 101).Select(r => "| row" + r + " | x |")).ToArray();

            var blocks = ChatMarkdown.Parse(string.Join("\n", lines));

            var block = Assert.Single(blocks);
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            string all = AllText(blocks);
            foreach (string line in lines) Assert.Contains(line, all, StringComparison.Ordinal);
            // Line by line what the parser made before tables: runs separated by breaks, nothing styled.
            Assert.Equal(lines.Length, block.Runs.Count(r => !r.IsLineBreak));
            Assert.Equal(lines, block.Runs.Where(r => !r.IsLineBreak).Select(r => r.Text));
        }

        [Fact]
        public void A_line_of_four_thousand_pipes_parses_quickly_and_loses_nothing()
        {
            string pipes = new string('|', 4000);
            string md = pipes + "\n" + pipes + "\n\n| x | y |\n|---|---|\n\n" + pipes;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var blocks = ChatMarkdown.Parse(md);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "took " + watch.Elapsed);
            string all = AllText(blocks);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(all, "[|]{4000}").Count);
            Assert.Contains("x", all, StringComparison.Ordinal);
        }

        [Fact]
        public void One_row_of_millions_of_characters_with_many_backtick_runs_parses_in_linear_time()
        {
            // 3,000 backtick runs of 3,000 different lengths, none able to close another: about 4.5 million
            // characters. A matcher that rescans the line for each run takes tens of seconds; ours, well under one.
            string unmatched = string.Concat(Enumerable.Range(1, 3000).Select(n => new string('`', n) + "x"));
            // And 200,000 code spans with a pipe each, which must stay in the one cell.
            string spans = string.Concat(Enumerable.Repeat("`|` ", 200000)).TrimEnd();
            string md = "| a | b |\n|---|---|\n| " + unmatched + " | y |\n| " + spans + " |";

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var blocks = ChatMarkdown.Parse(md);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
            var table = Assert.Single(blocks).Table!;
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { unmatched, "y" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { spans, "" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void A_table_of_five_thousand_rows_is_text()
        {
            string md = "| a | b |\n|---|---|\n" + string.Join("\n", Enumerable.Range(1, 5000).Select(r => "| " + r + " | **x** |"));

            var blocks = ChatMarkdown.Parse(md);

            Assert.DoesNotContain(blocks, b => b.Kind == ChatBlockKind.Table);
            Assert.Contains("| 5000 |", AllText(blocks), StringComparison.Ordinal);
        }

        [Fact]
        public void Tens_of_thousands_of_candidate_tables_over_the_row_limit_parse_in_linear_time()
        {
            // Every other line could head a table, and each would run to the end of the text (far over
            // the row limit). Reading the rejected lines again for each candidate is quadratic: 20,000
            // candidates over 40,000 lines is about 400 million line checks, minutes; ours reads each once.
            const int pairs = 20000;
            string md = string.Concat(Enumerable.Repeat("|a|b|\n|-|-|\n", pairs)).TrimEnd('\n');

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var blocks = ChatMarkdown.Parse(md);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
            // Exactly the parser's output before tables: one paragraph, a run per line, line breaks between.
            var block = Assert.Single(blocks);
            Assert.Equal(ChatBlockKind.Paragraph, block.Kind);
            Assert.Equal(2 * pairs, block.Runs.Count(r => !r.IsLineBreak));
            Assert.Equal("|a|b|", block.Runs[0].Text);
            Assert.Equal("|-|-|", block.Runs[2].Text);
        }

        [Fact]
        public void A_cell_longer_than_the_inline_limit_is_shown_plain()
        {
            string longCell = string.Concat(Enumerable.Repeat("*a ", ChatMarkdown.MaxInlineLength));
            var table = TableOf("| a | b |\n|---|---|\n| **ok** | " + longCell + " |");

            Assert.Equal(new[] { Plain(longCell.TrimEnd()) }, table.Rows[0][1].Runs);
        }

        [Fact]
        public void A_pipe_in_a_code_span_over_a_line_of_dashes_is_a_paragraph_and_a_rule()
        {
            var blocks = ChatMarkdown.Parse("Use `grep x | sort` here\n---");

            Assert.Equal(new[] { ChatBlockKind.Paragraph, ChatBlockKind.Rule }, blocks.Select(b => b.Kind));
            Assert.Equal(new[] { Plain("Use "), new ChatRun("grep x | sort", Code: true), Plain(" here") }, blocks[0].Runs);
        }

        [Theory]
        [InlineData("a |")]
        [InlineData("| a")]
        [InlineData("|")]
        [InlineData("a \\| b")]
        public void A_line_of_only_dashes_is_never_a_delimiter_row(string header)
        {
            // Each of these was a paragraph followed by a rule before tables; they still are.
            foreach (string dashes in new[] { "---", "-", ":---", "---:", ":-:" })
            {
                var blocks = ChatMarkdown.Parse(header + "\n" + dashes);
                Assert.DoesNotContain(blocks, b => b.Kind == ChatBlockKind.Table);
                Assert.Contains(header.Replace("\\", "", StringComparison.Ordinal).Trim(), AllText(blocks).Replace("\\", "", StringComparison.Ordinal), StringComparison.Ordinal);
            }
            var rule = ChatMarkdown.Parse(header + "\n---");
            Assert.Equal(new[] { ChatBlockKind.Paragraph, ChatBlockKind.Rule }, rule.Select(b => b.Kind));
        }

        [Fact]
        public void A_one_column_table_needs_a_pipe_on_both_lines()
        {
            Assert.Equal(new[] { "a" }, Texts(TableOf("| a |\n|---|").Header));
            Assert.Equal(new[] { "a" }, Texts(TableOf("| a |\n| --- |").Header));
            Assert.Equal(new[] { "a" }, Texts(TableOf("| a |\n|---").Header));
            var table = TableOf("| a |\n|---|\n| 1 |");
            Assert.Equal(new[] { "1" }, Texts(table.Rows[0]));
        }

        [Theory]
        [InlineData("## Results | summary", ChatBlockKind.Heading, "Results | summary")]
        [InlineData("- a | b", ChatBlockKind.Bullet, "a | b")]
        [InlineData("> x | y", ChatBlockKind.Quote, "x | y")]
        [InlineData("1. a | b", ChatBlockKind.Numbered, "a | b")]
        [InlineData("* a | b", ChatBlockKind.Bullet, "a | b")]
        public void A_line_that_starts_another_block_ends_the_table_even_with_a_pipe(string line, ChatBlockKind kind, string text)
        {
            var blocks = ChatMarkdown.Parse("| a | b |\n|---|---|\n| 1 | 2 |\n" + line);

            Assert.Equal(2, blocks.Count);
            Assert.Equal(ChatBlockKind.Table, blocks[0].Kind);
            Assert.Single(blocks[0].Table!.Rows);
            Assert.Equal(kind, blocks[1].Kind);
            Assert.Equal(new[] { Plain(text) }, blocks[1].Runs);
        }

        [Fact]
        public void In_a_table_without_outer_pipes_a_row_that_starts_like_a_list_item_or_heading_ends_it()
        {
            foreach (string row in new[] { "- x | y", "1. x | y", "# x | y" })
            {
                var blocks = ChatMarkdown.Parse("a | b\n- | -\n1 | 2\n" + row);
                Assert.Equal(2, blocks.Count);
                Assert.Equal(ChatBlockKind.Table, blocks[0].Kind);
                Assert.Single(blocks[0].Table!.Rows);
                Assert.Contains("x", AllText(new[] { blocks[1] }), StringComparison.Ordinal);
                Assert.Contains("y", AllText(new[] { blocks[1] }), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void With_outer_pipes_a_row_that_starts_like_a_list_item_stays_a_row()
        {
            var table = TableOf("| a | b |\n|---|---|\n| - x | y |\n| 1. x | # y |");
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { "- x", "y" }, Texts(table.Rows[0]));
            Assert.Equal(new[] { "1. x", "# y" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void A_line_of_plain_text_under_the_last_row_is_one_more_row()
        {
            var table = TableOf("| a | b |\n|---|---|\n| 1 | 2 |\nplain text");
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { "plain text", "" }, Texts(table.Rows[1]));
        }

        [Fact]
        public void Closed_is_true_for_a_closed_fence_false_for_an_open_one_and_true_for_everything_else()
        {
            Assert.True(Single("```js\nlet a;\n```").Closed);
            Assert.True(Single("~~~\nx\n~~~").Closed);
            Assert.False(Single("```js\nlet a;").Closed);
            Assert.False(Single("```js\nlet a;\n").Closed);
            Assert.False(Single("```").Closed);
            Assert.True(Single("plain text").Closed);
            Assert.True(Single("# Heading").Closed);
            Assert.True(Single("- item").Closed);
            Assert.True(Single("> quote").Closed);
            Assert.True(Single("---").Closed);
            Assert.True(Single("| a |\n|---|").Closed);
        }
    }
}
