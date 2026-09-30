using System.Globalization;
using System.Linq;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>History ▸ Compare with current: line rows with both numbers, the summary, and the 1 MB limit.</summary>
    public class HistoryDiffTests
    {
        private static DiffRow Row(DiffKind kind, string text, int? oldLine, int? newLine) => new(kind, text, oldLine, newLine);

        [Fact]
        public void Rows_show_removed_then_added_lines_with_both_numbers()
        {
            var outcome = HistoryDiff.Compare("one\ntwo\nthree", "zero\none\nthree\nfour");

            Assert.False(outcome.TooLarge);
            Assert.Equal(new[]
            {
                Row(DiffKind.Added, "zero", null, 1),
                Row(DiffKind.Unchanged, "one", 1, 2),
                Row(DiffKind.Removed, "two", 2, null),
                Row(DiffKind.Unchanged, "three", 3, 3),
                Row(DiffKind.Added, "four", null, 4),
            }, outcome.Rows);
            Assert.Equal(2, outcome.Added);
            Assert.Equal(1, outcome.Removed);
            Assert.Equal("+2 −1 lines", outcome.Summary);
        }

        [Fact]
        public void A_changed_line_is_a_removal_then_an_addition()
        {
            Assert.Equal(new[]
            {
                Row(DiffKind.Unchanged, "a", 1, 1),
                Row(DiffKind.Removed, "b", 2, null),
                Row(DiffKind.Added, "B", null, 2),
                Row(DiffKind.Unchanged, "c", 3, 3),
            }, HistoryDiff.Compare("a\nb\nc", "a\nB\nc").Rows);
        }

        [Fact]
        public void Identical_texts_have_no_changes()
        {
            var outcome = HistoryDiff.Compare("same\ntext", "same\ntext");
            Assert.Equal(2, outcome.Rows.Count);
            Assert.All(outcome.Rows, r => Assert.Equal(DiffKind.Unchanged, r.Kind));
            Assert.Equal("No changes", outcome.Summary);
        }

        [Fact]
        public void Line_endings_do_not_count_but_spaces_and_letter_case_do()
        {
            Assert.Equal("No changes", HistoryDiff.Compare("a\r\nb", "a\nb").Summary);
            Assert.Equal("+1 −1 lines", HistoryDiff.Compare("a  b", "a b").Summary);
            Assert.Equal("+1 −1 lines", HistoryDiff.Compare("Word", "word").Summary);
        }

        [Fact]
        public void Empty_texts_compare_too()
        {
            Assert.Equal(new[] { Row(DiffKind.Added, "x", null, 1) }, HistoryDiff.Compare("", "x").Rows);
            Assert.Equal(new[] { Row(DiffKind.Removed, "x", 1, null) }, HistoryDiff.Compare("x", "").Rows);
            Assert.Empty(HistoryDiff.Compare("", "").Rows);
            Assert.Equal("No changes", HistoryDiff.Compare("", "").Summary);
        }

        [Fact]
        public void A_final_line_break_is_a_line_of_its_own()
        {
            var outcome = HistoryDiff.Compare("a\nb\n", "a\nb");
            Assert.Equal(Row(DiffKind.Removed, "", 3, null), outcome.Rows[outcome.Rows.Count - 1]);
            Assert.Equal("+0 −1 lines", outcome.Summary);
        }

        [Fact]
        public void Two_texts_of_exactly_1_MB_are_compared()
        {
            // 32,768 lines of 32 characters (31 + the line break) = 1,048,576 characters exactly.
            var sb = new StringBuilder(HistoryDiff.MaxChars);
            for (int i = 0; i < 32_768; i++)
                sb.Append("line ").Append(i.ToString("D5", CultureInfo.InvariantCulture)).Append(' ').Append('.', 20).Append('\n');
            string old = sb.ToString();
            string current = old.Replace("line 00100 ", "LINE 00100 ");
            Assert.Equal(HistoryDiff.MaxChars, old.Length);
            Assert.Equal(HistoryDiff.MaxChars, current.Length);

            var outcome = HistoryDiff.Compare(old, current);

            Assert.False(outcome.TooLarge);
            Assert.Equal("+1 −1 lines", outcome.Summary);
            Assert.Equal(Row(DiffKind.Removed, "line 00100 " + new string('.', 20), 101, null), outcome.Rows[100]);
            Assert.Equal(Row(DiffKind.Added, "LINE 00100 " + new string('.', 20), null, 101), outcome.Rows[101]);
            Assert.Equal(32_770, outcome.Rows.Count);   // 32,768 unchanged (the last is the empty line after the final break) + 2
        }

        [Fact]
        public void Either_text_over_1_MB_is_too_large()
        {
            string big = new string('x', HistoryDiff.MaxChars + 1);

            foreach (var outcome in new[] { HistoryDiff.Compare(big, "x"), HistoryDiff.Compare("x", big) })
            {
                Assert.True(outcome.TooLarge);
                Assert.Empty(outcome.Rows);
                Assert.Equal("Too large to compare", outcome.Summary);
            }
        }

        [Fact]
        public void Texts_that_differ_on_too_many_lines_are_too_large()
        {
            // 30,000 lines on each side, every one changed: 60,000 lines to compare. With shorter
            // lines a 1 MB note holds far more, and DiffPlex would run for minutes.
            var old = new StringBuilder();
            var current = new StringBuilder();
            for (int i = 0; i < 30_000; i++)
            {
                old.Append("old ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
                current.Append("new ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            var outcome = HistoryDiff.Compare(old.ToString(), current.ToString());

            Assert.True(outcome.TooLarge);
            Assert.Empty(outcome.Rows);
            Assert.Equal("Too large to compare", outcome.Summary);
        }

        [Fact]
        public void A_long_note_of_short_lines_with_one_change_is_compared()
        {
            // 300,000 lines, far more than HistoryDiff.MaxChangedLines, but only one of them differs.
            string old = string.Concat(Enumerable.Repeat("1\n2\n3\n", 100_000));
            string current = old.Substring(0, 300_000) + "x" + old.Substring(300_001);   // line 150,001: "1" becomes "x"

            var outcome = HistoryDiff.Compare(old, current);

            Assert.False(outcome.TooLarge);
            Assert.Equal("+1 −1 lines", outcome.Summary);
            Assert.Equal(Row(DiffKind.Removed, "1", 150_001, null), outcome.Rows[150_000]);
            Assert.Equal(Row(DiffKind.Added, "x", null, 150_001), outcome.Rows[150_001]);
        }

        [Theory]
        [InlineData(12, 3, "+12 −3 lines")]
        [InlineData(1, 0, "+1 −0 lines")]
        [InlineData(0, 0, "No changes")]
        public void The_summary_counts_lines(int added, int removed, string expected)
        {
            Assert.Equal(expected, HistoryDiff.Describe(added, removed));
        }
    }
}
