using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One reference in a note's text: where it is and which credential it names.</summary>
    public readonly record struct SecretReference(int Offset, int Length, string Id);

    /// <summary>
    /// The text that stands in a note for a stored credential, <c>{{secret:K7Q2M9XD}}</c>: eight
    /// random characters of Crockford base32, which has no I, L, O or U, so an id read aloud or
    /// typed again is not misread. Upper case only.
    /// </summary>
    public static class SecretTokens
    {
        /// <summary>Crockford base32.</summary>
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Characters in an id.</summary>
        public const int IdLength = 8;

        /// <summary>One character of an id, as a regular expression class: the alphabet, written once for every pattern.</summary>
        public const string IdClass = "[0-9A-HJKMNP-TV-Z]";

        /// <summary>A reference; group 1 is the id.</summary>
        public const string Pattern = @"\{\{secret:(" + IdClass + @"{8})\}\}";

        private static readonly Regex Reference = new(Pattern, RegexOptions.CultureInvariant);

        /// <summary>The reference text for <paramref name="id"/>.</summary>
        public static string Format(string id) => "{{secret:" + id + "}}";

        /// <summary>Whether <paramref name="id"/> is eight characters of the alphabet.</summary>
        public static bool IsId(string? id)
        {
            if (id == null || id.Length != IdLength) return false;
            foreach (char c in id)
                if (Alphabet.IndexOf(c) < 0) return false;
            return true;
        }

        /// <summary>Every reference in <paramref name="text"/>, in order.</summary>
        public static IReadOnlyList<SecretReference> Find(string text)
        {
            var found = new List<SecretReference>();
            foreach (Match match in Reference.Matches(text))
                found.Add(new SecretReference(match.Index, match.Length, match.Groups[1].Value));
            return found;
        }

        /// <summary>Whether <paramref name="text"/> holds a reference.</summary>
        public static bool Contains(string text) => Reference.IsMatch(text);

        /// <summary>A random id that <paramref name="taken"/> does not claim.</summary>
        public static string NewId(Func<string, bool> taken)
        {
            Span<char> chars = stackalloc char[IdLength];
            while (true)
            {
                for (int i = 0; i < IdLength; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                string id = new(chars);
                if (!taken(id)) return id;
            }
        }
    }
}
