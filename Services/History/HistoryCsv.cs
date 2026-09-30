using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The history file format: one CSV line per <see cref="HistoryRow"/>, after a version line and
    /// a column line.
    ///
    /// <para>
    /// Every number and date goes through <see cref="CultureInfo.InvariantCulture"/>: a Thai locale
    /// would otherwise write Buddhist-era years (2569), a German one decimal commas, and neither the
    /// offline MCP bridge nor a later build could read the file back. An empty field means
    /// "unavailable", never 0.
    /// </para>
    /// </summary>
    public static class HistoryCsv
    {
        /// <summary>First line of every file; changes only if a column ever changes meaning.</summary>
        public const string VersionLine = "# MicaStats history v1";

        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        private static readonly string[] Columns =
        {
            "utc", "seconds",
            "cpu_avg", "cpu_max", "cpu_temp_avg", "cpu_temp_max",
            "ram_avg", "ram_max",
            "gpu_avg", "gpu_max", "gpu_temp_max",
            "net_up_avg_kbps", "net_down_avg_kbps",
            "disk_free_min_pct", "disk_activity_max",
            "battery_pct", "on_ac",
            "top_cpu_name", "top_cpu_path", "top_cpu_pct",
            "top_ram_name", "top_ram_mb",
        };

        /// <summary>Indexes of the columns that hold numbers (everything but time, seconds, flag and names).</summary>
        private static readonly int[] NumberColumns = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 19, 21 };

        /// <summary>The column names, the second line of every file.</summary>
        public static string ColumnLine { get; } = string.Join(",", Columns);

        /// <summary>The row as one line, without a line break.</summary>
        public static string Format(HistoryRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            var fields = new[]
            {
                MinuteAggregator.ToUtc(row.Utc).ToString(TimeFormat, CultureInfo.InvariantCulture),
                row.Seconds.ToString(CultureInfo.InvariantCulture),
                Number(row.CpuAvg), Number(row.CpuMax), Number(row.CpuTempAvg), Number(row.CpuTempMax),
                Number(row.RamAvg), Number(row.RamMax),
                Number(row.GpuAvg), Number(row.GpuMax), Number(row.GpuTempMax),
                Number(row.NetUpAvg), Number(row.NetDownAvg),
                Number(row.DiskFreeMinPercent), Number(row.DiskActivityMax),
                Number(row.BatteryPercent), row.OnAc == null ? "" : row.OnAc.Value ? "1" : "0",
                Text(row.TopCpuName), Text(row.TopCpuPath), Number(row.TopCpuPercent),
                Text(row.TopRamName), Number(row.TopRamMb),
            };
            return string.Join(",", fields);
        }

        /// <summary>
        /// Reads one line back. False for the header lines, a blank line, and any line with the
        /// wrong number of fields or a value that does not parse, such as a line cut short.
        /// </summary>
        public static bool TryParse(string line, out HistoryRow? row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return false;

            List<string>? f = Split(line.TrimEnd('\r'));
            if (f == null || f.Count != Columns.Length) return false;

            if (!DateTime.TryParseExact(f[0], TimeFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
                return false;
            if (!int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
                return false;

            var n = new float?[Columns.Length];
            foreach (int i in NumberColumns)
                if (!TryNumber(f[i], out n[i])) return false;

            bool? onAc;
            if (f[16].Length == 0) onAc = null;
            else if (f[16] == "1") onAc = true;
            else if (f[16] == "0") onAc = false;
            else return false;

            row = new HistoryRow
            {
                Utc = utc,
                Seconds = seconds,
                CpuAvg = n[2], CpuMax = n[3], CpuTempAvg = n[4], CpuTempMax = n[5],
                RamAvg = n[6], RamMax = n[7],
                GpuAvg = n[8], GpuMax = n[9], GpuTempMax = n[10],
                NetUpAvg = n[11], NetDownAvg = n[12],
                DiskFreeMinPercent = n[13], DiskActivityMax = n[14],
                BatteryPercent = n[15], OnAc = onAc,
                TopCpuName = OrNull(f[17]), TopCpuPath = OrNull(f[18]), TopCpuPercent = n[19],
                TopRamName = OrNull(f[20]), TopRamMb = n[21],
            };
            return true;
        }

        /// <summary>
        /// Several rows as one row starting at <paramref name="bucketUtc"/> and covering
        /// <paramref name="seconds"/>: the average of the averages, the highest peak, the lowest free
        /// space, the last battery reading by time, and the top process named most often (any case;
        /// a tie goes to the earliest), with its first known path and its average share.
        /// </summary>
        public static HistoryRow Combine(IReadOnlyList<HistoryRow> rows, DateTime bucketUtc, int seconds)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var ordered = rows.OrderBy(r => r.Utc).ToList();
            string? topCpu = MostFrequent(ordered.Select(r => r.TopCpuName));
            string? topRam = MostFrequent(ordered.Select(r => r.TopRamName));
            var cpuRows = ordered.Where(r => topCpu != null && string.Equals(r.TopCpuName, topCpu, StringComparison.OrdinalIgnoreCase)).ToList();
            var ramRows = ordered.Where(r => topRam != null && string.Equals(r.TopRamName, topRam, StringComparison.OrdinalIgnoreCase)).ToList();

            return new HistoryRow
            {
                Utc = MinuteAggregator.ToUtc(bucketUtc),
                Seconds = seconds,
                CpuAvg = Average(ordered, r => r.CpuAvg),
                CpuMax = Highest(ordered, r => r.CpuMax),
                CpuTempAvg = Average(ordered, r => r.CpuTempAvg),
                CpuTempMax = Highest(ordered, r => r.CpuTempMax),
                RamAvg = Average(ordered, r => r.RamAvg),
                RamMax = Highest(ordered, r => r.RamMax),
                GpuAvg = Average(ordered, r => r.GpuAvg),
                GpuMax = Highest(ordered, r => r.GpuMax),
                GpuTempMax = Highest(ordered, r => r.GpuTempMax),
                NetUpAvg = Average(ordered, r => r.NetUpAvg),
                NetDownAvg = Average(ordered, r => r.NetDownAvg),
                DiskFreeMinPercent = Lowest(ordered, r => r.DiskFreeMinPercent),
                DiskActivityMax = Highest(ordered, r => r.DiskActivityMax),
                BatteryPercent = ordered.Select(r => r.BatteryPercent).LastOrDefault(v => v.HasValue),
                OnAc = ordered.Select(r => r.OnAc).LastOrDefault(v => v.HasValue),
                TopCpuName = topCpu,
                TopCpuPath = cpuRows.Select(r => r.TopCpuPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                TopCpuPercent = Average(cpuRows, r => r.TopCpuPercent),
                TopRamName = topRam,
                TopRamMb = Average(ramRows, r => r.TopRamMb),
            };
        }

        private static string Number(float? value) =>
            value is float v && !float.IsNaN(v) && !float.IsInfinity(v)
                ? ((double)v).ToString("0.0", CultureInfo.InvariantCulture)
                : "";

        /// <summary>A name or path as one field: line breaks become spaces, commas and quotes are quoted.</summary>
        private static string Text(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = value.Replace('\r', ' ').Replace('\n', ' ');
            return clean.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + clean.Replace("\"", "\"\"") + "\"" : clean;
        }

        private static string? OrNull(string field) => field.Length == 0 ? null : field;

        private static bool TryNumber(string field, out float? value)
        {
            value = null;
            if (field.Length == 0) return true;
            if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                float.IsNaN(parsed) || float.IsInfinity(parsed))
                return false;
            value = parsed;
            return true;
        }

        /// <summary>Splits one CSV line; null when a quoted field is never closed (a line cut short).</summary>
        private static List<string>? Split(string line)
        {
            var fields = new List<string>(Columns.Length);
            var current = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c != '"') current.Append(c);
                    else if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"' && current.Length == 0) quoted = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }

            if (quoted) return null;
            fields.Add(current.ToString());
            return fields;
        }

        private static string? MostFrequent(IEnumerable<string?> names)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstSeen = new List<string>();
            foreach (string? name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (counts.TryGetValue(name, out int count)) counts[name] = count + 1;
                else { counts[name] = 1; firstSeen.Add(name); }
            }

            string? best = null;
            int bestCount = 0;
            foreach (string name in firstSeen)
            {
                if (counts[name] > bestCount) { best = name; bestCount = counts[name]; }
            }
            return best;
        }

        private static float? Average(IEnumerable<HistoryRow> rows, Func<HistoryRow, float?> pick)
        {
            var values = rows.Select(pick).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            return values.Count == 0 ? null : (float)values.Average();
        }

        private static float? Highest(IEnumerable<HistoryRow> rows, Func<HistoryRow, float?> pick)
        {
            var values = rows.Select(pick).Where(v => v.HasValue).ToList();
            return values.Count == 0 ? null : values.Max();
        }

        private static float? Lowest(IEnumerable<HistoryRow> rows, Func<HistoryRow, float?> pick)
        {
            var values = rows.Select(pick).Where(v => v.HasValue).ToList();
            return values.Count == 0 ? null : values.Min();
        }
    }
}
