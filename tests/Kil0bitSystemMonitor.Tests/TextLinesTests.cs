using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Whole-line helpers shared by the Format menu and the line operations.</summary>
    public class TextLinesTests
    {
        [Theory]
        [InlineData("first\nsecond\nthird", 9, 0, 6, 12)]          // caret inside "second"
        [InlineData("one\r\ntwo\r\nthree", 0, 10, 0, 8)]          // ends right after a CRLF: "three" not taken
        [InlineData("a\nb", 0, 3, 0, 3)]                            // everything
        [InlineData("", 0, 0, 0, 0)]                                // empty text
        [InlineData("x\ny", 2, 0, 2, 3)]                            // caret at the start of the last line
        [InlineData("x\ry\rz", 2, 1, 2, 3)]                         // CR-only endings
        public void Block_takes_the_whole_lines_a_selection_touches(string text, int start, int length, int blockStart, int blockEnd)
        {
            Assert.Equal((blockStart, blockEnd), TextLines.Block(text, start, length));
        }

        [Theory]
        [InlineData("a\r\nb\nc\rd")]
        [InlineData("single")]
        [InlineData("trailing\n")]
        [InlineData("")]
        public void Split_then_join_gives_back_the_same_text(string text)
        {
            var (lines, breaks) = TextLines.Split(text);
            Assert.Equal(lines.Count - 1, breaks.Count);
            Assert.Equal(text, TextLines.Join(lines, breaks));
        }

        [Fact]
        public void Split_remembers_each_line_break_exactly()
        {
            var (lines, breaks) = TextLines.Split("a\r\nb\nc\rd");
            Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
            Assert.Equal(new[] { "\r\n", "\n", "\r" }, breaks);
        }

        [Theory]
        [InlineData("a\r\nb", "\r\n")]
        [InlineData("a\nb", "\n")]
        [InlineData("a\rb", "\r")]
        [InlineData("none", "\r\n")]
        public void NewlineOf_is_the_first_break_or_crlf(string text, string expected)
        {
            Assert.Equal(expected, TextLines.NewlineOf(text));
        }

        [Fact]
        public void Line_bounds_and_breaks()
        {
            string text = "ab\r\ncd\nef";
            Assert.Equal(0, TextLines.LineStart(text, 1));
            Assert.Equal(4, TextLines.LineStart(text, 5));
            Assert.Equal(2, TextLines.LineEnd(text, 0));
            Assert.Equal(9, TextLines.LineEnd(text, 8));
            Assert.Equal(2, TextLines.BreakLength(text, 2));
            Assert.Equal(1, TextLines.BreakLength(text, 6));
            Assert.Equal(0, TextLines.BreakLength(text, 9));
            Assert.Equal(2, TextLines.BreakBefore(text, 4));   // the CRLF before "cd"
            Assert.Equal(1, TextLines.BreakBefore(text, 7));   // the LF before "ef"
            Assert.Equal(0, TextLines.BreakBefore(text, 0));
        }
    }
}
