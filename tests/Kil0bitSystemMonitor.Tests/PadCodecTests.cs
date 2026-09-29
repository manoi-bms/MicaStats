using System;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The codec decides whether Ctrl+S gives back the file the user opened or a quietly
    /// re-encoded copy. Every case here is one where a wrong answer changes bytes on disk.
    /// </summary>
    public class PadCodecTests
    {
        private const int Thai = 874;

        /// <summary>"สวัสดี", written with escapes so this file's own encoding cannot matter.</summary>
        private const string Sawasdee = "สวัสดี";

        /// <summary>The same word in TIS-620 / cp874. Not valid UTF-8: 0xCA opens a two-byte sequence that 0xC7 does not continue.</summary>
        private static readonly byte[] SawasdeeCp874 = { 0xCA, 0xC7, 0xD1, 0xCA, 0xB4, 0xD5 };

        [Fact]
        public void Utf8_with_bom_is_detected_and_round_trips()
        {
            byte[] bytes = { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', 0x0D, 0x0A };

            Assert.True(TextFileCodec.TryDecode(bytes, Thai, out var d, out _));
            Assert.Equal("hi\r\n", d!.Text);
            Assert.Equal(PadEncoding.Utf8Bom, d.Encoding);
            Assert.Equal(bytes, TextFileCodec.Encode(d.Text, d.Encoding, d.CodePage));
        }

        [Theory]
        [InlineData(PadEncoding.Utf16Le)]
        [InlineData(PadEncoding.Utf16Be)]
        public void Utf16_with_bom_is_detected_and_round_trips(PadEncoding encoding)
        {
            byte[] bytes = TextFileCodec.Encode(Sawasdee + "\nhello", encoding, 0);

            Assert.True(TextFileCodec.TryDecode(bytes, Thai, out var d, out _));
            Assert.Equal(encoding, d!.Encoding);
            Assert.Equal(Sawasdee + "\nhello", d.Text);
            Assert.Equal(bytes, TextFileCodec.Encode(d.Text, d.Encoding, d.CodePage));
        }

        [Fact]
        public void Utf8_without_bom_is_detected_and_round_trips()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Sawasdee + " world\n");

            Assert.True(TextFileCodec.TryDecode(bytes, Thai, out var d, out _));
            Assert.Equal(PadEncoding.Utf8, d!.Encoding);
            Assert.Equal(bytes, TextFileCodec.Encode(d.Text, d.Encoding, d.CodePage));
        }

        [Fact]
        public void Invalid_utf8_falls_back_to_the_ansi_code_page_and_round_trips()
        {
            Assert.True(TextFileCodec.TryDecode(SawasdeeCp874, Thai, out var d, out _));
            Assert.Equal(PadEncoding.Ansi, d!.Encoding);
            Assert.Equal(Thai, d.CodePage);
            Assert.Equal(Sawasdee, d.Text);
            Assert.Equal(SawasdeeCp874, TextFileCodec.Encode(d.Text, d.Encoding, d.CodePage));
        }

        [Fact]
        public void A_nul_byte_without_a_bom_is_refused_as_binary()
        {
            byte[] bytes = { (byte)'M', (byte)'Z', 0x00, 0x01 };

            Assert.False(TextFileCodec.TryDecode(bytes, Thai, out var d, out var failure));
            Assert.Null(d);
            Assert.Equal(DecodeFailure.Binary, failure);
        }

        [Fact]
        public void A_file_over_the_limit_is_refused_as_too_large()
        {
            byte[] bytes = new byte[TextFileCodec.MaxFileBytes + 1];

            Assert.False(TextFileCodec.TryDecode(bytes, Thai, out _, out var failure));
            Assert.Equal(DecodeFailure.TooLarge, failure);
        }

        [Theory]
        [InlineData("a\r\nb\r\nc\n", LineEnding.CrLf)]
        [InlineData("a\nb\nc\r\n", LineEnding.Lf)]
        [InlineData("a\rb\rc", LineEnding.Cr)]
        [InlineData("no breaks", LineEnding.CrLf)]
        [InlineData("", LineEnding.CrLf)]
        public void The_most_frequent_line_ending_wins(string text, LineEnding expected)
        {
            Assert.Equal(expected, TextFileCodec.DetectLineEnding(text));
        }

        [Fact]
        public void Converting_line_endings_rewrites_every_break()
        {
            Assert.Equal("a\nb\nc\nd", TextFileCodec.ConvertLineEndings("a\r\nb\rc\nd", LineEnding.Lf));
            Assert.Equal("a\r\nb\r\n", TextFileCodec.ConvertLineEndings("a\nb\r", LineEnding.CrLf));
        }

        [Fact]
        public void Thai_text_cannot_be_saved_losslessly_in_a_western_code_page()
        {
            Assert.False(TextFileCodec.CanEncodeLosslessly(Sawasdee, PadEncoding.Ansi, 1252));
            Assert.True(TextFileCodec.CanEncodeLosslessly(Sawasdee, PadEncoding.Ansi, Thai));
            Assert.True(TextFileCodec.CanEncodeLosslessly(Sawasdee, PadEncoding.Utf8, 0));
        }

        [Fact]
        public void A_lone_surrogate_cannot_be_saved_losslessly_as_utf8()
        {
            Assert.False(TextFileCodec.CanEncodeLosslessly("a\uD800b", PadEncoding.Utf8, 0));
        }

        [Fact]
        public void Display_names_are_stable()
        {
            Assert.Equal("UTF-8", TextFileCodec.Describe(PadEncoding.Utf8, 0));
            Assert.Equal("UTF-8 BOM", TextFileCodec.Describe(PadEncoding.Utf8Bom, 0));
            Assert.Equal("ANSI 874", TextFileCodec.Describe(PadEncoding.Ansi, 874));
            Assert.Equal("CRLF", TextFileCodec.Describe(LineEnding.CrLf));
            Assert.Equal("LF", TextFileCodec.Describe(LineEnding.Lf));
        }
    }
}
