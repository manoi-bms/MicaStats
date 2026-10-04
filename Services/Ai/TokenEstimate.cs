using System;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// A rough count of the tokens in a text, for sizing what is sent to a model and for refusing
    /// what a model cannot take. Safe for Thai and CJK, where a fixed characters-per-token ratio is not.
    /// </summary>
    public static class TokenEstimate
    {
        /// <summary>
        /// A quarter of a token for each ASCII character, one for each other character (a surrogate
        /// pair, an emoji, counts once), rounded up. Null or empty is 0. Never throws; linear; allocates nothing.
        /// </summary>
        public static int Of(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            long ascii = 0, other = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 128) { ascii++; continue; }
                other++;
                // The low half of a pair belongs to the high half just counted.
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            }

            long tokens = other + (ascii + 3) / 4;
            return (int)Math.Min(tokens, int.MaxValue);
        }
    }
}
