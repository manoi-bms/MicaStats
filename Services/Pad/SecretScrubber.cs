using System;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Finds and replaces every exact copy of a secret, so storing it leaves no copy behind in a note or its versions.</summary>
    public static class SecretScrubber
    {
        /// <summary>How many times <paramref name="value"/> appears in <paramref name="text"/>: exact (ordinal), without overlaps.</summary>
        public static int Count(string text, string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("The value to find is empty.", nameof(value));

            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        /// <summary>
        /// <paramref name="text"/> with every exact copy of <paramref name="value"/> replaced by
        /// <paramref name="reference"/>; the same instance when there is none.
        /// </summary>
        public static string Replace(string text, string value, string reference, out int count)
        {
            count = Count(text, value);
            if (count == 0) return text;

            var result = new StringBuilder(text.Length + count * (reference.Length - value.Length));
            int start = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, start, StringComparison.Ordinal))
            {
                result.Append(text, start, at - start).Append(reference);
                start = at + value.Length;
            }
            return result.Append(text, start, text.Length - start).ToString();
        }
    }
}
