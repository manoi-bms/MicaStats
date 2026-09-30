using System;
using System.Linq;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Which text becomes a link, and which links may ever be opened.</summary>
    public class SafeLinksTests
    {
        private static string[] Matches(string text) =>
            Regex.Matches(text, SafeLinks.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(m => m.Value).ToArray();

        [Theory]
        [InlineData("https://example.com/a?b=1#c")]
        [InlineData("http://intranet/page")]
        [InlineData("mailto:someone@example.com")]
        [InlineData("HTTPS://EXAMPLE.COM")]
        public void Only_web_and_mail_links_are_allowed(string text)
        {
            var uri = SafeLinks.TryCreate(text);
            Assert.NotNull(uri);
            Assert.True(SafeLinks.IsAllowed(uri!));
        }

        [Theory]
        [InlineData("file:///C:/Windows/System32/calc.exe")]
        [InlineData("javascript:alert(1)")]
        [InlineData("ms-settings:privacy")]
        [InlineData("C:\\Windows\\notepad.exe")]
        [InlineData("\\\\server\\share\\x.exe")]
        [InlineData("ftp://example.com/x")]
        [InlineData("https:")]
        [InlineData("mailto:")]
        [InlineData("")]
        public void Anything_else_is_refused(string text)
        {
            Assert.Null(SafeLinks.TryCreate(text));
        }

        [Theory]
        [InlineData("see https://example.com/x.", "https://example.com/x")]
        [InlineData("(https://example.com/a)", "https://example.com/a")]
        [InlineData("go to https://example.com, then", "https://example.com")]
        [InlineData("\"https://example.com/q\"", "https://example.com/q")]
        [InlineData("mail mailto:a@b.co; thanks", "mailto:a@b.co")]
        public void Trailing_punctuation_is_not_part_of_the_link(string text, string link)
        {
            Assert.Equal(new[] { link }, Matches(text));
        }

        [Fact]
        public void Other_schemes_are_not_even_matched()
        {
            Assert.Empty(Matches("file:///c:/x javascript:alert(1) ms-settings:privacy www.example.com"));
        }

        [Fact]
        public void The_link_under_a_column()
        {
            string line = "read https://example.com/doc now";
            Assert.Equal((5, 23), SafeLinks.LinkAt(line, 10));
            Assert.Equal((5, 23), SafeLinks.LinkAt(line, 5));
            Assert.Null(SafeLinks.LinkAt(line, 2));
            Assert.Null(SafeLinks.LinkAt(line, 29));
        }
    }
}
