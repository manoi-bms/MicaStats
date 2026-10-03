using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SelectionEditTests
    {
        [Fact] public void Clean_trims_leading_and_trailing_blank_lines() =>
            Assert.Equal("a\n\nb", SelectionEdit.Clean("\n  \n\r\na\n\nb\n \n\n"));

        [Fact] public void Clean_keeps_indentation_of_the_first_text_line() =>
            Assert.Equal("    code", SelectionEdit.Clean("\n    code\n"));

        [Fact] public void Clean_keeps_trailing_spaces_on_the_last_text_line() =>
            Assert.Equal("a  ", SelectionEdit.Clean("a  \n\n"));

        [Fact] public void Clean_of_blank_text_is_empty() =>
            Assert.Equal("", SelectionEdit.Clean(" \n\n  "));

        [Fact]
        public void Replace_swaps_the_range_and_selects_the_result()
        {
            var e = SelectionEdit.Replace(5, 3, "xyz!");
            Assert.Equal(new TextEdit(5, 3, "xyz!", 5, 4), e);
        }

        [Fact]
        public void InsertBelow_in_the_middle_of_an_LF_document()
        {
            string doc = "one\ntwo\nthree";
            var e = SelectionEdit.InsertBelow(doc, 5, "R", "\n"); // after "t" of two
            Assert.Equal(7, e.Offset);
            Assert.Equal(0, e.Length);
            Assert.Equal("\n\nR", e.Text);
            Assert.Equal("one\ntwo\n\nR\nthree", Apply(doc, e));
            Assert.Equal("R", Apply(doc, e).Substring(e.SelectionStart, e.SelectionLength));
        }

        [Fact]
        public void InsertBelow_in_the_middle_of_a_CRLF_document()
        {
            string doc = "one\r\ntwo\r\nthree";
            var e = SelectionEdit.InsertBelow(doc, 6, "R1\r\nR2", "\r\n");
            Assert.Equal("one\r\ntwo\r\n\r\nR1\r\nR2\r\nthree", Apply(doc, e));
            Assert.Equal("R1\r\nR2", Apply(doc, e).Substring(e.SelectionStart, e.SelectionLength));
        }

        [Fact]
        public void InsertBelow_a_selection_ending_at_a_line_start_goes_after_the_last_source_line()
        {
            string doc = "one\ntwo\nthree";
            var e = SelectionEdit.InsertBelow(doc, 8, "R", "\n"); // selection "two\n"
            Assert.Equal("one\ntwo\n\nR\nthree", Apply(doc, e));
        }

        [Fact]
        public void InsertBelow_a_CRLF_selection_ending_at_a_line_start()
        {
            string doc = "one\r\ntwo\r\nthree";
            var e = SelectionEdit.InsertBelow(doc, 10, "R", "\r\n");
            Assert.Equal("one\r\ntwo\r\n\r\nR\r\nthree", Apply(doc, e));
        }

        [Fact]
        public void InsertBelow_at_the_document_end_appends()
        {
            string doc = "one\ntwo";
            var e = SelectionEdit.InsertBelow(doc, doc.Length, "R", "\n");
            Assert.Equal("one\ntwo\n\nR", Apply(doc, e));
            Assert.Equal("R", Apply(doc, e).Substring(e.SelectionStart, e.SelectionLength));
        }

        [Fact]
        public void InsertBelow_on_an_empty_document()
        {
            var e = SelectionEdit.InsertBelow("", 0, "R", "\n");
            Assert.Equal("\n\nR", Apply("", e));
            Assert.Equal(2, e.SelectionStart);
            Assert.Equal(1, e.SelectionLength);
        }

        private static string Apply(string doc, TextEdit e) =>
            doc.Substring(0, e.Offset) + e.Text + doc.Substring(e.Offset + e.Length);
    }
}
