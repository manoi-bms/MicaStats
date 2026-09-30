using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    [Collection("AnthropicEnv")]
    public class AiAssistantTests : IDisposable
    {
        private readonly AiTestEnv _env = new();
        private readonly FakeMicaData _data = new();
        private readonly ScriptedChatClient _model = new();
        private readonly AiConversation _conversation = new();
        private readonly UsageMeter _usage;
        private int _limit = 100;

        public AiAssistantTests()
        {
            _usage = new UsageMeter(_env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 12, 0, 0));
        }

        public void Dispose() => _env.Dispose();

        private MicaTools Tools() => new(_data, new Redactor(@"C:\Users\alice", "alice", "DESK-7"));

        private AiAssistant Assistant(bool isClaude = false, IChatClient? client = null, TimeSpan? silence = null) =>
            new(client ?? _model, isClaude, Tools(), _usage,
                new AiAssistantOptions { DailyLimit = () => _limit, InactivityTimeout = silence ?? TimeSpan.FromSeconds(60) });

        private async Task<List<AssistantUpdate>> AskAsync(AiAssistant assistant, string question, CancellationToken ct = default)
        {
            var updates = new List<AssistantUpdate>();
            await foreach (AssistantUpdate update in assistant.AskAsync(_conversation, question, ct)) updates.Add(update);
            // Every question ends with exactly one Done, whatever happened before it.
            Assert.Equal(AssistantUpdateKind.Done, updates[^1].Kind);
            Assert.Single(updates, u => u.Kind == AssistantUpdateKind.Done);
            return updates;
        }

        private static string TextOf(IEnumerable<AssistantUpdate> updates) =>
            string.Concat(updates.Where(u => u.Kind == AssistantUpdateKind.Text).Select(u => u.Text));

        private static IEnumerable<AIContent> Contents(ScriptedChatClient.Request request) =>
            request.Messages.SelectMany(m => m.Contents);

        // ----- the tool loop -----------------------------------------------------------------

        [Fact]
        public async Task A_question_runs_a_tool_and_streams_the_answer()
        {
            _model.Call(ToolNames.GetLiveStatus).Reply("CPU is fine.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "How is my CPU?");

            AssistantUpdate used = Assert.Single(updates, u => u.Kind == AssistantUpdateKind.ToolUsed);
            Assert.Equal(ToolNames.GetLiveStatus, used.ToolName);
            Assert.Equal("{}", used.ToolArgs);
            Assert.Equal("CPU is fine.", TextOf(updates));
            Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant },
                _conversation.Messages.Select(m => m.Role));
            Assert.Equal("How is my CPU?", _conversation.Messages[0].Text);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task Tool_results_reach_the_model_as_json_not_as_a_quoted_string()
        {
            _model.Call(ToolNames.GetLiveStatus).Reply("Fine.");

            await AskAsync(Assistant(), "CPU?");

            FunctionResultContent result = Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single();
            JsonElement json = Assert.IsType<JsonElement>(result.Result);
            Assert.Equal(JsonValueKind.Object, json.ValueKind);
            Assert.Equal(12.3, json.GetProperty("cpu").GetProperty("usagePercent").GetDouble());
        }

        [Fact]
        public async Task Every_request_carries_the_system_prompt_the_tools_and_the_output_cap()
        {
            _model.Reply("Hello.");

            await AskAsync(Assistant(), "Hi");

            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(ChatRole.System, request.Messages[0].Role);
            Assert.Equal(AiPrompts.System, request.Messages[0].Text);
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction), request.ToolNames);
            Assert.Equal(2000, request.Options!.MaxOutputTokens);
            Assert.DoesNotContain(_conversation.Messages, m => m.Role == ChatRole.System);
        }

        [Fact]
        public async Task The_previous_exchange_is_sent_with_the_next_question()
        {
            _model.Reply("First.").Reply("Second.");
            AiAssistant assistant = Assistant();

            await AskAsync(assistant, "One?");
            await AskAsync(assistant, "Two?");

            Assert.Equal(new[] { "", "One?", "First.", "Two?" },
                _model.Requests[1].Messages.Select(m => m.Role == ChatRole.System ? "" : m.Text));
            Assert.Equal(2, _usage.UsedToday);
        }

        [Fact]
        public async Task After_eight_tool_rounds_the_model_must_answer_without_tools()
        {
            _model.Otherwise = request => request.ToolNames.Count > 0
                ? _model.CallMessage(ToolNames.GetLiveStatus)
                : new ChatMessage(ChatRole.Assistant, "Final answer.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Watch it closely");

            Assert.Equal(9, _model.Requests.Count);
            Assert.All(_model.Requests.Take(8), r => Assert.Equal(10, r.ToolNames.Count));
            ScriptedChatClient.Request last = _model.Requests[8];
            Assert.Empty(last.ToolNames);
            Assert.DoesNotContain(Contents(last), c => c is FunctionCallContent or FunctionResultContent);
            Assert.Equal(AiPrompts.ToolLimitReached, last.Messages[^1].Text);
            Assert.Equal(8, _data.LatestCalls);
            Assert.Equal(8, updates.Count(u => u.Kind == AssistantUpdateKind.ToolUsed));
            Assert.Equal("Final answer.", TextOf(updates));
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task A_model_that_never_stops_asking_for_tools_still_ends_with_an_answer()
        {
            _model.Otherwise = _ => _model.CallMessage(ToolNames.GetLiveStatus);

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Loop");

            Assert.Equal(AiAssistant.NoAnswer, TextOf(updates));
            var answered = new HashSet<string>(_conversation.Messages.SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().Select(r => r.CallId));
            Assert.All(_conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
                call => Assert.Contains(call.CallId, answered));
            Assert.Equal(AiAssistant.NoAnswer, _conversation.Messages[^1].Text);
        }

        // ----- limits and failures -----------------------------------------------------------

        [Fact]
        public async Task The_daily_limit_stops_a_question_before_any_request()
        {
            _limit = 1;
            _model.Reply("One.");
            AiAssistant assistant = Assistant();
            await AskAsync(assistant, "First");

            List<AssistantUpdate> updates = await AskAsync(assistant, "Second");

            AssistantUpdate error = updates[0];
            Assert.Equal(AssistantUpdateKind.Error, error.Kind);
            Assert.Contains("daily limit set in Settings > AI", error.Text);
            Assert.Equal(2, updates.Count);
            Assert.Single(_model.Requests);
            Assert.Equal(new[] { "First", "One." }, _conversation.Messages.Select(m => m.Text));
        }

        [Fact]
        public async Task A_provider_failure_is_one_error_and_the_question_is_not_kept()
        {
            _model.Fail(new HttpRequestException("No such host is known."));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU?");

            Assert.Equal(new[] { AssistantUpdateKind.Error, AssistantUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal(AiErrorText.Unreachable, updates[0].Text);
            Assert.Empty(_conversation.Messages);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task A_failure_after_a_tool_round_leaves_nothing_behind()
        {
            _model.Call(ToolNames.GetLiveStatus).Fail(new HttpRequestException("Connection reset."));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU?");

            Assert.Contains(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task Cancelling_ends_quietly_and_forgets_the_question()
        {
            _model.Hang();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Slow question", cts.Token);

            Assert.Single(updates);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task A_silent_model_ends_with_the_timeout_sentence_and_forgets_the_question()
        {
            _model.Hang();

            List<AssistantUpdate> updates = await AskAsync(Assistant(silence: TimeSpan.FromMilliseconds(200)), "Slow question");

            Assert.Equal(new[] { AssistantUpdateKind.Error, AssistantUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal(AiErrorText.TimedOut, updates[0].Text);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task A_slow_model_that_keeps_answering_within_the_deadline_finishes()
        {
            TimeSpan pause = TimeSpan.FromMilliseconds(250);
            _model.Slow(pause, tool: ToolNames.GetLiveStatus).Slow(pause, text: "Worth the wait.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(silence: TimeSpan.FromMilliseconds(600)), "Slow but steady");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Equal("Worth the wait.", TextOf(updates));
        }

        [Fact]
        public async Task Bad_tool_arguments_are_reported_to_the_model_which_can_try_again()
        {
            _model.Call(ToolNames.GetHistory).Call(ToolNames.GetHistory)
                  .Call(ToolNames.GetHistory, new Dictionary<string, object?> { ["metric"] = "cpu", ["from"] = "-1h" })
                  .Reply("Here is the history.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU history?");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Equal("Here is the history.", TextOf(updates));
            FunctionResultContent failed = Contents(_model.Requests[1]).OfType<FunctionResultContent>().First();
            Assert.Contains("metric", failed.Result?.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_network_failure_through_the_real_openai_sdk_reads_as_unreachable()
        {
            var handler = new ScriptedHttpHandler(_ => throw new HttpRequestException("No such host is known."));
            var config = new AppConfig
            {
                AiProvider = AiProviders.OpenAiCompatible,
                AiCompatibleBaseUrl = "http://localhost:11434/v1",
                AiCompatibleModel = "gemma:2b",
            };
            AiClientResult local = AiProviderFactory.Create(config, new SecretStore(_env.PathOf("secrets.bin"), _ => { }), handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(client: local.Client), "How is my PC?");

            Assert.Equal(AssistantUpdateKind.Error, updates[0].Kind);
            Assert.Equal(AiErrorText.Unreachable, updates[0].Text);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task An_empty_question_is_refused_without_counting()
        {
            List<AssistantUpdate> updates = await AskAsync(Assistant(), "   ");

            Assert.Equal(AiAssistant.EmptyQuestion, updates[0].Text);
            Assert.Equal(0, _usage.UsedToday);
            Assert.Empty(_model.Requests);
        }

        // ----- suggest_action ----------------------------------------------------------------

        [Fact]
        public async Task Suggest_action_records_a_button_and_does_nothing_else()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?>
            {
                ["kind"] = "end_process",
                ["pid"] = 4242,
                ["createTime"] = 134037504000000000L,
                ["processName"] = "chrome.exe",
                ["reason"] = "It uses most of the CPU.",
            }).Reply("You could end chrome.exe.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "What is slowing me down?");

            SuggestedAction action = Assert.Single(updates, u => u.Kind == AssistantUpdateKind.Suggestion).Suggestion!;
            Assert.Equal(new SuggestedAction(SuggestedActionKind.EndProcess, "End chrome.exe", "It uses most of the CPU.",
                4242, 134037504000000000L, "chrome.exe"), action);
            Assert.Equal(action, Assert.Single(_conversation.Suggestions));
            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.ToolUsed);
            JsonElement result = (JsonElement)Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result!;
            Assert.True(result.GetProperty("recorded").GetBoolean());
            Assert.Equal(0, _data.LatestCalls);
            Assert.Null(_data.LastTopRequest);
        }

        [Fact]
        public async Task A_suggestion_without_its_target_is_refused_to_the_model()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "end_process", ["reason"] = "Busy." })
                  .Reply("I could not suggest that.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "End it");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Suggestion);
            JsonElement result = (JsonElement)Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result!;
            Assert.Contains("pid and createTime", result.GetProperty("error").GetString());
        }

        [Fact]
        public async Task Suggestions_are_cleared_when_the_next_question_starts()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "open_diagnostics", ["reason"] = "See the reports." })
                  .Reply("Open Diagnostics.")
                  .Reply("Nothing else.");
            AiAssistant assistant = Assistant();
            await AskAsync(assistant, "Where are my reports?");
            Assert.Equal("Open Diagnostics", Assert.Single(_conversation.Suggestions).Label);

            await AskAsync(assistant, "Thanks");

            Assert.Empty(_conversation.Suggestions);
        }

        // ----- limited mode ------------------------------------------------------------------

        [Fact]
        public async Task An_endpoint_without_tools_answers_in_limited_mode_from_a_live_snapshot()
        {
            _data.Processes.Add(new ProcessInfo("chrome.exe", null, 4242, 1, 80f, 900, 0));
            _model.Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: CPU 12%.")
                  .Reply("Still limited.");
            AiAssistant assistant = Assistant();

            List<AssistantUpdate> first = await AskAsync(assistant, "How is my PC?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, first[0].Kind);
            Assert.Equal("From the snapshot: CPU 12%.", TextOf(first));
            ScriptedChatClient.Request retry = _model.Requests[1];
            Assert.Empty(retry.ToolNames);
            string sent = retry.Messages[^1].Text;
            Assert.StartsWith("How is my PC?", sent);
            Assert.Contains("Limited mode", sent);
            Assert.Contains("usagePercent", sent);
            Assert.Contains("chrome.exe", sent);
            Assert.Equal(new[] { "How is my PC?", "From the snapshot: CPU 12%." }, _conversation.Messages.Select(m => m.Text));
            Assert.Equal(1, _usage.UsedToday);

            List<AssistantUpdate> second = await AskAsync(assistant, "And now?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, second[0].Kind);
            Assert.Equal(3, _model.Requests.Count);
            Assert.Equal(2, _usage.UsedToday);
        }

        [Fact]
        public async Task Claude_never_falls_back_to_limited_mode()
        {
            _model.Fail(new ClientResultException("tools are not supported"));

            List<AssistantUpdate> updates = await AskAsync(Assistant(isClaude: true), "CPU?");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.LimitedMode);
            Assert.Equal(AssistantUpdateKind.Error, updates[0].Kind);
            Assert.Empty(_conversation.Messages);
        }

        // ----- through the real SDKs, offline ------------------------------------------------

        [Fact]
        public async Task Claude_runs_the_tool_loop_with_a_cached_system_prompt()
        {
            var handler = new ScriptedHttpHandler(sent => (HttpStatusCode.OK, "text/event-stream",
                sent.Body.Contains("tool_result", StringComparison.Ordinal)
                    ? ScriptedHttpHandler.ClaudeStreamText
                    : ScriptedHttpHandler.ClaudeStreamToolUse));
            var secrets = new SecretStore(_env.PathOf("secrets.bin"), _ => { });
            secrets.Set(SecretNames.ClaudeKey, "sk-ant-test");
            AiClientResult claude = AiProviderFactory.Create(new AppConfig(), secrets, handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(isClaude: true, client: claude.Client), "How is my CPU?");

            Assert.Equal(ToolNames.GetLiveStatus, Assert.Single(updates, u => u.Kind == AssistantUpdateKind.ToolUsed).ToolName);
            Assert.Equal("Streamed ok", TextOf(updates));
            Assert.Equal(2, handler.Requests.Count);
            JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!;
            JsonNode system = first["system"]![0]!;
            Assert.Equal(AiPrompts.System, system["text"]!.GetValue<string>());
            Assert.Equal("1h", system["cache_control"]!["ttl"]!.GetValue<string>());
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction),
                first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
            Assert.True(first["stream"]!.GetValue<bool>());
            JsonNode toolResult = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray()
                .SelectMany(m => m!["content"]!.AsArray())
                .First(c => c!["type"]!.GetValue<string>() == "tool_result")!;
            JsonNode sentResult = JsonNode.Parse(toolResult["content"]!.GetValue<string>())!;
            Assert.Equal(12.3, sentResult["cpu"]!["usagePercent"]!.GetValue<double>());
        }

        [Fact]
        public async Task A_local_model_without_tools_answers_in_limited_mode_through_the_real_sdk()
        {
            var handler = new ScriptedHttpHandler(sent => JsonNode.Parse(sent.Body)!["tools"] != null
                ? (HttpStatusCode.BadRequest, "application/json", ScriptedHttpHandler.OllamaNoTools)
                : (HttpStatusCode.OK, "text/event-stream", ScriptedHttpHandler.OpenAiStreamText));
            var config = new AppConfig
            {
                AiProvider = AiProviders.OpenAiCompatible,
                AiCompatibleBaseUrl = "http://localhost:11434/v1",
                AiCompatibleModel = "gemma:2b",
            };
            AiClientResult local = AiProviderFactory.Create(config, new SecretStore(_env.PathOf("secrets.bin"), _ => { }), handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(client: local.Client), "How is my PC?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, updates[0].Kind);
            Assert.Equal("Streamed", TextOf(updates));
            Assert.Equal(2, handler.Requests.Count);
            JsonNode retry = JsonNode.Parse(handler.Requests[1].Body)!;
            Assert.Null(retry["tools"]);
            Assert.Equal(2000, retry["max_tokens"]!.GetValue<int>());
            string question = retry["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            Assert.StartsWith("How is my PC?", question, StringComparison.Ordinal);
            Assert.Contains("Limited mode", question, StringComparison.Ordinal);
        }
    }
}
