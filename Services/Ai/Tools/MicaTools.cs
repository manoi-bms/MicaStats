using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.History;
using Kil0bitSystemMonitor.Services.Sensors;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The read-only data tools, shared by the in-app assistant and the MCP servers.
    ///
    /// <para>
    /// Every tool returns a JSON object and never throws for a data problem: an exception
    /// becomes <c>{"error": ...}</c> (<see cref="ToolJson.Error"/>), a reading that was not
    /// measured becomes <see cref="ToolJson.Unavailable"/> rather than 0, numbers are rounded to
    /// one decimal, times are UTC ISO-8601 written with the invariant culture, and the whole
    /// result passes through <see cref="Redactor.RedactJson"/> before it leaves. Only
    /// cancellation escapes, so a Stop in the Ask window really stops.
    /// </para>
    /// </summary>
    public sealed partial class MicaTools
    {
        /// <summary>Most points <c>get_history</c> returns; longer ranges are averaged down to this.</summary>
        public const int MaxHistoryPoints = 500;

        private const int MaxTopProcesses = 15;

        private const string NoHistory =
            "No history in this range. MicaStats records history only while Keep 7 days of history is on (Settings > AI).";

        /// <summary>The metric names <c>get_history</c> accepts, in the order its description lists them.</summary>
        internal static IReadOnlyList<string> HistoryMetrics { get; } = new[]
        {
            "cpu", "cpuTemp", "ram", "gpu", "gpuTemp", "netUp", "netDown",
            "diskFree", "diskActivity", "battery", "topProcesses", "all",
        };

        private readonly IMicaData _data;
        private readonly Redactor _redactor;

        /// <summary>Creates the tools over one data source; every result is redacted with <paramref name="redactor"/>.</summary>
        public MicaTools(IMicaData data, Redactor redactor)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        }

        /// <summary>
        /// <c>get_live_status</c>: the latest snapshot (CPU, per-core summary, temperatures, memory,
        /// GPU, disks, network, battery, sensors) plus min/avg/max over the in-memory window.
        /// </summary>
        public Task<JsonNode> GetLiveStatusAsync(CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildLiveStatus()), ct);

        /// <summary>
        /// <c>get_history</c>: recorded rows of <paramref name="metric"/> between two times
        /// (<see cref="TimeRange"/> forms), averaged down to at most <paramref name="maxPoints"/>
        /// (clamped to 1..<see cref="MaxHistoryPoints"/>).
        /// </summary>
        public Task<JsonNode> GetHistoryAsync(string metric, string from, string to, int maxPoints = 200, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildHistory(metric, from, to, maxPoints)), ct);

        /// <summary>
        /// <c>get_top_processes</c>: the busiest processes by <c>cpu</c>, <c>memory</c> or
        /// <c>disk</c>; <paramref name="count"/> is clamped to 1..15.
        /// </summary>
        public Task<JsonNode> GetTopProcessesAsync(string by = "cpu", int count = 10, CancellationToken ct = default) =>
            RunAsync(() => BuildTopProcessesAsync(by, count, ct), ct);

        /// <summary>
        /// Runs a read-only tool by name with JSON arguments, for the tool pipe and MCP. An
        /// unknown name (including <c>suggest_action</c>, which is in-app only) is an error
        /// result, never an exception.
        /// </summary>
        public async Task<JsonNode> InvokeAsync(string tool, JsonObject? args, CancellationToken ct = default)
        {
            switch (tool)
            {
                case ToolNames.GetLiveStatus:
                    return await GetLiveStatusAsync(ct).ConfigureAwait(false);
                case ToolNames.GetHistory:
                    return await GetHistoryAsync(ArgText(args, "metric") ?? "", ArgText(args, "from") ?? "-1h",
                        ArgText(args, "to") ?? "now", ArgInt(args, "maxPoints", 200), ct).ConfigureAwait(false);
                case ToolNames.GetTopProcesses:
                    return await GetTopProcessesAsync(ArgText(args, "by") ?? "cpu", ArgInt(args, "count", 10), ct).ConfigureAwait(false);
                case ToolNames.ListSlowdownReports:
                    return await ListSlowdownReportsAsync(ArgInt(args, "limit", 10), ct).ConfigureAwait(false);
                case ToolNames.GetSlowdownReport:
                    return await GetSlowdownReportAsync(ArgText(args, "id") ?? "", ct).ConfigureAwait(false);
                case ToolNames.ListAlerts:
                    return await ListAlertsAsync(ArgText(args, "since"), ct).ConfigureAwait(false);
                case ToolNames.GetHardware:
                    return await GetHardwareAsync(ct).ConfigureAwait(false);
                case ToolNames.GetBattery:
                    return await GetBatteryAsync(ct).ConfigureAwait(false);
                case ToolNames.GetBootSummary:
                    return await GetBootSummaryAsync(ct).ConfigureAwait(false);
                default:
                    return _redactor.RedactJson(ToolJson.Error("Unknown tool '" + tool + "'. MicaStats offers: " +
                        string.Join(", ", ToolNames.ReadOnly) + "."))!;
            }
        }

        // ----- Shared plumbing ---------------------------------------------------------------

        /// <summary>Runs one tool body: exceptions become error results, and the result is redacted.</summary>
        private async Task<JsonNode> RunAsync(Func<Task<JsonNode>> body, CancellationToken ct)
        {
            JsonNode result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DataUnavailableException ex)
            {
                result = ToolJson.Error(ex.Message);
            }
            catch (Exception ex)
            {
                result = ToolJson.Error("MicaStats could not read this: " + ex.Message);
            }
            return _redactor.RedactJson(result) ?? ToolJson.Error("The tool returned nothing.");
        }

        /// <summary>A string argument; numbers and objects come back as their JSON text.</summary>
        private static string? ArgText(JsonObject? args, string name)
        {
            if (args == null || !args.TryGetPropertyValue(name, out JsonNode? node) || node == null) return null;
            if (node is JsonValue value && value.TryGetValue(out string? text)) return text;
            return node.ToJsonString();
        }

        /// <summary>An integer argument, also accepted as a whole-number double or a numeric string.</summary>
        private static int ArgInt(JsonObject? args, string name, int fallback)
        {
            if (args == null || !args.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonValue value) return fallback;
            if (value.TryGetValue(out int i)) return i;
            if (value.TryGetValue(out double d) && double.IsFinite(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
            if (value.TryGetValue(out string? s) &&
                int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;
            return fallback;
        }

        /// <summary>A number rounded to one decimal.</summary>
        private static JsonNode Num(double value) =>
            JsonValue.Create(Math.Round(value, 1, MidpointRounding.AwayFromZero));

        /// <summary>A number when it was measured, otherwise <see cref="ToolJson.Unavailable"/> with the reason.</summary>
        private static JsonNode Reading(bool measured, double value, string reason) =>
            measured && double.IsFinite(value) ? Num(value) : ToolJson.Unavailable(reason);

        /// <summary>Adds a history value only when it was measured: a missing field means unavailable, never 0.</summary>
        private static void Put(JsonObject into, string name, float? value)
        {
            if (value is float v && float.IsFinite(v)) into[name] = Num(v);
        }

        private static double Gb(ulong bytes) => bytes / 1073741824d;

        /// <summary>UTC as <c>yyyy-MM-ddTHH:mm:ssZ</c>, invariant (a Thai culture would write year 2569).</summary>
        internal static string Iso(DateTime utc) =>
            AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>The same instant in this PC's local time, for answers the user reads.</summary>
        internal static string LocalText(DateTime utc) =>
            AsUtc(utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>This PC's UTC offset at <paramref name="utc"/>, e.g. <c>+07:00</c>, so the model can convert times.</summary>
        internal static string UtcOffset(DateTime utc)
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(AsUtc(utc));
            return (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>Treats an unspecified kind as UTC: history rows and the data clock are UTC.</summary>
        private static DateTime AsUtc(DateTime t) => t.Kind switch
        {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };

        /// <summary>Treats an unspecified kind as local: file times, alert times and event-log times are local.</summary>
        private static DateTime FromLocal(DateTime t) =>
            t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime();

        // ----- get_live_status ---------------------------------------------------------------

        private JsonObject BuildLiveStatus()
        {
            SystemMetrics? m = _data.Latest();
            if (m == null) return ToolJson.Unavailable("No measurement has arrived yet. Try again in a few seconds.");

            const string Unknown = "Windows did not report it.";
            DateTime now = _data.UtcNow;

            var cpu = new JsonObject
            {
                ["usagePercent"] = Num(m.CpuUsage),
                ["kernelPercent"] = Reading(m.CpuSystem >= 0, m.CpuSystem, "The kernel-time counter could not be read."),
                ["cores"] = CoreSummary(m.CoreUsage),
                ["clockGhz"] = Reading(m.CpuFrequencyGhz > 0, m.CpuFrequencyGhz, Unknown),
                ["temperatureC"] = Reading(m.CpuTemperature > 0, m.CpuTemperature,
                    "No CPU temperature source is running (Core Temp, HWiNFO or a similar tool publishes it)."),
            };

            var memory = new JsonObject
            {
                ["usedPercent"] = Num(m.RamPercent),
                ["usedGb"] = Reading(m.RamUsedBytes > 0, Gb(m.RamUsedBytes), Unknown),
                ["totalGb"] = Reading(m.RamTotalBytes > 0, Gb(m.RamTotalBytes), Unknown),
                ["commitPercent"] = Reading(m.CommitPercent > 0, m.CommitPercent, Unknown),
                ["cachedGb"] = Reading(m.CachedBytes > 0, Gb(m.CachedBytes), Unknown),
            };

            var gpu = new JsonObject
            {
                ["usagePercent"] = Reading(m.GpuUsage >= 0, m.GpuUsage, "No GPU usage counter was found."),
                ["temperatureC"] = Reading(m.GpuTemperature > 0, m.GpuTemperature, "No GPU temperature source was found."),
                ["memoryUsedGb"] = Reading(m.GpuVramUsedBytes > 0, Gb(m.GpuVramUsedBytes), Unknown),
            };

            var disks = new JsonArray();
            foreach (DiskMetric d in m.Disks)
            {
                bool ready = d.TotalBytes > 0;
                const string NotReady = "The drive was not ready.";
                disks.Add(new JsonObject
                {
                    ["name"] = d.Name,
                    ["freePercent"] = Reading(ready, ready ? d.FreeBytes * 100d / d.TotalBytes : 0, NotReady),
                    ["freeGb"] = Reading(ready, Gb(d.FreeBytes), NotReady),
                    ["totalGb"] = Reading(ready, Gb(d.TotalBytes), NotReady),
                    ["activityPercent"] = Num(d.ActivityPercent),
                });
            }

            // The adapter's IP address is deliberately left out: addresses never leave the PC.
            var network = new JsonObject
            {
                ["upKbps"] = Num(m.NetUpKbps),
                ["downKbps"] = Num(m.NetDownKbps),
            };
            if (!string.IsNullOrWhiteSpace(m.NetAdapterName)) network["adapter"] = m.NetAdapterName;

            JsonNode battery = !m.HasBattery
                ? ToolJson.Unavailable("This PC has no battery.")
                : new JsonObject
                {
                    ["percent"] = m.BatteryPercent,
                    ["onAc"] = m.BatteryOnAc,
                    ["charging"] = m.BatteryCharging,
                    ["watts"] = Reading(m.BatteryWatts > 0, m.BatteryWatts, "Idle or not reported."),
                    ["minutesLeft"] = Reading(m.BatteryMinutesLeft >= 0, m.BatteryMinutesLeft, "Cannot be estimated right now."),
                    ["healthPercent"] = Reading(m.BatteryHealthPercent >= 0, m.BatteryHealthPercent, "Not read yet."),
                };

            JsonNode sensors;
            if (m.Sensors.Count == 0)
            {
                sensors = ToolJson.Unavailable("No sensor source is available on this PC.");
            }
            else
            {
                var list = new JsonArray();
                foreach (SensorReading s in m.Sensors)
                {
                    list.Add(new JsonObject
                    {
                        ["label"] = s.Label,
                        ["kind"] = s.Category.ToString(),
                        ["value"] = Num(s.Value),
                        ["unit"] = s.Unit,
                        ["source"] = s.Source,
                    });
                }
                sensors = list;
            }

            var recent = new JsonObject();
            foreach (KeyValuePair<string, SeriesStats> pair in _data.RecentStats())
            {
                recent[pair.Key] = new JsonObject
                {
                    ["min"] = Num(pair.Value.Min),
                    ["avg"] = Num(pair.Value.Avg),
                    ["max"] = Num(pair.Value.Max),
                    ["samples"] = pair.Value.Count,
                };
            }

            return new JsonObject
            {
                ["time"] = Iso(now),
                ["utcOffset"] = UtcOffset(now),
                ["cpu"] = cpu,
                ["memory"] = memory,
                ["gpu"] = gpu,
                ["disks"] = disks,
                ["network"] = network,
                ["battery"] = battery,
                ["sensors"] = sensors,
                ["recent"] = recent,
            };
        }

        /// <summary>Count, min, average and max of the per-core loads, and the busiest core's index.</summary>
        private static JsonNode CoreSummary(float[] cores)
        {
            if (cores.Length == 0) return ToolJson.Unavailable("Per-core counters are unavailable.");
            int busiest = 0;
            for (int i = 1; i < cores.Length; i++) if (cores[i] > cores[busiest]) busiest = i;
            return new JsonObject
            {
                ["count"] = cores.Length,
                ["min"] = Num(cores.Min()),
                ["avg"] = Num(cores.Average()),
                ["max"] = Num(cores[busiest]),
                ["busiestIndex"] = busiest,
            };
        }

        // ----- get_history -------------------------------------------------------------------

        private JsonObject BuildHistory(string metric, string from, string to, int maxPoints)
        {
            string wanted = (metric ?? "").Trim();
            string? name = HistoryMetrics.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (name == null)
                return ToolJson.Error("Unknown metric '" + metric + "'. Use one of: " + string.Join(", ", HistoryMetrics) + ".");

            DateTime now = _data.UtcNow;
            if (!TimeRange.TryParse(from, now, out DateTime fromUtc)) return BadTime(from);
            if (!TimeRange.TryParse(to, now, out DateTime toUtc)) return BadTime(to);
            if (fromUtc > toUtc) return ToolJson.Error("from (" + Iso(fromUtc) + ") is after to (" + Iso(toUtc) + ").");

            IReadOnlyList<HistoryRow> rows = _data.History(fromUtc, toUtc);
            IReadOnlyList<HistoryRow> points = Downsample(rows, Math.Clamp(maxPoints, 1, MaxHistoryPoints));

            var array = new JsonArray();
            foreach (HistoryRow row in points)
            {
                var point = new JsonObject { ["utc"] = Iso(row.Utc), ["seconds"] = row.Seconds };
                AddMetric(point, name, row);
                array.Add(point);
            }

            var result = new JsonObject
            {
                ["metric"] = name,
                ["from"] = Iso(fromUtc),
                ["to"] = Iso(toUtc),
                ["utcOffset"] = UtcOffset(now),
                ["rows"] = rows.Count,
                ["downsampled"] = points.Count < rows.Count,
                ["points"] = array,
            };
            if (rows.Count == 0) result["note"] = NoHistory;
            return result;
        }

        private static JsonObject BadTime(string? text) => ToolJson.Error("Could not read the time '" + text +
            "'. Use now, -90s, -30m, -6h, -2d or an ISO-8601 UTC time such as 2026-09-30T08:00:00Z.");

        /// <summary>
        /// Averages consecutive rows into at most <paramref name="maxPoints"/> buckets with the
        /// same rules the store uses when it thins old days (<see cref="HistoryCsv.Combine"/>).
        /// </summary>
        internal static IReadOnlyList<HistoryRow> Downsample(IReadOnlyList<HistoryRow> rows, int maxPoints)
        {
            if (rows.Count <= maxPoints) return rows;
            var result = new List<HistoryRow>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
            {
                int start = (int)((long)i * rows.Count / maxPoints);
                int end = (int)((long)(i + 1) * rows.Count / maxPoints);
                var chunk = new List<HistoryRow>(end - start);
                int seconds = 0;
                for (int j = start; j < end; j++)
                {
                    chunk.Add(rows[j]);
                    seconds += rows[j].Seconds;
                }
                result.Add(HistoryCsv.Combine(chunk, chunk[0].Utc, seconds));
            }
            return result;
        }

        private static void AddMetric(JsonObject point, string metric, HistoryRow r)
        {
            switch (metric)
            {
                case "cpu": Put(point, "avg", r.CpuAvg); Put(point, "max", r.CpuMax); break;
                case "cpuTemp": Put(point, "avgC", r.CpuTempAvg); Put(point, "maxC", r.CpuTempMax); break;
                case "ram": Put(point, "avg", r.RamAvg); Put(point, "max", r.RamMax); break;
                case "gpu": Put(point, "avg", r.GpuAvg); Put(point, "max", r.GpuMax); break;
                case "gpuTemp": Put(point, "maxC", r.GpuTempMax); break;
                case "netUp": Put(point, "avgKbps", r.NetUpAvg); break;
                case "netDown": Put(point, "avgKbps", r.NetDownAvg); break;
                case "diskFree": Put(point, "minFreePercent", r.DiskFreeMinPercent); break;
                case "diskActivity": Put(point, "maxActivityPercent", r.DiskActivityMax); break;
                case "battery":
                    Put(point, "percent", r.BatteryPercent);
                    if (r.OnAc is bool onAc) point["onAc"] = onAc;
                    break;
                case "topProcesses":
                    AddTop(point, r);
                    break;
                default: // all
                    Put(point, "cpuAvg", r.CpuAvg); Put(point, "cpuMax", r.CpuMax);
                    Put(point, "cpuTempAvgC", r.CpuTempAvg); Put(point, "cpuTempMaxC", r.CpuTempMax);
                    Put(point, "ramAvg", r.RamAvg); Put(point, "ramMax", r.RamMax);
                    Put(point, "gpuAvg", r.GpuAvg); Put(point, "gpuMax", r.GpuMax); Put(point, "gpuTempMaxC", r.GpuTempMax);
                    Put(point, "netUpKbps", r.NetUpAvg); Put(point, "netDownKbps", r.NetDownAvg);
                    Put(point, "diskMinFreePercent", r.DiskFreeMinPercent); Put(point, "diskMaxActivityPercent", r.DiskActivityMax);
                    Put(point, "batteryPercent", r.BatteryPercent);
                    if (r.OnAc is bool ac) point["onAc"] = ac;
                    AddTop(point, r);
                    break;
            }
        }

        private static void AddTop(JsonObject point, HistoryRow r)
        {
            if (!string.IsNullOrEmpty(r.TopCpuName))
            {
                var top = new JsonObject { ["name"] = r.TopCpuName };
                if (!string.IsNullOrEmpty(r.TopCpuPath)) top["path"] = r.TopCpuPath;
                Put(top, "cpuPercent", r.TopCpuPercent);
                point["topCpu"] = top;
            }
            if (!string.IsNullOrEmpty(r.TopRamName))
            {
                var top = new JsonObject { ["name"] = r.TopRamName };
                Put(top, "memoryMb", r.TopRamMb);
                point["topMemory"] = top;
            }
        }

        // ----- get_top_processes -------------------------------------------------------------

        private async Task<JsonNode> BuildTopProcessesAsync(string by, int count, CancellationToken ct)
        {
            string? key = (by ?? "").Trim().ToLowerInvariant() switch
            {
                "cpu" => "cpu",
                "memory" or "ram" or "mem" => "memory",
                "disk" or "io" => "disk",
                _ => null,
            };
            if (key == null) return ToolJson.Error("Unknown ranking '" + by + "'. Use cpu, memory or disk.");

            int n = Math.Clamp(count, 1, MaxTopProcesses);
            IReadOnlyList<ProcessInfo> list = await _data.TopProcessesAsync(key, n, ct).ConfigureAwait(false);

            var array = new JsonArray();
            foreach (ProcessInfo p in list.Take(n))
            {
                var item = new JsonObject { ["name"] = p.Name };
                if (!string.IsNullOrEmpty(p.Path)) item["path"] = p.Path;
                item["pid"] = p.Pid;
                item["createTime"] = p.CreateTime;
                if (TryFileTime(p.CreateTime, out DateTime started))
                {
                    item["startedUtc"] = Iso(started);
                    item["startedLocal"] = LocalText(started);
                }
                item["cpuPercent"] = Num(p.CpuPercent);
                item["memoryMb"] = Num(p.WorkingSetMb);
                item["diskKBps"] = Num(p.DiskKBps);
                array.Add(item);
            }

            return new JsonObject
            {
                ["by"] = key,
                ["time"] = Iso(_data.UtcNow),
                ["processes"] = array,
            };
        }

        private static bool TryFileTime(long fileTime, out DateTime utc)
        {
            utc = default;
            if (fileTime <= 0) return false;
            try
            {
                utc = DateTime.FromFileTimeUtc(fileTime);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
