using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// <c>:name:</c> emoji codes (spec 5), from GitHub's gemoji 4.1.0 list (MIT), embedded as
    /// <c>Emoji.tsv</c> (name, tab, emoji). Names are lower case; unknown names are not emoji.
    /// </summary>
    public static class Emoji
    {
        internal const string ResourceName = "MicaPad.Emoji.tsv";
        private const int MaxNameLength = 40;
        private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

        /// <summary>How many names the table holds.</summary>
        public static int Count => Table.Value.Count;

        /// <summary>The emoji a name stands for, or null.</summary>
        public static string? GlyphOf(string name) => Table.Value.TryGetValue(name ?? "", out var glyph) ? glyph : null;

        /// <summary>
        /// Every known <c>:name:</c> in the line, outside backtick code spans: where it starts, its
        /// length with both colons, and its emoji. A code glued to a letter or digit on either side
        /// (<c>user:id:42</c>, <c>1:100:2</c>) is text. Lines over the inline limit give none.
        /// </summary>
        public static IReadOnlyList<(int Start, int Length, string Glyph)> Find(string line)
        {
            var found = new List<(int, int, string)>();
            string s = line ?? "";
            if (s.Length > MarkdownLineTokenizer.MaxInlineLength || s.IndexOf(':') < 0) return found;

            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '`')
                {
                    i = SkipCode(s, i);
                    continue;
                }
                if (s[i] != ':')
                {
                    i++;
                    continue;
                }
                int j = i + 1;
                while (j < s.Length && j - i <= MaxNameLength && IsNameChar(s[j])) j++;
                bool closed = j < s.Length && s[j] == ':';
                bool apart = (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (j + 1 >= s.Length || !char.IsLetterOrDigit(s[j + 1]));
                if (closed && j > i + 1 && apart && GlyphOf(s.Substring(i + 1, j - i - 1)) is { } glyph)
                {
                    found.Add((i, j - i + 1, glyph));
                    i = j + 1;
                }
                else
                {
                    i = closed ? j : i + 1;
                }
            }
            return found;
        }

        private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '+' or '-';

        /// <summary>Past a backtick code span (a run closed by a run of the same length), or past the run when it never closes.</summary>
        private static int SkipCode(string s, int i)
        {
            int n = 0;
            while (i + n < s.Length && s[i + n] == '`') n++;
            int j = i + n;
            while (j < s.Length)
            {
                if (s[j] != '`') { j++; continue; }
                int m = 0;
                while (j + m < s.Length && s[j + m] == '`') m++;
                if (m == n) return j + m;
                j += m;
            }
            return i + n;
        }

        private static Dictionary<string, string> Load()
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            using var stream = typeof(Emoji).Assembly.GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException("Missing resource " + ResourceName);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? row;
            while ((row = reader.ReadLine()) != null)
            {
                int tab = row.IndexOf('\t');
                if (tab > 0) table[row.Substring(0, tab)] = row.Substring(tab + 1);
            }
            return table;
        }
    }
}
