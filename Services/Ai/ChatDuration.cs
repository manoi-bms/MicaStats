using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// How long a request took, as MicaPad's AI pane ("Finished in 4 s") and the Ask window's
    /// footer say it: whole seconds, and from a minute on, minutes and seconds.
    /// </summary>
    public static class ChatDuration
    {
        /// <summary>
        /// "4 s" (never less than "1 s"), "59 s", "1 min 5 s", "2 min 0 s"; an hour is "60 min 0 s".
        /// Rounded to the nearest second, so what rounds up to a minute is said as one. The digits
        /// are 0 to 9 with no separators, whatever the culture of the PC.
        /// </summary>
        public static string Text(TimeSpan elapsed)
        {
            // Never less than a second: a reply that came at once, or a clock that was set back in between.
            long seconds = Math.Max(1, (long)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
            if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + " s";
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " min "
                   + (seconds % 60).ToString(CultureInfo.InvariantCulture) + " s";
        }
    }
}
