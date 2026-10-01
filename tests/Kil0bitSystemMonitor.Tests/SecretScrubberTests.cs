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
    }
}
