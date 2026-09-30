using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Which lines are fenced code: a line of three or more backticks or tildes (indented at most
    /// three spaces) opens a fence; a line of at least as many of the same character, and nothing
    /// else, closes it. An unclosed fence runs to the end.
    /// </summary>
    public static class FenceTracker
    {
        private static readonly Regex OpenRx = new(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant);

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
