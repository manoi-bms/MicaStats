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
            var client = new LengthClient { Chunks = 10, Gap = TimeSpan.FromMilliseconds(50) };
            List<PadAiUpdate> updates = await RunAsync(Runner(client, TimeSpan.FromMilliseconds(250)));

            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);
            Assert.Equal(10, updates.Count(u => u.Kind == PadAiUpdateKind.Text));
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
        }

        [Fact]
        public async Task A_slow_consumer_does_not_trip_the_silence_deadline()
        {
            var client = new LengthClient { Chunks = 1, Gap = TimeSpan.Zero };
            var runner = Runner(client, TimeSpan.FromMilliseconds(100));
            var updates = new List<PadAiUpdate>();
            await foreach (PadAiUpdate u in runner.RunAsync("hello", CancellationToken.None))
            {
                updates.Add(u);
                if (u.Kind == PadAiUpdateKind.Text) await Task.Delay(300);
            }

            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);
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

        // ---- a reply the provider ended early (MicaPad AI spec 5) ---------------------------------

        private static ChatResponseUpdate Piece(string? text, ChatFinishReason? finish = null)
        {
            var update = text == null ? new ChatResponseUpdate { Role = ChatRole.Assistant } : new ChatResponseUpdate(ChatRole.Assistant, text);
            update.FinishReason = finish;
            return update;
        }

        private static string TextOf(IEnumerable<PadAiUpdate> updates) =>
            string.Concat(updates.Where(u => u.Kind == PadAiUpdateKind.Text).Select(u => u.Text));

        [Theory]
        [InlineData(true)]    // on the last piece of text
        [InlineData(false)]   // on a final piece with no text
        public async Task A_content_filter_after_partial_text_is_an_Error_and_never_a_clean_end(bool onText)
        {
            var client = onText
                ? new PiecesClient(Piece("Here is "), Piece("half", ChatFinishReason.ContentFilter))
                : new PiecesClient(Piece("Here is "), Piece("half"), Piece(null, ChatFinishReason.ContentFilter));

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Text, PadAiUpdateKind.Error, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal("Here is half", TextOf(updates));
            Assert.Equal("The AI provider stopped the reply (content filter).", updates[2].Text);
            Assert.Equal(PadAiRunner.StoppedByFilter, updates[2].Text);
        }

        [Fact]
        public async Task A_length_finish_reason_on_a_final_piece_with_no_text_gives_CutShort()
        {
            var client = new PiecesClient(Piece("a"), Piece("b"), Piece(null, ChatFinishReason.Length));

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Text, PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
        }

        [Theory]
        [InlineData("tool_calls")]
        [InlineData("something_new")]   // a reason this build has never heard of
        public async Task Any_other_finish_reason_that_is_not_stop_gives_CutShort(string reason)
        {
            var client = new PiecesClient(Piece("a"), Piece(null, new ChatFinishReason(reason)));

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
        }

        [Fact]
        public async Task A_stop_finish_reason_is_a_clean_end()
        {
            var client = new PiecesClient(Piece("a"), Piece("b", ChatFinishReason.Stop), Piece(null, ChatFinishReason.Stop));

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Text, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
        }

        [Fact]
        public async Task A_content_filter_counts_over_a_length_stop()
        {
            var client = new PiecesClient(Piece("a", ChatFinishReason.Length), Piece(null, ChatFinishReason.ContentFilter));

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.Error, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
        }

        [Fact]
        public async Task A_reply_that_passes_64000_characters_is_stopped_cancelled_and_cut_short()
        {
            Assert.Equal(64000, PadAiRunner.MaxReplyChars);
            // A provider that ignores the output limit: 200,000 characters, a thousand at a time.
            var client = new PiecesClient(Enumerable.Range(0, 200).Select(_ => Piece(new string('x', 1000))).ToArray());

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(64000, TextOf(updates).Length);                       // the pane is not flooded
            Assert.Equal(new[] { PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Skip(updates.Count - 2).Select(u => u.Kind));
            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);   // not a timeout: the runner stopped it
            Assert.InRange(client.Pulled, 64, 66);                             // it stopped reading
            Assert.True(client.Cancelled);                                     // and cancelled the request
            Assert.True(client.Disposed);
        }

        [Fact]
        public async Task One_huge_piece_is_cut_at_the_cap()
        {
            // 63,999 characters, then a character of two UTF-16 units across the cap, then far too much.
            var client = new PiecesClient(Piece(new string('x', 63999) + char.ConvertFromUtf32(0x1F600) + new string('y', 50000)), Piece("never read"));

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            string text = TextOf(updates);
            Assert.Equal(63999, text.Length);                                  // not half of the pair that straddles the cap
            Assert.DoesNotContain('y', text);
            Assert.Equal(new[] { PadAiUpdateKind.Text, PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Select(u => u.Kind));
        }

        [Fact]
        public async Task A_reply_of_exactly_64000_characters_that_ends_is_whole()
        {
            var client = new PiecesClient(Enumerable.Range(0, 64).Select(_ => Piece(new string('x', 1000))).ToArray());

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(64000, TextOf(updates).Length);
            Assert.Equal(PadAiUpdateKind.Done, updates[^1].Kind);
            Assert.DoesNotContain(updates, u => u.Kind is PadAiUpdateKind.CutShort or PadAiUpdateKind.Error);
            Assert.False(client.Cancelled);
        }

        [Fact]
        public async Task A_cancel_callback_that_throws_at_the_cap_never_escapes_the_runner()
        {
            var client = new PiecesClient(Enumerable.Range(0, 70).Select(_ => Piece(new string('x', 1000))).ToArray()) { ThrowOnCancel = true };

            List<PadAiUpdate> updates = await RunAsync(Runner(client));

            Assert.Equal(new[] { PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Skip(updates.Count - 2).Select(u => u.Kind));
        }

        // ---- the model's own limits (AI model limits spec 2.2) ---------------------------------------

        /// <summary>A window of 262,144 tokens: MicaPad asks for up to 32,000 tokens and keeps a reply of up to 128,000 characters.</summary>
        private static readonly AiBudget Large = AiBudget.For(262_144, 0, 0);

        /// <summary>A model that sends <paramref name="thousands"/> thousand characters, a thousand at a time.</summary>
        private static PiecesClient Thousands(int thousands) =>
            new(Enumerable.Range(0, thousands).Select(_ => Piece(new string('x', 1000))).ToArray());

        [Fact]
        public async Task The_request_carries_the_output_limit_the_runner_was_built_with()
        {
            _model.Reply("ok");
            var runner = new PadAiRunner(() => new AiClientResult(_model, null, false), _usage, () => _limit,
                                         maxOutputTokens: 32000, maxReplyChars: 128000);

            await RunAsync(runner);

            Assert.Equal(32000, Assert.Single(_model.Requests).Options?.MaxOutputTokens);
        }

        [Fact]
        public async Task A_runner_built_with_a_reply_cap_stops_a_reply_there()
        {
            PiecesClient client = Thousands(200);
            var runner = new PadAiRunner(() => new AiClientResult(client, null, false), _usage, () => _limit,
                                         maxOutputTokens: 32000, maxReplyChars: 128000);

            List<PadAiUpdate> updates = await RunAsync(runner);

            Assert.Equal(128000, TextOf(updates).Length);         // twice what the fixed cap let through
            Assert.Equal(new[] { PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Skip(updates.Count - 2).Select(u => u.Kind));
            Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.Error);
            Assert.InRange(client.Pulled, 128, 130);              // it stopped reading
            Assert.True(client.Cancelled);                        // and cancelled the request
        }

        [Fact]
        public async Task Within_a_budget_the_runner_asks_for_its_output_limit_and_stops_at_its_reply_cap()
        {
            Assert.Equal(32000, Large.PadOutputTokens);
            Assert.Equal(128000, Large.PadReplyChars);

            _model.Reply("ok");
            await RunAsync(Runner().Within(Large));
            Assert.Equal(32000, Assert.Single(_model.Requests).Options?.MaxOutputTokens);

            // 100,000 characters: over the fixed cap, under this budget's. The reply is whole.
            PiecesClient whole = Thousands(100);
            List<PadAiUpdate> updates = await RunAsync(Runner(whole).Within(Large));
            Assert.Equal(100000, TextOf(updates).Length);
            Assert.DoesNotContain(updates, u => u.Kind is PadAiUpdateKind.CutShort or PadAiUpdateKind.Error);
            Assert.False(whole.Cancelled);

            // 200,000: cut at the budget's 128,000.
            PiecesClient flood = Thousands(200);
            updates = await RunAsync(Runner(flood).Within(Large));
            Assert.Equal(128000, TextOf(updates).Length);
            Assert.Equal(new[] { PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Skip(updates.Count - 2).Select(u => u.Kind));
            Assert.True(flood.Cancelled);
        }

        [Fact]
        public async Task Within_a_small_window_the_runner_asks_for_less_than_the_fixed_limit()
        {
            AiBudget small = AiBudget.For(8192, 0, 0);
            Assert.Equal(2048, small.PadOutputTokens);
            _model.Reply("ok");

            await RunAsync(Runner().Within(small));

            Assert.Equal(2048, Assert.Single(_model.Requests).Options?.MaxOutputTokens);   // 4,096 would be half the window
        }

        [Fact]
        public async Task Within_a_budget_whose_model_reports_a_largest_output_the_runner_asks_for_no_more()
        {
            AiBudget capped = AiBudget.For(262_144, 8192, 0);
            Assert.Equal(8192, capped.PadOutputTokens);
            _model.Reply("ok");

            await RunAsync(Runner().Within(capped));

            Assert.Equal(8192, Assert.Single(_model.Requests).Options?.MaxOutputTokens);
        }

        [Fact]
        public async Task Within_the_standard_budget_or_none_the_limits_are_the_fixed_ones()
        {
            foreach (AiBudget? budget in new[] { AiBudget.Standard, null })
            {
                var model = new ScriptedChatClient().Reply("ok");
                await RunAsync(Runner(model).Within(budget));
                Assert.Equal(4096, Assert.Single(model.Requests).Options?.MaxOutputTokens);

                PiecesClient flood = Thousands(200);
                List<PadAiUpdate> updates = await RunAsync(Runner(flood).Within(budget));
                Assert.Equal(64000, TextOf(updates).Length);
                Assert.Equal(new[] { PadAiUpdateKind.CutShort, PadAiUpdateKind.Done }, updates.Skip(updates.Count - 2).Select(u => u.Kind));
            }
        }

        [Fact]
        public async Task A_limit_that_is_not_a_positive_number_is_the_fixed_one()
        {
            foreach (int bad in new[] { 0, -1, int.MinValue })
            {
                var model = new ScriptedChatClient().Reply("ok");
                var runner = new PadAiRunner(() => new AiClientResult(model, null, false), _usage, () => _limit,
                                             maxOutputTokens: bad, maxReplyChars: bad);
                List<PadAiUpdate> updates = await RunAsync(runner);

                Assert.Equal(4096, Assert.Single(model.Requests).Options?.MaxOutputTokens);
                Assert.Equal("ok", TextOf(updates));              // a cap of nothing would cut every reply at once
                Assert.DoesNotContain(updates, u => u.Kind == PadAiUpdateKind.CutShort);
            }
        }

        [Fact]
        public async Task Within_a_budget_the_runner_keeps_its_provider_its_daily_count_and_its_silence_deadline()
        {
            // The daily limit: one use, shared by the runner and the one sized from it.
            _limit = 1;
            _model.Reply("one");
            PadAiRunner first = Runner();
            await RunAsync(first.Within(Large));
            List<PadAiUpdate> second = await RunAsync(first);

            Assert.Equal(1, _usage.UsedToday);
            Assert.Equal(AiAssistant.LimitText(1), second[0].Text);
            Assert.Single(_model.Requests);                       // the same provider, asked once

            // The silence deadline.
            _limit = 100;
            _model.Hang();
            List<PadAiUpdate> silent = await RunAsync(Runner(silence: TimeSpan.FromMilliseconds(50)).Within(Large));
            Assert.Equal(AiErrorText.TimedOut, silent[0].Text);
        }

        /// <summary>A model that streams the pieces it is given, and tells how far the runner read and whether it cancelled.</summary>
        private sealed class PiecesClient : IChatClient
        {
            private readonly ChatResponseUpdate[] _pieces;

            public PiecesClient(params ChatResponseUpdate[] pieces) => _pieces = pieces;

            /// <summary>A callback on the request's token throws when the request is cancelled.</summary>
            public bool ThrowOnCancel { get; init; }

            /// <summary>How many pieces the runner asked for.</summary>
            public int Pulled { get; private set; }

            /// <summary>True once the request's token was cancelled.</summary>
            public bool Cancelled { get; private set; }

            public bool Disposed { get; private set; }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                       CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.Register(() =>
                {
                    Cancelled = true;
                    if (ThrowOnCancel) throw new InvalidOperationException("a cancel callback threw");
                });
                await Task.Yield();
                foreach (ChatResponseUpdate piece in _pieces)
                {
                    Pulled++;
                    yield return piece;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() => Disposed = true;
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
