using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Colors inside fenced code (spec 1.3): the same as a whole file in that language.</summary>
    public class FenceColorsTests
    {
        private static TextView Render(string text)
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument(text) };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark));
            Layout(view);
            return view;
        }

        /// <summary>What a shown view does after an edit: runs what was queued, then lays out again (no explicit Redraw).</summary>
        private static void Layout(TextView view)
        {
            PadLanguageWindowTests.Pump();
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.EnsureVisualLines();
        }

        private static IHighlightingDefinition? Definition(string? id) => PadLanguages.ById(id) is { } language ? PadHighlighting.For(language) : null;

        private static List<(int, int, string?)> Sections(HighlightedLine line) =>
            line.Sections.Select(s => (s.Offset, s.Length, s.Color?.Name)).ToList();

        internal static PadColor? ForegroundAt(TextView view, int lineNumber, int column)
        {
            var element = view.GetVisualLine(lineNumber)!.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
            return element.TextRunProperties.ForegroundBrush is SolidColorBrush b ? new PadColor(b.Color.A, b.Color.R, b.Color.G, b.Color.B) : null;
        }

        /// <summary>What whole-file highlighting paints at each column of line <paramref name="lineNumber"/> of <paramref name="code"/>: the last section covering it with a palette color wins.</summary>
        private static Dictionary<int, PadColor> WholeFileColors(string languageId, string code, int lineNumber)
        {
            var definition = PadHighlighting.For(PadLanguages.ById(languageId)!)!;
            var document = new TextDocument(code);
            var highlighter = new DocumentHighlighter(document, definition);
            var colors = new Dictionary<int, PadColor>();
            var line = document.GetLineByNumber(lineNumber);
            foreach (var section in highlighter.HighlightLine(lineNumber).Sections)
            {
                var color = section.Color;
                PadColor? original = color?.Foreground?.GetColor(null) is Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
                if (color == null || SyntaxColors.Resolve(color.Name, original, PadPalette.Dark) is not PadColor paint) continue;
                for (int i = section.Offset; i < section.Offset + section.Length; i++) colors[i - line.Offset] = paint;
            }
            return colors;
        }

        [Fact]
        public void A_csharp_fence_is_colored_like_a_csharp_file() => UiThread.Run(() =>
        {
            const string code = "public class A { string s = \"x\"; } // done";
            var view = Render("text\n```cs\n" + code + "\n```");
            var expected = WholeFileColors("csharp", code, 1);

            Assert.NotEmpty(expected);
            foreach (var (column, color) in expected) Assert.Equal(color, ForegroundAt(view, 3, column));
        });

        [Fact]
        public void A_comment_that_spans_lines_stays_a_comment() => UiThread.Run(() =>
        {
            const string code = "/* start\nend */ int x;";
            var view = Render("```csharp\n" + code + "\n```");
            var expected = WholeFileColors("csharp", code, 2);

            Assert.True(expected.ContainsKey(0));
            Assert.Equal(expected[0], ForegroundAt(view, 3, 0));
        });

        [Fact]
        public void An_unknown_language_is_not_colored() => UiThread.Run(() =>
        {
            var view = Render("plain\n```klingon\necho hi\n```");

            Assert.Equal(ForegroundAt(view, 1, 0), ForegroundAt(view, 3, 0));
        });

        [Theory]
        [InlineData("bash", "shell", "# note", "# note")]
        [InlineData("md", "markdown-fence", "# Title", "# Title")]
        [InlineData("ts", "typescript", "interface A { b: string }", "interface")]
        public void A_new_language_fence_is_colored_like_a_file_in_that_language(string word, string id, string code, string part) => UiThread.Run(() =>
        {
            var view = Render("text\n```" + word + "\n" + code + "\n```");
            var definition = PadHighlighting.For(PadLanguages.ForFence(id)!)!;
            var document = new TextDocument(code);
            int start = code.IndexOf(part, System.StringComparison.Ordinal);
            var section = new DocumentHighlighter(document, definition).HighlightLine(1).Sections.First(x => x.Offset == start && x.Color != null);
            var expected = SyntaxColors.Resolve(section.Color.Name, null, PadPalette.Dark)!.Value;

            Assert.NotEqual(ForegroundAt(view, 1, 0), expected);
            Assert.Equal(expected, ForegroundAt(view, 3, start));
        });

        private static List<string> FunctionNames(HighlightedLine line) =>
            line.Sections.Where(s => SyntaxColors.Categorize(s.Color?.Name) == SyntaxCategory.Function)
                .Select(s => line.Document.GetText(s.Offset, s.Length)).ToList();

        [Fact]
        public void A_go_fence_colors_function_names() => UiThread.Run(() =>
        {
            var view = Render("text\n```go\nfunc main() {\n```");
            var line = new FenceHighlighter(new MarkdownDocumentCache()).HighlightLine(view.Document, 3)!;

            line.ValidateInvariants();
            Assert.Equal(new[] { "main" }, FunctionNames(line));
            Assert.Equal(PadPalette.Dark.SyntaxFunction, ForegroundAt(view, 3, 5));
            Assert.Equal(PadPalette.Dark.SyntaxKeyword, ForegroundAt(view, 3, 0));   // func stays a keyword
        });

        [Fact]
        public void A_sql_fence_gets_no_function_pass() => UiThread.Run(() =>
        {
            const string code = "SELECT COUNT(*), dbo.fn_total(1)";
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache());

            Assert.Empty(FunctionNames(highlighter.HighlightLine(new TextDocument("```sql\n" + code + "\n```"), 2)!));
            Assert.Equal(new[] { "COUNT", "fn_total" }, FunctionNames(highlighter.HighlightLine(new TextDocument("```js\n" + code + "\n```"), 2)!));
        });

        [Fact]
        public void A_failing_function_pass_in_a_fence_is_logged_once_on_its_own_and_the_block_keeps_its_colors() => UiThread.Run(() =>
        {
            var failures = new List<System.Exception>();
            var warnings = new List<string>();
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache(), null, failures.Add, warnings.Add)
            {
                FunctionPass = (line, failed) => failed(new System.InvalidOperationException("boom")),
            };
            var document = new TextDocument("```cs\nint a = f(1);\nint b = g(2);\n```");

            var first = highlighter.HighlightLine(document, 2);
            var second = highlighter.HighlightLine(document, 3);

            Assert.Empty(failures);                                         // not the block's failure: its colors stay
            Assert.NotEmpty(first!.Sections);
            Assert.NotEmpty(second!.Sections);
            var warning = Assert.Single(warnings);
            Assert.StartsWith("Function colors", warning);
            Assert.Contains("InvalidOperationException", warning);
            Assert.DoesNotContain("boom", warning);
        });

        [Fact]
        public void The_markdown_colorizer_hands_its_log_to_the_fence_function_pass() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var colorizer = new MarkdownColorizer(new MarkdownDocumentCache(), () => PadPalette.Dark, null, null, warnings.Add);

            colorizer.Fences.FunctionPass = (line, failed) => failed(new System.InvalidOperationException());
            var view = new TextView { Document = new TextDocument("```go\nfunc main() {\nf(1)\n```\nafter") };
            view.LineTransformers.Add(colorizer);
            Layout(view);

            Assert.StartsWith("Function colors", Assert.Single(warnings));
        });

        [Fact]
        public void A_block_over_2000_inside_lines_is_not_colored_at_all() => UiThread.Run(() =>
        {
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache());
            var over = new TextDocument("```cs\n" + string.Join("\n", Enumerable.Repeat("int x;", 2001)) + "\n```");
            var exact = new TextDocument("```cs\n" + string.Join("\n", Enumerable.Repeat("int x;", 2000)) + "\n```");
            var unclosed = new TextDocument("```cs\n" + string.Join("\n", Enumerable.Repeat("int x;", 2001)));

            Assert.Null(highlighter.HighlightLine(over, 2));
            Assert.Null(highlighter.HighlightLine(over, 2002));
            Assert.Null(highlighter.HighlightLine(unclosed, 2));
            Assert.NotNull(highlighter.HighlightLine(exact, 2));
            Assert.NotNull(highlighter.HighlightLine(exact, 2001));
            Assert.Null(highlighter.HighlightLine(exact, 1));   // the fence line itself
        });

        [Fact]
        public void A_line_over_4000_characters_ends_the_colors_and_edits_are_followed() => UiThread.Run(() =>
        {
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache());
            var wide = new TextDocument("```cs\nint x;\n" + new string('a', 4001) + "\nint y;\n```");
            Assert.NotNull(highlighter.HighlightLine(wide, 2));
            Assert.Null(highlighter.HighlightLine(wide, 3));
            Assert.Null(highlighter.HighlightLine(wide, 4));

            var small = new TextDocument("```cs\nint x;\n```");
            var line = highlighter.HighlightLine(small, 2)!;
            int sections = line.Sections.Count;
            small.Insert(small.GetLineByNumber(2).EndOffset, " // note");
            Assert.True(highlighter.HighlightLine(small, 2)!.Sections.Count > sections);
        });

        [Fact]
        public void A_failure_inside_a_fence_costs_only_that_blocks_colors() => UiThread.Run(() =>
        {
            int lookups = 0;
            var failures = new List<System.Exception>();
            var warnings = new List<string>();
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument("```cs\nint x;\nint y;\n```\nplain `code` here") };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark, failures.Add, null, warnings.Add,
                id => { lookups++; throw new System.InvalidOperationException("boom"); }));
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.EnsureVisualLines();

            Assert.Empty(failures);                       // the note stays Markdown
            Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warnings[0]);
            Assert.DoesNotContain("boom", warnings[0]);
            Assert.Equal(1, lookups);                     // the dead block is not retried on the next line
            Assert.Equal(ForegroundAt(view, 2, 0), ForegroundAt(view, 3, 0));
            Assert.NotEqual(ForegroundAt(view, 5, 0), ForegroundAt(view, 5, 7));   // `code` is still styled
        });

        [Fact]
        public void Opening_a_comment_recolors_the_lines_below_it() => UiThread.Run(() =>
        {
            var view = Render("```cs\nint a;\nint b;\nint c;\n```\nafter");
            var document = view.Document;
            var keyword = ForegroundAt(view, 4, 0);

            document.Insert(document.GetLineByNumber(3).Offset, "/*");
            Layout(view);

            var comment = WholeFileColors("csharp", "int a;\n/*int b;\nint c;", 3)[0];
            Assert.NotEqual(keyword, comment);
            Assert.Equal(comment, ForegroundAt(view, 3, 0));   // the edited line
            Assert.Equal(comment, ForegroundAt(view, 4, 0));   // and the one below it
        });

        [Fact]
        public void Naming_the_language_of_a_bare_fence_colors_its_block() => UiThread.Run(() =>
        {
            const string code = "public class A { string s = \"x\"; } // done";
            var view = Render("```\n" + code + "\n```");
            var document = view.Document;
            var plain = ForegroundAt(view, 2, 0);

            document.Insert(3, "cs");
            Layout(view);

            var expected = WholeFileColors("csharp", code, 1);
            Assert.NotEqual(plain, expected[0]);
            foreach (var (column, color) in expected) Assert.Equal(color, ForegroundAt(view, 2, column));
        });

        [Fact]
        public void An_edit_keeps_the_colors_above_it_and_recolors_from_it_on() => UiThread.Run(() =>
        {
            int lookups = 0;
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache(), id =>
            {
                lookups++;
                return Definition(id);
            });
            var document = new TextDocument("```cs\nint a;\nint b;\nint c;\n```\n```cs\nint d;\n```");
            var above = highlighter.HighlightLine(document, 2);
            var below = highlighter.HighlightLine(document, 4);
            Assert.NotNull(highlighter.HighlightLine(document, 7));
            Assert.Equal(2, lookups);

            document.Insert(document.GetLineByNumber(3).Offset, "/*");

            Assert.Same(above, highlighter.HighlightLine(document, 2));     // above the edit: kept
            var again = highlighter.HighlightLine(document, 4)!;
            Assert.NotSame(below, again);                                   // from the edit on: highlighted again
            var fresh = new FenceHighlighter(new MarkdownDocumentCache()).HighlightLine(document, 4)!;
            Assert.Equal(Sections(fresh), Sections(again));
            Assert.Equal(2, lookups);                                       // the edited block was not started over
            Assert.NotNull(highlighter.HighlightLine(document, 7));
            Assert.Equal(3, lookups);                                       // a block below the edit was
        });

        [Fact]
        public void Shortening_a_wide_line_and_any_edit_after_a_failure_bring_the_colors_back() => UiThread.Run(() =>
        {
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache());
            var wide = new TextDocument("```cs\nint x;\n" + new string('a', 4001) + "\nint y;\n```");
            Assert.NotNull(highlighter.HighlightLine(wide, 2));
            Assert.Null(highlighter.HighlightLine(wide, 4));
            wide.Remove(wide.GetLineByNumber(3).Offset, 3990);
            Assert.NotNull(highlighter.HighlightLine(wide, 4));

            int calls = 0;
            var flaky = new FenceHighlighter(new MarkdownDocumentCache(), id => calls++ == 0 ? throw new System.InvalidOperationException() : Definition(id));
            var document = new TextDocument("```cs\nint a;\n```\ntext");
            Assert.Null(flaky.HighlightLine(document, 2));
            document.Insert(document.TextLength, "x");                      // an edit below the block
            Assert.NotNull(flaky.HighlightLine(document, 2));
        });
    }
}
