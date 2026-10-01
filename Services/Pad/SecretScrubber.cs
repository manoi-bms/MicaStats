using System;
using System.Collections.Generic;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Finds and replaces every exact copy of a secret, so storing it leaves no copy behind in a
    /// note or its versions. A copy that overlaps a reference already in the text is not a copy of
    /// the secret but part of that reference: it is left alone, so references are never nested or broken.
    /// </summary>
    public static class SecretScrubber
    {
        /// <summary>
        /// Where <paramref name="value"/> appears in <paramref name="text"/>: exact (ordinal), without
        /// overlaps, and never overlapping a reference already in the text. In order.
        /// </summary>
        public static IReadOnlyList<int> Find(string text, string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("The value to find is empty.", nameof(value));

            var found = new List<int>();
            int at = text.IndexOf(value, StringComparison.Ordinal);
            if (at < 0) return found;

            var references = SecretTokens.Find(text);
            int next = 0;   // the first reference that does not end before the copy at "at"
            while (at >= 0)
            {
                while (next < references.Count && references[next].Offset + references[next].Length <= at) next++;
                if (next < references.Count && references[next].Offset < at + value.Length)
                {
                    at = text.IndexOf(value, at + 1, StringComparison.Ordinal);   // inside a reference: look on from the next character
                    continue;
                }

                found.Add(at);
                at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal);
            }
            return found;
        }

        /// <summary>How many times <paramref name="value"/> appears in <paramref name="text"/>, as <see cref="Find"/> counts them.</summary>
        public static int Count(string text, string value) => Find(text, value).Count;

        /// <summary>
        /// <paramref name="text"/> with every copy of <paramref name="value"/> that <see cref="Find"/>
        /// finds replaced by <paramref name="reference"/>; the same instance when there is none.
        /// </summary>
        public static string Replace(string text, string value, string reference, out int count)
        {
            var copies = Find(text, value);
            count = copies.Count;
            if (count == 0) return text;

            var result = new StringBuilder(text.Length + count * (reference.Length - value.Length));
            int start = 0;
            foreach (int at in copies)
            {
                result.Append(text, start, at - start).Append(reference);
                start = at + value.Length;
            }
            return result.Append(text, start, text.Length - start).ToString();
        }
    }
}
