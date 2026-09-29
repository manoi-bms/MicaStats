using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The find bar's three toggles.</summary>
    public sealed record FindOptions(bool MatchCase, bool WholeWord, bool UseRegex);

    /// <summary>One match: where it starts and how long it is.</summary>
    public readonly record struct FindMatch(int Offset, int Length);

    /// <summary>
    /// Find and replace over a string. Every mode is compiled to one regex, so literal, whole-word
    /// and regex search share a single code path. Every regex carries a timeout: a pathological
    /// pattern stops with a message instead of freezing the window. FindAll and TryReplaceAll
    /// report timeouts to the caller; the single-match helpers (FindNext, FindPrevious, ExpandAt)
    /// treat a timed-out search as no match.
    /// </summary>
    public static class FindReplaceEngine
    {
        /// <summary>Longest any single search may run.</summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

        /// <summary>Most matches highlighted at once; beyond it the count shows "10,000+".</summary>
        public const int MaxHighlights = 10_000;

        /// <summary>Message shown in the find bar when a search times out.</summary>
        public const string TimedOutMessage = "The search took too long and was stopped.";

        /// <summary>
        /// Compiles the pattern. False with a null error for an empty pattern (nothing to search
        /// for); false with a message for an invalid regex.
        /// </summary>
        public static bool TryBuild(string pattern, FindOptions options, out Regex? regex, out string? error)
        {
            regex = null;
            error = null;
            if (string.IsNullOrEmpty(pattern)) return false;

            var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;

            try
            {
                // In regex mode, validate the raw pattern first: wrapping can make an unbalanced ")" or
                // a trailing backslash valid, so we compile the user's pattern to report its error, not the wrapper's.
                if (options.UseRegex)
                    _ = new Regex(pattern, flags, MatchTimeout);

                string body = options.UseRegex ? pattern : Regex.Escape(pattern);

                // Look-arounds instead of \b, so whole word also works for terms that start or end with punctuation.
                if (options.WholeWord) body = @"(?<!\w)(?:" + body + @")(?!\w)";

                regex = new Regex(body, flags, MatchTimeout);
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Every non-empty match, up to <paramref name="max"/>, in order, delegating to the overload that reports timeouts.</summary>
        public static IReadOnlyList<FindMatch> FindAll(string text, Regex regex, int max = MaxHighlights) =>
            FindAll(text, regex, out _, max);

        /// <summary>
        /// Every non-empty match, up to <paramref name="max"/>, in order. Sets <paramref name="timedOut"/> to true
        /// if the search ran too long and was stopped, false otherwise.
        /// </summary>
        public static IReadOnlyList<FindMatch> FindAll(string text, Regex regex, out bool timedOut, int max = MaxHighlights)
        {
            var matches = new List<FindMatch>();
            timedOut = false;
            try
            {
                for (Match m = regex.Match(text); m.Success && matches.Count < max; m = m.NextMatch())
                    if (m.Length > 0) matches.Add(new FindMatch(m.Index, m.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
                // Return what was found before the timeout.
            }
            return matches;
        }

        /// <summary>
        /// The first non-empty match at or after <paramref name="from"/>, wrapping to the start when
        /// asked. Empty matches are skipped, so pressing F3 on a pattern like <c>x*</c> cannot stall.
        /// </summary>
        public static FindMatch? FindNext(string text, Regex regex, int from, bool wrap = true)
        {
            from = Math.Clamp(from, 0, text.Length);
            var match = FirstNonEmpty(text, regex, from);
            if (match == null && wrap && from > 0) match = FirstNonEmpty(text, regex, 0);
            return match;
        }

        /// <summary>The last non-empty match starting before <paramref name="before"/>, wrapping to the end when asked.</summary>
        public static FindMatch? FindPrevious(string text, Regex regex, int before, bool wrap = true)
        {
            before = Math.Clamp(before, 0, text.Length);
            FindMatch? last = null, lastOverall = null;
            try
            {
                for (Match m = regex.Match(text); m.Success; m = m.NextMatch())
                {
                    if (m.Length == 0) continue;
                    var found = new FindMatch(m.Index, m.Length);
                    if (m.Index < before) last = found;
                    lastOverall = found;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return last ?? (wrap ? lastOverall : null);
        }

        /// <summary>The index of the match starting exactly at <paramref name="offset"/>, or -1.</summary>
        public static int IndexOf(IReadOnlyList<FindMatch> matches, int offset)
        {
            int i = FirstAtOrAfter(matches, offset);
            return i < matches.Count && matches[i].Offset == offset ? i : -1;
        }

        /// <summary>The index of the first match starting at or after <paramref name="offset"/> (binary search).</summary>
        public static int FirstAtOrAfter(IReadOnlyList<FindMatch> matches, int offset)
        {
            int lo = 0, hi = matches.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (matches[mid].Offset < offset) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// The replacement for <paramref name="match"/>, or null when it is not a real match any
        /// more (the text changed, or the user moved the selection).
        /// </summary>
        public static string? ExpandAt(string text, Regex regex, FindMatch match, string replacement, bool useRegex)
        {
            try
            {
                Match m = regex.Match(text, match.Offset);
                if (!m.Success || m.Index != match.Offset || m.Length != match.Length) return null;
                return useRegex ? m.Result(replacement) : replacement;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Replaces every match, including zero-length matches (unlike FindAll, which skips them).
        /// In regex mode <c>$1</c> and friends expand; in literal mode the replacement is inserted exactly as typed.
        /// Returns false and sets <paramref name="error"/> to <see cref="TimedOutMessage"/> if the search times out.
        /// </summary>
        public static bool TryReplaceAll(string text, Regex regex, string replacement, bool useRegex,
                                         out string result, out int count, out string? error)
        {
            int replaced = 0;
            try
            {
                result = regex.Replace(text, m =>
                {
                    replaced++;
                    return useRegex ? m.Result(replacement) : replacement;
                });
                count = replaced;
                error = null;
                return true;
            }
            catch (RegexMatchTimeoutException)
            {
                result = text;
                count = 0;
                error = TimedOutMessage;
                return false;
            }
        }

        private static FindMatch? FirstNonEmpty(string text, Regex regex, int start)
        {
            try
            {
                for (Match m = regex.Match(text, start); m.Success; m = m.NextMatch())
                    if (m.Length > 0) return new FindMatch(m.Index, m.Length);
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return null;
        }
    }
}
