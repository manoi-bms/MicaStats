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

        [Theory]
        [InlineData("see http://x.com/api{", "http://x.com/api")]
        [InlineData("list http://x.com/a[", "http://x.com/a")]
        [InlineData("x https://a.com/b}c", "https://a.com/b")]
        [InlineData("x https://a.com/b]c", "https://a.com/b")]
        public void A_link_never_holds_a_brace_or_bracket_where_a_fold_may_start(string text, string link)
        {
            Assert.Equal(new[] { link }, Matches(text));
        }

        [Theory]
        [InlineData("https://en.wikipedia.org/wiki/Mercury_(planet)", "https://en.wikipedia.org/wiki/Mercury_(planet)")]
        [InlineData("(see https://en.wikipedia.org/wiki/Mercury_(planet))", "https://en.wikipedia.org/wiki/Mercury_(planet)")]
        [InlineData("(https://a.com/x)", "https://a.com/x")]
        [InlineData("https://a.com/x) and more", "https://a.com/x")]
        [InlineData("https://a.com/(x", "https://a.com/")]
        public void Balanced_parentheses_belong_to_the_link_and_an_unbalanced_close_does_not(string text, string link)
        {
            Assert.Equal(new[] { link }, Matches(text));
        }

        [Fact]
        public void A_link_may_follow_a_thai_letter_but_not_an_ascii_one()
        {
            // "see at" in Thai, ending in a tone mark: \b finds no word boundary between it and the scheme.
            Assert.Equal(new[] { "https://example.com" }, Matches("\u0E14\u0E39\u0E17\u0E35\u0E48https://example.com"));
            Assert.Equal(new[] { "https://example.com" }, Matches("\u0E14\u0E39https://example.com"));
            Assert.Empty(Matches("xhttps://example.com"));
            Assert.Empty(Matches("9https://example.com"));
        }

        [Fact]
        public void Thai_right_after_a_link_stays_part_of_it()
        {
            string text = "https://th.wikipedia.org/wiki/\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22";
            Assert.Equal(new[] { text }, Matches(text));
        }

        [Fact]
        public void Many_open_parentheses_still_match_quickly()
        {
            string text = "https://example.com/" + string.Concat(Enumerable.Repeat("(a", 2500));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = Matches(text);
            Assert.True(watch.ElapsedMilliseconds < 1000);
            Assert.Equal(new[] { "https://example.com/" }, found);
        }

        [Theory]
        [InlineData("https://user@example.com")]
        [InlineData("https://example.com/..%2f..")]
        public void Uri_edge_cases_stay_https_links(string text)
        {
            var uri = SafeLinks.TryCreate(text);
            Assert.NotNull(uri);
            Assert.Equal("https", uri!.Scheme);
            Assert.True(SafeLinks.IsAllowed(uri));
            // Trailing dots are sentence punctuation, so the match may stop before them; it is still one https link.
            var found = Matches(text);
            Assert.Single(found);
            Assert.StartsWith(found[0], text);
        }

        [Fact]
        public void A_very_long_link_matches_as_one_quickly()
        {
            string text = "https://example.com/" + new string('a', 4980);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = Matches(text);
            Assert.True(watch.ElapsedMilliseconds < 1000);
            Assert.Equal(new[] { text }, found);
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
