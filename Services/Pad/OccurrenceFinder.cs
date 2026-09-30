using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Every whole-word, case-sensitive occurrence of a selected word (spec 3.4). A word is
    /// <see cref="WordChars"/> (letters, digits, underscores and combining marks); the selection
    /// must be exactly one whole word.
    /// </summary>
    public static class OccurrenceFinder
    {
        /// <summary>Counting stops here.</summary>
        public const int Cap = 10_000;

        /// <summary>Longer selections are not treated as a word.</summary>
        public const int MaxWordLength = 100;

        /// <summary>True when the selection is one whole word: word characters only, with no word character just outside it.</summary>
        public static bool IsWholeWordSelection(string text, int start, int length)
        {
            if (length <= 0 || length > MaxWordLength || start < 0 || start + length > text.Length) return false;
            for (int i = start; i < start + length; i++) if (!WordChars.IsWordChar(text[i])) return false;
            bool leftOk = start == 0 || !WordChars.IsWordChar(text[start - 1]);
            bool rightOk = start + length >= text.Length || !WordChars.IsWordChar(text[start + length]);
            return leftOk && rightOk;
        }

        /// <summary>Offsets of every whole-word occurrence, stopping at <see cref="Cap"/>.</summary>
        public static (IReadOnlyList<int> Offsets, bool Capped) FindAll(string text, string word)
        {
            var offsets = new List<int>();
            int i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                int end = i + word.Length;
                bool whole = (i == 0 || !WordChars.IsWordChar(text[i - 1])) && (end >= text.Length || !WordChars.IsWordChar(text[end]));
                if (whole)
                {
                    if (offsets.Count == Cap) return (offsets, true);
                    offsets.Add(i);
                }
                i = end;
            }
            return (offsets, false);
        }

        /// <summary>"1 match", "5 matches", "10,000+ matches".</summary>
        public static string Describe(int count, bool capped)
        {
            if (capped) return Cap.ToString("N0", CultureInfo.InvariantCulture) + "+ matches";
            return count == 1 ? "1 match" : count.ToString("N0", CultureInfo.InvariantCulture) + " matches";
        }
    }
}
