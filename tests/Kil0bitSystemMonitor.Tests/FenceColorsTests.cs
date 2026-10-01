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
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.EnsureVisualLines();
            return view;
        }

        private static PadColor? ForegroundAt(TextView view, int lineNumber, int column)
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
            var view = Render("plain\n```bash\necho hi\n```");

            Assert.Equal(ForegroundAt(view, 1, 0), ForegroundAt(view, 3, 0));
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
        });    }
}
