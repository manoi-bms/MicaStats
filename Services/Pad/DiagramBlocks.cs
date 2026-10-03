using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// A diagram block: its kind, its opening and closing fence lines (1-based) and its source,
    /// the text between them joined with "\n" (in the kroki form, the lines after the type line).
    /// A source over <see cref="DiagramBlocks.MaxSourceLength"/>
    /// characters is not read: <see cref="TooLarge"/> is set and the source is empty.
    /// </summary>
    public sealed record DiagramBlock(DiagramKind Kind, int OpenLine, int CloseLine, string Source, bool TooLarge);

    /// <summary>
    /// Finds the diagram blocks of a document (spec section 1): a closed fenced block whose info
    /// string's first word is a diagram word, or a closed <c>$$</c> math block (Markdown spec 6.1),
    /// with something other than whitespace inside (R4). A kroki block takes its kind from its
    /// first inside line (Markdown spec 6.2).
    /// </summary>
    public static class DiagramBlocks
    {
        /// <summary>The longest source that is drawn.</summary>
        public const int MaxSourceLength = 50_000;

        /// <summary>Every diagram block of the lines, in order.</summary>
        public static IReadOnlyList<DiagramBlock> Find(IReadOnlyList<string> lines, IReadOnlyList<MdFence> kinds)
        {
            var openings = FenceTracker.Openings(kinds);
            var blocks = new List<DiagramBlock>();
            for (int i = 0; i < openings.Length; i++)
            {
                if (openings[i] == 0) continue;
                if (Read(n => lines[n - 1] ?? "", openings[i], i + 1) is { } block) blocks.Add(block);
            }
            return blocks;
        }

        /// <summary>
        /// The block between the fence on <paramref name="openLine"/> and the one on
        /// <paramref name="closeLine"/> (1-based, already known to pair), or null when the opening
        /// line names no diagram, a kroki block's type line names none, or the source is blank.
        /// </summary>
        public static DiagramBlock? Read(Func<int, string> lineText, int openLine, int closeLine)
        {
            if (openLine < 1 || closeLine <= openLine) return null;
            var kind = DiagramKinds.FromFence(lineText(openLine));
            if (kind == null) return null;

            int first = openLine + 1;
            if (ReferenceEquals(kind, DiagramKinds.KrokiForm))
            {
                if (first >= closeLine) return null;
                kind = DiagramKinds.FromKrokiType(lineText(first));
                if (kind == null) return null;
                first++;
            }

            var parts = new List<string>();
            int length = 0;
            bool blank = true;
            for (int n = first; n < closeLine; n++)
            {
                string text = lineText(n) ?? "";
                length += text.Length + (parts.Count > 0 ? 1 : 0);
                if (length > MaxSourceLength) return new DiagramBlock(kind, openLine, closeLine, "", TooLarge: true);
                if (blank && !string.IsNullOrWhiteSpace(text)) blank = false;
                parts.Add(text);
            }
            return blank ? null : new DiagramBlock(kind, openLine, closeLine, string.Join("\n", parts), TooLarge: false);
        }

        /// <summary>
        /// The lines that hold the source of the block between the fences on
        /// <paramref name="openLine"/> and <paramref name="closeLine"/> (1-based, both inclusive): the
        /// lines strictly between the two; in the kroki form, the lines after its type line. This is
        /// what Fix with AI sends and replaces (MicaPad AI part 2, spec 2.2), so neither fence and no
        /// type line is ever in it. A block with nothing between its fences gives a range whose last
        /// line is before its first.
        ///
        /// <para>
        /// Null when the two lines are not the fences of one diagram block as the text is now: one
        /// of them is no fence, they pair with other fences, the opening line names no diagram
        /// (ordinary code), or a kroki block's type line names none. The pairing is worked out
        /// here, from the whole text, never taken from the caller.
        /// </para>
        /// </summary>
        public static (int First, int Last)? SourceLines(IReadOnlyList<string> lines, int openLine, int closeLine)
        {
            if (openLine < 1 || closeLine <= openLine || closeLine > lines.Count) return null;
            if (FenceTracker.Openings(FenceTracker.Classify(lines))[closeLine - 1] != openLine) return null;

            var kind = DiagramKinds.FromFence(lines[openLine - 1] ?? "");
            if (kind == null) return null;

            int first = openLine + 1;
            if (ReferenceEquals(kind, DiagramKinds.KrokiForm))
            {
                if (first >= closeLine || DiagramKinds.FromKrokiType(lines[first - 1]) == null) return null;
                first++;
            }
            return (first, closeLine - 1);
        }

        /// <summary>
        /// The word that names the language of the block opened on <paramref name="openLine"/>
        /// (1-based) to a reader of its source: the fence word as it is written ("mermaid", "dot"),
        /// "math" for a <c>$$</c> block, and for the kroki form the type its first inside line
        /// names. "" for a fence with no word, or a line that opens none.
        /// </summary>
        public static string WordOf(Func<int, string> lineText, int openLine)
        {
            string open = lineText(openLine) ?? "";
            if (FenceTracker.IsMathDelimiter(open)) return "math";
            string word = FenceTracker.InfoWord(open) ?? "";
            return ReferenceEquals(DiagramKinds.FromWord(word), DiagramKinds.KrokiForm) ? (lineText(openLine + 1) ?? "").Trim() : word;
        }
    }
}
