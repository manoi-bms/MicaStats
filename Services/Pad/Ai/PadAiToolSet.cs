using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    public sealed record PadAiTask(
        string Id,
        int Line,
        string Text,
        bool Completed,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? FinishedAt);

    public enum PadAiProposalKind
    {
        TaskState,
        StoreCredential,
    }

    /// <summary>A user-reviewable request. No note or credential state changes when this is created.</summary>
    public sealed record PadAiProposal(
        string Id,
        PadAiProposalKind Kind,
        string TargetId,
        bool? Completed = null);

    /// <summary>
    /// Pure tools for one MicaPad note snapshot. They expose safe task/candidate descriptions and
    /// record proposals for the host UI; they never edit a note or write the credential vault.
    /// </summary>
    public sealed class PadAiToolSet
    {
        public const int MaxSourceLength = 24_000;
        public const int MaxProposals = 32;
        private const int MaxTaskText = 500;
        private const int MaxListTasksJsonChars = 12_000;
        private const int ListTasksRowsBudget = 11_500;

        private readonly IReadOnlyList<PasswordCandidateMatch> _credentialMatches;
        private readonly List<PadAiProposal> _proposals = new();

        public PadAiToolSet(string source, IReadOnlyDictionary<int, TaskDateRecord>? taskDates = null)
        {
            source ??= "";
            if (source.Length > MaxSourceLength)
                throw new ArgumentOutOfRangeException(nameof(source), "MicaPad AI tool text is limited to 24,000 characters.");

            _credentialMatches = PasswordCandidates.ScanAll(source);
            Credentials = _credentialMatches.Take(PasswordCandidates.MaxCandidates).Select(match => new PadAiCredentialCandidate(
                match.Id, match.Line, match.Label, match.Reason)).ToArray();
            Mask = SecretMask.Of(source, _credentialMatches.Select(match => match.Value).ToArray());
            Tasks = ParseTasks(source, taskDates);

            var target = new Target(this);
            Functions = new AITool[]
            {
                AIFunctionFactory.Create(target.ListTasks, Options("list_tasks",
                    "List Markdown checkbox tasks in the current note. Dates are read-only metadata stored outside the Markdown.")),
                AIFunctionFactory.Create(target.ProposeTaskState, Options("propose_task_state",
                    "Propose checking or reopening one listed task. This only creates a review item; it does not edit the note.")),
                AIFunctionFactory.Create(target.ScanCredentials, Options("scan_credentials",
                    "List conservative, locally detected credential candidates in the current note. Values are never returned.")),
                AIFunctionFactory.Create(target.ProposeCredentialStorage, Options("propose_credential_storage",
                    "Propose storing selected scan_credentials candidates. This only creates review items; it does not reveal or store values.")),
            };
        }

        public SecretMask Mask { get; }
        public IReadOnlyList<AITool> Functions { get; }
        public IReadOnlyList<PadAiTask> Tasks { get; }
        public IReadOnlyList<PadAiCredentialCandidate> Credentials { get; }
        public IReadOnlyList<PadAiProposal> Proposals => _proposals;

        /// <summary>Removes stored references, known candidates, and newly detected explicit credentials.</summary>
        public string Redact(string text)
        {
            text ??= "";
            string[] freshValues = PasswordCandidates.ScanAll(text).Select(match => match.Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            return Mask.RedactFragment(text, freshValues);
        }

        /// <summary>For the app-owned, post-approval vault commit only.</summary>
        internal string? GetCredentialValue(string id) =>
            _credentialMatches.FirstOrDefault(candidate => candidate.Id == id)?.Value;

        private IReadOnlyList<PadAiTask> ParseTasks(string source, IReadOnlyDictionary<int, TaskDateRecord>? taskDates)
        {
            List<(string Text, int Offset)> lines = Lines(source);
            MdLineFacts[] facts = MarkdownStructure.Scan(lines.Select(line => line.Text).ToArray()).Facts;
            var result = new List<PadAiTask>();
            var usedIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Text;
                if (facts[i].Fence != MdFence.None || !MarkdownTasks.IsTask(line)) continue;
                TaskDateRecord? dates = null;
                if (taskDates != null)
                {
                    if (!taskDates.TryGetValue(lines[i].Offset, out TaskDateRecord? tracked)) continue;
                    dates = tracked;
                }
                string id = dates is { Id.Length: > 0 } ? dates.Id : "line-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (!usedIds.Add(id)) id += "-line-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                string text = Clip(Redact(MarkdownTasks.Identity(line)), MaxTaskText);
                result.Add(new PadAiTask(id, i + 1, text, MarkdownTasks.IsFinished(line), dates?.Created, dates?.Finished));
            }
            return result;
        }

        private JsonElement ListTasks()
        {
            var tasks = new JsonArray();
            int used = 100;
            foreach (PadAiTask task in Tasks)
            {
                var row = new JsonObject
                {
                    ["id"] = task.Id,
                    ["line"] = task.Line,
                    ["text"] = task.Text,
                    ["completed"] = task.Completed,
                    ["createdAt"] = task.CreatedAt?.ToString("O", CultureInfo.InvariantCulture),
                    ["finishedAt"] = task.FinishedAt?.ToString("O", CultureInfo.InvariantCulture),
                };
                int rowChars = row.ToJsonString().Length + 1;
                if (used + rowChars > ListTasksRowsBudget) break;
                tasks.Add(row);
                used += rowChars;
            }
            var result = new JsonObject
            {
                ["tasks"] = tasks,
                ["truncated"] = tasks.Count < Tasks.Count,
                ["totalCount"] = Tasks.Count,
            };
            // The row budget normally leaves ample room; trim defensively if serializer escaping grows.
            while (result.ToJsonString().Length > MaxListTasksJsonChars && tasks.Count > 0)
            {
                tasks.RemoveAt(tasks.Count - 1);
                result["truncated"] = true;
            }
            return Element(result);
        }

        private JsonElement ScanCredentials()
        {
            var candidates = new JsonArray(Credentials.Select(candidate => (JsonNode)new JsonObject
            {
                ["id"] = candidate.Id,
                ["line"] = candidate.Line,
                ["label"] = candidate.Label,
                ["reason"] = candidate.Reason,
            }).ToArray());
            return Element(new JsonObject
            {
                ["candidates"] = candidates,
                ["truncated"] = _credentialMatches.Count > PasswordCandidates.MaxCandidates,
                ["note"] = "Conservative matches only; review each candidate. This is not a guarantee that every secret was found.",
            });
        }

        private JsonElement ProposeTaskState(string taskId, bool completed)
        {
            PadAiTask? task = Tasks.FirstOrDefault(item => item.Id == taskId);
            if (task == null) return Error("Unknown task_id. Call list_tasks again and use an id from that result.");
            return AddProposal(new PadAiProposal(
                "task-state:" + task.Id + ":" + (completed ? "completed" : "open"),
                PadAiProposalKind.TaskState, task.Id, completed));
        }

        private JsonElement ProposeCredentialStorage(string[]? candidateIds)
        {
            if (candidateIds == null || candidateIds.Length == 0)
                return Error("Choose at least one candidate_id from scan_credentials.");
            string[] ids = candidateIds.Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > MaxProposals) return Error("At most 32 credential candidates may be proposed at once.");
            if (ids.Any(id => Credentials.All(candidate => candidate.Id != id)))
                return Error("One or more candidate_ids are stale or unknown. Call scan_credentials again.");
            if (_proposals.Count + ids.Count(id => !_proposals.Any(p => p.Kind == PadAiProposalKind.StoreCredential && p.TargetId == id)) > MaxProposals)
                return Error("This answer already contains the maximum of 32 proposals.");

            foreach (string id in ids)
            {
                var proposal = new PadAiProposal("store-credential:" + id, PadAiProposalKind.StoreCredential, id);
                if (!_proposals.Contains(proposal)) _proposals.Add(proposal);
            }
            return Element(new JsonObject
            {
                ["recorded"] = ids.Length,
                ["note"] = "The user must review and approve these items. No credential has been stored.",
            });
        }

        private JsonElement AddProposal(PadAiProposal proposal)
        {
            int existing = _proposals.FindIndex(item => item.Kind == proposal.Kind && item.TargetId == proposal.TargetId);
            if (existing >= 0)
            {
                if (_proposals[existing] == proposal)
                    return Element(new JsonObject { ["recorded"] = true, ["duplicate"] = true });
                _proposals[existing] = proposal;
                return Element(new JsonObject { ["recorded"] = true, ["replaced"] = true });
            }
            if (_proposals.Count >= MaxProposals) return Error("This answer already contains the maximum of 32 proposals.");
            _proposals.Add(proposal);
            return Element(new JsonObject
            {
                ["recorded"] = true,
                ["note"] = "The user must review and approve this item. The note has not changed.",
            });
        }

        private static JsonElement Error(string message) => Element(new JsonObject { ["error"] = message });
        private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
        private static AIFunctionFactoryOptions Options(string name, string description) => new() { Name = name, Description = description };
        private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

        private static List<(string Text, int Offset)> Lines(string source)
        {
            var result = new List<(string, int)>();
            int start = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] is not ('\r' or '\n')) continue;
                result.Add((source.Substring(start, i - start), start));
                if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                start = i + 1;
            }
            result.Add((source.Substring(start), start));
            return result;
        }

        private sealed class Target
        {
            private readonly PadAiToolSet _owner;
            public Target(PadAiToolSet owner) => _owner = owner;

            public JsonElement ListTasks() => _owner.ListTasks();

            public JsonElement ProposeTaskState(
                [Description("The id from list_tasks.")] string task_id,
                [Description("true to complete the task; false to reopen it.")] bool completed) =>
                _owner.ProposeTaskState(task_id, completed);

            public JsonElement ScanCredentials() => _owner.ScanCredentials();

            public JsonElement ProposeCredentialStorage(
                [Description("One or more ids from scan_credentials.")] string[]? candidate_ids) =>
                _owner.ProposeCredentialStorage(candidate_ids);
        }
    }
}
