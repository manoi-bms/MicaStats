using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// Reads the times a model or MCP client passes to the tools: <c>now</c>, a relative time
    /// such as <c>-90s</c>, <c>-30m</c>, <c>-6h</c> or <c>-2d</c>, or an ISO-8601 time. A time
    /// without an offset is UTC, because every time the tools return is UTC.
    /// </summary>
    public static class TimeRange
    {
        // [0-9] rather than \d: \d also matches Thai and other Unicode digits.
        private static readonly Regex Relative = new("^-([0-9]{1,7})([smhd])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex IsoStart = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}", RegexOptions.CultureInvariant);

        /// <summary>Ten years: further back is a typo, and far enough back would overflow DateTime.</summary>
        private const double MaxBackSeconds = 3650d * 86400d;

        /// <summary>
        /// Parses <paramref name="text"/> against <paramref name="nowUtc"/>. Returns false for
        /// anything else, including dates written in a local calendar: the parse is invariant, so
        /// a Thai (Buddhist-era) system culture cannot shift a year by 543.
        /// </summary>
        public static bool TryParse(string? text, DateTime nowUtc, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            DateTime now = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

            if (string.Equals(t, "now", StringComparison.OrdinalIgnoreCase))
            {
                utc = now;
                return true;
            }

            Match m = Relative.Match(t);
            if (m.Success)
            {
                double amount = double.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                double seconds = char.ToLowerInvariant(m.Groups[2].Value[0]) switch
                {
                    's' => amount,
                    'm' => amount * 60d,
                    'h' => amount * 3600d,
                    _ => amount * 86400d,
                };
                if (seconds > MaxBackSeconds) return false;
                utc = now.AddSeconds(-seconds);
                return true;
            }

            if (!IsoStart.IsMatch(t)) return false;
            if (!DateTime.TryParse(t, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                return false;
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
    }
}
