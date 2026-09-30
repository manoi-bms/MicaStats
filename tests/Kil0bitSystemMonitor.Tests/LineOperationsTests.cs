using System.Globalization;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Duplicate, move, join, sort, dedupe and trim over lines, keeping line endings.</summary>
    public class LineOperationsTests
    {
        private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        private static string Apply(string text, TextEdit? edit) =>
            edit is TextEdit e ? text.Remove(e.Offset, e.Length).Insert(e.Offset, e.Text) : text;

        [Fact]
        public void Duplicate_copies_the_caret_line_below_and_keeps_the_column()
        {
            string text = "one\r\ntwo\r\nthree";
            var edit = LineOperations.Duplicate(text, 6, 0);            // caret in "two"
            Assert.Equal("one\r\ntwo\r\ntwo\r\nthree", Apply(text, edit));
            Assert.Equal(11, edit.SelectionStart);                      // same column on the copy
        }

        [Fact]
        public void Duplicate_of_the_last_line_uses_the_notes_line_ending()
        {
            Assert.Equal("a\nb\nb", Apply("a\nb", LineOperations.Duplicate("a\nb", 2, 0)));
            Assert.Equal("solo\r\nsolo", Apply("solo", LineOperations.Duplicate("solo", 0, 0)));
        }

        [Fact]
        public void Duplicate_of_a_selection_inserts_a_copy_after_it_and_selects_the_copy()
        {
            var edit = LineOperations.Duplicate("abc", 0, 2);
            Assert.Equal("ababc", Apply("abc", edit));
            Assert.Equal((2, 2), (edit.SelectionStart, edit.SelectionLength));
        }

        [Fact]
        public void Duplicate_lines_copies_every_line_a_rectangle_touches_below_them()
        {
            string text = "abcd\nefgh\nijkl";
            var edit = LineOperations.DuplicateLines(text, 1, 7);             // line 1 column 2 to line 2 column 4
            string result = Apply(text, edit);
            Assert.Equal("abcd\nefgh\nabcd\nefgh\nijkl", result);
            Assert.Equal("abcd\nefgh", result.Substring(edit.SelectionStart, edit.SelectionLength));

            // Ending at the start of the last line still takes that line; no break after it: the note's own.
            Assert.Equal("a\r\nb\r\na\r\nb", Apply("a\r\nb", LineOperations.DuplicateLines("a\r\nb", 0, 3)));
        }

        [Theory]
        [InlineData("a\nb\nc", 2, 0, "b\na\nc", 0)]
        [InlineData("a\r\nb\r\nc", 3, 0, "b\r\na\r\nc", 0)]
        [InlineData("a\nb\nc", 4, 0, "a\nc\nb", 2)]
        [InlineData("a\nb", 0, 0, null, 0)]
        [InlineData("1\n2\n3", 2, 2, "2\n1\n3", 0)]           // selection ends after a break, next line is last
        [InlineData("1\r\n2\r\n3", 3, 3, "2\r\n1\r\n3", 0)]
        public void Move_up(string text, int caret, int length, string? expected, int newCaret)
        {
            var edit = LineOperations.MoveUp(text, caret, length);
            if (expected == null) { Assert.Null(edit); return; }
            string result = Apply(text, edit);
            Assert.Equal(expected, result);
            Assert.Equal(newCaret, edit!.Value.SelectionStart);
            Assert.InRange(edit.Value.SelectionStart + edit.Value.SelectionLength, 0, result.Length);
        }

        [Theory]
        [InlineData("a\nb\nc", 0, 0, "b\na\nc", 2)]
        [InlineData("a\r\nb", 0, 0, "b\r\na", 3)]
        [InlineData("a\nb", 2, 0, null, 0)]
        [InlineData("1\n2", 0, 2, "2\n1", 2)]                 // selection ends after a break, next line is last
        [InlineData("1\r\n2", 0, 3, "2\r\n1", 3)]
        [InlineData("1\n2\n3", 2, 2, "1\n3\n2", 4)]
        public void Move_down(string text, int caret, int length, string? expected, int newCaret)
        {
            var edit = LineOperations.MoveDown(text, caret, length);
            if (expected == null) { Assert.Null(edit); return; }
            string result = Apply(text, edit);
            Assert.Equal(expected, result);
            Assert.Equal(newCaret, edit!.Value.SelectionStart);
            Assert.InRange(edit.Value.SelectionStart + edit.Value.SelectionLength, 0, result.Length);
        }

        [Fact]
        public void Moving_down_onto_the_last_line_keeps_the_selection_inside_the_text()
        {
            var edit = LineOperations.MoveDown("1\n2", 0, 2)!.Value;
            Assert.Equal("2\n1", Apply("1\n2", edit));
            Assert.Equal((2, 1), (edit.SelectionStart, edit.SelectionLength));   // the moved "1"

            var crlf = LineOperations.MoveDown("1\r\n2", 0, 3)!.Value;
            Assert.Equal("2\r\n1", Apply("1\r\n2", crlf));
            Assert.Equal((3, 1), (crlf.SelectionStart, crlf.SelectionLength));
        }

        [Fact]
        public void Moving_a_multi_line_selection_moves_the_whole_block()
        {
            string text = "1\n2\n3\n4";
            var edit = LineOperations.MoveDown(text, 2, 3);             // "2\n3" selected
            Assert.Equal("1\n4\n2\n3", Apply(text, edit));
        }

        [Fact]
        public void Join_joins_selected_lines_or_this_line_with_the_next()
        {
            Assert.Equal("a b c", Apply("a\n  b  \nc", LineOperations.Join("a\n  b  \nc", 0, 9)));
            Assert.Equal("a b\nc", Apply("a\nb\nc", LineOperations.Join("a\nb\nc", 0, 0)));
            Assert.Null(LineOperations.Join("last", 0, 0));
        }

        [Fact]
        public void Sort_is_case_insensitive_stable_and_keeps_line_endings()
        {
            string text = "b\r\nA\r\na\r\nC";
            Assert.Equal("A\r\na\r\nb\r\nC", Apply(text, LineOperations.Sort(text, 0, 0, false, En)));
            Assert.Equal("C\r\nb\r\nA\r\na", Apply(text, LineOperations.Sort(text, 0, 0, true, En)));
        }

        [Fact]
        public void Sorting_keeps_a_trailing_newline_last()
        {
            Assert.Equal("a\nb\n", Apply("b\na\n", LineOperations.Sort("b\na\n", 0, 0, false, En)));
        }

        [Fact]
        public void Sort_only_touches_the_selected_lines_and_says_when_nothing_changes()
        {
            string text = "z\nb\na\ny";
            Assert.Equal("z\na\nb\ny", Apply(text, LineOperations.Sort(text, 2, 3, false, En)));
            Assert.Null(LineOperations.Sort("a\nb", 0, 0, false, En));
        }

        [Fact]
        public void Remove_duplicates_keeps_the_first_and_no_extra_newline()
        {
            Assert.Equal("a\nb", Apply("a\nb\na", LineOperations.RemoveDuplicates("a\nb\na", 0, 0)));
            Assert.Equal("a\n", Apply("a\na\n", LineOperations.RemoveDuplicates("a\na\n", 0, 0)));
            Assert.Null(LineOperations.RemoveDuplicates("a\nb", 0, 0));
        }

        [Fact]
        public void Removing_duplicates_keeps_blank_lines()
        {
            string text = "p\n\nq\n\nq";
            Assert.Equal("p\n\nq\n", Apply(text, LineOperations.RemoveDuplicates(text, 0, 0)));
        }

        [Fact]
        public void Trim_removes_trailing_spaces_and_tabs_only()
        {
            string text = "a  \r\n\tb\t\r\nc";
            Assert.Equal("a\r\n\tb\r\nc", Apply(text, LineOperations.TrimTrailing(text, 0, 0)));
            Assert.Null(LineOperations.TrimTrailing("clean\nlines", 0, 0));
        }


        [Fact]
        public void Join_skips_the_separator_after_an_empty_first_line() =>
            Assert.Equal("b", Apply("\nb", LineOperations.Join("\nb", 0, 0)));

        [Fact]
        public void Sorting_keeps_every_trailing_empty_line_last() =>
            Assert.Equal("a\nb\n\n", Apply("b\na\n\n", LineOperations.Sort("b\na\n\n", 0, 0, false, En)));

        [Fact]
        public void Move_down_handles_lone_cr_and_mixed_endings()
        {
            Assert.Equal("b\ra\rc", Apply("a\rb\rc", LineOperations.MoveDown("a\rb\rc", 0, 0)));
            Assert.Equal("b\r\na\nc", Apply("a\r\nb\nc", LineOperations.MoveDown("a\r\nb\nc", 0, 0)));
        }

        [Fact]
        public void A_selection_ending_right_after_a_line_break_does_not_take_the_next_line()
        {
            Assert.Equal("2\n1\n3", Apply("1\n2\n3", LineOperations.MoveDown("1\n2\n3", 0, 2)));
            var edit = LineOperations.Duplicate("1\n2\n3", 0, 2);
            Assert.Equal("1\n1\n2\n3", Apply("1\n2\n3", edit));
        }

        [Fact]
        public void Remove_duplicates_works_on_crlf_text() =>
            Assert.Equal("a\r\nb\r\n", Apply("a\r\nb\r\na\r\n", LineOperations.RemoveDuplicates("a\r\nb\r\na\r\n", 0, 0)));

        [Fact]
        public void Moving_keeps_a_multi_line_selection_on_the_same_text()
        {
            string text = "1\n2\n3\n4";
            var edit = LineOperations.MoveDown(text, 2, 3);
            string result = Apply(text, edit);
            Assert.Equal("2\n3", result.Substring(edit!.Value.SelectionStart, edit.Value.SelectionLength));
            var up = LineOperations.MoveUp(text, 2, 3);
            Assert.Equal("2\n3", Apply(text, up).Substring(up!.Value.SelectionStart, up.Value.SelectionLength));
        }
        [Fact]
        public void A_whole_document_operation_keeps_the_caret_on_its_line()
        {
            string text = "b  \na  \nc  ";
            var edit = LineOperations.TrimTrailing(text, 5, 0).GetValueOrDefault();   // caret on line 2, column 1
            Assert.Equal(0, edit.SelectionLength);
            Assert.Equal(3, edit.SelectionStart);                                     // "b\na\nc": line 2 starts at 2, column 1
        }
    }
}
