using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.HardwareInfo
{
    /// <summary>One label/value line in a spec group (a CPU-Z field row).</summary>
    public sealed record SpecRow(string Label, string Value);

    /// <summary>A titled box of rows (a CPU-Z group box).</summary>
    public sealed class SpecGroup
    {
        public SpecGroup(string title) => Title = title;
        public string Title { get; }
        public List<SpecRow> Rows { get; } = new();

        public SpecGroup Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Rows.Add(new SpecRow(label, value));
            return this;
        }

        /// <summary>Adds the row even when the value is unknown, shown as an honest dash.</summary>
        public SpecGroup AddAlways(string label, string value)
        {
            Rows.Add(new SpecRow(label, string.IsNullOrWhiteSpace(value) ? "—" : value));
            return this;
        }
    }

    /// <summary>One tab of the hardware window (CPU, MAINBOARD, ...).</summary>
    public sealed class HardwareTab
    {
        public HardwareTab(string name) => Name = name;
        public string Name { get; }
        public List<SpecGroup> Groups { get; } = new();
    }

    /// <summary>Everything the hardware window and the text report render.</summary>
    public sealed class HardwareSnapshot
    {
        public List<HardwareTab> Tabs { get; } = new();
        public DateTime GeneratedAt { get; init; } = DateTime.Now;
        public TimeSpan GatherDuration { get; set; }

        /// <summary>One-line identity used for the diagnostics log.</summary>
        public string Summary { get; set; } = "";
    }

    /// <summary>Display formatting shared by the window and the report.</summary>
    public static class SpecFormat
    {
        /// <summary>Binary-unit size with up to two significant decimals: "48 KB", "1.25 MB", "32 GB".</summary>
        public static string Bytes(ulong bytes)
        {
            if (bytes == 0) return "0";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[u];
        }

        /// <summary>Decimal-unit capacity the way drives are sold: 500107862016 → "500.11 GB".</summary>
        public static string DiskBytes(ulong bytes)
        {
            if (bytes == 0) return "0";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int u = 0;
            while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[u];
        }

        public static string Mhz(double mhz) =>
            mhz <= 0 ? "—" : mhz.ToString("N0", CultureInfo.InvariantCulture) + " MHz";

        public static string MtPerSec(int mt) =>
            mt <= 0 ? "—" : mt.ToString("N0", CultureInfo.InvariantCulture) + " MT/s";

        /// <summary>"6 × 1.25 MB" for uniform caches, with the aggregate when count > 1.</summary>
        public static string CacheLine(CacheGroup c) => c.Count > 1
            ? $"{c.Count} × {Bytes((ulong)c.SizeBytes)}  ({Bytes((ulong)(c.SizeBytes * c.Count))} total)"
            : Bytes((ulong)c.SizeBytes);
    }

    /// <summary>
    /// Renders a snapshot as the plain-text report saved for investigation — the same idea as
    /// CPU-Z's "Tools → Save report as .TXT". Pure over the snapshot so tests can pin the shape.
    /// </summary>
    public static class HardwareReportWriter
    {
        public static string Write(HardwareSnapshot snapshot, string appVersion)
        {
            var sb = new StringBuilder(8 * 1024);
            sb.AppendLine("==============================================================");
            sb.AppendLine(" MicaStats Hardware Report");
            sb.AppendLine(" Generated : " + snapshot.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(" Version   : MicaStats " + appVersion);
            sb.AppendLine("==============================================================");

            foreach (var tab in snapshot.Tabs)
            {
                foreach (var group in tab.Groups)
                {
                    if (group.Rows.Count == 0) continue;
                    sb.AppendLine();
                    sb.AppendLine("[" + tab.Name + " — " + group.Title + "]");
                    foreach (var row in group.Rows)
                    {
                        string label = row.Label.Length >= 26 ? row.Label + " " : row.Label + " " + new string('.', 26 - row.Label.Length);
                        sb.AppendLine("  " + label + ": " + row.Value);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("Gathered in " + snapshot.GatherDuration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.");
            return sb.ToString();
        }
    }
}
