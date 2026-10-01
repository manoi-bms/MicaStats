using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// A diagram block: its kind, its opening and closing fence lines (1-based) and its source,
    /// the text between them joined with "\n". A source over <see cref="DiagramBlocks.MaxSourceLength"/>
    /// characters is not read: <see cref="TooLarge"/> is set and the source is empty.
    /// </summary>
    public sealed record DiagramBlock(DiagramKind Kind, int OpenLine, int CloseLine, string Source, bool TooLarge);

    /// <summary>
    /// Finds the diagram blocks of a document (spec section 1): a closed fenced block whose info
    /// string's first word is a diagram word, with something other than whitespace inside (R4).
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
        /// fence names no diagram or the source is blank.
        /// </summary>
        public static DiagramBlock? Read(Func<int, string> lineText, int openLine, int closeLine)
        {
            if (openLine < 1 || closeLine <= openLine) return null;
            var kind = DiagramKinds.FromFence(lineText(openLine));
            if (kind == null) return null;

            var parts = new List<string>();
            int length = 0;
            bool blank = true;
            for (int n = openLine + 1; n < closeLine; n++)
            {
                string text = lineText(n) ?? "";
                length += text.Length + (parts.Count > 0 ? 1 : 0);
                if (length > MaxSourceLength) return new DiagramBlock(kind, openLine, closeLine, "", TooLarge: true);
                if (blank && !string.IsNullOrWhiteSpace(text)) blank = false;
                parts.Add(text);
            }
            return blank ? null : new DiagramBlock(kind, openLine, closeLine, string.Join("\n", parts), TooLarge: false);
        }
    }
}
