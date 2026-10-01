using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Wiki.js constructs of a line (spec 2-5): structure-dependent blocks and the inline extras.</summary>
    public class MarkdownExtrasTests
    {
        private static List<(string Text, MdStyle Style)> Runs(string line, MdLineFacts facts = default, IReadOnlySet<string>? terms = null) =>
            MarkdownLineTokenizer.Tokenize(line, facts, terms).Spans.Select(s => (line.Substring(s.Start, s.Length), s.Style)).ToList();

        private static MdLineFacts Plain => new(MdFence.None);

        [Fact]
        public void Table_lines_have_a_bold_header_and_dimmed_pipes()
        {
            var header = MarkdownLineTokenizer.Tokenize("| a | **b** |", Plain with { Table = MdTableRole.Header });
            Assert.Equal(MdBlock.Table, header.Block);
            var runs = Runs("| a | **b** |", Plain with { Table = MdTableRole.Header });
            Assert.Contains(("| a | **b** |", MdStyle.TableHeader), runs);
            Assert.Equal(3, runs.Count(r => r.Text == "|" && r.Style == MdStyle.Marker));
            Assert.Contains(("b", MdStyle.Bold), runs);

            Assert.Equal(new[] { ("|:--|--:|", MdStyle.Marker) }, Runs("|:--|--:|", Plain with { Table = MdTableRole.Delimiter }));
            var row = Runs(@"| x \| y | `c|d` |", Plain with { Table = MdTableRole.Row });
            Assert.DoesNotContain(row, r => r.Style == MdStyle.TableHeader);
            Assert.Contains(("c|d", MdStyle.Code), row);
        }

        [Fact]
        public void Setext_text_is_a_heading_and_its_underline_is_dimmed()
        {
            var text = MarkdownLineTokenizer.Tokenize("Title", Plain with { SetextLevel = 1 });
            Assert.Equal(MdBlock.Heading, text.Block);
            Assert.Contains(("Title", MdStyle.Heading1), Runs("Title", Plain with { SetextLevel = 1 }));
            Assert.Contains(("Sub", MdStyle.Heading2), Runs("Sub", Plain with { SetextLevel = 2 }));

            var under = MarkdownLineTokenizer.Tokenize("---", Plain with { SetextUnderline = true });
            Assert.Equal(MdBlock.SetextUnderline, under.Block);
            Assert.Equal(new[] { ("---", MdStyle.Marker) }, Runs("---", Plain with { SetextUnderline = true }));
        }

        [Fact]
        public void Front_matter_and_callout_class_lines_are_dimmed()
        {
            Assert.Equal(MdBlock.FrontMatter, MarkdownLineTokenizer.Tokenize("title: x", Plain with { FrontMatter = true }).Block);
            Assert.Equal(new[] { ("title: **x**", MdStyle.Marker) }, Runs("title: **x**", Plain with { FrontMatter = true }));
            Assert.Equal(MdBlock.CalloutClass, MarkdownLineTokenizer.Tokenize("{.is-info}", Plain with { CalloutClass = true }).Block);
            Assert.Equal(new[] { ("{.is-info}", MdStyle.Marker) }, Runs("{.is-info}", Plain with { CalloutClass = true }));
        }

        [Theory]
        [InlineData("> a", 1)]
        [InlineData(">> a", 2)]
        [InlineData("> > > a", 3)]
        [InlineData("   >a", 1)]
        [InlineData("a > b", 0)]
        [InlineData(">>>>>>>> deep", 6)]
        public void Quote_depth_counts_the_markers(string line, int depth) => Assert.Equal(depth, MarkdownLineTokenizer.QuoteDepth(line));

        [Fact]
        public void Nested_quotes_dim_every_marker()
        {
            var line = MarkdownLineTokenizer.Tokenize("> > inner **b**", Plain);
            Assert.Equal(MdBlock.Quote, line.Block);
            Assert.Equal(2, line.QuoteDepth);
            var runs = Runs("> > inner **b**");
            Assert.Equal(2, runs.Count(r => r.Text == ">" && r.Style == MdStyle.Marker));
            Assert.Contains(("inner **b**", MdStyle.QuoteText), runs);
            Assert.Contains(("b", MdStyle.Bold), runs);
        }

        [Fact]
        public void Definitions_dim_their_labels()
        {
            Assert.Equal(MdBlock.Definition, MarkdownLineTokenizer.Tokenize("[^1]: the note", Plain).Block);
            Assert.Contains(("[^1]:", MdStyle.Marker), Runs("[^1]: the note"));
            Assert.Contains(("*[HTML]:", MdStyle.Marker), Runs("*[HTML]: Hyper Text"));
            var reference = Runs("[docs]: https://example.com \"Docs\"");
            Assert.Contains(("[docs]:", MdStyle.Marker), reference);
            Assert.Contains(("https://example.com", MdStyle.LinkText), reference);
        }

        [Fact]
        public void Footnote_references_are_small_raised_links()
        {
            var runs = Runs("text[^note] more");
            Assert.Contains(("[^", MdStyle.Marker), runs);
            Assert.Contains(("note", MdStyle.FootnoteRef), runs);
            Assert.Contains(("]", MdStyle.Marker), runs);
            Assert.DoesNotContain(runs, r => r.Style == MdStyle.LinkText);
        }

        [Fact]
        public void Reference_links_and_images()
        {
            var runs = Runs("see [docs][d] and [faq][] and ![logo](a.png)");
            Assert.Contains(("docs", MdStyle.LinkText), runs);
            Assert.Contains(("][d]", MdStyle.Marker), runs);
            Assert.Contains(("faq", MdStyle.LinkText), runs);
            Assert.Contains(("][]", MdStyle.Marker), runs);
            Assert.Contains(("!", MdStyle.Marker), runs);
            Assert.Contains(("logo", MdStyle.LinkText), runs);
        }

        [Fact]
        public void Sub_and_superscript()
        {
            Assert.Contains(("2", MdStyle.Subscript), Runs("H~2~O"));
            Assert.Contains(("2", MdStyle.Superscript), Runs("x^2^ + y"));
            Assert.Contains(("gone", MdStyle.Strike), Runs("~~gone~~"));
            Assert.Equal(2, Runs("H~2~O").Count(r => r.Text == "~" && r.Style == MdStyle.Marker));
        }

        [Fact]
        public void Keys_tags_attributes_and_escapes()
        {
            var keys = Runs("press <kbd>Ctrl</kbd>+<KBD>C</KBD>");
            Assert.Contains(("Ctrl", MdStyle.KbdText), keys);
            Assert.Contains(("C", MdStyle.KbdText), keys);
            Assert.Contains(("<kbd>", MdStyle.Marker), keys);
            Assert.Contains(("</kbd>", MdStyle.Marker), keys);

            var tags = Runs("a<br>b <sup>x</sup> <!-- note -->");
            Assert.Contains(("<br>", MdStyle.Marker), tags);
            Assert.Contains(("<sup>", MdStyle.Marker), tags);
            Assert.Contains(("<!-- note -->", MdStyle.Marker), tags);

            Assert.Contains(("{.tabset}", MdStyle.Marker), Runs("# Tabs {.tabset}"));
            Assert.Contains(("{#id .wide}", MdStyle.Marker), Runs("text {#id .wide}"));
            Assert.Contains((@"\", MdStyle.Marker), Runs(@"\*not italic\*"));
        }

        [Fact]
        public void Inline_math_is_colored()
        {
            var runs = Runs("area $\\pi r^2$ and $$E=mc^2$$");
            Assert.Contains(("\\pi r^2", MdStyle.MathText), runs);
            Assert.Contains(("E=mc^2", MdStyle.MathText), runs);
            Assert.DoesNotContain(runs, r => r.Style == MdStyle.Superscript);
        }

        [Theory]
        [InlineData("costs $5 and $10")]
        [InlineData("a < b and c > d")]
        [InlineData("x^ 2^")]
        [InlineData("a~b")]
        [InlineData("$ x$")]
        [InlineData("x$5")]
        [InlineData("{not attributes} here")]
        public void Text_that_only_looks_like_syntax_stays_text(string line)
        {
            Assert.DoesNotContain(Runs(line), r => r.Style is MdStyle.MathText or MdStyle.Superscript or MdStyle.Subscript or MdStyle.Marker);
        }

        [Fact]
        public void Code_hides_the_extras()
        {
            var runs = Runs("`$x$ <kbd>K</kbd> H~2~O`");
            Assert.Single(runs, r => r.Style == MdStyle.Code);
            Assert.DoesNotContain(runs, r => r.Style is MdStyle.MathText or MdStyle.KbdText or MdStyle.Subscript);
        }

        [Fact]
        public void Abbreviations_get_a_dotted_underline_on_whole_words_outside_code()
        {
            var terms = new HashSet<string> { "HTML" };
            var runs = Runs("HTML and XHTML and `HTML` and HTML5 and (HTML)", default, terms);

            Assert.Equal(2, runs.Count(r => r.Style == MdStyle.Abbreviation));
            Assert.Equal(new[] { "HTML", "HTML" }, runs.Where(r => r.Style == MdStyle.Abbreviation).Select(r => r.Text));
            Assert.DoesNotContain(Runs("*[HTML]: Hyper Text", default, terms), r => r.Style == MdStyle.Abbreviation);
        }

        [Fact]
        public void The_old_overloads_still_work()
        {
            Assert.Equal(MdBlock.Quote, MarkdownLineTokenizer.BlockOf("> q", MdFence.None));
            Assert.Equal(MdBlock.Fence, MarkdownLineTokenizer.BlockOf("x", MdFence.Inside));
            Assert.Equal(MdBlock.Table, MarkdownLineTokenizer.BlockOf("| a |", Plain with { Table = MdTableRole.Row }));
            Assert.Equal(new[] { ("```cs", MdStyle.Marker) },
                MarkdownLineTokenizer.Tokenize("```cs", MdFence.Delimiter).Spans.Select(s => ("```cs".Substring(s.Start, s.Length), s.Style)));
        }
    }
}
