using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// notepad4-style line operations (spec 3.2). Each returns one <see cref="TextEdit"/> for the
    /// window to apply as a single undoable change, or null when there is nothing to change. They
    /// work on the whole lines the selection touches (the whole text for sort, dedupe and trim when
    /// nothing is selected) and keep every line break exactly as it was.
    /// </summary>
    public static class LineOperations
    {
        /// <summary>Duplicates the selection after itself (selecting the copy), or the caret's line below it (same column).</summary>
        public static TextEdit Duplicate(string text, int start, int length)
        {
            if (length > 0)
            {
                string selected = text.Substring(start, length);
                return new TextEdit(start + length, 0, selected, start + length, length);
            }
            var (lineStart, lineEnd) = TextLines.Block(text, start, 0);
            string line = text.Substring(lineStart, lineEnd - lineStart);
            int breakLength = TextLines.BreakLength(text, lineEnd);
            string newline = breakLength > 0 ? text.Substring(lineEnd, breakLength) : TextLines.NewlineOf(text);
            return new TextEdit(lineEnd, 0, newline + line, lineEnd + newline.Length + (start - lineStart), 0);
        }

        /// <summary>Moves the selected lines above the line before them; null on the first line.</summary>
        public static TextEdit? MoveUp(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            if (blockStart == 0) return null;
            int prevEnd = blockStart - TextLines.BreakBefore(text, blockStart);
            int prevStart = TextLines.LineStart(text, prevEnd);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            string separator = text.Substring(prevEnd, blockStart - prevEnd);
            string previous = text.Substring(prevStart, prevEnd - prevStart);
            int shift = blockStart - prevStart;
            return new TextEdit(prevStart, blockEnd - prevStart, block + separator + previous, start - shift, length);
        }

        /// <summary>Moves the selected lines below the line after them; null on the last line.</summary>
        public static TextEdit? MoveDown(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            if (blockEnd >= text.Length) return null;
            int nextStart = blockEnd + TextLines.BreakLength(text, blockEnd);
            int nextEnd = TextLines.LineEnd(text, nextStart);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            string separator = text.Substring(blockEnd, nextStart - blockEnd);
            string next = text.Substring(nextStart, nextEnd - nextStart);
            int shift = next.Length + separator.Length;
            return new TextEdit(blockStart, nextEnd - blockStart, next + separator + block, start + shift, length);
        }

        /// <summary>
        /// Joins the selected lines (or this line with the next) with one space, trimming the spaces
        /// around each join; empty lines disappear. Null when there is no next line.
        /// </summary>
        public static TextEdit? Join(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            var (lines, _) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            if (lines.Count < 2)
            {
                if (blockEnd >= text.Length) return null;
                blockEnd = TextLines.LineEnd(text, blockEnd + TextLines.BreakLength(text, blockEnd));
                (lines, _) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            }
            string first = lines[0].TrimEnd();
            string joined = first;
            foreach (string line in lines.Skip(1))
            {
                string part = line.Trim();
                if (part.Length == 0) continue;
                joined += joined.Length == 0 ? part : " " + part;
            }
            return length > 0
                ? new TextEdit(blockStart, blockEnd - blockStart, joined, blockStart, joined.Length)
                : new TextEdit(blockStart, blockEnd - blockStart, joined, blockStart + first.Length, 0);
        }

        /// <summary>
        /// Sorts lines, case-insensitive in <paramref name="culture"/>, stable (equal lines keep their
        /// order). Trailing empty lines (the text's last newlines) stay last. Null when already sorted.
        /// </summary>
        public static TextEdit? Sort(string text, int start, int length, bool descending, CultureInfo culture)
        {
            var comparer = StringComparer.Create(culture, ignoreCase: true);
            return Rewrite(text, start, length, lines =>
            {
                int keep = 0;
                while (keep < lines.Count - 1 && lines[lines.Count - 1 - keep].Length == 0) keep++;
                var body = lines.Take(lines.Count - keep);
                var sorted = (descending ? body.OrderByDescending(l => l, comparer) : body.OrderBy(l => l, comparer)).ToList();
                for (int i = 0; i < keep; i++) sorted.Add("");
                return sorted;
            });
        }

        /// <summary>Removes repeated lines, keeping the first of each; blank lines are never removed.</summary>
        public static TextEdit? RemoveDuplicates(string text, int start, int length)
        {
            var (blockStart, blockEnd, whole) = Range(text, start, length);
            var (lines, breaks) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keptLines = new List<string>();
            var keptBreaks = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]) && !seen.Add(lines[i])) continue;
                keptLines.Add(lines[i]);
                keptBreaks.Add(i < breaks.Count ? breaks[i] : "");
            }
            if (keptLines.Count == lines.Count) return null;
            // The block never ended with a break: neither may the kept lines.
            keptBreaks[^1] = "";
            string block = string.Concat(keptLines.Select((l, i) => l + keptBreaks[i]));
            return Result(text, start, length, blockStart, blockEnd, block, whole);
        }

        /// <summary>Removes spaces and tabs at the end of each line.</summary>
        public static TextEdit? TrimTrailing(string text, int start, int length) =>
            Rewrite(text, start, length, lines => lines.Select(l => l.TrimEnd(' ', '\t')).ToList());

        /// <summary>The lines to work on: the selection's lines, or the whole text when nothing is selected.</summary>
        private static (int Start, int End, bool Whole) Range(string text, int start, int length)
        {
            if (length == 0) return (0, text.Length, true);
            var (s, e) = TextLines.Block(text, start, length);
            return (s, e, false);
        }

        /// <summary>Applies a per-line rewrite that keeps the number of lines, then builds the edit.</summary>
        private static TextEdit? Rewrite(string text, int start, int length, Func<List<string>, List<string>> rewrite)
        {
            var (blockStart, blockEnd, whole) = Range(text, start, length);
            var (lines, breaks) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            var rewritten = rewrite(lines);
            if (rewritten.SequenceEqual(lines, StringComparer.Ordinal)) return null;
            string block = TextLines.Join(rewritten, breaks);
            return Result(text, start, length, blockStart, blockEnd, block, whole);
        }

        /// <summary>
        /// The edit replacing the block. A selection selects the new block; with nothing selected the
        /// caret stays on the same line number, at the same column where that line is long enough.
        /// </summary>
        private static TextEdit Result(string text, int start, int length, int blockStart, int blockEnd, string block, bool whole)
        {
            if (!whole) return new TextEdit(blockStart, blockEnd - blockStart, block, blockStart, block.Length);

            int lineIndex = 0;
            for (int i = 0; i < start && i < text.Length; i++)
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) lineIndex++;
            int column = start - TextLines.LineStart(text, start);

            var (lines, breaks) = TextLines.Split(block);
            lineIndex = Math.Min(lineIndex, lines.Count - 1);
            int offset = 0;
            for (int i = 0; i < lineIndex; i++) offset += lines[i].Length + breaks[i].Length;
            int caret = blockStart + offset + Math.Min(column, lines[lineIndex].Length);
            return new TextEdit(blockStart, blockEnd - blockStart, block, caret, 0);
        }
    }
}
