using System;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The only links MicaPad ever opens (spec 4.1): http, https and mailto. Text in a note can never
    /// launch a program — not file:, not paths, not custom protocols.
    /// </summary>
    public static class SafeLinks
    {
        /// <summary>
        /// A web or mail address in text. It never ends with sentence punctuation or a closing
        /// bracket or quote, so "see https://a.com/x." links "https://a.com/x".
        /// </summary>
        public const string Pattern = @"\b(?:https?://|mailto:)[^\s<>""'`]*[^\s<>""'`.,;:!?)\]}]";

        private static readonly Regex Rx = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The address as a link, or null when it is not an allowed, well-formed one.</summary>
        public static Uri? TryCreate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            return IsAllowed(uri) ? uri : null;
        }

        /// <summary>True for http and https with a host, and mailto with an address.</summary>
        public static bool IsAllowed(Uri uri)
        {
            if (!uri.IsAbsoluteUri) return false;
            return uri.Scheme switch
            {
                "http" or "https" => !string.IsNullOrEmpty(uri.Host),
                "mailto" => uri.AbsoluteUri.Length > "mailto:".Length,
                _ => false,
            };
        }

        /// <summary>The link covering <paramref name="column"/> (zero-based) in a line, or null.</summary>
        public static (int Start, int Length)? LinkAt(string line, int column)
        {
            foreach (Match m in Rx.Matches(line))
            {
                if (column >= m.Index && column < m.Index + m.Length && TryCreate(m.Value) != null)
                    return (m.Index, m.Length);
            }
            return null;
        }
    }
}
