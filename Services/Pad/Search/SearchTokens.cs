using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Keyword tokens (spec 3.2), lower-cased with the invariant culture. A run of letters and
    /// digits joined by <c>. - _ : /</c> is one token and, when joined, each part is a token too, so
    /// <c>ERR-1042</c> and <c>10.0.0.1</c> match whole and by part. Thai and CJK have no spaces
    /// between words, so their runs become overlapping two-character pieces, found wherever the
    /// letters appear without a dictionary.
    /// </summary>
    public static class SearchTokens
    {
        public const int MaxTokenChars = 128;

        /// <summary>Thai, kana, CJK ideographs and Hangul: indexed as two-character pieces.</summary>
        public static bool IsPieceScript(char c) =>
            (c >= '฀' && c <= '๿') || (c >= '぀' && c <= 'ヿ') ||
            (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿') ||
            (c >= '가' && c <= '힯');

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) && !IsPieceScript(c);

        private static bool IsJoiner(char c) => c is '.' or '-' or '_' or ':' or '/';

        /// <summary>The tokens of <paramref name="text"/> in order, repeats kept.</summary>
        public static List<string> Of(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (IsPieceScript(c))
                {
                    int start = i;
                    while (i < text.Length && IsPieceScript(text[i])) i++;
                    if (i - start == 1) tokens.Add(text.Substring(start, 1));
                    else for (int k = start; k < i - 1; k++) tokens.Add(text.Substring(k, 2));
                    continue;
                }
                if (IsWordChar(c))
                {
                    int start = i, partStart = i;
                    var parts = new List<string>();
                    while (i < text.Length)
                    {
                        if (IsWordChar(text[i])) { i++; continue; }
                        if (IsJoiner(text[i]) && i + 1 < text.Length && IsWordChar(text[i + 1]))
                        {
                            parts.Add(text.Substring(partStart, i - partStart));
                            i++;
                            partStart = i;
                            continue;
                        }
                        break;
                    }
                    parts.Add(text.Substring(partStart, i - partStart));
                    Add(tokens, text.Substring(start, i - start));
                    if (parts.Count > 1) foreach (string part in parts) Add(tokens, part);
                    continue;
                }
                i++;
            }
            return tokens;
        }

        private static void Add(List<string> tokens, string token)
        {
            if (token.Length <= MaxTokenChars) tokens.Add(token.ToLowerInvariant());
        }

        /// <summary>
        /// The word still being typed at the end of the query (two characters or more, letters and
        /// digits, no trailing space), lower-cased; null otherwise.
        /// </summary>
        public static string? LastWordPrefix(string query)
        {
            int end = query.Length;
            int start = end;
            while (start > 0 && IsWordChar(query[start - 1])) start--;
            if (end - start < 2) return null;
            return query.Substring(start, end - start).ToLowerInvariant();
        }
    }
}
