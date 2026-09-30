using System;
using System.Collections.Generic;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Whole-line helpers shared by the Format menu and the line operations: which lines a
    /// selection covers, and splitting and joining lines while keeping every line break exactly
    /// as it was (CRLF, LF or CR).
    /// </summary>
    public static class TextLines
    {
        private static readonly char[] Breaks = { '\r', '\n' };

        /// <summary>
        /// The whole lines a selection touches (or the caret's line), as [Start, End) without the
        /// final line break. A selection that ends right after a line break does not take the next line.
        /// </summary>
        public static (int Start, int End) Block(string text, int start, int length)
        {
            int end = start + length;
            if (length > 0 && text[end - 1] == '\n')
            {
                end--;
                if (end > start && text[end - 1] == '\r') end--;
            }
            else if (length > 0 && text[end - 1] == '\r')
            {
                end--;
            }
            int blockStart = LineStart(text, start);
            int lineEnd = text.IndexOfAny(Breaks, Math.Max(end, blockStart));
            return (blockStart, lineEnd < 0 ? text.Length : lineEnd);
        }

        /// <summary>Splits text into lines and the exact break after each; there is one more line than breaks.</summary>
        public static (List<string> Lines, List<string> Breaks) Split(string block)
        {
            var lines = new List<string>();
            var breaks = new List<string>();
            int lineStart = 0;
            int i = 0;
            while (i < block.Length)
            {
                char c = block[i];
                if (c != '\r' && c != '\n') { i++; continue; }
                int width = c == '\r' && i + 1 < block.Length && block[i + 1] == '\n' ? 2 : 1;
                lines.Add(block.Substring(lineStart, i - lineStart));
                breaks.Add(block.Substring(i, width));
                i += width;
                lineStart = i;
            }
            lines.Add(block.Substring(lineStart));
            return (lines, breaks);
        }

        /// <summary>Joins lines back with the breaks <see cref="Split"/> returned (or any list one shorter).</summary>
        public static string Join(IReadOnlyList<string> lines, IReadOnlyList<string> breaks)
        {
            var sb = new StringBuilder(lines.Count == 0 ? "" : lines[0]);
            for (int i = 1; i < lines.Count; i++) sb.Append(breaks[i - 1]).Append(lines[i]);
            return sb.ToString();
        }

        /// <summary>The text's line ending: its first line break, or CRLF for a text without one yet.</summary>
        public static string NewlineOf(string text)
        {
            int lf = text.IndexOf('\n');
            if (lf < 0) return text.Contains('\r') ? "\r" : "\r\n";
            return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        }

        /// <summary>The start of the line containing <paramref name="offset"/>.</summary>
        public static int LineStart(string text, int offset) =>
            offset <= 0 ? 0 : text.LastIndexOfAny(Breaks, offset - 1) + 1;

        /// <summary>The end of the line containing <paramref name="offset"/>, before its line break.</summary>
        public static int LineEnd(string text, int offset)
        {
            int i = text.IndexOfAny(Breaks, Math.Min(offset, text.Length));
            return i < 0 ? text.Length : i;
        }

        /// <summary>The length of the line break starting at <paramref name="offset"/>: 2 for CRLF, 1, or 0 at the end of the text.</summary>
        public static int BreakLength(string text, int offset)
        {
            if (offset >= text.Length) return 0;
            return text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
        }

        /// <summary>The length of the line break that ends just before <paramref name="lineStart"/>, or 0 on the first line.</summary>
        public static int BreakBefore(string text, int lineStart)
        {
            if (lineStart <= 0) return 0;
            return lineStart >= 2 && text[lineStart - 1] == '\n' && text[lineStart - 2] == '\r' ? 2 : 1;
        }
    }
}
