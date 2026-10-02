using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Where a line of code names a function it calls or declares (ruling R4): an identifier
    /// followed, after optional spaces or tabs, by <c>(</c>. Whether the language calls functions
    /// that way, and whether its own colors already cover the name, is for the caller to decide.
    /// </summary>
    public static class FunctionCalls
    {
        /// <summary>
        /// The (start, length) of each such name in <paramref name="line"/>, left to right. A name is
        /// <c>[A-Za-z_$][A-Za-z0-9_$]*</c> and is never the tail of a longer word, so <c>1foo(</c> has
        /// none. A line longer than <paramref name="maxLength"/> has none: a huge line costs nothing.
        /// </summary>
        public static IReadOnlyList<(int Start, int Length)> Find(string line, int maxLength)
        {
            if (line.Length > maxLength || line.IndexOf('(') < 0) return Array.Empty<(int, int)>();

            List<(int, int)>? found = null;
            int i = 0;
            while (i < line.Length)
            {
                if (!IsWordChar(line[i]))
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < line.Length && IsWordChar(line[i])) i++;
                if (char.IsAsciiDigit(line[start])) continue;

                int next = i;
                while (next < line.Length && (line[next] == ' ' || line[next] == '\t')) next++;
                if (next < line.Length && line[next] == '(') (found ??= new List<(int, int)>()).Add((start, i - start));
            }
            return found ?? (IReadOnlyList<(int, int)>)Array.Empty<(int, int)>();
        }

        private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$';
    }
}
