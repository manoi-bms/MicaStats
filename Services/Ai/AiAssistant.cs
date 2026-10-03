using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>Limits for one <see cref="AiAssistant"/>.</summary>
    public sealed class AiAssistantOptions
    {
        /// <summary>Tool rounds per question; after the last one the model must answer without tools.</summary>
        public int MaxToolRounds { get; init; } = 8;

        /// <summary>Output cap per request, in tokens.</summary>
        public int MaxOutputTokens { get; init; } = 2000;

        /// <summary>Questions allowed per local day; read at each question so a Settings change applies at once.</summary>
        public Func<int> DailyLimit { get; init; } = () => 100;

        /// <summary>
        /// How long the model may stay silent before the question fails; re-armed by every update,
        /// because each SDK timeout covers one attempt and retries would otherwise multiply it.
        /// </summary>
        internal TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Answers one question at a time: adds the system prompt, offers the tools, runs the tool
    /// loop through <see cref="FunctionInvokingChatClient"/> and streams the answer as
    /// <see cref="AssistantUpdate"/>s.
    ///
    /// <para>
    /// <see cref="AskAsync"/> never throws for a provider failure or a cancellation: every
    /// question ends with exactly one <see cref="AssistantUpdateKind.Done"/>, after an
    /// <see cref="AssistantUpdateKind.Error"/> when it failed. A failed or cancelled question
    /// is taken back out of the conversation; the daily count keeps it. An endpoint that
    /// rejects tools (some local models) gets the question again without tools, with a live
    /// snapshot attached (<see cref="AssistantUpdateKind.LimitedMode"/>); Claude never does.
    /// </para>
    ///
    /// <para>
    /// The iterator resumes on the caller's context, so the conversation is changed on the
    /// thread that enumerates it (the UI thread in the Ask window).
    /// </para>
    /// </summary>
    public sealed class AiAssistant
    {
        internal const string EmptyQuestion = "Type a question first.";
        internal const string NoAnswer = "I could not finish an answer within the tool limit. Try a narrower question.";
        internal const string LimitedModeNote =
            "This AI endpoint cannot use MicaStats tools, so this answer comes from a snapshot of the PC taken now (limited mode).";

        private static readonly AssistantUpdate Done = new(AssistantUpdateKind.Done);

        private readonly IChatClient _client;
        private readonly IChatClient _toolClient;
        private readonly bool _isClaude;
        private readonly MicaTools _tools;
        private readonly UsageMeter _usage;
        private readonly AiAssistantOptions _options;
        private readonly IReadOnlyList<AIFunction> _readOnlyTools;
        private bool _toolsUnsupported;

        /// <summary>
        /// Creates the assistant over a provider client (<see cref="AiProviderFactory"/>);
        /// <paramref name="isClaude"/> turns on prompt caching and turns off the limited-mode fallback.
        /// </summary>
        public AiAssistant(IChatClient client, bool isClaude, MicaTools tools, UsageMeter usage, AiAssistantOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _usage = usage ?? throw new ArgumentNullException(nameof(usage));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _isClaude = isClaude;
            _readOnlyTools = AiToolFunctions.ReadOnly(tools);
            _toolClient = new ChatClientBuilder(new FinalAnswerChatClient(client))
                .UseFunctionInvocation(configure: f =>
                {
                    f.MaximumIterationsPerRequest = Math.Max(1, options.MaxToolRounds);
                    // Tool errors carry no secrets, and a small model can correct bad arguments from them.
                    f.IncludeDetailedErrors = true;
                    f.MaximumConsecutiveErrorsPerRequest = Math.Max(1, options.MaxToolRounds);
                })
                .Build();
        }

        /// <summary>
        /// Asks <paramref name="question"/> in <paramref name="conversation"/> and streams the
        /// answer. Counts one question against the daily limit before any request is made.
        /// </summary>
        public async IAsyncEnumerable<AssistantUpdate> AskAsync(AiConversation conversation, string question,
                                                                [EnumeratorCancellation] CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            conversation.Suggestions.Clear();

            string text = (question ?? "").Trim();
            if (text.Length == 0)
            {
                yield return new AssistantUpdate(AssistantUpdateKind.Error, EmptyQuestion);
                yield return Done;
                yield break;
            }

            int limit = Math.Max(1, _options.DailyLimit());
            if (!_usage.TryConsume(limit))
            {
                yield return new AssistantUpdate(AssistantUpdateKind.Error, LimitText(limit));
                yield return Done;
                yield break;
            }

            int start = conversation.Messages.Count;
            conversation.Messages.Add(new ChatMessage(ChatRole.User, text));

            // Cancelled by the caller, or by this deadline when the model stays silent.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TimeSpan silence = _options.InactivityTimeout;
            deadline.CancelAfter(silence);
            CancellationToken token = deadline.Token;

            if (!_toolsUnsupported)
            {
                var turn = new Turn();
                var updates = new List<ChatResponseUpdate>();
                var calls = new Dictionary<string, FunctionCallContent>();
                bool anyText = false;
                Exception? failure = null;

                IAsyncEnumerator<ChatResponseUpdate> stream = _toolClient
                    .GetStreamingResponseAsync(WithSystem(conversation.Messages), ToolOptions(turn), token)
                    .GetAsyncEnumerator(token);
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
                        deadline.CancelAfter(silence);

                        ChatResponseUpdate update = stream.Current;
                        updates.Add(update);
                        foreach (AIContent content in update.Contents)
                        {
                            if (content is FunctionCallContent call)
                            {
                                calls[call.CallId] = call;
                            }
                            else if (content is FunctionResultContent result &&
                                     calls.TryGetValue(result.CallId, out FunctionCallContent? ran) &&
                                     ran.Name != ToolNames.SuggestAction)
                            {
                                yield return new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: ran.Name,
                                                                 ToolArgs: ToolHistory.ArgsJson(ran.Arguments));
                            }
                        }
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            anyText = true;
                            yield return new AssistantUpdate(AssistantUpdateKind.Text, update.Text);
                        }
                        foreach (SuggestedAction action in turn.Drain())
                        {
                            conversation.Suggestions.Add(action);
                            yield return new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: action);
                        }
                    }
                }
                finally
                {
                    await DisposeQuietly(stream);
                }

                if (failure == null)
                {
                    conversation.Messages.AddRange(ToolHistory.KeepAnswered(updates.ToChatResponse().Messages));
                    if (!anyText)
                    {
                        conversation.Messages.Add(new ChatMessage(ChatRole.Assistant, NoAnswer));
                        yield return new AssistantUpdate(AssistantUpdateKind.Text, NoAnswer);
                    }
                    yield return Done;
                    yield break;
                }

                bool fallBack = !ct.IsCancellationRequested && !_isClaude && !anyText && AiErrorText.IsToolsUnsupported(failure);
                if (!fallBack)
                {
                    conversation.Suggestions.Clear();
                    conversation.Messages.RemoveRange(start, conversation.Messages.Count - start);
                    if (!ct.IsCancellationRequested)
                        yield return new AssistantUpdate(AssistantUpdateKind.Error,
                            deadline.IsCancellationRequested ? AiErrorText.TimedOut : AiErrorText.Describe(failure));
                    yield return Done;
                    yield break;
                }
                _toolsUnsupported = true;
            }

            // Limited mode: the same question without tools, with a live snapshot attached.
            yield return new AssistantUpdate(AssistantUpdateKind.LimitedMode, LimitedModeNote);

            deadline.CancelAfter(silence);
            JsonNode? snapshot = await SnapshotAsync(token);
            var answer = new StringBuilder();
            Exception? limitedFailure = null;
            if (snapshot != null)
            {
                List<ChatMessage> messages = LimitedMessages(conversation.Messages, text, snapshot);
                IAsyncEnumerator<ChatResponseUpdate> plain = _client
                    .GetStreamingResponseAsync(messages, new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens }, token)
                    .GetAsyncEnumerator(token);
                try
                {
                    while (true)
                    {
                        (bool moved, Exception? error) = await MoveAsync(plain);
                        if (error != null)
                        {
                            limitedFailure = error;
                            break;
                        }
                        if (!moved) break;
                        deadline.CancelAfter(silence);
                        string piece = plain.Current.Text;
                        if (string.IsNullOrEmpty(piece)) continue;
                        answer.Append(piece);
                        yield return new AssistantUpdate(AssistantUpdateKind.Text, piece);
                    }
                }
                finally
                {
                    await DisposeQuietly(plain);
                }
            }

            if (snapshot == null || limitedFailure != null)
            {
                conversation.Messages.RemoveRange(start, conversation.Messages.Count - start);
                if (!ct.IsCancellationRequested && (limitedFailure != null || deadline.IsCancellationRequested))
                    yield return new AssistantUpdate(AssistantUpdateKind.Error,
                        deadline.IsCancellationRequested ? AiErrorText.TimedOut : AiErrorText.Describe(limitedFailure!));
                yield return Done;
                yield break;
            }

            if (answer.Length == 0) answer.Append(NoAnswer);
            conversation.Messages.Add(new ChatMessage(ChatRole.Assistant, answer.ToString()));
            yield return Done;
        }

        internal static string LimitText(int limit) =>
            "You have asked " + limit.ToString(CultureInfo.InvariantCulture) +
            " questions today, the daily limit set in Settings > AI. The count starts again at midnight.";

        private ChatMessage SystemMessage() => _isClaude
            ? ClaudeCache.SystemMessage(AiPrompts.System)
            : new ChatMessage(ChatRole.System, AiPrompts.System);

        private List<ChatMessage> WithSystem(List<ChatMessage> conversation)
        {
            var messages = new List<ChatMessage>(conversation.Count + 1) { SystemMessage() };
            messages.AddRange(conversation);
            return messages;
        }

        private ChatOptions ToolOptions(Turn turn)
        {
            var tools = new List<AITool>(_readOnlyTools.Count + 1);
            tools.AddRange(_readOnlyTools);
            tools.Add(AiToolFunctions.SuggestAction(turn.Record));
            return new ChatOptions { Tools = tools, MaxOutputTokens = _options.MaxOutputTokens };
        }

        /// <summary>System prompt, earlier turns as plain text, then the question with the snapshot appended.</summary>
        private List<ChatMessage> LimitedMessages(List<ChatMessage> conversation, string question, JsonNode snapshot)
        {
            var messages = new List<ChatMessage> { SystemMessage() };
            messages.AddRange(ToolHistory.Flatten(conversation.GetRange(0, conversation.Count - 1)));
            messages.Add(new ChatMessage(ChatRole.User, question + "\n\n" + AiPrompts.LimitedModeContext(snapshot)));
            return messages;
        }

        /// <summary>Live status plus the top five processes by CPU, or null when cancelled.</summary>
        private async Task<JsonNode?> SnapshotAsync(CancellationToken ct)
        {
            try
            {
                JsonNode status = await _tools.GetLiveStatusAsync(ct);
                JsonNode top = await _tools.GetTopProcessesAsync("cpu", 5, ct);
                return new JsonObject { ["status"] = status, ["topProcesses"] = top };
            }
            catch (OperationCanceledException)
            {
                return null;
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

        /// <summary>The suggestions one question records, handed to the conversation as they arrive.</summary>
        private sealed class Turn
        {
            private readonly object _gate = new();
            private readonly List<SuggestedAction> _pending = new();
            private int _count;

            /// <summary>Keeps <paramref name="action"/>; returns why not when the answer already has the most allowed.</summary>
            public string? Record(SuggestedAction action)
            {
                lock (_gate)
                {
                    if (_count >= AiToolFunctions.MaxSuggestions)
                        return "At most " + AiToolFunctions.MaxSuggestions.ToString(CultureInfo.InvariantCulture) +
                               " suggestions per answer.";
                    _count++;
                    _pending.Add(action);
                    return null;
                }
            }

            public List<SuggestedAction> Drain()
            {
                lock (_gate)
                {
                    var drained = new List<SuggestedAction>(_pending);
                    _pending.Clear();
                    return drained;
                }
            }
        }
    }
}
