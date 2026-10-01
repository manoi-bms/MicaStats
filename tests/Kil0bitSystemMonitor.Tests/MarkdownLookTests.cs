using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

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
    }
}
