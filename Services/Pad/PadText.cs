using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>MicaPad's status-bar and history-list text. Invariant culture throughout.</summary>
    public static class PadText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>"Saved 2s ago", "Saved 3m ago", "Saved 2h ago".</summary>
        public static string SavedAgo(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed.TotalSeconds < 60) return "Saved " + ((int)elapsed.TotalSeconds).ToString(Inv) + "s ago";
            if (elapsed.TotalMinutes < 60) return "Saved " + ((int)elapsed.TotalMinutes).ToString(Inv) + "m ago";
            return "Saved " + ((int)elapsed.TotalHours).ToString(Inv) + "h ago";
        }

        /// <summary>The caret position as "Ln 12, Col 5".</summary>
        public static string CaretPosition(int line, int column) =>
            "Ln " + line.ToString(Inv) + ", Col " + column.ToString(Inv);

        /// <summary>A character count with thousands separators, such as "1,284 chars".</summary>
        public static string CharCount(int count) => count.ToString("N0", Inv) + " chars";

        /// <summary>Bytes as B, KB or MB with one decimal.</summary>
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(Inv) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", Inv) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", Inv) + " MB";
        }

        /// <summary>A size change with its sign, for the history list.</summary>
        public static string SizeDelta(long delta) => (delta < 0 ? "-" : "+") + Size(Math.Abs(delta));
    }
}
