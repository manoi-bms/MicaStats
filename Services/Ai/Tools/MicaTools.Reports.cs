using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    // The tools over saved and slow-to-gather data: slowdown reports, alerts, hardware,
    // battery and boot. Same rules as MicaTools.cs: JSON objects only, errors as results.
    public sealed partial class MicaTools
    {
        private const int MaxReports = 30;

        /// <summary>
        /// Longest report text returned. A 15-minute window is 900 timeline rows; the head (the
        /// header) and the tail (the moment the threshold was crossed, and the offenders) matter most.
        /// </summary>
        private const int MaxReportChars = 40_000;

        private const int ReportHeadChars = 4_000;
        private const int MaxBoots = 10;
        private const int MaxBootDelays = 10;
        private const int MaxStartupPrograms = 40;

        /// <summary><c>list_slowdown_reports</c>: id, time, trigger and a one-line summary, newest first; <paramref name="limit"/> is clamped to 1..30.</summary>
        public Task<JsonNode> ListSlowdownReportsAsync(int limit = 10, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildReportList(limit)), ct);

        /// <summary><c>get_slowdown_report</c>: the report text, redacted; the middle of a very long timeline is left out.</summary>
        public Task<JsonNode> GetSlowdownReportAsync(string id, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildReport(id)), ct);

        /// <summary><c>list_alerts</c>: the rules in force and the alerts raised since MicaStats started, optionally only those after <paramref name="since"/>.</summary>
        public Task<JsonNode> ListAlertsAsync(string? since = null, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildAlerts(since)), ct);

        /// <summary><c>get_hardware</c>: CPU, board, memory, graphics, storage and Windows details as the hardware report text.</summary>
        public Task<JsonNode> GetHardwareAsync(CancellationToken ct = default) =>
            RunAsync(() => BuildHardwareAsync(ct), ct);

        /// <summary><c>get_battery</c>: charge, power, time left, health, wear, capacities and cycle count.</summary>
        public Task<JsonNode> GetBatteryAsync(CancellationToken ct = default) =>
            RunAsync(() => BuildBatteryAsync(ct), ct);

        /// <summary><c>get_boot_summary</c>: recent boot durations, the trend, what slowed the last boot, and the startup programs.</summary>
        public Task<JsonNode> GetBootSummaryAsync(CancellationToken ct = default) =>
            RunAsync(() => BuildBootAsync(ct), ct);

        // ----- slowdown reports --------------------------------------------------------------

        private JsonObject BuildReportList(int limit)
        {
            IReadOnlyList<SavedReport> reports = _data.SlowdownReports();
            var array = new JsonArray();
            foreach (SavedReport report in reports.Take(Math.Clamp(limit, 1, MaxReports)))
            {
                string id = Path.GetFileNameWithoutExtension(report.Name);
                DateTime utc = FromLocal(report.At);
                (string? trigger, string summary) = Summarize(_data.ReadSlowdownReport(id));
                var item = new JsonObject { ["id"] = id, ["utc"] = Iso(utc), ["local"] = LocalText(utc) };
                if (trigger != null) item["trigger"] = trigger;
                item["summary"] = summary;
                array.Add(item);
            }
            return new JsonObject { ["total"] = reports.Count, ["reports"] = array };
        }

        private JsonObject BuildReport(string id)
        {
            string key = (id ?? "").Trim();
            if (!SlowdownReportFiles.IsValidId(key))
                return ToolJson.Error("'" + id + "' is not a slowdown report id. Ids look like slowdown-20260930-140200; list_slowdown_reports gives them.");

            string? text = _data.ReadSlowdownReport(key);
            if (text == null)
                return ToolJson.Error("No slowdown report has the id '" + key + "'. Call list_slowdown_reports for the current ids.");

            bool truncated = text.Length > MaxReportChars;
            if (truncated)
            {
                int omitted = text.Length - MaxReportChars;
                text = text[..ReportHeadChars] +
                       "\n[... " + omitted.ToString(CultureInfo.InvariantCulture) + " characters of the timeline left out ...]\n" +
                       text[^(MaxReportChars - ReportHeadChars)..];
            }
            return new JsonObject { ["id"] = key, ["truncated"] = truncated, ["text"] = text };
        }

        /// <summary>
        /// The trigger line and the first worst offender by CPU and by disk, read from the report
        /// text that <c>SlowdownReportWriter.Write</c> produces.
        /// </summary>
        internal static (string? Trigger, string Summary) Summarize(string? text)
        {
            if (text == null) return (null, "The report could not be read.");

            string? trigger = null, topCpu = null, topDisk = null;
            bool empty = false;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (trigger == null && line.StartsWith("Trigger", StringComparison.Ordinal) && line.Contains(':'))
                    trigger = line[(line.IndexOf(':') + 1)..].Trim();
                else if (line.StartsWith("No samples were held", StringComparison.Ordinal))
                    empty = true;
                else if (line.StartsWith("By CPU", StringComparison.Ordinal))
                    topCpu = FirstOffender(lines, i);
                else if (line.StartsWith("By disk", StringComparison.Ordinal))
                    topDisk = FirstOffender(lines, i);
            }

            if (empty) return (trigger, "No samples were held when the report was written.");
            var parts = new List<string>();
            if (topCpu != null) parts.Add("busiest by CPU: " + topCpu);
            if (topDisk != null) parts.Add("busiest by disk: " + topDisk);
            return (trigger, parts.Count == 0 ? "No busy process was measured." : string.Join("; ", parts));
        }

        private static string? FirstOffender(string[] lines, int headingIndex)
        {
            if (lines[headingIndex].Trim().EndsWith("nothing measurable", StringComparison.Ordinal)) return null;
            if (headingIndex + 1 >= lines.Length) return null;
            string next = Regex.Replace(lines[headingIndex + 1].Trim(), "\\s{2,}", " ");
            return next.Length == 0 || next == "nothing measurable" ? null : next;
        }

        // ----- alerts ------------------------------------------------------------------------

        private JsonObject BuildAlerts(string? since)
        {
            DateTime? sinceUtc = null;
            if (!string.IsNullOrWhiteSpace(since))
            {
                if (!TimeRange.TryParse(since, _data.UtcNow, out DateTime parsed)) return BadTime(since);
                sinceUtc = parsed;
            }

            var rules = new JsonArray();
            foreach (AlertRule rule in _data.AlertRules())
            {
                rules.Add(new JsonObject
                {
                    ["id"] = rule.Id,
                    ["label"] = rule.Label,
                    ["metric"] = rule.Metric.ToString(),
                    ["threshold"] = Num(rule.Threshold),
                    ["unit"] = rule.Unit.Trim(),
                    ["firesWhen"] = rule.Above ? "above" : "below",
                    ["sustainSeconds"] = rule.SustainSeconds,
                    ["enabled"] = rule.Enabled,
                });
            }

            var raised = new JsonArray();
            foreach (AlertEvent alert in _data.RecentAlerts())
            {
                DateTime utc = FromLocal(alert.At);
                if (sinceUtc is DateTime from && utc < from) continue;
                raised.Add(new JsonObject
                {
                    ["ruleId"] = alert.Rule.Id,
                    ["title"] = alert.Title,
                    ["message"] = alert.Message,
                    ["value"] = Num(alert.Value),
                    ["unit"] = alert.Rule.Unit.Trim(),
                    ["utc"] = Iso(utc),
                    ["local"] = LocalText(utc),
                });
            }

            var result = new JsonObject
            {
                ["rules"] = rules,
                ["raised"] = raised,
                ["note"] = "Raised alerts are kept in memory since MicaStats started (the last 50); cleared alerts are not recorded.",
            };
            if (sinceUtc is DateTime s) result["since"] = Iso(s);
            return result;
        }

        // ----- hardware, battery, boot -------------------------------------------------------

        private async Task<JsonNode> BuildHardwareAsync(CancellationToken ct)
        {
            string? report = await _data.HardwareReportAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(report)
                ? ToolJson.Unavailable("The hardware details could not be read.")
                : new JsonObject { ["report"] = report };
        }

        private async Task<JsonNode> BuildBatteryAsync(CancellationToken ct)
        {
            BatteryReading? reading = _data.Battery();
            if (reading == null || !reading.Present) return ToolJson.Unavailable("This PC has no battery.");
            BatteryHealth? health = await _data.BatteryHealthAsync(ct).ConfigureAwait(false);

            var result = new JsonObject
            {
                ["percent"] = reading.Percent,
                ["onAc"] = reading.OnAcPower,
                ["charging"] = reading.Charging,
                ["watts"] = Reading(reading.RateMw > 0, reading.Watts, "Idle or not reported."),
            };

            if (reading.Discharging)
            {
                TimeSpan? left = BatteryEstimate.TimeToEmpty(reading.RemainingMwh, reading.RateMw);
                result["minutesLeft"] = Reading(left.HasValue, left?.TotalMinutes ?? 0, "Cannot be estimated right now.");
            }
            if (reading.Charging)
            {
                TimeSpan? toFull = health == null ? null
                    : BatteryEstimate.TimeToFull(reading.RemainingMwh, health.FullChargeCapacityMwh, reading.RateMw);
                result["minutesToFull"] = Reading(toFull.HasValue, toFull?.TotalMinutes ?? 0, "Cannot be estimated right now.");
            }

            if (health == null || !health.Any || health.HealthPercent < 0)
            {
                result["health"] = ToolJson.Unavailable("Windows did not report the design capacity of this battery.");
            }
            else
            {
                result["health"] = new JsonObject
                {
                    ["healthPercent"] = Num(health.HealthPercent),
                    ["wearPercent"] = Num(Math.Max(0d, 100d - health.HealthPercent)),
                    ["verdict"] = BatteryEstimate.HealthVerdict(health.HealthPercent),
                    ["designCapacityMwh"] = health.DesignCapacityMwh,
                    ["fullChargeCapacityMwh"] = health.FullChargeCapacityMwh,
                    ["cycleCount"] = Reading(health.CycleCount > 0, health.CycleCount, "The battery does not report a cycle count."),
                    ["packs"] = health.Packs.Count,
                };
            }
            return result;
        }

        private async Task<JsonNode> BuildBootAsync(CancellationToken ct)
        {
            BootAnalysis? boot = await _data.BootAsync(ct).ConfigureAwait(false);
            if (boot == null) return ToolJson.Unavailable("The boot history could not be read.");
            if (boot.Boots.Count == 0) return ToolJson.Unavailable(boot.Problem ?? "Windows has not recorded a boot yet.");

            var boots = new JsonArray();
            foreach (BootRecord b in boot.Boots.Take(MaxBoots))
            {
                DateTime utc = FromLocal(b.BootAt);
                boots.Add(new JsonObject
                {
                    ["utc"] = Iso(utc),
                    ["local"] = LocalText(utc),
                    ["seconds"] = Num(b.Seconds),
                    ["mainPathSeconds"] = Num(b.MainPathMs / 1000d),
                    ["afterSignInSeconds"] = Num(b.PostBootMs / 1000d),
                    ["startupApps"] = b.StartupAppCount,
                    ["slowerThanUsual"] = b.IsDegradation,
                });
            }

            var delays = new JsonArray();
            foreach (StartupDelay d in boot.Delays.Take(MaxBootDelays))
            {
                var item = new JsonObject
                {
                    ["kind"] = d.Kind.ToString(),
                    ["name"] = d.DisplayName,
                    ["seconds"] = Num(d.TotalMs / 1000d),
                    ["delaySeconds"] = Num(d.DegradationMs / 1000d),
                };
                if (!string.IsNullOrWhiteSpace(d.Company)) item["company"] = d.Company;
                delays.Add(item);
            }

            // Names only: a startup entry's command line can carry anything, and no tool ever
            // sends a command line.
            var programs = new JsonArray();
            foreach (StartupEntry e in boot.Entries.Take(MaxStartupPrograms))
            {
                programs.Add(new JsonObject { ["name"] = e.Name, ["enabled"] = e.Enabled, ["scope"] = e.Scope.ToString() });
            }

            return new JsonObject
            {
                ["boots"] = boots,
                ["averageSeconds"] = Num(boot.AverageSeconds),
                ["trendSeconds"] = Num(boot.TrendSeconds),
                ["slowedLastBoot"] = delays,
                ["startupPrograms"] = programs,
            };
        }
    }
}
