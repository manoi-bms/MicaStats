using System;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// How often an answer that streams in may be drawn again (AI chat UI spec 1.5). A redraw
    /// builds the whole document from the whole text, so its cost grows with the answer: one
    /// with a table of 100 rows by 12 columns takes about 0.6 s, against a redraw timer of
    /// 100 ms. The pace keeps a redraw to one part in five of the time.
    /// </summary>
    internal static class RedrawPace
    {
        /// <summary>A redraw is followed by this many times what it cost before the next: it may take one part in five of the time.</summary>
        public const int Share = 4;

        /// <summary>The longest the cost of a redraw makes the next one wait.</summary>
        public static readonly TimeSpan Longest = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The shortest time from one redraw to the next: at least <paramref name="least"/>, at
        /// least <see cref="Share"/> times <paramref name="lastCost"/>, at most
        /// <see cref="Longest"/>, and never less than <paramref name="least"/>. A negative cost
        /// counts as zero.
        /// </summary>
        public static TimeSpan Next(TimeSpan least, TimeSpan lastCost)
        {
            TimeSpan paced = lastCost <= TimeSpan.Zero ? TimeSpan.Zero
                : lastCost.Ticks >= Longest.Ticks / Share ? Longest   // compared before it is multiplied: a huge cost cannot overflow
                : TimeSpan.FromTicks(lastCost.Ticks * Share);
            return paced > least ? paced : least;
        }
    }
}
