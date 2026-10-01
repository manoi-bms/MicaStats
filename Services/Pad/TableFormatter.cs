using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Format → Format table (spec 3): pads a pipe table's cells so the pipes line up by display
    /// width (East Asian wide characters count 2, combining marks 0), honoring each column's
    /// alignment. Pure: it returns one replacement for the window to apply as one undo step. The
    /// text is changed only by this command.
    /// </summary>
    public static class TableFormatter
    {
        private enum Align
        {
            None,
            Left,
            Center,
            Right,
        }

        private readonly record struct Line(int Start, string Text, string Break);

        /// <summary>The 0-based first and last line of the table around <paramref name="offset"/>, or null.</summary>
        public static (int First, int Last)? TableAt(string text, int offset)
        {
            var lines = Lines(text ?? "");
            return Range(lines, Facts(lines), LineOf(lines, offset));
        }

        /// <summary>The replacement that formats the table around <paramref name="offset"/>, or null when there is none.</summary>
        public static TextEdit? Format(string text, int offset)
        {
            var lines = Lines(text ?? "");
            int at = LineOf(lines, offset);
            if (Range(lines, Facts(lines), at) is not { } range) return null;

            var rows = new List<List<string>>();
            var aligns = new List<Align>();
            for (int i = range.First; i <= range.Last; i++)
            {
                var cells = TableCells.Split(lines[i].Text);
                if (i == range.First + 1) aligns = cells.Select(AlignOf).ToList();
                else rows.Add(cells);
            }

            int columns = Math.Max(rows.Max(r => r.Count), aligns.Count);
            var widths = new int[columns];
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(3, rows.Max(r => c < r.Count ? DisplayWidth(r[c]) : 0));

            string indent = Indent(lines[range.First].Text);
            var output = new List<string> { Row(rows[0], widths, aligns, indent), Delimiter(widths, aligns, indent) };
            output.AddRange(rows.Skip(1).Select(row => Row(row, widths, aligns, indent)));

            string newline = lines[range.First].Break.Length > 0 ? lines[range.First].Break : "\n";
            int start = lines[range.First].Start;
            int end = lines[range.Last].Start + lines[range.Last].Text.Length;
            int caretLine = Math.Clamp(at, range.First, range.Last) - range.First;
            int caret = start + output.Take(caretLine).Sum(l => l.Length + newline.Length);
            return new TextEdit(start, end - start, string.Join(newline, output), caret, 0);
        }

        /// <summary>How many monospace columns <paramref name="s"/> takes: wide East Asian characters 2, combining marks 0, others 1.</summary>
        public static int DisplayWidth(string s)
        {
            int width = 0;
            foreach (var rune in (s ?? "").EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
                width += IsWide(rune.Value) ? 2 : 1;
            }
            return width;
        }

        private static bool IsWide(int c) =>
            (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) || (c >= 0xAC00 && c <= 0xD7A3)
            || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6) || (c >= 0x1F300 && c <= 0x1F64F) || (c >= 0x1F900 && c <= 0x1F9FF)
            || (c >= 0x20000 && c <= 0x3FFFD);

        private static MdLineFacts[] Facts(List<Line> lines) => MarkdownStructure.Scan(lines.Select(l => l.Text).ToList()).Facts;

        private static (int First, int Last)? Range(List<Line> lines, MdLineFacts[] facts, int at)
        {
            if (at < 0 || facts[at].Table == MdTableRole.None) return null;
            int first = at;
            while (facts[first].Table != MdTableRole.Header && first > 0 && facts[first - 1].Table != MdTableRole.None) first--;
            int last = at;
            while (last + 1 < lines.Count && facts[last + 1].Table is MdTableRole.Delimiter or MdTableRole.Row) last++;
            return (first, last);
        }

        private static List<Line> Lines(string text)
        {
            var lines = new List<Line>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int textEnd = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(new Line(start, text.Substring(start, textEnd - start), text.Substring(textEnd, i + 1 - textEnd)));
                start = i + 1;
            }
            lines.Add(new Line(start, text.Substring(start), ""));
            return lines;
        }

        private static int LineOf(List<Line> lines, int offset)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
                if (offset >= lines[i].Start) return i;
            return -1;
        }

        private static Align AlignOf(string cell)
        {
            bool left = cell.StartsWith(':');
            bool right = cell.EndsWith(':');
            return left && right ? Align.Center : right ? Align.Right : left ? Align.Left : Align.None;
        }

        private static string Indent(string line)
        {
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            return line.Substring(0, i);
        }

        private static string Row(List<string> cells, int[] widths, List<Align> aligns, string indent)
        {
            var row = new StringBuilder(indent).Append('|');
            for (int c = 0; c < widths.Length; c++)
            {
                string cell = c < cells.Count ? cells[c] : "";
                int pad = widths[c] - DisplayWidth(cell);
                var align = c < aligns.Count ? aligns[c] : Align.None;
                int left = align switch { Align.Right => pad, Align.Center => pad / 2, _ => 0 };
                row.Append(' ').Append(' ', left).Append(cell).Append(' ', pad - left).Append(" |");
            }
            return row.ToString();
        }

        private static string Delimiter(int[] widths, List<Align> aligns, string indent)
        {
            var row = new StringBuilder(indent).Append('|');
            for (int c = 0; c < widths.Length; c++)
            {
                int w = widths[c];
                string dashes = (c < aligns.Count ? aligns[c] : Align.None) switch
                {
                    Align.Left => ":" + new string('-', w - 1),
                    Align.Center => ":" + new string('-', w - 2) + ":",
                    Align.Right => new string('-', w - 1) + ":",
                    _ => new string('-', w),
                };
                row.Append(' ').Append(dashes).Append(" |");
            }
            return row.ToString();
        }
    }
}
