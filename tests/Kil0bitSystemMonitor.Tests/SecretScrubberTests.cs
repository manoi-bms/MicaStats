using System;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Replacing a secret's copies with its reference.</summary>
    public class SecretScrubberTests
    {
        private const string Reference = "{{secret:K7Q2M9XD}}";

        [Fact]
        public void Every_copy_is_replaced_and_counted()
        {
            string text = SecretScrubber.Replace("a=hunter2\nb=hunter2\r\nc=hunter", "hunter2", Reference, out int count);

            Assert.Equal(2, count);
            Assert.Equal("a=" + Reference + "\nb=" + Reference + "\r\nc=hunter", text);
        }

        [Fact]
        public void Copies_do_not_overlap() => Assert.Equal(2, SecretScrubber.Count("aaaaa", "aa"));

        [Fact]
        public void Matching_is_exact() => Assert.Equal(0, SecretScrubber.Count("Hunter2", "hunter2"));

        [Fact]
        public void A_multi_line_secret_is_matched_with_its_line_breaks()
        {
            string key = "-----BEGIN KEY-----\r\nabc\r\n-----END KEY-----";

            Assert.Equal(1, SecretScrubber.Count("x\r\n" + key + "\r\ny", key));
        }

        [Fact]
        public void No_copy_leaves_the_text_as_it_is()
        {
            string text = "nothing here";

            Assert.Same(text, SecretScrubber.Replace(text, "secret", Reference, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void An_empty_value_is_refused() => Assert.Throws<ArgumentException>(() => SecretScrubber.Count("x", ""));

        [Fact]
        public void A_copy_inside_an_existing_reference_is_left_alone()
        {
            // Replacing "secret" or "K7Q2" inside a reference would nest one reference in another.
            const string text = "old={{secret:K7Q2M9XD}} new=secret K7Q2";

            Assert.Equal(new[] { 28 }, SecretScrubber.Find(text, "secret"));
            Assert.Equal("old={{secret:K7Q2M9XD}} new=" + Reference + " K7Q2", SecretScrubber.Replace(text, "secret", Reference, out int count));
            Assert.Equal(1, count);
            Assert.Equal(1, SecretScrubber.Count(text, "K7Q2"));
            Assert.Equal(0, SecretScrubber.Count(text, "M9XD}} "));   // runs out of a reference
            Assert.Equal(new[] { 0 }, SecretScrubber.Find("aaaaa", "aaa"));
        }
    }
}
