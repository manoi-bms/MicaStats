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
    ///
    /// <para>
    /// A reply the provider ended early is never a clean end (MicaPad AI spec 5): the length
    /// limit, or any ending it names that is not a normal stop, is
    /// <see cref="PadAiUpdateKind.CutShort"/>; its content filter is an Error
    /// (<see cref="StoppedByFilter"/>). A reply that passes <see cref="MaxReplyChars"/> is
    /// stopped there, its request cancelled, and reported as cut short.
    /// </para>
    /// </summary>
    public sealed class PadAiRunner
    {
        internal const int MaxOutputTokens = 4096;
        internal const int MaxReplyChars = 64000;
        internal const string StoppedByFilter = "The AI provider stopped the reply (content filter).";

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

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(_silence);
                CancellationToken token = deadline.Token;

                Exception? failure = null;
                bool cutShort = false, filtered = false, capped = false;
                int length = 0;
                (IAsyncEnumerator<ChatResponseUpdate>? stream, Exception? openError) = Open(client, result.IsClaude, userMessage, token);
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
                            // On every update, with text or without: a provider may say why it stopped on a last, empty one.
                            if (update.FinishReason is { } reason)
                            {
                                if (reason == ChatFinishReason.ContentFilter) filtered = true;
                                else if (reason != ChatFinishReason.Stop) cutShort = true;   // the length limit, or anything else that is not a normal end
                            }

                            string? text = update.Text;
                            if (string.IsNullOrEmpty(text)) continue;

                            // The cap: a provider that ignores the output limit must not flood the pane.
                            string piece = Fitting(text, MaxReplyChars - length);
                            capped = piece.Length < text.Length;
                            if (piece.Length > 0)
                            {
                                // The clock covers the model's silence, not a slow consumer.
                                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                                yield return new PadAiUpdate(PadAiUpdateKind.Text, piece);
                                length += piece.Length;
                                deadline.CancelAfter(_silence);
                            }
                            if (capped)
                            {
                                CancelQuietly(deadline);   // nothing more is read: the request ends here
                                break;
                            }
                        }
                    }
                    finally
                    {
                        await DisposeQuietly(stream);
                    }
                }

                // At the cap the runner cancelled the request itself: that is a reply cut short, not a silent model.
                bool timedOut = !capped && !ct.IsCancellationRequested && deadline.IsCancellationRequested;
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
                else if (filtered && !ct.IsCancellationRequested)
                {
                    // The provider took the rest of the reply away: what came is not the answer.
                    DiagnosticsLog.Log("pad", "AI request failed: the provider stopped the reply (content filter)");
                    yield return new PadAiUpdate(PadAiUpdateKind.Error, StoppedByFilter);
                }
                else if ((cutShort || capped) && !ct.IsCancellationRequested)
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
            IChatClient client, bool isClaude, string? userMessage, CancellationToken token)
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    isClaude
                        ? ClaudeCache.SystemMessage(PadAiPrompts.System)
                        : new ChatMessage(ChatRole.System, PadAiPrompts.System),
                    new ChatMessage(ChatRole.User, userMessage ?? ""),
                };
                return (client.GetStreamingResponseAsync(messages, new ChatOptions { MaxOutputTokens = MaxOutputTokens }, token)
                              .GetAsyncEnumerator(token), null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        /// <summary>
        /// As much of <paramref name="text"/> as fits in <paramref name="room"/> characters, never
        /// ending on half of a character that takes two (a surrogate pair).
        /// </summary>
        private static string Fitting(string text, int room)
        {
            if (text.Length <= room) return text;
            if (room <= 0) return "";
            if (char.IsHighSurrogate(text[room - 1])) room--;
            return text.Substring(0, room);
        }

        /// <summary>Cancels the request. A callback on its token that throws must not come out of the runner, which never throws.</summary>
        private static void CancelQuietly(CancellationTokenSource source)
        {
            try
            {
                source.Cancel();
            }
            catch (Exception)
            {
                // The token is cancelled all the same; the reply is reported as cut short.
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
