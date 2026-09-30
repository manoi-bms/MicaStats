using System;
using System.Collections.Generic;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One replacement: <see cref="Length"/> characters at <see cref="Offset"/> become <see cref="Text"/>.</summary>
    public readonly record struct TextPiece(int Offset, int Length, string Text);

    /// <summary>
    /// Splits a replacement into the pieces that really change, the way Scintilla edits, so an
    /// editor applying them one by one (inside one undo group) leaves everything anchored outside
    /// the pieces — bookmarks above all — where it was. Replacing a whole block in one go would
    /// drop every anchor inside it at one place.
    /// </summary>
    public static class TextPieces
    {
        /// <summary>More pieces than this are applied as one: thousands of separate edits are slow.</summary>
        public const int MaxPieces = 5_000;

        /// <summary>
        /// The pieces turning <paramref name="oldText"/> into <paramref name="newText"/>: offsets into
        /// the old text, ascending, never overlapping (apply them from the last). With the same
        /// number of lines, one piece per line whose content changed (the whole content, so an anchor
        /// at the line start stays there) and one per changed line break. Otherwise, or past
        /// <see cref="MaxPieces"/>, a single piece from the first difference to the last. Empty when equal.
        /// </summary>
        public static IReadOnlyList<TextPiece> Plan(string oldText, string newText)
        {
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) return Array.Empty<TextPiece>();

            var (oldLines, oldBreaks) = TextLines.Split(oldText);
            var (newLines, newBreaks) = TextLines.Split(newText);
            if (oldLines.Count == newLines.Count)
            {
                var pieces = new List<TextPiece>();
                int offset = 0;
                for (int i = 0; i < oldLines.Count && pieces.Count <= MaxPieces; i++)
                {
                    if (!string.Equals(oldLines[i], newLines[i], StringComparison.Ordinal))
                        pieces.Add(new TextPiece(offset, oldLines[i].Length, newLines[i]));
                    offset += oldLines[i].Length;
                    if (i < oldBreaks.Count)
                    {
                        if (oldBreaks[i] != newBreaks[i]) pieces.Add(new TextPiece(offset, oldBreaks[i].Length, newBreaks[i]));
                        offset += oldBreaks[i].Length;
                    }
                }
                if (pieces.Count <= MaxPieces) return pieces;
            }
            return new[] { Around(oldText, newText) };
        }

        /// <summary>Pieces (ascending, not overlapping) as one piece from the first to the end of the last.</summary>
        public static TextPiece Combine(string text, IReadOnlyList<TextPiece> pieces)
        {
            int start = pieces[0].Offset;
            var sb = new StringBuilder();
            int at = start;
            foreach (var piece in pieces)
            {
                sb.Append(text, at, piece.Offset - at).Append(piece.Text);
                at = piece.Offset + piece.Length;
            }
            return new TextPiece(start, at - start, sb.ToString());
        }

        /// <summary>One piece covering only what differs: the common start and end are left out.</summary>
        private static TextPiece Around(string oldText, string newText)
        {
            int shorter = Math.Min(oldText.Length, newText.Length);
            int prefix = 0;
            while (prefix < shorter && oldText[prefix] == newText[prefix]) prefix++;
            int suffix = 0;
            while (suffix < shorter - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
            return new TextPiece(prefix, oldText.Length - prefix - suffix, newText.Substring(prefix, newText.Length - prefix - suffix));
        }
    }
}
