using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The text that stands in a note for a stored credential.</summary>
    public class SecretTokensTests
    {
        [Fact]
        public void A_reference_is_the_id_in_double_braces() =>
            Assert.Equal("{{secret:K7Q2M9XD}}", SecretTokens.Format("K7Q2M9XD"));

        [Fact]
        public void References_are_found_with_their_offsets()
        {
            string text = "user: admin\npass: {{secret:K7Q2M9XD}} \u0E44\u0E17\u0E22 {{secret:00000000}}";

            var found = SecretTokens.Find(text);

            Assert.Equal(2, found.Count);
            Assert.Equal(new SecretReference(text.IndexOf("{{", System.StringComparison.Ordinal), SecretTokens.Format("K7Q2M9XD").Length, "K7Q2M9XD"), found[0]);
            Assert.Equal("00000000", found[1].Id);
            Assert.True(SecretTokens.Contains(text));
        }

        [Theory]
        [InlineData("{{secret:k7q2m9xd}}")]   // lower case
        [InlineData("{{secret:K7Q2M9XI}}")]   // I, L, O and U are not in the alphabet
        [InlineData("{{secret:K7Q2M9XL}}")]
        [InlineData("{{secret:K7Q2M9XO}}")]
        [InlineData("{{secret:K7Q2M9XU}}")]
        [InlineData("{{secret:K7Q2M9X}}")]    // 7 characters
        [InlineData("{{secret:K7Q2M9XDD}}")]  // 9
        [InlineData("{secret:K7Q2M9XD}")]
        [InlineData("{{Secret:K7Q2M9XD}}")]
        public void Near_misses_are_not_references(string text)
        {
            Assert.Empty(SecretTokens.Find(text));
            Assert.False(SecretTokens.Contains(text));
        }

        [Fact]
        public void New_ids_use_the_alphabet_and_skip_taken_ones()
        {
            var seen = new List<string>();

            string id = SecretTokens.NewId(candidate =>
            {
                seen.Add(candidate);
                return seen.Count < 3;
            });

            Assert.Equal(3, seen.Count);
            Assert.Equal(seen[2], id);
            Assert.All(seen, s => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{8}$", s));
        }

        [Fact]
        public void The_alphabet_is_crockford_base32()
        {
            Assert.Equal(32, SecretTokens.Alphabet.Distinct().Count());
            Assert.Equal(32, SecretTokens.Alphabet.Length);
            foreach (char c in "ILOU") Assert.DoesNotContain(c, SecretTokens.Alphabet);
        }

        [Theory]
        [InlineData("K7Q2M9XD", true)]
        [InlineData("k7q2m9xd", false)]
        [InlineData("K7Q2M9X", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Ids_are_checked(string? id, bool valid) => Assert.Equal(valid, SecretTokens.IsId(id));
    }
}
