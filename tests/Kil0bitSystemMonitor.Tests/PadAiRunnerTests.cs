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
    public class PadAiRunnerTests : IDisposable
    {
        private readonly AiTestEnv _env = new();
        private readonly UsageMeter _usage;
        private readonly ScriptedChatClient _model = new();
        private int _limit = 100;

        public PadAiRunnerTests()
        {
            _usage = new UsageMeter(_env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 12, 0, 0));
        }

        public void Dispose() => _env.Dispose();

        private PadAiRunner Runner(IChatClient? client = null, TimeSpan? silence = null) =>
            new(() => new AiClientResult(client ?? _model, null, false), _usage, () => _limit, silence);

        private static async Task<List<PadAiUpdate>> RunAsync(PadAiRunner runner, string message = "hello", CancellationToken ct = default)
        {
            var list = new List<PadAiUpdate>();
            await foreach (PadAiUpdate u in runner.RunAsync(message, ct)) list.Add(u);
            return list;
        }

        [Fact]
        public async Task Text_streams_in_order_then_one_Done_with_the_system_and_user_messages()
        {
            _model.Reply("abcdef");
            List<PadAiUpdate> updates = await RunAsync(Runner(), "the user message");

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Text, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal("abcdef", string.Concat(updates.Where(u => u.Kind == PadAiUpdateKind.Text).Select(u => u.Text)));

            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(2, request.Messages.Count);
            Assert.Equal(ChatRole.System, request.Messages[0].Role);
            Assert.Equal(PadAiPrompts.System, request.Messages[0].Text);
            Assert.Equal(ChatRole.User, request.Messages[1].Role);
            Assert.Equal("the user message", request.Messages[1].Text);
            Assert.Equal(4096, request.Options?.MaxOutputTokens);
            Assert.Empty(request.ToolNames);
        }

        [Fact]
        public async Task A_client_problem_is_an_Error_makes_no_request_and_is_not_counted()
        {
            var runner = new PadAiRunner(() => new AiClientResult(null, "No key set.", false), _usage, () => _limit);
            List<PadAiUpdate> updates = await RunAsync(runner);

            Assert.Equal(2, updates.Count);
            Assert.Equal(PadAiUpdateKind.Error, updates[0].Kind);
            Assert.Equal("No key set.", updates[0].Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[1].Kind);
            Assert.Empty(_model.Requests);
            Assert.Equal(0, _usage.UsedToday);
        }

        [Fact]
        public async Task The_daily_limit_stops_the_second_run_without_a_request()
        {
            _limit = 1;
            _model.Reply("one");
            await RunAsync(Runner());
            List<PadAiUpdate> second = await RunAsync(Runner());

            Assert.Equal(PadAiUpdateKind.Error, second[0].Kind);
            Assert.Equal(AiAssistant.LimitText(1), second[0].Text);
            Assert.Equal(PadAiUpdateKind.Done, second[1].Kind);
            Assert.Equal(2, second.Count);
            Assert.Single(_model.Requests);
        }

        [Fact]
        public async Task A_provider_exception_is_a_worded_Error_then_Done()
        {
            var ex = new InvalidOperationException("boom");
            _model.Fail(ex);
            List<PadAiUpdate> updates = await RunAsync(Runner());

            Assert.Equal(2, updates.Count);
            Assert.Equal(PadAiUpdateKind.Error, updates[0].Kind);
            Assert.Equal(AiErrorText.Describe(ex), updates[0].Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[1].Kind);
        }

        [Fact]
        public async Task A_silent_model_times_out()
        {
            _model.Hang();
            List<PadAiUpdate> updates = await RunAsync(Runner(silence: TimeSpan.FromMilliseconds(50)));

            Assert.Equal(2, updates.Count);
            Assert.Equal(PadAiUpdateKind.Error, updates[0].Kind);
            Assert.Equal(AiErrorText.TimedOut, updates[0].Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[1].Kind);
        }

        [Fact]
        public async Task Cancelling_gives_only_Done()
        {
            _model.Hang();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);
            List<PadAiUpdate> updates = await RunAsync(Runner(silence: TimeSpan.FromSeconds(5)), ct: cts.Token);

            PadAiUpdate only = Assert.Single(updates);
            Assert.Equal(PadAiUpdateKind.Done, only.Kind);
            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);
        }

        [Fact]
        public async Task A_synchronous_throw_from_the_client_is_an_Error_then_Done()
        {
            var client = new LengthClient { ThrowSync = true };
            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(2, updates.Count);
            Assert.Equal(PadAiUpdateKind.Error, updates[0].Kind);
            Assert.Equal(AiErrorText.Describe(new InvalidOperationException("sync")), updates[0].Text);
            Assert.Equal(PadAiUpdateKind.Done, updates[1].Kind);
            Assert.True(client.Disposed);
        }

        [Fact]
        public async Task A_throwing_daily_limit_is_an_Error_then_Done_and_the_client_is_disposed()
        {
            var client = new LengthClient();
            var runner = new PadAiRunner(() => new AiClientResult(client, null, false), _usage,
                                         () => throw new InvalidOperationException("limit"));
            List<PadAiUpdate> updates = await RunAsync(runner);

            Assert.Equal(new[] { PadAiUpdateKind.Error, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.True(client.Disposed);
        }

        [Fact]
        public async Task An_already_cancelled_token_spends_nothing_and_makes_no_request()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var client = new LengthClient();
            List<PadAiUpdate> updates = await RunAsync(Runner(client), ct: cts.Token);

            PadAiUpdate only = Assert.Single(updates);
            Assert.Equal(PadAiUpdateKind.Done, only.Kind);
            Assert.Equal(0, _usage.UsedToday);
            Assert.False(client.Streamed);
            Assert.True(client.Disposed);
        }

        [Fact]
        public async Task A_stream_that_ends_quietly_after_the_deadline_is_a_timeout()
        {
            var client = new LengthClient { QuietEnd = true };
            List<PadAiUpdate> updates = await RunAsync(Runner(client, TimeSpan.FromMilliseconds(50)));

            Assert.Equal(new[] { PadAiUpdateKind.Error, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal(AiErrorText.TimedOut, updates[0].Text);
        }

        [Fact]
        public async Task Every_update_re_arms_the_silence_deadline()
        {
            var client = new LengthClient { Chunks = 8, Gap = TimeSpan.FromMilliseconds(30) };
            List<PadAiUpdate> updates = await RunAsync(Runner(client, TimeSpan.FromMilliseconds(300)));

            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);
            Assert.Equal(8, updates.Count(u => u.Kind == PadAiUpdateKind.Text));
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
        }

        [Fact]
        public async Task The_client_is_disposed_when_the_daily_limit_stops_the_run()
        {
            _limit = 1;
            _usage.TryConsume(1);
            var client = new LengthClient();
            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(PadAiUpdateKind.Error, updates[0].Kind);
            Assert.True(client.Disposed);
            Assert.False(client.Streamed);
        }

        [Fact]
        public async Task A_Claude_client_still_gets_two_messages_with_the_system_text()
        {
            _model.Reply("hi there");
            var runner = new PadAiRunner(() => new AiClientResult(_model, null, true), _usage, () => _limit);
            await RunAsync(runner, "question");

            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(2, request.Messages.Count);
            Assert.Equal(PadAiPrompts.System, request.Messages[0].Text);
            Assert.Equal("question", request.Messages[1].Text);
        }

        [Fact]
        public async Task A_length_finish_reason_gives_CutShort_after_the_text_and_before_Done()
        {
            List<PadAiUpdate> updates = await RunAsync(Runner(new LengthClient()));

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Text, PadAiUpdateKind.CutShort, PadAiUpdateKind.Done },
                         updates.Select(u => u.Kind));
            Assert.Equal("ab", string.Concat(updates.Where(u => u.Kind == PadAiUpdateKind.Text).Select(u => u.Text)));
        }

        [Fact]
        public async Task A_failed_run_still_counts_one_use_and_the_client_is_disposed()
        {
            var client = new LengthClient { Throw = true };
            await RunAsync(Runner(client));

            Assert.Equal(1, _usage.UsedToday);
            Assert.True(client.Disposed);
        }

        private sealed class LengthClient : IChatClient
        {
            public bool Throw { get; init; }
            public bool ThrowSync { get; init; }
            public bool QuietEnd { get; init; }
            public int Chunks { get; init; }
            public TimeSpan Gap { get; init; }
            public bool Disposed { get; private set; }
            public bool Streamed { get; private set; }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                       CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Streamed = true;
                if (ThrowSync) throw new InvalidOperationException("sync");
                return Stream(cancellationToken);
            }

            private async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                if (Throw) throw new InvalidOperationException("fail");
                if (QuietEnd)
                {
                    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                    yield break;
                }
                for (int i = 0; i < Chunks; i++)
                {
                    await Task.Delay(Gap, ct);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "x");
                }
                if (Chunks > 0) yield break;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "a");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "b") { FinishReason = ChatFinishReason.Length };
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() => Disposed = true;
        }
    }
}
