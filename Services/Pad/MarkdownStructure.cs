using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A table line's part (spec 2).</summary>
    public enum MdTableRole
    {
        None,
        Header,
        Delimiter,
        Row,
    }

    /// <summary>A Wiki.js callout kind: <c>{.is-info}</c> and friends under a quote.</summary>
    public enum MdCallout
    {
        None,
        Info,
        Success,
        Warning,
        Danger,
    }

    /// <summary>What a line is in its document, beyond what the line alone says (spec 2).</summary>
    public readonly record struct MdLineFacts(
        MdFence Fence,
        MdTableRole Table = MdTableRole.None,
        int SetextLevel = 0,
        bool SetextUnderline = false,
        bool FrontMatter = false,
        MdCallout Callout = MdCallout.None,
        bool CalloutClass = false)
    {
        /// <summary>True when the document, not the line alone, decides how the line is shown.</summary>
        public bool IsStructural =>
            Fence != MdFence.None || Table != MdTableRole.None || SetextLevel != 0 || SetextUnderline
            || FrontMatter || Callout != MdCallout.None || CalloutClass;
    }

    /// <summary>
    /// One scan of a Markdown document (spec 2): fences and <c>$$</c> blocks, front matter,
    /// tables, setext headings, Wiki.js callouts and abbreviation terms. Pure; always recomputed
    /// from the text, nothing is saved.
    /// </summary>
    public sealed class MarkdownStructure
    {
        /// <summary>How far down front matter may close (line index).</summary>
        public const int FrontMatterSearch = 200;

        private static readonly Regex DelimiterCellRx = new(@"^:?-+:?$", RegexOptions.CultureInvariant);
        private static readonly Regex SetextRx = new(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex CalloutRx = new(@"^ {0,3}\{\.is-(info|success|warning|danger)\}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}>", RegexOptions.CultureInvariant);
        private static readonly Regex AbbreviationRx = new(@"^\*\[([^\]]+)\]:", RegexOptions.CultureInvariant);

        private MarkdownStructure(MdFence[] fences, int[] openings, int[] closings, int[] blockOpenings, MdLineFacts[] facts, IReadOnlySet<string> abbreviations)
        {
            Fences = fences;
            Openings = openings;
            Closings = closings;
            BlockOpenings = blockOpenings;
            Facts = facts;
            Abbreviations = abbreviations;
        }

        /// <summary>The structure of an empty document.</summary>
        public static MarkdownStructure Empty { get; } = Scan(Array.Empty<string>());

        public int LineCount => Facts.Length;

        /// <summary>Each line's fence kind (index = line number - 1).</summary>
        public MdFence[] Fences { get; }

        /// <summary>For a closing delimiter, the 1-based line it closes; else 0.</summary>
        public int[] Openings { get; }

        /// <summary>For an opening delimiter, the 1-based line that closes it; else 0.</summary>
        public int[] Closings { get; }

        /// <summary>For a line inside a fenced or <c>$$</c> block, the 1-based line that opened it; else 0.</summary>
        public int[] BlockOpenings { get; }

        public MdLineFacts[] Facts { get; }

        /// <summary>The terms defined by <c>*[TERM]: text</c> lines.</summary>
        public IReadOnlySet<string> Abbreviations { get; }

        public static MarkdownStructure Scan(IReadOnlyList<string> lines)
        {
            int n = lines.Count;
            var fences = FenceTracker.Classify(lines);
            var openings = FenceTracker.Openings(fences);
            var closings = new int[n];
            var blockOpenings = new int[n];
            int open = 0;
            for (int i = 0; i < n; i++)
            {
                if (openings[i] > 0) closings[openings[i] - 1] = i + 1;
                if (fences[i] == MdFence.Delimiter) open = open == 0 ? i + 1 : 0;
                else if (fences[i] == MdFence.Inside) blockOpenings[i] = open;
            }

            var facts = new MdLineFacts[n];
            for (int i = 0; i < n; i++) facts[i] = new MdLineFacts(fences[i]);
            MarkFrontMatter(lines, facts);
            MarkTables(lines, facts);
            MarkSetext(lines, facts);
            MarkCallouts(lines, facts);
            return new MarkdownStructure(fences, openings, closings, blockOpenings, facts, CollectAbbreviations(lines, facts));
        }

        /// <summary>
        /// A delimiter row: it holds a pipe and every cell is dashes with optional colons at its
        /// ends (<c>:--</c>, <c>:-:</c>, <c>--:</c>).
        /// </summary>
        public static bool IsDelimiterRow(string line, out int columns)
        {
            columns = 0;
            if (line == null || line.IndexOf('|') < 0 || line.IndexOf('-') < 0) return false;
            var cells = TableCells.Split(line);
            foreach (string cell in cells)
                if (!DelimiterCellRx.IsMatch(cell)) return false;
            columns = cells.Count;
            return true;
        }

        private static string At(IReadOnlyList<string> lines, int i) => lines[i] ?? "";

        /// <summary>Neither fenced, front matter nor already part of a table.</summary>
        private static bool Free(MdLineFacts f) => f.Fence == MdFence.None && !f.FrontMatter && f.Table == MdTableRole.None;

        private static void MarkFrontMatter(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            if (lines.Count < 2 || At(lines, 0).TrimEnd() != "---") return;
            int last = Math.Min(lines.Count - 1, FrontMatterSearch);
            for (int k = 1; k <= last; k++)
            {
                string text = At(lines, k).TrimEnd();
                if (text != "---" && text != "...") continue;
                for (int i = 0; i <= k; i++) facts[i] = facts[i] with { FrontMatter = true };
                return;
            }
        }

        private static void MarkTables(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            int i = 0;
            while (i + 1 < lines.Count)
            {
                string header = At(lines, i);
                if (!Free(facts[i]) || !Free(facts[i + 1]) || header.IndexOf('|') < 0 || QuoteRx.IsMatch(header)
                    || !IsDelimiterRow(At(lines, i + 1), out int columns) || TableCells.Split(header).Count != columns)
                {
                    i++;
                    continue;
                }

                facts[i] = facts[i] with { Table = MdTableRole.Header };
                facts[i + 1] = facts[i + 1] with { Table = MdTableRole.Delimiter };
                int j = i + 2;
                while (j < lines.Count && Free(facts[j]) && At(lines, j).IndexOf('|') >= 0)
                {
                    facts[j] = facts[j] with { Table = MdTableRole.Row };
                    j++;
                }
                i = j;
            }
        }

        private static void MarkSetext(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var m = SetextRx.Match(At(lines, i));
                if (!m.Success || m.Groups[1].Length < 2 || !Free(facts[i]) || !Free(facts[i - 1]) || facts[i - 1].SetextUnderline) continue;

                string text = At(lines, i - 1);
                if (string.IsNullOrWhiteSpace(text) || MarkdownLineTokenizer.BlockOf(text, MdFence.None) != MdBlock.Paragraph) continue;

                facts[i - 1] = facts[i - 1] with { SetextLevel = m.Groups[1].Value[0] == '=' ? 1 : 2 };
                facts[i] = facts[i] with { SetextUnderline = true };
            }
        }

        private static void MarkCallouts(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var m = CalloutRx.Match(At(lines, i));
                if (!m.Success || facts[i].Fence != MdFence.None || !IsQuote(lines, facts, i - 1)) continue;

                var kind = m.Groups[1].Value switch
                {
                    "info" => MdCallout.Info,
                    "success" => MdCallout.Success,
                    "warning" => MdCallout.Warning,
                    _ => MdCallout.Danger,
                };
                facts[i] = facts[i] with { CalloutClass = true };
                for (int k = i - 1; k >= 0 && IsQuote(lines, facts, k); k--) facts[k] = facts[k] with { Callout = kind };
            }
        }

        private static bool IsQuote(IReadOnlyList<string> lines, MdLineFacts[] facts, int i) =>
            facts[i].Fence == MdFence.None && !facts[i].FrontMatter && QuoteRx.IsMatch(At(lines, i));

        private static IReadOnlySet<string> CollectAbbreviations(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            var terms = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                if (facts[i].Fence != MdFence.None) continue;
                var m = AbbreviationRx.Match(At(lines, i));
                if (m.Success && m.Groups[1].Value.Trim().Length > 0) terms.Add(m.Groups[1].Value.Trim());
            }
            return terms;
        }
    }
}
