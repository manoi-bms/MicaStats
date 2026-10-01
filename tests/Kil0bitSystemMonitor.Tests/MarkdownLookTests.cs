using System.Linq;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The looks of the new Markdown styles (spec 1.4, 3, 5).</summary>
    public class MarkdownLookTests
    {
        [Fact]
        public void Inline_code_is_monospace_in_the_code_color_on_the_code_background()
        {
            var dark = MarkdownStyles.LookOf(MdStyle.Code, PadPalette.Dark);
            var light = MarkdownStyles.LookOf(MdStyle.Code, PadPalette.Light);

            Assert.True(dark.Mono);
            Assert.Equal(PadColor.Parse("#F2A97B"), dark.Foreground);
            Assert.Equal(PadPalette.Dark.MdCodeBackground, dark.Background);
            Assert.Equal(PadColor.Parse("#A33D1F"), light.Foreground);
        }

        [Fact]
        public void Small_raised_and_lowered_text_is_three_quarters_size()
        {
            var palette = PadPalette.Dark;

            Assert.Equal(0.75, MarkdownStyles.SmallSize);
            Assert.Equal(MdBaseline.Superscript, MarkdownStyles.LookOf(MdStyle.Superscript, palette).Baseline);
            Assert.Equal(MdBaseline.Subscript, MarkdownStyles.LookOf(MdStyle.Subscript, palette).Baseline);
            var note = MarkdownStyles.LookOf(MdStyle.FootnoteRef, palette);
            Assert.Equal(MdBaseline.Superscript, note.Baseline);
            Assert.Equal(0.75, note.SizeFactor);
            Assert.Equal(palette.MdLink, note.Foreground);
        }

        [Fact]
        public void Keys_tables_abbreviations_and_math_have_their_looks()
        {
            var palette = PadPalette.Light;

            var key = MarkdownStyles.LookOf(MdStyle.KbdText, palette);
            Assert.True(key.Mono);
            Assert.Equal(palette.MdKbdBackground, key.Background);
            Assert.Equal(MdWeight.Bold, MarkdownStyles.LookOf(MdStyle.TableHeader, palette).Weight);
            Assert.True(MarkdownStyles.LookOf(MdStyle.Abbreviation, palette).Dotted);
            Assert.Equal(palette.MdMath, MarkdownStyles.LookOf(MdStyle.MathText, palette).Foreground);
            Assert.False(MarkdownStyles.LookOf(MdStyle.Bold, palette).Mono);
        }

        private static readonly FontFamily Mono = new("Consolas");

        private static TextView Render(string text, out MarkdownDocumentCache cache)
        {
            cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument(text) };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark, null, () => Mono));
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            view.EnsureVisualLines();
            return view;
        }

        /// <summary>The element of line <paramref name="lineNumber"/> holding column <paramref name="column"/> (0-based).</summary>
        private static VisualLineElement ElementAt(TextView view, int lineNumber, int column) =>
            view.GetVisualLine(lineNumber)!.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);

        [Fact]
        public void Code_tables_and_front_matter_are_monospace_and_prose_is_not() => UiThread.Run(() =>
        {
            var view = Render("---\nk: v\n---\ntext `code` here\n```\nx\n```\n| a | b |\n|---|---|", out _);

            Assert.Equal("Consolas", ElementAt(view, 2, 0).TextRunProperties.Typeface.FontFamily.Source);   // front matter
            Assert.NotEqual("Consolas", ElementAt(view, 4, 0).TextRunProperties.Typeface.FontFamily.Source); // prose
            Assert.Equal("Consolas", ElementAt(view, 4, 6).TextRunProperties.Typeface.FontFamily.Source);   // inline code
            Assert.Equal("Consolas", ElementAt(view, 6, 0).TextRunProperties.Typeface.FontFamily.Source);   // fenced
            Assert.Equal("Consolas", ElementAt(view, 8, 2).TextRunProperties.Typeface.FontFamily.Source);   // table header
            Assert.Equal(FontWeights.Bold, ElementAt(view, 8, 2).TextRunProperties.Typeface.Weight);
        });

        [Fact]
        public void Scripts_are_small_and_shifted_and_abbreviations_dotted() => UiThread.Run(() =>
        {
            var view = Render("H~2~O x^3^\n*[AB]: a b\nsee AB", out _);
            double size = ElementAt(view, 1, 0).TextRunProperties.FontRenderingEmSize;

            var sub = ElementAt(view, 1, 2).TextRunProperties;
            Assert.Equal(BaselineAlignment.Subscript, sub.BaselineAlignment);
            Assert.Equal(size * 0.75, sub.FontRenderingEmSize, 3);
            Assert.Equal(BaselineAlignment.Superscript, ElementAt(view, 1, 8).TextRunProperties.BaselineAlignment);

            var abbreviation = ElementAt(view, 3, 4).TextRunProperties.TextDecorations;
            Assert.NotNull(abbreviation);
            Assert.Contains(abbreviation!, d => d.Location == TextDecorationLocation.Underline && d.Pen?.DashStyle == DashStyles.Dot);
        });

        [Fact]
        public void Without_a_mono_font_nothing_changes_family() => UiThread.Run(() =>
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument("text `code`\n```\nx\n```") };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark));
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            view.EnsureVisualLines();

            string family = ElementAt(view, 1, 0).TextRunProperties.Typeface.FontFamily.Source;
            Assert.Equal(family, ElementAt(view, 1, 6).TextRunProperties.Typeface.FontFamily.Source);
            Assert.Equal(family, ElementAt(view, 3, 0).TextRunProperties.Typeface.FontFamily.Source);
        });

        [Fact]
        public void Quotes_draw_a_bar_per_level_and_callouts_a_tint() => UiThread.Run(() =>
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument("> > a\n> b\n{.is-danger}\n> plain") };
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            view.EnsureVisualLines();
            var renderer = new MarkdownBackgroundRenderer(cache, () => PadPalette.Dark);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) renderer.Draw(view, context);
            var rects = Rectangles(visual.Drawing).ToList();

            var danger = PadPalette.Dark.MdCalloutDanger;
            Color Of(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
            double top1 = view.GetVisualLine(1)!.VisualTop;
            Assert.Contains(rects, r => r.Rect.X == 0 && r.Rect.Width == 3 && r.Rect.Y == top1 && r.Color == Of(danger));
            Assert.Contains(rects, r => r.Rect.X == 8 && r.Rect.Width == 3 && r.Rect.Y == top1 && r.Color == Of(danger));
            Assert.Contains(rects, r => r.Rect.Width > 100 && r.Rect.Y == top1 && r.Color == Of(danger with { A = 0x26 }));

            double top4 = view.GetVisualLine(4)!.VisualTop;
            Assert.Contains(rects, r => r.Rect.X == 0 && r.Rect.Y == top4 && r.Color == Of(PadPalette.Dark.MdQuoteBar));
            Assert.DoesNotContain(rects, r => r.Rect.Y == top4 && r.Rect.Width > 100);
        });

        private static System.Collections.Generic.IEnumerable<(Rect Rect, Color Color)> Rectangles(Drawing? drawing)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children)
                        foreach (var r in Rectangles(child)) yield return r;
                    break;
                case GeometryDrawing { Geometry: RectangleGeometry rect, Brush: SolidColorBrush brush }:
                    yield return (rect.Rect, brush.Color);
                    break;
            }
        }
    }
}
