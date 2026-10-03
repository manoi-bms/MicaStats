using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    public enum PadAiUpdateKind { Text, Error, CutShort, Done }

    /// <summary>One step of a MicaPad AI request: a piece of text, a worded error, a cut-short notice, or the end.</summary>
    public sealed record PadAiUpdate(PadAiUpdateKind Kind, string? Text = null);

    /// <summary>
    /// One streaming request to the LLM provider of Settings > AI, with the shared key and the
    /// shared daily count. <see cref="RunAsync"/> never throws for a provider failure or a
    /// cancellation: every run ends with exactly one <see cref="PadAiUpdateKind.Done"/>, after an
    /// <see cref="PadAiUpdateKind.Error"/> when it failed. A caller cancel yields no Error.
    /// </summary>
    public sealed class PadAiRunner
    {
        internal const int MaxOutputTokens = 4096;

        private static readonly PadAiUpdate Done = new(PadAiUpdateKind.Done);

        private readonly Func<AiClientResult> _client;
        private readonly UsageMeter _usage;
        private readonly Func<int> _dailyLimit;
        private readonly TimeSpan _silence;

        public PadAiRunner(Func<AiClientResult> client, UsageMeter usage, Func<int> dailyLimit, TimeSpan? silence = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _usage = usage ?? throw new ArgumentNullException(nameof(usage));
            _dailyLimit = dailyLimit ?? throw new ArgumentNullException(nameof(dailyLimit));
            _silence = silence ?? TimeSpan.FromSeconds(60);
        }

        public async IAsyncEnumerable<PadAiUpdate> RunAsync(string userMessage, [EnumeratorCancellation] CancellationToken ct)
        {
            AiClientResult? result = null;
            Exception? setupFailure = null;
            try
            {
                result = _client();
            }
            catch (Exception ex)
            {
                setupFailure = ex;
            }
            if (result == null)
            {
                DiagnosticsLog.Log("pad", "AI request failed: " + (setupFailure?.GetType().Name ?? "NoClient"));
                yield return new PadAiUpdate(PadAiUpdateKind.Error,
                    setupFailure != null ? AiErrorText.Describe(setupFailure) : "The AI provider could not be set up. Check Settings > AI.");
                yield return Done;
                yield break;
            }

            IChatClient? client = result.Client;
            if (client == null)
            {
                yield return new PadAiUpdate(PadAiUpdateKind.Error,
                    result.Problem ?? "The AI provider could not be set up. Check Settings > AI.");
                yield return Done;
                yield break;
            }

            try
            {
                // Checked before the count, so a request already cancelled spends no use.
                if (ct.IsCancellationRequested)
                {
                    yield return Done;
                    yield break;
                }

                int limit = 1;
                bool allowed = false;
                Exception? gateFailure = null;
                try
                {
                    limit = Math.Max(1, _dailyLimit());
                    allowed = _usage.TryConsume(limit);
                }
                catch (Exception ex)
                {
                    gateFailure = ex;
                }
                if (gateFailure != null)
                {
                    DiagnosticsLog.Log("pad", "AI request failed: " + gateFailure.GetType().Name);
                    yield return new PadAiUpdate(PadAiUpdateKind.Error, AiErrorText.Describe(gateFailure));
                    yield return Done;
                    yield break;
                }
                if (!allowed)
                {
                    yield return new PadAiUpdate(PadAiUpdateKind.Error, AiAssistant.LimitText(limit));
                    yield return Done;
                    yield break;
                }

                var messages = new List<ChatMessage>
                {
                    result.IsClaude
                        ? ClaudeCache.SystemMessage(PadAiPrompts.System)
                        : new ChatMessage(ChatRole.System, PadAiPrompts.System),
                    new ChatMessage(ChatRole.User, userMessage ?? ""),
                };

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(_silence);
                CancellationToken token = deadline.Token;

                Exception? failure = null;
                bool cutShort = false;
                (IAsyncEnumerator<ChatResponseUpdate>? stream, Exception? openError) = Open(client, messages, token);
                if (stream == null) failure = openError;
                else
                {
                    try
                    {
                        while (true)
                        {
                            (bool moved, Exception? error) = await MoveAsync(stream);
                            if (error != null)
                            {
                                failure = error;
                                break;
                            }
                            if (!moved) break;
                            deadline.CancelAfter(_silence);

                            ChatResponseUpdate update = stream.Current;
                            if (update.FinishReason == ChatFinishReason.Length) cutShort = true;
                            if (!string.IsNullOrEmpty(update.Text))
                                yield return new PadAiUpdate(PadAiUpdateKind.Text, update.Text);
                        }
                    }
                    finally
                    {
                        await DisposeQuietly(stream);
                    }
                }

                bool timedOut = !ct.IsCancellationRequested && deadline.IsCancellationRequested;
                if (failure != null)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        if (!timedOut)
                            DiagnosticsLog.Log("pad", "AI request failed: " + failure.GetType().Name);
                        yield return new PadAiUpdate(PadAiUpdateKind.Error,
                            timedOut ? AiErrorText.TimedOut : AiErrorText.Describe(failure));
                    }
                }
                else if (timedOut)
                {
                    // Some enumerators stop quietly when cancelled; that is not a finished answer.
                    yield return new PadAiUpdate(PadAiUpdateKind.Error, AiErrorText.TimedOut);
                }
                else if (cutShort && !ct.IsCancellationRequested)
                {
                    yield return new PadAiUpdate(PadAiUpdateKind.CutShort);
                }
                yield return Done;
            }
            finally
            {
                try { client.Dispose(); } catch (Exception) { }
            }
        }

        private static (IAsyncEnumerator<ChatResponseUpdate>? Stream, Exception? Error) Open(
            IChatClient client, List<ChatMessage> messages, CancellationToken token)
        {
            try
            {
                return (client.GetStreamingResponseAsync(messages, new ChatOptions { MaxOutputTokens = MaxOutputTokens }, token)
                              .GetAsyncEnumerator(token), null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        private static async Task<(bool Moved, Exception? Error)> MoveAsync(IAsyncEnumerator<ChatResponseUpdate> stream)
        {
            try
            {
                return (await stream.MoveNextAsync(), null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
        }

        private static async Task DisposeQuietly(IAsyncEnumerator<ChatResponseUpdate> stream)
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception)
            {
                // The request already failed or finished; a second error would only hide the first.
            }
        }
    }
}
