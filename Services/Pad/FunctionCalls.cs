using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// How a language writes calls, for the function pass (ruling R4). After one of
    /// <see cref="Declarations"/> a name is the type being declared, not a call (Python's
    /// <c>class Foo(Base)</c>, Rust's <c>impl Fn(</c>); a word that declares nothing in the language
    /// (C#'s <c>object</c> before a method name) leaves the name a call. With <see cref="Tight"/> a
    /// call's <c>(</c> follows its name directly (PowerShell, where <c>-f ($x)</c> passes an
    /// argument), except after one of <see cref="FunctionWords"/>, which name the function they
    /// declare (<c>function Foo ($x)</c>).
    /// </summary>
    public sealed record CallSyntax(IReadOnlyList<string> Declarations, bool Tight = false, IReadOnlyList<string>? FunctionWords = null)
    {
        /// <summary>No declaration words, and calls may have spaces before their parenthesis.</summary>
        public static CallSyntax Plain { get; } = new(Array.Empty<string>());

        private static readonly CallSyntax Python = new(new[] { "class" });
        private static readonly CallSyntax CSharpOrJava = new(new[] { "class", "struct", "interface", "enum", "record" });
        private static readonly CallSyntax Kotlin = new(new[] { "class", "interface", "enum" });
        private static readonly CallSyntax Rust = new(new[] { "struct", "enum", "trait", "impl", "type", "union" });
        private static readonly CallSyntax JavaScript = new(new[] { "class", "interface", "type", "enum", "namespace" });
        private static readonly CallSyntax Go = new(new[] { "type" });
        private static readonly CallSyntax Php = new(new[] { "class", "interface", "trait", "enum" });
        private static readonly CallSyntax Cpp = new(new[] { "class", "struct", "enum", "union" });
        private static readonly CallSyntax Ruby = new(new[] { "class", "module" });
        private static readonly CallSyntax PowerShell = new(Array.Empty<string>(), Tight: true, FunctionWords: new[] { "function", "filter" });

        /// <summary>The call syntax of a language id: <see cref="Plain"/> for a language without one.</summary>
        public static CallSyntax For(string? languageId) => languageId switch
        {
            "python" => Python,
            "csharp" or "java" => CSharpOrJava,
            "kotlin" => Kotlin,
            "rust" => Rust,
            "javascript" or "typescript" => JavaScript,
            "go" => Go,
            "php" => Php,
            "cpp" => Cpp,
            "ruby" => Ruby,
            "powershell" => PowerShell,
            _ => Plain,
        };

        /// <summary>The name at <paramref name="start"/> follows a word that declares a type in this language.</summary>
        internal bool DeclaresTypeAt(string line, int start) =>
            Declarations.Count > 0 && Contains(Declarations, FunctionCalls.PreviousWord(line, start));

        /// <summary>The name at <paramref name="start"/> follows a word that declares a function in this language.</summary>
        internal bool DeclaresFunctionAt(string line, int start) =>
            FunctionWords != null && Contains(FunctionWords, FunctionCalls.PreviousWord(line, start));

        private static bool Contains(IReadOnlyList<string> words, ReadOnlySpan<char> word)
        {
            foreach (string candidate in words)
                if (word.SequenceEqual(candidate)) return true;
            return false;
        }
    }

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
        /// <c>$(...)</c> is no call), no <see cref="IsKeyword">keyword</see> unless it follows
        /// <c>.</c>, <c>::</c> or <c>-&gt;</c>, and no type the language's declaration words name
        /// (<see cref="CallSyntax"/>, which also says whether a space may come before the <c>(</c>).
        /// A word with a non-ASCII letter anywhere in it has none (no tail of it is colored), and
        /// neither has <c>1foo(</c>. A line longer than <paramref name="maxLength"/> has none: a huge
        /// line costs nothing.
        /// </summary>
        /// <param name="syntax">The language's call syntax; null is <see cref="CallSyntax.Plain"/>.</param>
        public static IReadOnlyList<(int Start, int Length)> Find(string line, int maxLength, CallSyntax? syntax = null)
        {
            if (line.Length > maxLength || line.IndexOf('(') < 0) return Array.Empty<(int, int)>();
            syntax ??= CallSyntax.Plain;

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
                if (!IsName(word) || (IsKeyword(word) && !AfterMemberAccess(line, start)) || syntax.DeclaresTypeAt(line, start)) continue;
                if (next > i && syntax.Tight && !syntax.DeclaresFunctionAt(line, start)) continue;
                (found ??= new List<(int, int)>()).Add((start, i - start));
            }
            return found ?? (IReadOnlyList<(int, int)>)Array.Empty<(int, int)>();
        }

        /// <summary>
        /// A word some language writes before <c>(</c> without calling anything: <c>if (</c>,
        /// <c>foreach (</c>, <c>catch (E) when (</c>, <c>decltype(</c>, <c>await (</c>, C#'s <c>is not (</c>...
        /// Letter case counts, so <c>Regex.Match(</c> and <c>errors.New(</c> stay calls.
        /// </summary>
        internal static bool IsKeyword(ReadOnlySpan<char> word) => word is
            "if" or "elseif" or "else" or "for" or "foreach" or "while" or "do" or "switch" or "case" or "catch"
            or "return" or "function" or "fn" or "match" or "when" or "assert" or "decltype" or "static_assert"
            or "alignof" or "alignas" or "noexcept" or "sizeof" or "typeof" or "nameof" or "async" or "await"
            or "yield" or "new" or "delete" or "throw" or "using" or "lock" or "fixed" or "checked" or "unchecked"
            or "default" or "not";

        /// <summary>The word before <paramref name="start"/> on the line, spaces and tabs skipped; empty when something else comes first.</summary>
        internal static ReadOnlySpan<char> PreviousWord(string line, int start)
        {
            int end = start;
            while (end > 0 && (line[end - 1] == ' ' || line[end - 1] == '\t')) end--;
            int from = end;
            while (from > 0 && IsWordChar(line[from - 1])) from--;
            return line.AsSpan(from, end - from);
        }

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
