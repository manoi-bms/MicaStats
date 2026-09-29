using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One line of the history list.</summary>
    public sealed record HistoryRow(SnapshotInfo Snapshot, string Group, string Time, string Size, string Delta);

    /// <summary>One line of the closed-notes list.</summary>
    public sealed record ClosedNoteRow(string Id, string Title, string Detail)
    {
        /// <summary>Case-insensitive search over the title and the detail line; an empty query matches.</summary>
        public bool Matches(string query) =>
            query.Length == 0 ||
            Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The text of MicaPad's history and closed-notes lists. Dates are fixed Gregorian
    /// ("12 Sep 2026") whatever the culture, matching the snapshot file names.
    /// </summary>
    public static class HistoryRows
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Rows for versions listed newest first; each size change is against the next-older version.</summary>
        public static IReadOnlyList<HistoryRow> Build(IReadOnlyList<SnapshotInfo> newestFirst, DateTime localNow)
        {
            var rows = new List<HistoryRow>(newestFirst.Count);
            for (int i = 0; i < newestFirst.Count; i++)
            {
                var snapshot = newestFirst[i];
                string delta = i + 1 < newestFirst.Count ? PadText.SizeDelta(snapshot.Size - newestFirst[i + 1].Size) : "";
                rows.Add(new HistoryRow(
                    snapshot,
                    GroupOf(snapshot.Stamp, localNow),
                    snapshot.Stamp.ToString("HH:mm", Inv),
                    PadText.Size(snapshot.Size),
                    delta));
            }
            return rows;
        }

        /// <summary>"Today", "Yesterday", or the date.</summary>
        public static string GroupOf(DateTime stamp, DateTime localNow)
        {
            if (stamp.Date == localNow.Date) return "Today";
            if (stamp.Date == localNow.Date.AddDays(-1)) return "Yesterday";
            return stamp.ToString("d MMM yyyy", Inv);
        }

        /// <summary>"Today 14:30", "Yesterday 09:05", "12 Sep 2026 08:00".</summary>
        public static string When(DateTime stamp, DateTime localNow) =>
            GroupOf(stamp, localNow) + " " + stamp.ToString("HH:mm", Inv);

        /// <summary>Rows for closed notes, in the order given.</summary>
        public static IReadOnlyList<ClosedNoteRow> Closed(IReadOnlyList<NoteMeta> newestFirst, DateTime localNow)
        {
            var rows = new List<ClosedNoteRow>(newestFirst.Count);
            foreach (var meta in newestFirst)
            {
                string when = meta.ClosedAtUtc is DateTime closed ? "Closed " + When(closed.ToLocalTime(), localNow) : "Closed";
                string detail = meta.SourcePath == null ? when : when + " · " + meta.SourcePath;
                rows.Add(new ClosedNoteRow(meta.Id, meta.Title, detail));
            }
            return rows;
        }
    }
}
