using System;
using System.Collections.Generic;
using System.Globalization;
using DiffPlex;
using DiffPlex.Chunkers;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What happened to one line between a version and the current text.</summary>
    public enum DiffKind
    {
        Unchanged,
        Added,
        Removed,
    }

    /// <summary>
    /// One line of a compare: its text (without the line break), what happened to it, and its
    /// 1-based number in the old version and in the current text (null where it is not in that text).
    /// </summary>
    public readonly record struct DiffRow(DiffKind Kind, string Text, int? OldLine, int? NewLine);

    /// <summary>A compare: the rows in reading order and the counts, or <see cref="TooLarge"/> with no rows.</summary>
    public sealed record DiffOutcome(IReadOnlyList<DiffRow> Rows, int Added, int Removed, bool TooLarge)
    {
        /// <summary><c>+12 −3 lines</c>, <c>No changes</c> or <c>Too large to compare</c>, for the banner.</summary>
        public string Summary => TooLarge ? HistoryDiff.TooLargeText : HistoryDiff.Describe(Added, Removed);
    }

    /// <summary>
    /// History ▸ Compare with current (spec 5.2): a line diff of a version against the current
    /// text, by DiffPlex 1.9.0 (Apache-2.0). Spaces and letter case count; line endings do not (a
    /// CRLF line and an LF line compare equal). Within a change, removed lines come before added
    /// ones. Pure and thread-safe (DiffPlex's <see cref="Differ"/> keeps no state), so the window
    /// runs it off the UI thread; with the two limits below a compare takes a second or two at most.
    /// </summary>
    public static class HistoryDiff
    {
        /// <summary>Either text longer than this (1 MB of characters, like MicaPad's other MB limits) is not compared.</summary>
        public const int MaxChars = 1024 * 1024;

        /// <summary>
        /// More lines than this between the texts' common first and last lines, counted over both,
        /// are not compared either. DiffPlex's time grows with the lines times the changes: two
        /// 1 MB notes of short lines that differ throughout would keep a core busy for minutes. At
        /// this limit a compare takes about a second; a long note with a few changes stays well under it.
        /// </summary>
        public const int MaxChangedLines = 50_000;

        public const string TooLargeText = "Too large to compare";

        public static DiffOutcome Compare(string oldText, string newText)
        {
            if (oldText.Length > MaxChars || newText.Length > MaxChars || LinesBetweenCommonEnds(oldText, newText) > MaxChangedLines)
                return new DiffOutcome(Array.Empty<DiffRow>(), 0, 0, TooLarge: true);

            var result = Differ.Instance.CreateDiffs(oldText, newText, ignoreWhiteSpace: false, ignoreCase: false, chunker: LineChunker.Instance);
            var oldLines = result.PiecesOld;     // an empty text has no lines at all
            var newLines = result.PiecesNew;
            var rows = new List<DiffRow>(Math.Max(oldLines.Count, newLines.Count));
            int a = 0, b = 0, added = 0, removed = 0;

            foreach (var block in result.DiffBlocks)
            {
                // The lines before a block are the same in both texts.
                while (a < block.DeleteStartA && b < block.InsertStartB)
                {
                    rows.Add(new DiffRow(DiffKind.Unchanged, newLines[b], a + 1, b + 1));
                    a++;
                    b++;
                }
                for (int i = 0; i < block.DeleteCountA; i++, a++, removed++)
                    rows.Add(new DiffRow(DiffKind.Removed, oldLines[a], a + 1, null));
                for (int i = 0; i < block.InsertCountB; i++, b++, added++)
                    rows.Add(new DiffRow(DiffKind.Added, newLines[b], null, b + 1));
            }
            while (a < oldLines.Count && b < newLines.Count)
            {
                rows.Add(new DiffRow(DiffKind.Unchanged, newLines[b], a + 1, b + 1));
                a++;
                b++;
            }
            return new DiffOutcome(rows, added, removed, TooLarge: false);
        }

        /// <summary>
        /// The lines of both texts left once their common first and last lines are set aside, split
        /// as the diff splits them: an upper bound on the changes, so on the diff's work.
        /// </summary>
        private static int LinesBetweenCommonEnds(string oldText, string newText)
        {
            var a = LineChunker.Instance.Chunk(oldText);
            var b = LineChunker.Instance.Chunk(newText);
            int start = 0;
            while (start < a.Count && start < b.Count && string.Equals(a[start], b[start], StringComparison.Ordinal)) start++;
            int end = 0;
            while (end < a.Count - start && end < b.Count - start
                   && string.Equals(a[a.Count - 1 - end], b[b.Count - 1 - end], StringComparison.Ordinal)) end++;
            return (a.Count - start - end) + (b.Count - start - end);
        }

        /// <summary><c>+12 −3 lines</c> (U+2212 minus), or <c>No changes</c>.</summary>
        public static string Describe(int added, int removed) =>
            added == 0 && removed == 0
                ? "No changes"
                : string.Format(CultureInfo.InvariantCulture, "+{0} −{1} lines", added, removed);
    }
}
