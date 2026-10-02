using System;
using System.Collections.Generic;
using System.Globalization;

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
        /// a whole word of <c>[A-Za-z_$][A-Za-z0-9_$]*</c> with more than a lone <c>$</c> (PowerShell's
        /// <c>$(...)</c> is no call), and no <see cref="IsKeyword">keyword</see> unless it follows
        /// <c>.</c>, <c>::</c> or <c>-&gt;</c>. A word with a non-ASCII letter anywhere in it has none
        /// (no tail of it is colored), and neither has <c>1foo(</c>. A line longer than
        /// <paramref name="maxLength"/> has none: a huge line costs nothing.
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

                int next = i;
                while (next < line.Length && (line[next] == ' ' || line[next] == '\t')) next++;
                if (next == line.Length || line[next] != '(') continue;

                var word = line.AsSpan(start, i - start);
                if (IsName(word) && !(IsKeyword(word) && !AfterMemberAccess(line, start)))
                    (found ??= new List<(int, int)>()).Add((start, i - start));
            }
            return found ?? (IReadOnlyList<(int, int)>)Array.Empty<(int, int)>();
        }

        /// <summary>
        /// A word some language writes before <c>(</c> without calling anything: <c>if (</c>,
        /// <c>foreach (</c>, <c>catch (E) when (</c>, <c>decltype(</c>, <c>await (</c>... Letter case
        /// counts, so <c>Regex.Match(</c> and <c>errors.New(</c> stay calls.
        /// </summary>
        internal static bool IsKeyword(ReadOnlySpan<char> word) => word is
            "if" or "elseif" or "else" or "for" or "foreach" or "while" or "do" or "switch" or "case" or "catch"
            or "return" or "function" or "fn" or "match" or "when" or "assert" or "decltype" or "static_assert"
            or "alignof" or "alignas" or "noexcept" or "sizeof" or "typeof" or "nameof" or "async" or "await"
            or "yield" or "new" or "delete" or "throw" or "using" or "lock" or "fixed" or "checked" or "unchecked"
            or "default";

        /// <summary>The word at <paramref name="start"/> follows <c>.</c>, <c>::</c> or <c>-&gt;</c>: a member (<c>s.match(</c>, <c>Vec::new(</c>), never a keyword.</summary>
        internal static bool AfterMemberAccess(string line, int start) =>
            start > 0 && (line[start - 1] == '.'
                          || (start > 1 && ((line[start - 2] == ':' && line[start - 1] == ':') || (line[start - 2] == '-' && line[start - 1] == '>'))));

        /// <summary>ASCII identifier characters only, not starting with a digit, and more than <c>$</c> alone.</summary>
        private static bool IsName(ReadOnlySpan<char> word)
        {
            if (char.IsAsciiDigit(word[0])) return false;
            bool named = false;
            foreach (char c in word)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$')) return false;
                if (c != '$') named = true;
            }
            return named;
        }

        /// <summary>Part of a word: any letter or digit (with the marks that follow letters, as Thai's), <c>_</c> or <c>$</c>.</summary>
        private static bool IsWordChar(char c) => c < 0x80
            ? char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$'
            : char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }
}
