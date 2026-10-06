using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Splitting a replacement into the pieces that change, so anchors elsewhere stay put.</summary>
    public class TextPiecesTests
    {
        /// <summary>Applies pieces from the last to the first, as the editor does.</summary>
        private static string Apply(string text, IReadOnlyList<TextPiece> pieces)
        {
            for (int i = pieces.Count - 1; i >= 0; i--)
                text = text.Remove(pieces[i].Offset, pieces[i].Length).Insert(pieces[i].Offset, pieces[i].Text);
            return text;
        }

        [Fact]
        public void The_same_number_of_lines_replaces_only_the_changed_lines_content()
        {
            var pieces = TextPieces.Plan("a  \nb\nc  ", "a\nb\nc");
            Assert.Equal(new[] { new TextPiece(1, 2, ""), new TextPiece(7, 2, "") }, pieces);
        }

        [Fact]
        public void A_changed_line_break_is_its_own_piece()
        {
            var pieces = TextPieces.Plan("x\r\ny\nz", "y\nx\r\nz");
            Assert.Equal("y\nx\r\nz", Apply("x\r\ny\nz", pieces));
            Assert.All(pieces, p => Assert.True(p.Length <= 2));
        }

        [Fact]
        public void A_different_number_of_lines_is_one_piece_around_what_differs()
        {
            var pieces = TextPieces.Plan("a\nb\na\nc", "a\nb\nc");
            var piece = Assert.Single(pieces);
            Assert.Equal("a\nb\nc", Apply("a\nb\na\nc", pieces));
            Assert.True(piece.Offset >= 3);                 // the first two lines are not touched
        }

        [Fact]
        public void Equal_text_has_no_pieces() => Assert.Empty(TextPieces.Plan("same\ntext", "same\ntext"));

        [Fact]
        public void Wrapping_preserves_the_original_content_as_two_insertions()
        {
            const string text = "- [ ] task (created: 2026-10-06 07:30)";
            string wrapped = "```\r\n" + text + "\r\n```";
            var pieces = TextPieces.Plan(text, wrapped);
            Assert.Equal(new[] { new TextPiece(0, 0, "```\r\n"), new TextPiece(text.Length, 0, "\r\n```") }, pieces);
            Assert.Equal(wrapped, Apply(text, pieces));
        }

        [Fact]
        public void Past_the_limit_the_lines_become_one_piece()
        {
            string before = string.Join("\n", Enumerable.Repeat("x ", TextPieces.MaxPieces + 1));
            string after = string.Join("\n", Enumerable.Repeat("x", TextPieces.MaxPieces + 1));
            var piece = Assert.Single(TextPieces.Plan(before, after));
            Assert.Equal(after, Apply(before, new[] { piece }));
        }

        [Theory]
        [InlineData("", "abc")]
        [InlineData("abc", "")]
        [InlineData("one\ntwo", "one\ntwo\nthree")]
        [InlineData("1\r\n2\r\n3", "3\r\n2\r\n1")]
        [InlineData("héllo\nwörld", "hello\nworld")]
        public void Applying_the_pieces_gives_the_new_text(string before, string after)
        {
            Assert.Equal(after, Apply(before, TextPieces.Plan(before, after)));
        }

        [Fact]
        public void Combine_makes_one_piece_from_the_first_to_the_last()
        {
            var combined = TextPieces.Combine("a-b-c", new[] { new TextPiece(1, 1, "+"), new TextPiece(3, 1, "++") });
            Assert.Equal(new TextPiece(1, 3, "+b++"), combined);
        }
    }
}
