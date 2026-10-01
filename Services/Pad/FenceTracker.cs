using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Which lines are fenced code: a line of three or more backticks or tildes (indented at most
    /// three spaces) opens a fence; a line of at least as many of the same character, and nothing
    /// else, closes it. An unclosed fence runs to the end. A line that is only <c>$$</c> opens a
    /// math block in the same way.
    /// </summary>
    public static class FenceTracker
    {
        private static readonly Regex OpenRx = new(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex MathRx = new(@"^ {0,3}\$\$[ \t]*$", RegexOptions.CultureInvariant);

        /// <summary>One entry per line: delimiter, inside, or neither.</summary>
        public static MdFence[] Classify(IReadOnlyList<string> lines)
        {
            var kinds = new MdFence[lines.Count];
            char fenceChar = '\0';
            int fenceLength = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? "";
                if (fenceLength == 0)
                {
                    if (!MayBeDelimiter(line)) continue;   // most lines: no regex run at all
                    if (MathRx.IsMatch(line))
                    {
                        // A line of only $$ opens a math block (spec 2); the next such line closes it.
                        fenceChar = '$';
                        fenceLength = 2;
                        kinds[i] = MdFence.Delimiter;
                        continue;
                    }
                    Match m = OpenRx.Match(line);
                    // A backtick fence's info string cannot contain a backtick (that is inline code).
                    if (m.Success && !(m.Groups[1].Value[0] == '`' && m.Groups[2].Value.Contains('`')))
                    {
                        fenceChar = m.Groups[1].Value[0];
                        fenceLength = m.Groups[1].Length;
                        kinds[i] = MdFence.Delimiter;
                    }
                }
                else if (IsClose(line, fenceChar, fenceLength))
                {
                    kinds[i] = MdFence.Delimiter;
                    fenceLength = 0;
                }
                else
                {
                    kinds[i] = MdFence.Inside;
                }
            }
            return kinds;
        }

        /// <summary>
        /// For each line, the 1-based number of the line that opened the fence it closes, or 0 when
        /// it closes none. Delimiters alternate (open, close, open...), as <see cref="Classify"/> made
        /// them; a last opening fence that never closes gets no entry.
        /// </summary>
        public static int[] Openings(IReadOnlyList<MdFence> kinds)
        {
            var openings = new int[kinds.Count];
            int open = 0;
            for (int i = 0; i < kinds.Count; i++)
            {
                if (kinds[i] != MdFence.Delimiter) continue;
                if (open == 0)
                {
                    open = i + 1;
                }
                else
                {
                    openings[i] = open;
                    open = 0;
                }
            }
            return openings;
        }

        /// <summary>
        /// The cheap test before the full match: only a line whose first non-space character is a
        /// backtick, tilde or dollar can be a fence delimiter. The start of a line is enough to ask it.
        /// </summary>
        public static bool MayBeDelimiter(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i < line.Length && line[i] is '`' or '~' or '$';
        }

        /// <summary>
        /// The first word of an opening fence line's info string (<c>```cs title</c> gives "cs"), or
        /// null when the line is no backtick or tilde fence (at most three spaces before it) or has
        /// no info string.
        /// </summary>
        public static string? InfoWord(string openingLine)
        {
            string line = openingLine ?? "";
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            if (i >= line.Length || line[i] is not ('`' or '~')) return null;
            char fence = line[i];
            while (i < line.Length && line[i] == fence) i++;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            return i > start ? line.Substring(start, i - start) : null;
        }

        private static bool IsClose(string line, char c, int length)
        {
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            int run = 0;
            while (i + run < line.Length && line[i + run] == c) run++;
            if (run < length) return false;
            for (int k = i + run; k < line.Length; k++)
                if (!char.IsWhiteSpace(line[k])) return false;
            return true;
        }
    }
}
