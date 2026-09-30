using System.Linq;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Find and replace over plain text, independent of any editor control.</summary>
    public class PadFindTests
    {
        private static Regex Build(string pattern, bool matchCase = false, bool wholeWord = false, bool regex = false)
        {
            Assert.True(FindReplaceEngine.TryBuild(pattern, new FindOptions(matchCase, wholeWord, regex), out var built, out _));
            return built!;
        }

        [Fact]
        public void A_literal_search_ignores_case_unless_asked()
        {
            Assert.Equal(3, FindReplaceEngine.FindAll("Foo foo FOO", Build("foo")).Count);
            Assert.Equal(new[] { new FindMatch(4, 3) }, FindReplaceEngine.FindAll("Foo foo FOO", Build("foo", matchCase: true)));
        }

        [Fact]
        public void Whole_word_skips_words_that_contain_the_term()
        {
            var matches = FindReplaceEngine.FindAll("cat concat cat.", Build("cat", wholeWord: true));

            Assert.Equal(new[] { 0, 11 }, matches.Select(m => m.Offset));
        }

        [Fact]
        public void Whole_word_works_for_terms_with_punctuation()
        {
            var matches = FindReplaceEngine.FindAll("a.b xa.b a.bc", Build("a.b", wholeWord: true));

            Assert.Equal(new[] { 0 }, matches.Select(m => m.Offset));
        }

        [Fact]
        public void Literal_mode_treats_regex_characters_literally()
        {
            Assert.Equal(new[] { new FindMatch(0, 3) }, FindReplaceEngine.FindAll("1+1=2", Build("1+1")));
        }

        [Fact]
        public void A_regex_replacement_uses_capture_groups()
        {
            var regex = Build(@"(\d+)-(\d+)-(\d+)", regex: true);

            Assert.True(FindReplaceEngine.TryReplaceAll("2026-09-29", regex, "$3/$2/$1", useRegex: true, out string result, out int count, out _));
            Assert.Equal("29/09/2026", result);
            Assert.Equal(1, count);
        }

        [Fact]
        public void A_literal_replacement_keeps_dollar_signs()
        {
            Assert.True(FindReplaceEngine.TryReplaceAll("price", Build("price"), "$1", useRegex: false, out string result, out _, out _));
            Assert.Equal("$1", result);
        }

        [Fact]
        public void An_invalid_pattern_reports_an_error_instead_of_throwing()
        {
            Assert.False(FindReplaceEngine.TryBuild("(", new FindOptions(false, false, true), out var regex, out string? error));
            Assert.Null(regex);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void An_empty_pattern_is_not_a_search()
        {
            Assert.False(FindReplaceEngine.TryBuild("", new FindOptions(false, false, false), out _, out string? error));
            Assert.Null(error);
        }

        [Fact]
        public void Zero_length_matches_never_loop()
        {
            var regex = Build("x*", regex: true);

            Assert.Empty(FindReplaceEngine.FindAll("abc", regex));
            Assert.Null(FindReplaceEngine.FindNext("abc", regex, 0));
            Assert.True(FindReplaceEngine.TryReplaceAll("abc", regex, "-", useRegex: true, out string result, out int count, out _));
            Assert.Equal("-a-b-c-", result);
            Assert.Equal(4, count);
        }

        [Fact]
        public void The_replace_all_plan_is_one_edit_per_match_with_groups_expanded()
        {
            var regex = Build(@"(\w+)@", regex: true);

            Assert.True(FindReplaceEngine.TryPlanReplaceAll("a@ b@", regex, "<$1>", useRegex: true, out var edits, out int count, out string? error));
            Assert.Equal(new[] { new TextPiece(0, 2, "<a>"), new TextPiece(3, 2, "<b>") }, edits);
            Assert.Equal(2, count);
            Assert.Null(error);

            Assert.True(FindReplaceEngine.TryPlanReplaceAll("price", Build("price"), "$1", useRegex: false, out edits, out _, out _));
            Assert.Equal(new[] { new TextPiece(0, 5, "$1") }, edits);
        }

        [Fact]
        public void The_replace_all_plan_keeps_zero_length_matches()
        {
            Assert.True(FindReplaceEngine.TryPlanReplaceAll("abc", Build("x*", regex: true), "-", useRegex: true, out var edits, out int count, out _));
            Assert.Equal(4, count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, edits.Select(e => e.Offset));
            Assert.All(edits, e => Assert.Equal(0, e.Length));
        }

        [Fact]
        public void A_timed_out_plan_says_so()
        {
            var regex = new Regex("(x+x+)+y", RegexOptions.None, System.TimeSpan.FromMilliseconds(1));
            Assert.False(FindReplaceEngine.TryPlanReplaceAll(new string('x', 40), regex, "-", useRegex: true, out var edits, out int count, out string? error));
            Assert.Empty(edits);
            Assert.Equal(0, count);
            Assert.Equal(FindReplaceEngine.TimedOutMessage, error);
        }

        [Fact]
        public void Find_next_wraps_to_the_start()
        {
            var regex = Build("ab");

            Assert.Equal(new FindMatch(3, 2), FindReplaceEngine.FindNext("ab ab", regex, 1));
            Assert.Equal(new FindMatch(0, 2), FindReplaceEngine.FindNext("ab ab", regex, 4));
            Assert.Null(FindReplaceEngine.FindNext("ab ab", regex, 4, wrap: false));
        }

        [Fact]
        public void Find_previous_wraps_to_the_end()
        {
            var regex = Build("ab");

            Assert.Equal(new FindMatch(0, 2), FindReplaceEngine.FindPrevious("ab ab", regex, 3));
            Assert.Equal(new FindMatch(3, 2), FindReplaceEngine.FindPrevious("ab ab", regex, 0));
        }

        [Fact]
        public void ExpandAt_only_replaces_a_real_match()
        {
            var regex = Build("cat");

            Assert.Equal("cow", FindReplaceEngine.ExpandAt("cat dog", regex, new FindMatch(0, 3), "cow", useRegex: false));
            Assert.Null(FindReplaceEngine.ExpandAt("cat dog", regex, new FindMatch(4, 3), "cow", useRegex: false));
        }

        [Fact]
        public void The_index_helpers_find_positions()
        {
            var matches = new[] { new FindMatch(0, 3), new FindMatch(11, 3) };

            Assert.Equal(1, FindReplaceEngine.IndexOf(matches, 11));
            Assert.Equal(-1, FindReplaceEngine.IndexOf(matches, 5));
            Assert.Equal(1, FindReplaceEngine.FirstAtOrAfter(matches, 5));
            Assert.Equal(2, FindReplaceEngine.FirstAtOrAfter(matches, 20));
        }

        [Fact]
        public void A_regex_that_is_only_valid_once_wrapped_is_still_an_error()
        {
            Assert.False(FindReplaceEngine.TryBuild("a)(b", new FindOptions(false, WholeWord: true, UseRegex: true), out var regex, out string? error));
            Assert.Null(regex);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void A_search_that_runs_too_long_reports_a_timeout()
        {
            var slow = new Regex("(x+x+)+y", RegexOptions.None, TimeSpan.FromMilliseconds(1));

            FindReplaceEngine.FindAll(new string('x', 40), slow, out bool timedOut);

            Assert.True(timedOut);
            Assert.False(FindReplaceEngine.FindAll("abc", Build("b"), out bool quick).Count == 0);
            Assert.False(quick);
        }
    }
}
