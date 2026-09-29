using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Why a snapshot is being taken. Every reason but <see cref="Pause"/> is forced.</summary>
    public enum SnapshotReason
    {
        /// <summary>The user paused after at least a minute of changes.</summary>
        Pause,
        /// <summary>The tab is closing.</summary>
        Closing,
        /// <summary>MicaStats is exiting or Windows is ending the session.</summary>
        Exiting,
        /// <summary>Ctrl+S is writing the note to its file.</summary>
        SavedToFile,
        /// <summary>Reload, Restore, Replace All or a line-ending conversion is about to replace the text.</summary>
        BeforeReplace,
    }

    /// <summary>
    /// When a version is kept, and which versions are pruned. Pure: the clock is always passed in,
    /// so every rule is pinned by a test instead of by waiting.
    /// </summary>
    public static class HistoryPolicy
    {
        /// <summary>Idle time that counts as a pause.</summary>
        public static readonly TimeSpan PauseIdle = TimeSpan.FromSeconds(3);

        /// <summary>Minimum time between two pause snapshots.</summary>
        public static readonly TimeSpan PauseInterval = TimeSpan.FromSeconds(60);

        /// <summary>Notes longer than this still autosave but take no snapshots.</summary>
        public const int MaxSnapshotChars = 10 * 1024 * 1024;

        /// <summary>The shortest retention allowed, so the hourly tier always exists.</summary>
        public const int MinHistoryDays = 7;

        /// <summary>
        /// Snapshot file name format. Always formatted with the invariant culture: under th-TH a
        /// culture-sensitive format writes the Buddhist year.
        /// </summary>
        public const string StampFormat = "yyyyMMdd-HHmmss-fff";

        /// <summary>True when the user has been idle for <see cref="PauseIdle"/> and <see cref="PauseInterval"/> has passed since <paramref name="clockStart"/>.</summary>
        public static bool IsDueOnPause(DateTime now, DateTime lastEdit, DateTime clockStart) =>
            now - lastEdit >= PauseIdle && now - clockStart >= PauseInterval;

        /// <summary>SHA-256 of the text as hex. Stored in the note's meta, so it is stable across runs, unlike <see cref="string.GetHashCode()"/>.</summary>
        public static string Hash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        /// <summary>The snapshot file name (without extension) for a local time.</summary>
        public static string FormatStamp(DateTime local) =>
            local.ToString(StampFormat, CultureInfo.InvariantCulture);

        /// <summary>Parses a snapshot file name (without extension) back to its local time.</summary>
        public static bool TryParseStamp(string name, out DateTime local) =>
            DateTime.TryParseExact(name, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out local);

        /// <summary>
        /// The snapshots to delete: everything under 24 hours is kept; from 1 to 7 days the newest
        /// in each clock hour; from 7 days to <paramref name="historyDays"/> the newest in each
        /// calendar day; older, none. The newest snapshot overall is always kept, so a note that
        /// has not changed for months still has a restore point.
        /// </summary>
        public static IReadOnlyList<DateTime> SelectToPrune(IReadOnlyList<DateTime> stamps, DateTime now, int historyDays)
        {
            var prune = new List<DateTime>();
            if (stamps.Count == 0) return prune;

            TimeSpan limit = TimeSpan.FromDays(Math.Max(historyDays, MinHistoryDays));
            var hours = new HashSet<DateTime>();
            var days = new HashSet<DateTime>();

            var newestFirst = stamps.OrderByDescending(s => s).ToList();
            for (int i = 0; i < newestFirst.Count; i++)
            {
                DateTime s = newestFirst[i];
                TimeSpan age = now - s;

                // Walking newest first, the first stamp seen in a bucket is its newest: Add succeeds for it only.
                bool keep;
                if (age < TimeSpan.FromHours(24)) keep = true;
                else if (age < TimeSpan.FromDays(7)) keep = hours.Add(new DateTime(s.Year, s.Month, s.Day, s.Hour, 0, 0));
                else if (age < limit) keep = days.Add(s.Date);
                else keep = false;

                if (i == 0) keep = true;
                if (!keep) prune.Add(s);
            }
            return prune;
        }
    }
}
