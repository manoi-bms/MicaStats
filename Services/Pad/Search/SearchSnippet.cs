using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>A piece of a result's snippet; matched words are bold.</summary>
    public readonly record struct SnippetRun(string Text, bool Bold);

    /// <summary>
    /// The snippet under a result (spec 1): the passage line with the most distinct query tokens
    /// and up to two lines after it, about <see cref="MaxChars"/> characters around the first
    /// match, every match bold. A passage found only by meaning shows its first three lines.
    /// </summary>
    public static class SearchSnippet
    {
        public const int MaxChars = 200;

        public static IReadOnlyList<SnippetRun> Make(string body, string query)
        {
            var tokens = SearchTokens.Of(query).Where(t => t.Length >= 2 || SearchTokens.IsPieceScript(t[0]))
                                               .Distinct(StringComparer.Ordinal).ToList();
            var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            int best = -1, bestCount = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                int count = tokens.Count(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase));
                if (count > bestCount) { best = i; bestCount = count; }
            }

            string text;
            if (best < 0)
            {
                text = string.Join("\n", lines.Where(l => l.Trim().Length > 0).Take(3));
                return new[] { new SnippetRun(Shorten(text, 0), false) };
            }

            text = string.Join("\n", lines.Skip(best).Take(3));
            int firstMatch = tokens.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            text = Shorten(text, firstMatch);
            return Bold(text, tokens);
        }

        /// <summary>About <see cref="MaxChars"/> characters starting a little before <paramref name="focus"/>, with ellipses where cut.</summary>
        private static string Shorten(string text, int focus)
        {
            if (text.Length <= MaxChars) return text;
            int start = Math.Max(0, Math.Min(focus - 40, text.Length - MaxChars));
            string cut = text.Substring(start, Math.Min(MaxChars, text.Length - start));
            return (start > 0 ? "…" : "") + cut + (start + cut.Length < text.Length ? "…" : "");
        }

        private static IReadOnlyList<SnippetRun> Bold(string text, IReadOnlyList<string> tokens)
        {
            var marked = new bool[text.Length];
            foreach (string token in tokens)
            {
                int at = 0;
                while ((at = text.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    for (int i = at; i < at + token.Length; i++) marked[i] = true;
                    at += token.Length;
                }
            }

            var runs = new List<SnippetRun>();
            int start = 0;
            for (int i = 1; i <= text.Length; i++)
            {
                if (i == text.Length || marked[i] != marked[start])
                {
                    runs.Add(new SnippetRun(text.Substring(start, i - start), marked[start]));
                    start = i;
                }
            }
            return runs;
        }
    }
}
