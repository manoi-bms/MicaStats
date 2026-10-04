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

        // ---- a reply to Fix with AI that came back in a code fence (part 2, spec 2.2) ----------------

        [Theory]
        [InlineData("```mermaid\nflowchart LR\n  a --> b\n```", "flowchart LR\n  a --> b")]
        [InlineData("```\nx\n```", "x")]                                    // no info word
        [InlineData("~~~~dot my graph\ndigraph {}\n~~~~", "digraph {}")]     // tildes, and an info string
        [InlineData("```\r\na\r\nb\r\n```", "a\r\nb")]                       // the reply's own line breaks stay
        [InlineData("```\ra\r```", "a")]
        [InlineData("  ```mermaid\na\n   ```  ", "a")]                       // up to three spaces before either, spaces after the closing one
        [InlineData("```mermaid\na\n`````", "a")]                           // a closing fence may be longer
        [InlineData("```\n\n  a\n\n```", "\n  a\n")]                         // only the two fence lines go
        [InlineData("```\n```", "")]                                        // nothing between them
        public void Unfenced_drops_one_enclosing_fence_pair(string reply, string inside) =>
            Assert.Equal(inside, SelectionEdit.Unfenced(reply));

        [Theory]
        [InlineData("")]
        [InlineData("flowchart LR\n  a --> b")]
        [InlineData("```")]                                                 // one line is no pair
        [InlineData("```mermaid\nflowchart LR")]                            // never closed
        [InlineData("flowchart LR\n```")]                                   // closed, never opened
        [InlineData("````mermaid\na\n```")]                                 // a shorter fence does not close it
        [InlineData("```mermaid\na\n~~~")]                                  // nor does the other character
        [InlineData("```mermaid\na\n``` done")]                             // nor a line with words after it
        [InlineData("Here it is:\n```mermaid\na\n```")]                     // the first line is no fence
        [InlineData("```mermaid\na\n```\nThat fixes it.")]                  // the last line is no fence
        [InlineData("$$\nx^2\n$$")]                                         // backticks and tildes only
        [InlineData("    ```\na\n    ```")]                                 // four spaces: indented code, not a fence
        public void Unfenced_leaves_anything_else_as_it_is(string reply) =>
            Assert.Equal(reply, SelectionEdit.Unfenced(reply));

        [Fact]
        public void Unfenced_takes_one_pair_only_and_null_is_empty()
        {
            Assert.Equal("```\na\n```", SelectionEdit.Unfenced("````\n```\na\n```\n````"));
            Assert.Equal("", SelectionEdit.Unfenced(null!));
        }

        [Theory]
        [InlineData("```mermaid", '`', 3)]
        [InlineData("   ~~~~", '~', 4)]
        [InlineData("`````  js title", '`', 5)]
        [InlineData("$$", '$', 2)]
        [InlineData(" $$  ", '$', 2)]
        public void The_delimiter_of_an_opening_line_is_its_fence_character_and_how_many(string line, char fence, int length) =>
            Assert.Equal((fence, length), FenceTracker.DelimiterOf(line));

        [Theory]
        [InlineData("")]
        [InlineData("text")]
        [InlineData("``")]
        [InlineData("    ```")]                                             // four spaces
        [InlineData("``` a `b`")]                                           // a backtick in a backtick fence's info string: inline code
        [InlineData("$$ x $$")]
        [InlineData("$$$")]
        public void A_line_that_opens_no_block_has_no_delimiter(string line) =>
            Assert.Null(FenceTracker.DelimiterOf(line));

        [Theory]
        [InlineData("```", '`', 3, true)]
        [InlineData("   `````  ", '`', 3, true)]                            // longer, indented up to three spaces, spaces after
        [InlineData("``", '`', 3, false)]
        [InlineData("```js", '`', 3, false)]                                // an info word: it would open, not close
        [InlineData("~~~", '`', 3, false)]
        [InlineData("    ```", '`', 3, false)]
        [InlineData("~~~~~", '~', 4, true)]
        [InlineData("~~~", '~', 4, false)]
        [InlineData("$$", '$', 2, true)]
        [InlineData(" $$ ", '$', 2, true)]
        [InlineData("$$$", '$', 2, false)]                                  // a math block closes on exactly $$
        [InlineData("$$ x", '$', 2, false)]
        public void A_line_closes_a_block_when_it_is_only_that_blocks_fence_at_least_as_long(string line, char fence, int length, bool closes) =>
            Assert.Equal(closes, FenceTracker.Closes(line, fence, length));

        [Fact]
        public void Normalize_turns_every_line_break_into_the_notes_own()
        {
            Assert.Equal("a\r\nb\r\nc\r\nd", SelectionEdit.Normalize("a\nb\r\nc\rd", "\r\n"));
            Assert.Equal("a\nb\nc\nd", SelectionEdit.Normalize("a\nb\r\nc\rd", "\n"));
            Assert.Equal("a\rb\rc\rd", SelectionEdit.Normalize("a\nb\r\nc\rd", "\r"));
        }

        [Fact]
        public void Normalize_keeps_blank_lines_and_never_splits_or_doubles_a_CRLF()
        {
            Assert.Equal("a\r\n\r\nb\r\n", SelectionEdit.Normalize("a\n\nb\n", "\r\n"));
            Assert.Equal("a\r\nb", SelectionEdit.Normalize("a\r\nb", "\r\n"));        // already the note's own
            Assert.Equal("\r\n\r\n", SelectionEdit.Normalize("\r\r\n", "\r\n"));      // a lone CR, then a CRLF
            Assert.Equal("\n\n", SelectionEdit.Normalize("\n\r", "\n"));              // an LF, then a lone CR
        }

        [Fact]
        public void Normalize_leaves_text_without_a_line_break_as_it_is()
        {
            Assert.Equal("plain text", SelectionEdit.Normalize("plain text", "\r\n"));
            Assert.Equal("", SelectionEdit.Normalize("", "\n"));
            Assert.Equal("", SelectionEdit.Normalize(null!, "\n"));
        }

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

        [Fact]
        public void InsertBelow_walks_back_over_several_trailing_line_breaks()
        {
            string doc = "two\n\nnext";
            var e = SelectionEdit.InsertBelow(doc, 5, "R", "\n");
            Assert.Equal("two\n\nR\n\nnext", Apply(doc, e));
            Assert.Equal("R", Apply(doc, e).Substring(e.SelectionStart, e.SelectionLength));
        }

        [Fact]
        public void InsertBelow_walks_back_over_several_CRLF_line_breaks()
        {
            string doc = "two\r\n\r\nnext";
            var e = SelectionEdit.InsertBelow(doc, 7, "R", "\r\n");
            Assert.Equal("two\r\n\r\nR\r\n\r\nnext", Apply(doc, e));
            Assert.Equal("R", Apply(doc, e).Substring(e.SelectionStart, e.SelectionLength));
        }

        [Fact]
        public void InsertBelow_whole_note_ending_in_blank_lines()
        {
            string doc = "text\n\n";
            var e = SelectionEdit.InsertBelow(doc, doc.Length, "R", "\n");
            Assert.Equal("text\n\nR\n\n", Apply(doc, e));
            string crlf = "text\r\n\r\n";
            var f = SelectionEdit.InsertBelow(crlf, crlf.Length, "R", "\r\n");
            Assert.Equal("text\r\n\r\nR\r\n\r\n", Apply(crlf, f));
        }

        [Fact]
        public void InsertBelow_a_source_of_only_line_breaks_inserts_at_the_first_line_end()
        {
            string doc = "\n\nnext";
            var e = SelectionEdit.InsertBelow(doc, 2, "R", "\n");
            Assert.Equal(0, e.Offset);
            Assert.Equal("\n\nR\n\nnext", Apply(doc, e));
        }

        [Fact]
        public void InsertBelow_at_offset_zero_goes_after_the_first_line()
        {
            string doc = "one\ntwo";
            var e = SelectionEdit.InsertBelow(doc, 0, "R", "\n");
            Assert.Equal("one\n\nR\ntwo", Apply(doc, e));
        }

        [Fact]
        public void InsertBelow_a_document_ending_with_one_line_break()
        {
            string doc = "one\ntwo\n";
            var e = SelectionEdit.InsertBelow(doc, doc.Length, "R", "\n");
            Assert.Equal("one\ntwo\n\nR\n", Apply(doc, e));
        }

        private static string Apply(string doc, TextEdit e) =>
            doc.Substring(0, e.Offset) + e.Text + doc.Substring(e.Offset + e.Length);
    }
}
