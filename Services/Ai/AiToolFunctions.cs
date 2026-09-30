using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The assistant's tools as <see cref="AIFunction"/>s: the nine read-only
    /// <see cref="MicaTools"/> plus the in-app <c>suggest_action</c>. Each returns a
    /// <see cref="JsonElement"/>, so a result reaches the model as JSON rather than as a
    /// JSON-quoted string, and the parameter descriptions become the schema the model reads.
    /// </summary>
    internal static class AiToolFunctions
    {
        /// <summary>Most suggested actions one answer may carry.</summary>
        public const int MaxSuggestions = 3;

        /// <summary>The nine read-only tools, in <see cref="ToolNames.ReadOnly"/> order.</summary>
        public static IReadOnlyList<AIFunction> ReadOnly(MicaTools tools)
        {
            var t = new Target(tools);
            return new[]
            {
                AIFunctionFactory.Create(t.GetLiveStatus, Options(ToolNames.GetLiveStatus,
                    "Current readings of this PC: CPU (total, per-core summary, temperature, clock), memory, GPU, disks " +
                    "(free space, activity), network rates, battery and sensors, plus min/avg/max over the last two minutes.")),
                AIFunctionFactory.Create(t.GetHistory, Options(ToolNames.GetHistory,
                    "Recorded history of one metric between two times: one row per minute for the last 24 hours, one per " +
                    "5 minutes up to 7 days, averaged down to maxPoints. Recorded only while Keep 7 days of history is on. " +
                    "A missing field means the reading was unavailable in that interval (never 0).")),
                AIFunctionFactory.Create(t.GetTopProcesses, Options(ToolNames.GetTopProcesses,
                    "The busiest processes right now, measured over about 2 seconds: name, path, pid, createTime, CPU %, " +
                    "memory MB and disk KB/s. pid and createTime identify a process for suggest_action.")),
                AIFunctionFactory.Create(t.ListSlowdownReports, Options(ToolNames.ListSlowdownReports,
                    "Slowdown reports MicaStats saved when the PC struggled (or the user recorded one), newest first: id, " +
                    "time, trigger and a one-line summary.")),
                AIFunctionFactory.Create(t.GetSlowdownReport, Options(ToolNames.GetSlowdownReport,
                    "The text of one slowdown report: a per-second timeline of CPU, memory, disk and the busiest process, " +
                    "then the worst offenders. The last timeline rows are the moment the threshold was crossed.")),
                AIFunctionFactory.Create(t.ListAlerts, Options(ToolNames.ListAlerts,
                    "The alert rules (threshold, sustain time, on or off) and the alerts raised since MicaStats started, newest first.")),
                AIFunctionFactory.Create(t.GetHardware, Options(ToolNames.GetHardware,
                    "Hardware of this PC as a text report: CPU, mainboard, memory modules, graphics, storage and Windows version.")),
                AIFunctionFactory.Create(t.GetBattery, Options(ToolNames.GetBattery,
                    "Battery charge, power draw, time left, health (full versus design capacity), wear and cycle count.")),
                AIFunctionFactory.Create(t.GetBootSummary, Options(ToolNames.GetBootSummary,
                    "Recent Windows boot durations (newest first), the trend (seconds the latest boot was slower than the " +
                    "earlier average), what slowed the last boot, and the programs set to start at sign-in.")),
            };
        }

        /// <summary>
        /// The in-app <c>suggest_action</c> tool. It validates the arguments and hands the
        /// suggestion to <paramref name="record"/>, which returns null when it was kept or a
        /// sentence saying why not. It never runs anything.
        /// </summary>
        public static AIFunction SuggestAction(Func<SuggestedAction, string?> record) =>
            AIFunctionFactory.Create(new Suggester(record).Suggest, Options(ToolNames.SuggestAction,
                "Offer the user a button for an action MicaStats can do: end_process, record_slowdown, open_diagnostics " +
                "or open_process_window. Nothing happens until the user clicks it, and MicaStats checks the target again " +
                "first. Use it only when the action clearly helps, at most " +
                MaxSuggestions.ToString(CultureInfo.InvariantCulture) + " per answer."));

        /// <summary>
        /// Checks and completes a suggestion: a known kind, a reason, and for end_process a pid
        /// and createTime. Null with <paramref name="error"/> set when something is missing.
        /// </summary>
        internal static SuggestedAction? Build(string? kind, string? reason, string? label, int? pid, long? createTime,
                                               string? processName, out string? error)
        {
            error = null;
            SuggestedActionKind? parsed = (kind ?? "").Trim().ToLowerInvariant() switch
            {
                "end_process" => SuggestedActionKind.EndProcess,
                "record_slowdown" => SuggestedActionKind.RecordSlowdown,
                "open_diagnostics" => SuggestedActionKind.OpenDiagnostics,
                "open_process_window" => SuggestedActionKind.OpenProcessWindow,
                _ => null,
            };
            if (parsed is not SuggestedActionKind k)
            {
                error = "Unknown kind '" + kind + "'. Use end_process, record_slowdown, open_diagnostics or open_process_window.";
                return null;
            }

            string why = Clip((reason ?? "").Trim(), 300);
            if (why.Length == 0)
            {
                error = "Give a short reason the user can read.";
                return null;
            }

            if (k == SuggestedActionKind.EndProcess)
            {
                if (pid is not > 0 || createTime is not > 0)
                {
                    error = "end_process needs the pid and createTime of the process, from get_top_processes.";
                    return null;
                }
                string name = (processName ?? "").Trim();
                // The model's label is ignored: the one destructive button always names its target.
                return new SuggestedAction(k, SuggestedAction.EndProcessText(name, pid), why, pid, createTime,
                                           name.Length > 0 ? name : null);
            }

            string text = string.IsNullOrWhiteSpace(label) ? k switch
            {
                SuggestedActionKind.RecordSlowdown => "Record a slowdown now",
                SuggestedActionKind.OpenDiagnostics => "Open Diagnostics",
                _ => "Open the process list",
            } : label.Trim();
            return new SuggestedAction(k, Clip(text, 60), why);
        }

        /// <summary>A tool result as the <see cref="JsonElement"/> the function-calling loop sends on.</summary>
        internal static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node, ToolJson.TextOptions);

        private static AIFunctionFactoryOptions Options(string name, string description) =>
            new() { Name = name, Description = description };

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

        /// <summary>The read-only tools as instance methods, so the parameter descriptions become the schema.</summary>
        private sealed class Target
        {
            private readonly MicaTools _tools;

            public Target(MicaTools tools) => _tools = tools;

            public async Task<JsonElement> GetLiveStatus(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetLiveStatusAsync(cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetHistory(
                [Description("One of: cpu, cpuTemp, ram, gpu, gpuTemp, netUp, netDown, diskFree, diskActivity, battery, topProcesses, all.")] string metric,
                [Description("Start: now, a relative time such as -30m, -6h or -2d, or an ISO-8601 UTC time.")] string from,
                [Description("End, in the same forms; usually now.")] string to = "now",
                [Description("Most points to return, 1-500. Use fewer for long ranges, and at most 60 with all.")] int maxPoints = 200,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.GetHistoryAsync(metric, from, to, maxPoints, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetTopProcesses(
                [Description("Ranking: cpu, memory or disk.")] string by = "cpu",
                [Description("How many processes, 1-15.")] int count = 10,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.GetTopProcessesAsync(by, count, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> ListSlowdownReports(
                [Description("How many reports, 1-30, newest first.")] int limit = 10,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.ListSlowdownReportsAsync(limit, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetSlowdownReport(
                [Description("The report id from list_slowdown_reports, e.g. slowdown-20260930-140200.")] string id,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.GetSlowdownReportAsync(id, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> ListAlerts(
                [Description("Optional: only alerts raised after this time (-6h, -2d or ISO-8601 UTC).")] string? since = null,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.ListAlertsAsync(since, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetHardware(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetHardwareAsync(cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetBattery(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetBatteryAsync(cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetBootSummary(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetBootSummaryAsync(cancellationToken).ConfigureAwait(false));
        }

        /// <summary>The suggest_action body, bound to one question's recorder.</summary>
        private sealed class Suggester
        {
            private readonly Func<SuggestedAction, string?> _record;

            public Suggester(Func<SuggestedAction, string?> record) => _record = record;

            public JsonElement Suggest(
                [Description("end_process, record_slowdown, open_diagnostics or open_process_window.")] string kind,
                [Description("One short sentence in the user's language: why this helps.")] string reason,
                [Description("Button text in the user's language, e.g. Open Diagnostics. Optional; ignored for end_process, whose button always names the process and PID.")] string? label = null,
                [Description("end_process only: the pid from get_top_processes.")] int? pid = null,
                [Description("end_process only: the createTime from get_top_processes.")] long? createTime = null,
                [Description("end_process only: the process name.")] string? processName = null)
            {
                SuggestedAction? action = Build(kind, reason, label, pid, createTime, processName, out string? error);
                if (action == null) return ToElement(ToolJson.Error(error!));
                string? refused = _record(action);
                if (refused != null) return ToElement(ToolJson.Error(refused));
                return ToElement(new JsonObject
                {
                    ["recorded"] = true,
                    ["note"] = "The user now sees a button labelled '" + action.Label + "'. Nothing has been done; do not say it was.",
                });
            }
        }
    }
}
