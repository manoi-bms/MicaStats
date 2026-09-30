using System;
using System.Collections.Generic;
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
        /// A web or mail address in text. It never ends with sentence punctuation, so
        /// "see https://a.com/x." links "https://a.com/x". Parentheses belong to it only in balanced
        /// pairs: "https://en.wikipedia.org/wiki/Mercury_(planet)" keeps its ")", "(https://a.com/x)"
        /// does not. It never holds a brace or square bracket, because folds start there and a link
        /// that swallows a collapsed fold's start breaks AvalonEdit's rendering. Only an ASCII letter
        /// or digit right before the scheme stops a match, so Thai text may run into the address.
        /// </summary>
        public const string Pattern =
            @"(?<![A-Za-z0-9])(?:https?://|mailto:)" +
            @"(?:[^\s<>""'`()\[\]{}]|\([^\s<>""'`()\[\]{}]*\))*" +
            @"(?:[^\s<>""'`.,;:!?()\[\]{}]|\([^\s<>""'`()\[\]{}]*\))";

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
            foreach (var link in LinksIn(line))
            {
                if (column >= link.Start && column < link.Start + link.Length) return link;
            }
            return null;
        }

        /// <summary>Every allowed link in a line, in order: what the editor underlines.</summary>
        public static IEnumerable<(int Start, int Length)> LinksIn(string line)
        {
            foreach (Match m in Rx.Matches(line))
            {
                if (TryCreate(m.Value) != null) yield return (m.Index, m.Length);
            }
        }
    }
}
