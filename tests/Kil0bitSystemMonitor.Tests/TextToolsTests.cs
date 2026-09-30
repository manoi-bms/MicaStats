using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Tools: Base64, GUIDs, timestamps, and the one edit each tool makes.</summary>
    public class TextToolsTests
    {
        private static readonly DateTimeOffset Stamp = new(2026, 9, 30, 18, 5, 12, TimeSpan.FromHours(7));

        [Theory]
        [InlineData("hello", "aGVsbG8=")]
        [InlineData("สวัสดี", "4Liq4Lin4Lix4Liq4LiU4Li1")]
        [InlineData("ไทย 😀", "4LmE4LiX4LiiIPCfmIA=")]
        public void Base64_round_trips_thai_and_emoji(string text, string base64)
        {
            Assert.Equal(base64, TextTools.Base64Encode(text));
            var (decoded, problem) = TextTools.Base64Decode(base64);
            Assert.Null(problem);
            Assert.Equal(text, decoded);
        }

        [Theory]
        [InlineData("aGVs\r\nbG8=", "hello")]      // wrapped at a line break
        [InlineData("aGVsbG8", "hello")]           // padding left off
        [InlineData("Pz8-", "??>")]                // the URL-safe alphabet ("Pz8+")
        [InlineData("77u/aGk=", "hi")]             // a UTF-8 byte order mark is dropped
        public void Base64_decode_takes_wrapped_unpadded_and_url_safe_text(string base64, string text)
        {
            var (decoded, problem) = TextTools.Base64Decode(base64);
            Assert.Null(problem);
            Assert.Equal(text, decoded);
        }

        [Theory]
        [InlineData("hello!", TextTools.NotBase64)]
        [InlineData("a", TextTools.NotBase64)]
        [InlineData("////", TextTools.NotUtf8)]     // bytes FF FF FF
        [InlineData("AAAA", TextTools.NotUtf8)]     // three NULs: binary, not text
        [InlineData("77u/", TextTools.DecodesToNothing)]   // a byte order mark alone: nothing is left once it is dropped
        public void Not_base64_or_not_text_is_reported(string text, string problem)
        {
            var (decoded, reason) = TextTools.Base64Decode(text);
            Assert.Null(decoded);
            Assert.Equal(problem, reason);
        }

        [Fact]
        public void A_guid_is_lowercase_d_format()
        {
            Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                         TextTools.FormatGuid(new Guid("3F2504E0-4F89-11D3-9A0C-0305E82C3301")));
        }

        [Fact]
        public void Timestamps_are_gregorian_under_the_thai_culture()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");     // its calendar would write 2569
            try
            {
                Assert.Equal("2026-09-30T18:05:12+07:00", TextTools.Iso8601(Stamp));
                Assert.Equal("2026-09-30", TextTools.Date(Stamp));
                Assert.Equal("1790766312", TextTools.UnixSeconds(Stamp));
                Assert.Equal("2026-09-30T11:05:12+00:00", TextTools.Iso8601(Stamp.ToUniversalTime()));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void A_selection_tool_replaces_what_is_between_the_spaces_and_selects_it()
        {
            // "say  255 \nnow", selection "  255 \n": only "255" is converted; the spaces and the break stay.
            var outcome = TextTools.OnSelection("say  255 \nnow", 3, 7, s => NumberConverter.Convert(s, NumberBase.Hex));
            Assert.Null(outcome.Problem);
            Assert.Equal(new TextEdit(5, 3, "0xFF", 5, 4), outcome.Edit);
        }

        [Fact]
        public void Nothing_but_spaces_selected_asks_for_a_selection()
        {
            Assert.Equal(TextTools.SelectFirst, TextTools.OnSelection("a   b", 1, 3, s => (s, null)).Problem);
            Assert.Equal(TextTools.SelectFirst, TextTools.OnSelection("abc", 1, 0, s => (s, null)).Problem);
            Assert.Null(TextTools.OnSelection("abc", 1, 0, s => (s, null)).Edit);
        }

        [Fact]
        public void A_problem_leaves_no_edit()
        {
            var outcome = TextTools.OnSelection("xyz", 0, 3, s => NumberConverter.Convert(s, NumberBase.Hex));
            Assert.Null(outcome.Edit);
            Assert.Equal(NumberConverter.NotANumber, outcome.Problem);
        }

        [Fact]
        public void Evaluate_appends_the_result_after_the_expression_and_selects_it()
        {
            // Selection "1500+230 \n" (offset 7, length 10): the result goes right after "230".
            var outcome = TextTools.Evaluate("total: 1500+230 \n", 7, 10);
            Assert.Equal(new TextEdit(15, 0, " = 1730", 18, 4), outcome.Edit);
        }

        [Fact]
        public void Evaluate_reports_a_bad_expression()
        {
            var outcome = TextTools.Evaluate("1/0", 0, 3);
            Assert.Null(outcome.Edit);
            Assert.Equal(ExpressionEvaluator.DivisionByZero, outcome.Problem);
        }

        [Fact]
        public void Insert_replaces_the_selection_and_leaves_the_caret_after_it()
        {
            Assert.Equal(new TextEdit(2, 3, "2026-09-30", 12, 0), TextTools.Insert(2, 3, "2026-09-30").Edit);
        }
    }
}
