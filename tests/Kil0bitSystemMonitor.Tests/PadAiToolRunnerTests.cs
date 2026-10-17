using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class PadAiToolRunnerTests : IDisposable
    {
        private readonly AiTestEnv _env = new();
        private readonly UsageMeter _usage;
        private readonly ScriptedChatClient _model = new();

        public PadAiToolRunnerTests()
        {
            _usage = new UsageMeter(_env.PathOf("ai-tool-usage.json"), () => new DateTime(2026, 10, 8, 12, 0, 0));
        }

        public void Dispose() => _env.Dispose();

        private PadAiRunner Runner(IChatClient? client = null, TimeSpan? silence = null) =>
            new(() => new AiClientResult(client ?? _model, null, false), _usage, () => 100, silence);

        private static AIFunction Tool(Delegate method, string name) =>
            AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name, Description = "Test tool." });

        private static async Task<List<PadAiUpdate>> RunAsync(PadAiRunner runner, IReadOnlyList<AITool>? tools = null,
                                                               CancellationToken ct = default,
                                                               string? additionalSystemPrompt = null)
        {
            var updates = new List<PadAiUpdate>();
            await foreach (PadAiUpdate update in runner.RunAsync("help", ct, tools, additionalSystemPrompt))
                updates.Add(update);
            return updates;
        }

        [Fact]
        public async Task A_tool_is_invoked_then_its_result_is_sent_to_the_model_for_the_answer()
        {
            int calls = 0;
            AIFunction lookup = Tool((Func<string, string>)(query =>
            {
                calls++;
                return "private result for " + query;
            }), "lookup_note");
            _model.Call("lookup_note", new Dictionary<string, object?> { ["query"] = "secret phrase" })
                  .Reply("Useful answer.");

            List<PadAiUpdate> updates = await RunAsync(
                Runner(), new AITool[] { lookup }, additionalSystemPrompt: "Use note tools only when needed.");

            Assert.Equal(1, calls);
            Assert.Equal(2, _model.Requests.Count);
            Assert.Equal("lookup_note", Assert.Single(updates, u => u.Kind == PadAiUpdateKind.Tool).Text);
            Assert.Equal("Useful answer.", string.Concat(updates.Where(u => u.Kind == PadAiUpdateKind.Text).Select(u => u.Text)));
            Assert.Contains(_model.Requests[1].Messages.SelectMany(m => m.Contents), c => c is FunctionResultContent);
            Assert.EndsWith("\n\nUse note tools only when needed.", _model.Requests[0].Messages[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain(updates, u => (u.Text ?? "").Contains("secret phrase", StringComparison.Ordinal));
            Assert.DoesNotContain(updates, u => (u.Text ?? "").Contains("private result", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Eight_tool_rounds_are_followed_by_one_tool_free_final_answer_request()
        {
            int calls = 0;
            AIFunction count = Tool((Func<int>)(() => ++calls), "count");
            _model.Otherwise = request => request.ToolNames.Count > 0
                ? _model.CallMessage("count")
                : new ChatMessage(ChatRole.Assistant, "Final answer.");

            List<PadAiUpdate> updates = await RunAsync(Runner(), new AITool[] { count });

            Assert.Equal(9, _model.Requests.Count);
            Assert.All(_model.Requests.Take(8), request => Assert.Equal(new[] { "count" }, request.ToolNames));
            Assert.Empty(_model.Requests[8].ToolNames);
            Assert.Equal(AiPrompts.ToolLimitReached, _model.Requests[8].Messages[^1].Text);
            Assert.Equal(8, calls);
            Assert.Equal(8, updates.Count(u => u.Kind == PadAiUpdateKind.Tool));
            Assert.Equal("Final answer.", string.Concat(updates.Where(u => u.Kind == PadAiUpdateKind.Text).Select(u => u.Text)));
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task An_unknown_tool_call_terminates_as_a_controlled_error_without_invocation_or_another_request()
        {
            int calls = 0;
            AIFunction known = Tool((Func<int>)(() => ++calls), "known");
            _model.Call("made_up_tool").Reply("must not be requested");

            List<PadAiUpdate> updates = await RunAsync(Runner(), new AITool[] { known });

            Assert.Single(_model.Requests);
            Assert.Equal(0, calls);
            Assert.DoesNotContain(updates, u => u.Kind is PadAiUpdateKind.Tool or PadAiUpdateKind.CutShort);
            Assert.Equal(PadAiRunner.ToolFailed, Assert.Single(updates, u => u.Kind == PadAiUpdateKind.Error).Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
        }

        [Fact]
        public async Task An_unknown_call_after_a_valid_tool_marks_the_whole_run_failed()
        {
            int calls = 0;
            AIFunction known = Tool((Func<int>)(() => ++calls), "known");
            _model.Call("known").Call("made_up_tool").Reply("must not be requested");

            List<PadAiUpdate> updates = await RunAsync(Runner(), new AITool[] { known });

            Assert.Equal(2, _model.Requests.Count);
            Assert.Equal(1, calls);
            Assert.Equal("known", Assert.Single(updates, u => u.Kind == PadAiUpdateKind.Tool).Text);
            Assert.Equal(PadAiRunner.ToolFailed, Assert.Single(updates, u => u.Kind == PadAiUpdateKind.Error).Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
        }

        [Fact]
        public async Task Cancellation_during_a_tool_is_silent_and_still_ends_once()
        {
            static async Task<string> Wait(CancellationToken ct)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "never";
            }

            AIFunction wait = Tool((Func<CancellationToken, Task<string>>)Wait, "wait");
            _model.Call("wait");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            List<PadAiUpdate> updates = await RunAsync(Runner(silence: TimeSpan.FromSeconds(5)),
                                                        new AITool[] { wait }, cancelled.Token);

            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
            Assert.Single(updates, u => u.Kind == PadAiUpdateKind.Done);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task Two_tool_error_recoveries_are_allowed_and_the_third_failure_stops_without_exposing_details()
        {
            int calls = 0;
            AIFunction fail = Tool((Func<string>)(() =>
            {
                calls++;
                throw new InvalidOperationException("credential=purple-walrus");
            }), "fail_safely");
            // MEAI captures and returns two failed iterations so the model can recover. On the
            // third it invokes with exception capture off, so that failure escapes and no fourth
            // model request is made.
            _model.Call("fail_safely").Call("fail_safely").Call("fail_safely").Reply("must not be requested");

            List<PadAiUpdate> updates = await RunAsync(Runner(), new AITool[] { fail });

            Assert.Equal(3, calls);
            Assert.Equal(3, _model.Requests.Count);
            Assert.Equal(PadAiUpdateKind.Error, updates[^2].Kind);
            Assert.Equal(PadAiRunner.ToolFailed, updates[^2].Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
            Assert.DoesNotContain(updates, u => (u.Text ?? "").Contains("purple-walrus", StringComparison.Ordinal));
            FunctionResultContent[] failuresSeenByModel = _model.Requests[2].Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .ToArray();
            Assert.Equal(2, failuresSeenByModel.Length);
            Assert.All(failuresSeenByModel, failure =>
                Assert.DoesNotContain("purple-walrus", failure.Result?.ToString() ?? "", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ToolCalls_finish_reason_is_an_intermediate_round_not_a_cut_short_answer()
        {
            int calls = 0;
            AIFunction tool = Tool((Func<int>)(() => ++calls), "read");
            var model = new ToolFinishClient("read");

            List<PadAiUpdate> updates = await RunAsync(Runner(model), new AITool[] { tool });

            Assert.Equal(1, calls);
            Assert.Equal(2, model.Requests);
            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.CutShort);
            Assert.Equal("answer", string.Concat(updates.Where(u => u.Kind == PadAiUpdateKind.Text).Select(u => u.Text)));
        }

        [Fact]
        public async Task With_no_tools_the_original_two_message_streaming_request_is_unchanged()
        {
            _model.Reply("abcdef");

            List<PadAiUpdate> updates = await RunAsync(Runner());

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Text, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(PadAiPrompts.System, request.Messages[0].Text);
            Assert.Equal("help", request.Messages[1].Text);
            Assert.Empty(request.ToolNames);
            Assert.Equal(4096, request.Options?.MaxOutputTokens);
        }

        private sealed class ToolFinishClient : IChatClient
        {
            private readonly string _tool;

            public ToolFinishClient(string tool) => _tool = tool;

            public int Requests { get; private set; }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                        CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                Requests++;
                if (Requests == 1)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        new List<AIContent> { new FunctionCallContent("call_1", _tool) })
                    {
                        FinishReason = ChatFinishReason.ToolCalls,
                    };
                }
                else
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "answer")
                    {
                        FinishReason = ChatFinishReason.Stop,
                    };
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
