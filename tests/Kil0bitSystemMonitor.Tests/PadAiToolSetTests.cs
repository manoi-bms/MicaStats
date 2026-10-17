using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PadAiToolSetTests
    {
        private static AIFunction Function(PadAiToolSet tools, string name) =>
            tools.Functions.OfType<AIFunction>().Single(function => function.Name == name);

        [Fact]
        public void Lists_tasks_outside_fences_with_separate_dates_and_redacted_text()
        {
            var created = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(7));
            const string source = "- [] Deploy password=owl-Secret-88\n- [oops] malformed\n```md\n- [x] example\n```\n- [x] Done";
            var dates = new Dictionary<int, TaskDateRecord>
            {
                [0] = new("task-a", 0, "- [] Deploy password=owl-Secret-88", created, null),
                [source.IndexOf("- [x] Done", StringComparison.Ordinal)] = new("task-b", source.IndexOf("- [x] Done", StringComparison.Ordinal), "- [x] Done", created, created),
            };

            var tools = new PadAiToolSet(source, dates);

            Assert.Equal(2, tools.Tasks.Count);
            Assert.Equal("task-a", tools.Tasks[0].Id);
            Assert.Equal(created, tools.Tasks[0].CreatedAt);
            Assert.DoesNotContain("owl-Secret-88", tools.Tasks[0].Text);
            Assert.False(tools.Tasks[0].Completed);
            Assert.True(tools.Tasks[1].Completed);
        }

        [Fact]
        public async Task Tools_only_record_valid_proposals_and_never_serialize_plaintext()
        {
            const string secret = "owl-Secret-88";
            var tools = new PadAiToolSet("- [] Ship\npassword=" + secret);

            var scan = Assert.IsType<JsonElement>(await Function(tools, "scan_credentials").InvokeAsync(new AIFunctionArguments()));
            Assert.DoesNotContain(secret, scan.GetRawText());

            var stale = Assert.IsType<JsonElement>(await Function(tools, "propose_task_state").InvokeAsync(
                new AIFunctionArguments { ["task_id"] = "old-id", ["completed"] = true }));
            Assert.True(stale.TryGetProperty("error", out _));
            Assert.Empty(tools.Proposals);

            string taskId = tools.Tasks[0].Id;
            var accepted = Assert.IsType<JsonElement>(await Function(tools, "propose_task_state").InvokeAsync(
                new AIFunctionArguments { ["task_id"] = taskId, ["completed"] = true }));
            Assert.True(accepted.GetProperty("recorded").GetBoolean());

            string candidateId = tools.Credentials[0].Id;
            Assert.Equal(secret, tools.GetCredentialValue(candidateId));
            await Function(tools, "propose_credential_storage").InvokeAsync(
                new AIFunctionArguments { ["candidate_ids"] = new[] { candidateId } });
            Assert.Equal(2, tools.Proposals.Count);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(tools.Proposals));
            Assert.DoesNotContain(secret, tools.Credentials[0].ToString());
        }

        [Fact]
        public async Task Missing_and_stale_credential_arguments_do_not_partially_record()
        {
            var tools = new PadAiToolSet("password=owl-Secret-88");
            AIFunction propose = Function(tools, "propose_credential_storage");

            var missing = Assert.IsType<JsonElement>(await propose.InvokeAsync(
                new AIFunctionArguments { ["candidate_ids"] = Array.Empty<string>() }));
            Assert.True(missing.TryGetProperty("error", out _));

            var stale = Assert.IsType<JsonElement>(await propose.InvokeAsync(
                new AIFunctionArguments { ["candidate_ids"] = new[] { tools.Credentials[0].Id, "credential-old" } }));
            Assert.True(stale.TryGetProperty("error", out _));
            Assert.Empty(tools.Proposals);
        }

        [Fact]
        public async Task Latest_task_state_replaces_the_opposite_proposal()
        {
            var tools = new PadAiToolSet("- [] Ship");
            AIFunction propose = Function(tools, "propose_task_state");
            string id = tools.Tasks[0].Id;

            await propose.InvokeAsync(new AIFunctionArguments { ["task_id"] = id, ["completed"] = true });
            var replaced = Assert.IsType<JsonElement>(await propose.InvokeAsync(
                new AIFunctionArguments { ["task_id"] = id, ["completed"] = false }));

            Assert.True(replaced.GetProperty("replaced").GetBoolean());
            PadAiProposal proposal = Assert.Single(tools.Proposals);
            Assert.Equal(false, proposal.Completed);
        }

        [Fact]
        public async Task List_tasks_stays_within_the_model_output_budget_and_reports_total()
        {
            var source = new StringBuilder();
            for (int i = 0; i < 40; i++)
                source.Append("- [] ").Append(new string('ก', 450)).Append(i).Append('\n');
            var tools = new PadAiToolSet(source.ToString());

            var result = Assert.IsType<JsonElement>(await Function(tools, "list_tasks").InvokeAsync(new AIFunctionArguments()));

            Assert.True(result.GetRawText().Length <= 12_000);
            Assert.True(result.GetProperty("truncated").GetBoolean());
            Assert.Equal(40, result.GetProperty("totalCount").GetInt32());
            Assert.True(result.GetProperty("tasks").GetArrayLength() < 40);
        }

        [Fact]
        public async Task Tool_fragments_keep_the_source_credential_numbers_for_result_restoration()
        {
            const string source = "- [] First {{secret:AAAAAAAA}}\n- [] Second {{secret:BBBBBBBB}}";
            var tools = new PadAiToolSet(source);

            var result = Assert.IsType<JsonElement>(await Function(tools, "list_tasks").InvokeAsync(new AIFunctionArguments()));
            string second = result.GetProperty("tasks")[1].GetProperty("text").GetString()!;

            Assert.Equal("Second [[CREDENTIAL_2]]", second);
            Assert.Equal("Second {{secret:BBBBBBBB}}", tools.Mask.Unmask(second));
        }

        [Fact]
        public void Redact_catches_known_values_fresh_assignments_and_stored_pills()
        {
            var tools = new PadAiToolSet("password=owl-Secret-88");

            string safe = tools.Redact("Known owl-Secret-88\ntoken=fresh-Token-99\n{{secret:K7Q2M9XD}}");

            Assert.DoesNotContain("owl-Secret-88", safe);
            Assert.DoesNotContain("fresh-Token-99", safe);
            Assert.DoesNotContain("{{secret:", safe);
            Assert.Contains("[[LOCAL_SECRET_1]]", safe);
            Assert.Contains("[[HIDDEN_CREDENTIAL]]", safe);
            Assert.DoesNotContain("fresh-Token-99", tools.Mask.Unmask(safe));
        }

        [Fact]
        public async Task Candidate_review_is_capped_but_every_detected_value_is_masked()
        {
            var source = new StringBuilder();
            for (int i = 0; i < 33; i++) source.Append("password=private-value-").Append(i).Append('\n');
            var tools = new PadAiToolSet(source.ToString());

            Assert.Equal(32, tools.Credentials.Count);
            Assert.DoesNotContain("private-value-32", tools.Mask.Text);
            var scan = Assert.IsType<JsonElement>(await Function(tools, "scan_credentials").InvokeAsync(new AIFunctionArguments()));
            Assert.True(scan.GetProperty("truncated").GetBoolean());
        }

        [Fact]
        public void Supplied_tracker_offsets_authorize_tasks_when_selection_lacks_parent_fence_context()
        {
            const string source = "- [] looks like a task\n- [] tracked task";
            int trackedOffset = source.IndexOf("- [] tracked", StringComparison.Ordinal);
            var dates = new Dictionary<int, TaskDateRecord>
            {
                [trackedOffset] = new("tracked", trackedOffset, "- [] tracked task", DateTimeOffset.UtcNow, null),
            };

            var tools = new PadAiToolSet(source, dates);

            PadAiTask task = Assert.Single(tools.Tasks);
            Assert.Equal("tracked", task.Id);
        }

        [Fact]
        public void Rejects_source_larger_than_the_app_contract() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new PadAiToolSet(new string('a', PadAiToolSet.MaxSourceLength + 1)));
    }
}
