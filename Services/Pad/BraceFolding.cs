using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A range of text that folds; <see cref="End"/> is exclusive.</summary>
    public readonly record struct FoldRange(int Start, int End);

    /// <summary>
    /// What a brace-folding language calls comments and strings, so braces inside them are skipped.
    /// <see cref="MultiLineStrings"/> are spans such as PowerShell here-strings that run across lines;
    /// <see cref="Escape"/> is the character that escapes the next one inside a quoted string.
    /// </summary>
    public sealed record BraceSyntax(
        IReadOnlyList<string> LineComments,
        IReadOnlyList<(string Open, string Close)> BlockComments,
        IReadOnlyList<char> Quotes,
        char Escape = '\\',
        IReadOnlyList<(string Open, string Close)>? MultiLineStrings = null)
    {
        private static readonly (string, string)[] None = Array.Empty<(string, string)>();

        public static BraceSyntax CLike { get; } = new(new[] { "//" }, new[] { ("/*", "*/") }, new[] { '"', '\'' });
        public static BraceSyntax JavaScript { get; } = new(new[] { "//" }, new[] { ("/*", "*/") }, new[] { '"', '\'', '`' });
        public static BraceSyntax Php { get; } = new(new[] { "//", "#" }, new[] { ("/*", "*/") }, new[] { '"', '\'' });
        public static BraceSyntax Json { get; } = new(Array.Empty<string>(), None, new[] { '"' });
        public static BraceSyntax Css { get; } = new(Array.Empty<string>(), new[] { ("/*", "*/") }, new[] { '"', '\'' });
        public static BraceSyntax PowerShell { get; } = new(new[] { "#" }, new[] { ("<#", "#>") }, new[] { '"', '\'' }, '`',
            new[] { ("@\"", "\n\"@"), ("@'", "\n'@") });

        /// <summary>The syntax of a brace-folding language (spec 2.5).</summary>
        public static BraceSyntax For(string languageId) => languageId switch
        {
            "json" => Json,
            "css" => Css,
            "powershell" => PowerShell,
            "javascript" => JavaScript,
            "php" => Php,
            _ => CLike,
        };
    }

    /// <summary>
    /// Fold ranges for every <c>{ }</c> and <c>[ ]</c> pair that spans more than one line, skipping
    /// braces in strings and comments with a small lexer. Mismatched brackets are dropped, never
    /// thrown on. Ranges come sorted by start, as AvalonEdit's FoldingManager requires.
    /// </summary>
    public static class BraceFolding
    {
        public static IReadOnlyList<FoldRange> Compute(string text, BraceSyntax syntax)
        {
            var folds = new List<FoldRange>();
            var open = new Stack<(char Brace, int Offset, int Line)>();
            int line = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n') { line++; i++; continue; }

                if (TryBlock(text, i, syntax.BlockComments, out int blockEnd))
                {
                    line += CountLines(text, i, blockEnd);
                    i = blockEnd;
                    continue;
                }
                if (syntax.MultiLineStrings != null && TryBlock(text, i, syntax.MultiLineStrings, out int hereEnd))
                {
                    line += CountLines(text, i, hereEnd);
                    i = hereEnd;
                    continue;
                }
                if (StartsWithAny(text, i, syntax.LineComments))
                {
                    int newline = text.IndexOf('\n', i);
                    i = newline < 0 ? text.Length : newline;
                    continue;
                }
                if (Contains(syntax.Quotes, c))
                {
                    int stop = SkipString(text, i, c, syntax.Escape);
                    line += CountLines(text, i, stop);
                    i = stop;
                    continue;
                }

                if (c is '{' or '[')
                {
                    open.Push((c, i, line));
                }
                else if (c is '}' or ']')
                {
                    char opener = c == '}' ? '{' : '[';
                    while (open.Count > 0 && open.Peek().Brace != opener) open.Pop();
                    if (open.Count > 0)
                    {
                        var start = open.Pop();
                        if (start.Line != line) folds.Add(new FoldRange(start.Offset, i + 1));
                    }
                }
                i++;
            }
            folds.Sort((a, b) => a.Start.CompareTo(b.Start));
            return folds;
        }

        private static bool TryBlock(string text, int i, IReadOnlyList<(string Open, string Close)> spans, out int end)
        {
            foreach (var (openMark, closeMark) in spans)
            {
                if (string.CompareOrdinal(text, i, openMark, 0, openMark.Length) != 0) continue;
                int close = text.IndexOf(closeMark, i + openMark.Length, StringComparison.Ordinal);
                end = close < 0 ? text.Length : close + closeMark.Length;
                return true;
            }
            end = i;
            return false;
        }

        private static bool StartsWithAny(string text, int i, IReadOnlyList<string> prefixes)
        {
            foreach (string prefix in prefixes)
                if (string.CompareOrdinal(text, i, prefix, 0, prefix.Length) == 0) return true;
            return false;
        }

        private static bool Contains(IReadOnlyList<char> chars, char c)
        {
            foreach (char x in chars) if (x == c) return true;
            return false;
        }

        /// <summary>
        /// The offset just past a string starting at <paramref name="i"/>. The syntax's escape character (backslash, or backtick in PowerShell) escapes; an
        /// unclosed ' or " string ends at the line's end (so one stray quote cannot swallow the file);
        /// backtick strings (JavaScript) may span lines.
        /// </summary>
        private static int SkipString(string text, int i, char quote, char escape)
        {
            int j = i + 1;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == escape) { j += 2; continue; }
                if (c == quote) return j + 1;
                if (c == '\n' && quote != '`') return j;
                j++;
            }
            return text.Length;
        }

        private static int CountLines(string text, int from, int to)
        {
            int count = 0;
            for (int k = from; k < to && k < text.Length; k++) if (text[k] == '\n') count++;
            return count;
        }
    }
}
