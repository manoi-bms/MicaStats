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
            Assert.Contains(@"\f0\fs21\afs21 ", rtf);     // 14 px = 10.5 pt = 21 half-points, for Thai (\afs) too
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
            Assert.Contains(@"\plain\f0\cf2\b\ab\fs34\afs34 Title", rtf);      // 21 * 1.6 = 33.6, rounded to 34
            Assert.Contains(@"\plain\f0\cf1\i\ai\strike\fs21\afs21 body", rtf);
        }

        [Fact]
        public void Thai_gets_the_complex_script_size_bold_and_italic_too()
        {
            // Word formats Thai with the associated (complex-script) properties, and \plain resets them.
            string rtf = Write("# หัวข้อ", new RtfRun(0, 8, null, null, true, true, false, 1.6));
            Assert.Contains(@"\plain\f0\cf1\b\ab\i\ai\fs34\afs34 # " + U('ห'), rtf);
        }

        /// <summary>A character as RTF writes it: a signed decimal unicode escape and its fallback.</summary>
        private static string U(char c) => "\\u" + ((short)c).ToString(System.Globalization.CultureInfo.InvariantCulture) + "?";

        [Fact]
        public void Control_characters_become_spaces_and_the_rtf_stays_ascii()
        {
            string text = "a" + (char)0x0C + "b" + (char)0 + "c" + (char)0x01 + "d" + (char)0x1F + "e";
            string rtf = Write(text);
            Assert.Contains("a b c d e", rtf);
            Assert.All(rtf, c => Assert.True(c >= 0x20 || c == '\r' || c == '\n', "raw control character " + (int)c));
        }

        [Fact]
        public void Noncharacters_and_lone_surrogates_become_question_marks()
        {
            string text = "a" + (char)0xFFFF + "b" + (char)0xFFFE + "c" + (char)0xD83D + "d" + (char)0xDE00 + "e" + (char)0xD83D;
            string rtf = Write(text);
            Assert.Contains("a?b?c?d?e?}", rtf);
            Assert.DoesNotContain(@"\u-1?", rtf);
            Assert.DoesNotContain(@"\u-2?", rtf);
            Assert.DoesNotContain(U((char)0xD83D), rtf);
            Assert.DoesNotContain(U((char)0xDE00), rtf);
        }

        [Fact]
        public void A_surrogate_pair_is_still_written_as_its_two_halves()
        {
            string rtf = Write("x😀y");
            Assert.Contains("x" + U((char)0xD83D) + U((char)0xDE00) + "y", rtf);
        }

        [Fact]
        public void A_non_ascii_font_name_is_escaped()
        {
            string rtf = RtfWriter.Write("x", Array.Empty<RtfRun>(), "TH Sarabun {ฟ}", 14, Black);
            Assert.Contains(@"{\fonttbl{\f0\fmodern TH Sarabun \{" + U('ฟ') + @"\};}}", rtf);
            Assert.All(rtf, c => Assert.True(c < 0x80));
        }

        [Theory]
        [InlineData("abc", 3)]
        [InlineData("a\tb\r\n", 5)]
        [InlineData("กข", 16)]
        [InlineData("a😀", 17)]
        public void The_estimated_length_counts_ascii_as_one_and_the_rest_as_eight(string text, long expected)
        {
            Assert.Equal(expected, RtfWriter.EstimatedLength(text));
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
            Assert.Contains(@"\plain\f0\cf2\b\ab\fs21\afs21 x", rtf);
            Assert.Contains(@"\plain\f0\cf3\b\ab\i\ai\fs21\afs21 y", rtf);
        }

        [Fact]
        public void A_background_uses_character_shading()
        {
            var shade = PadColor.Parse("#EEEEF2");
            string rtf = Write("code", new RtfRun(0, 4, null, shade, false, false, false, 1));
            Assert.Contains(@"\chcbpat2", rtf);
        }

        [Fact]
        public void A_huge_text_without_runs_is_one_style_header_and_the_text()
        {
            string text = new string('a', 1_000_000);
            string rtf = Write(text);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(rtf, @"\\plain"));
            Assert.Contains(@"\fs21\afs21 " + text + "}", rtf);
        }

        [Fact]
        public void The_rtf_loads_in_wpf_with_the_same_text() => UiThread.Run(() =>
        {
            string text = "ไทย 😀 \\ { } a\tb\r\nc\nd\re" + (char)0x0C + "f" + (char)0 + "g";
            string rtf = RtfWriter.Write(text, new[] { new RtfRun(0, 3, null, null, true, false, false, 1.6) },
                                         "Cascadia Mono", 14, Black);
            var document = new System.Windows.Documents.FlowDocument();
            var range = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd);

            using (var stream = new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes(rtf)))
                range.Load(stream, System.Windows.DataFormats.Rtf);

            string loaded = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.Equal("ไทย 😀 \\ { } a\tb\r\nc\r\nd\r\ne f g\r\n", loaded);
        });
    }
}
