using System.Diagnostics;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The token estimate: a quarter of a token per ASCII character, one per other character.</summary>
    public class TokenEstimateTests
    {
        [Fact]
        public void Null_and_empty_are_no_tokens()
        {
            Assert.Equal(0, TokenEstimate.Of(null));
            Assert.Equal(0, TokenEstimate.Of(""));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(4, 1)]
        [InlineData(5, 2)]
        [InlineData(8, 2)]
        [InlineData(9, 3)]
        [InlineData(1000, 250)]
        public void Four_ASCII_characters_are_a_token_and_the_total_rounds_up(int length, int tokens) =>
            Assert.Equal(tokens, TokenEstimate.Of(new string('a', length)));

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(50_000)]
        public void Each_Thai_letter_is_a_token(int length) =>
            Assert.Equal(length, TokenEstimate.Of(new string((char)0x0E01, length)));

        [Fact]
        public void A_surrogate_pair_is_one_token_not_two()
        {
            string emoji = char.ConvertFromUtf32(0x1F600);

            Assert.Equal(2, emoji.Length);
            Assert.Equal(1, TokenEstimate.Of(emoji));
            Assert.Equal(3, TokenEstimate.Of(emoji + emoji + emoji));
        }

        [Fact]
        public void A_lone_surrogate_is_one_token_and_does_not_throw()
        {
            Assert.Equal(1, TokenEstimate.Of(((char)0xD83D).ToString()));
            Assert.Equal(1, TokenEstimate.Of(((char)0xDE00).ToString()));
            Assert.Equal(2, TokenEstimate.Of(((char)0xDE00).ToString() + (char)0xD83D));
        }

        [Fact]
        public void Mixed_text_adds_the_ASCII_quarters_to_the_whole_tokens()
        {
            // 8 ASCII characters (2 tokens) + 3 Thai letters + 1 emoji = 6.
            string text = "abcd efg" + new string((char)0x0E01, 3) + char.ConvertFromUtf32(0x1F600);

            Assert.Equal(6, TokenEstimate.Of(text));
            // 1 ASCII character on top of the 4 whole tokens: a quarter, rounded up.
            Assert.Equal(5, TokenEstimate.Of("a" + new string((char)0x0E01, 3) + char.ConvertFromUtf32(0x1F600)));
        }

        [Fact]
        public void Characters_just_past_ASCII_count_as_whole_tokens()
        {
            Assert.Equal(1, TokenEstimate.Of(((char)127).ToString()));
            Assert.Equal(1, TokenEstimate.Of(((char)128).ToString()));
            Assert.Equal(4, TokenEstimate.Of(new string((char)128, 4)));
            Assert.Equal(1, TokenEstimate.Of(new string((char)127, 4)));
        }

        [Fact]
        public void A_five_million_character_text_is_counted_at_once()
        {
            string ascii = new string('a', 5_000_000);
            string thai = new string((char)0x0E01, 5_000_000);

            var watch = Stopwatch.StartNew();
            int a = TokenEstimate.Of(ascii);
            int t = TokenEstimate.Of(thai);
            watch.Stop();

            Assert.Equal(1_250_000, a);
            Assert.Equal(5_000_000, t);
            Assert.True(watch.ElapsedMilliseconds < 30_000, "took " + watch.ElapsedMilliseconds + " ms");
        }
    }
}
