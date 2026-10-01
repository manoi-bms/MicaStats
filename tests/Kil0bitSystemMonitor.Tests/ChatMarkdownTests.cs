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
    }
}
