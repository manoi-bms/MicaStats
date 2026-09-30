using System;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Styled text to RTF.</summary>
    public class RtfWriterTests
    {
        private static readonly PadColor Black = PadColor.Parse("#1B1B1F");

        private static string Write(string text, params RtfRun[] runs) => RtfWriter.Write(text, runs, "Cascadia Mono", 14, Black);

        [Fact]
        public void A_document_has_the_font_color_table_and_size()
        {
            string rtf = Write("hi");
            Assert.StartsWith(@"{\rtf1\ansi", rtf);
            Assert.Contains(@"{\fonttbl{\f0\fmodern Cascadia Mono;}}", rtf);
            Assert.Contains(@"\red27\green27\blue31;", rtf);
            Assert.Contains(@"\fs21 ", rtf);              // 14 px = 10.5 pt = 21 half-points
            Assert.EndsWith("}", rtf);
            Assert.Contains("hi", rtf);
        }

        [Fact]
        public void Special_characters_are_escaped()
        {
            string rtf = Write("a\\b{c}d\te");
            Assert.Contains(@"a\\b\{c\}d\tab e", rtf);
        }

        [Theory]
        [InlineData("a\r\nb")]
        [InlineData("a\nb")]
        [InlineData("a\rb")]
        public void Every_line_ending_becomes_a_paragraph(string text)
        {
            string rtf = Write(text);
            Assert.Contains(@"a\par" + "\r\n" + "b", rtf);
        }

        [Fact]
        public void Thai_and_emoji_are_written_as_unicode()
        {
            string rtf = Write("ไทย 😀");
            Assert.Contains(@"\u3652?", rtf);             // ไ U+0E44
            Assert.Contains(@"\u-10179?\u-8704?", rtf);  // 😀 as a UTF-16 surrogate pair, signed
            Assert.DoesNotContain("ไ", rtf);
        }

        [Fact]
        public void Runs_become_bold_italic_strike_color_and_size()
        {
            var red = PadColor.Parse("#C42B1C");
            string rtf = Write("Title body",
                new RtfRun(0, 5, red, null, true, false, false, 1.6),
                new RtfRun(6, 4, null, null, false, true, true, 1));

            Assert.Contains(@"\red196\green43\blue28;", rtf);
            Assert.Matches(@"\\cf2\\b\\fs34 Title", rtf);      // 21 * 1.6 = 33.6, rounded to 34
            Assert.Matches(@"\\i\\strike\\fs21 body|\\i\\strike body", rtf);
        }

        [Fact]
        public void Overlapping_runs_combine_and_a_later_color_wins()
        {
            var a = PadColor.Parse("#111111");
            var b = PadColor.Parse("#222222");
            string rtf = Write("xyz",
                new RtfRun(0, 3, a, null, true, false, false, 1),
                new RtfRun(1, 1, b, null, false, true, false, 1));
            // x: bold a; y: bold italic b; z: bold a
            Assert.Matches(@"\\cf2\\b\\fs21 x", rtf);
            Assert.Matches(@"\\cf3\\b\\i\\fs21 y", rtf);
        }

        [Fact]
        public void A_background_uses_character_shading()
        {
            var shade = PadColor.Parse("#EEEEF2");
            string rtf = Write("code", new RtfRun(0, 4, null, shade, false, false, false, 1));
            Assert.Contains(@"\chcbpat2", rtf);
        }
    }
}
