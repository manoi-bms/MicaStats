using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Sits under the function-invoking client and handles its last request. After
    /// <c>MaximumIterationsPerRequest</c> rounds that client sends one more request with no
    /// tools; the history then still holds tool calls and results, which some APIs reject
    /// without a tool list. This rewrites them as plain text and asks the model to answer now.
    /// Requests that offer tools pass through untouched.
    ///
    /// <para>
    /// Every request of the tool loop passes here, so this is also where notes access is asked
    /// about right before a request: when what was read from the notes may not go out with it
    /// (the Ask switch is off, or the request goes somewhere other than where the notes were
    /// read to), it is taken out of the messages before they go
    /// (<see cref="ToolHistory.TakeBackNotes"/>). That covers a switch turned off between two
    /// rounds of one question.
    /// </para>
    /// </summary>
    internal sealed class FinalAnswerChatClient : DelegatingChatClient
    {
        private readonly Func<bool>? _takeBackNotes;
        private readonly int _historyTokens;
        private readonly int _maxResultTokens;
        private readonly Action? _trimmed;

        /// <param name="inner">The provider's client.</param>
        /// <param name="takeBackNotes">Whether what was read from the notes must be taken out before the request that is about to go; null never takes anything out.</param>
        public FinalAnswerChatClient(IChatClient inner, Func<bool>? takeBackNotes = null, int historyTokens = int.MaxValue,
                                     Action? trimmed = null, int maxResultTokens = 0) : base(inner)
        {
            _takeBackNotes = takeBackNotes;
            _historyTokens = historyTokens;
            _trimmed = trimmed;
            _maxResultTokens = maxResultTokens;
        }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                            CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(Prepare(messages, options), options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(Prepare(messages, options), options, cancellationToken);

        private IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            if (_takeBackNotes != null && _takeBackNotes())
            {
                if (messages is not ICollection<ChatMessage>) messages = messages.ToList();   // read twice: here and by the request
                ToolHistory.TakeBackNotes(messages);
            }

            bool hasTools = options?.Tools is { Count: > 0 };
            int historyTokens = hasTools
                ? _historyTokens
                : Math.Max(0, _historyTokens - TokenEstimate.Of(AiPrompts.ToolLimitReached));
            IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            list = ConversationTrim.Fit(list, historyTokens, out bool dropped);
            if (dropped) _trimmed?.Invoke();

            if (hasTools) return list;
            List<ChatMessage> flat = ToolHistory.Flatten(list, _maxResultTokens);
            flat.Add(new ChatMessage(ChatRole.User, AiPrompts.ToolLimitReached));
            return flat;
        }
    }

    /// <summary>Tidies tool calls in a conversation for requests and for keeping.</summary>
    internal static class ToolHistory
    {
        /// <summary>Most characters of one tool result sent again after its round: in the no-tools request and in kept questions.</summary>
        internal const int MaxResultChars = 20_000;

        /// <summary>
        /// The messages worth keeping from one answer: text, tool calls that got a result, and
        /// the results. A call left without a result (the model asked after the last round) is
        /// dropped, because sending it back would fail the next question. A result longer than
        /// <see cref="MaxResultChars"/> is kept shortened (<see cref="Shortened"/>): the
        /// conversation is sent again with every later request, so one large lookup would
        /// otherwise cost its full size on every round of every later question.
        /// </summary>
        public static List<ChatMessage> KeepAnswered(IEnumerable<ChatMessage> messages, int maxResultTokens = 0)
        {
            List<ChatMessage> list = messages.ToList();
            var answered = new HashSet<string>(list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId));
            var noteCalls = new HashSet<string>(
                list.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
                    .Where(c => c.Name is ToolNames.SearchNotes or ToolNames.GetNote).Select(c => c.CallId),
                StringComparer.Ordinal);
            var kept = new List<ChatMessage>();
            foreach (ChatMessage message in list)
            {
                var contents = new List<AIContent>();
                foreach (AIContent content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                        case FunctionCallContent call when answered.Contains(call.CallId):
                            contents.Add(content);
                            break;
                        case FunctionResultContent result:
                            string full = ResultText(result.Result);
                            contents.Add(Fits(full, maxResultTokens)
                                ? result
                                : Shortened(result, full, noteCalls.Contains(result.CallId), maxResultTokens));
                            break;
                    }
                }
                if (contents.Count > 0) kept.Add(new ChatMessage(message.Role, contents));
            }
            return kept;
        }

        /// <summary>
        /// A result over <see cref="MaxResultChars"/> as it is kept. The result of a PC tool is
        /// cut to text (<see cref="Cap"/>). So cut, the result of a note tool would end in the
        /// middle of its JSON and lose the line that says it is data, and 16,000 characters of a
        /// note are easily over the cap once written as JSON (an emoji is twelve characters
        /// there, a quote two). It is kept in its own shape with less of the note
        /// (<see cref="NoteTools.Shortened"/>): at least as many characters of note text go as the
        /// cut would have taken off the whole result, so never more of the note is kept than
        /// before. One that cannot be shortened that way is cut to text like any other; the
        /// take-back counts such a string as a note read.
        /// </summary>
        private static FunctionResultContent Shortened(FunctionResultContent result, string full, bool ofNoteTool,
                                                        int maxResultTokens)
        {
            if (maxResultTokens > 0)
            {
                if (ofNoteTool && result.Result is JsonElement tokenResult &&
                    ShortenedNote(tokenResult, full, maxResultTokens) is { } tokenShorter)
                    return new FunctionResultContent(result.CallId, AiToolFunctions.ToElement(tokenShorter));
                return new FunctionResultContent(result.CallId, CapTokens(full, maxResultTokens));
            }
            if (ofNoteTool && result.Result is JsonElement given &&
                NoteTools.Shortened(given, full.Length - CapKeeps(full, CapNote(full.Length)), MaxResultChars) is { } shorter)
            {
                JsonElement json = AiToolFunctions.ToElement(shorter);
                if (json.GetRawText().Length <= MaxResultChars) return new FunctionResultContent(result.CallId, json);
            }
            return new FunctionResultContent(result.CallId, Cap(full));
        }

        private static bool Fits(string full, int maxResultTokens) => maxResultTokens > 0
            ? TokenEstimate.Of(full) <= maxResultTokens
            : full.Length <= MaxResultChars;

        private static JsonObject? ShortenedNote(JsonElement result, string full, int maxTokens)
        {
            int low = 1, high = Math.Max(1, full.Length);
            JsonObject? answer = null;
            while (low <= high)
            {
                int lose = low + (high - low) / 2;
                JsonObject? candidate = NoteTools.Shortened(result, lose, int.MaxValue);
                if (candidate == null) return answer;
                if (TokenEstimate.Of(ToolJson.ToText(candidate)) <= maxTokens)
                {
                    answer = candidate;
                    high = lose - 1;
                }
                else
                {
                    low = lose + 1;
                }
            }
            return answer;
        }

        private static string CapTokens(string text, int maxTokens)
        {
            if (TokenEstimate.Of(text) <= maxTokens) return text;
            string note = "\n[MicaStats shortened this result. Call the tool again if the rest is needed.]";
            if (TokenEstimate.Of(note) >= maxTokens) return PrefixForTokens(note, maxTokens);
            int keepTokens = maxTokens - TokenEstimate.Of(note);
            return PrefixForTokens(text, keepTokens) + note;
        }

        private static string PrefixForTokens(string text, int maxTokens)
        {
            int low = 0, high = text.Length;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (TokenEstimate.Of(text[..middle]) <= maxTokens) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1])) low--;
            return text[..low];
        }

        /// <summary>
        /// <paramref name="text"/> when it fits in <see cref="MaxResultChars"/>; otherwise its start
        /// followed by a note naming the full length, at most <see cref="MaxResultChars"/> in all, so
        /// shortening twice changes nothing.
        /// </summary>
        internal static string Cap(string text)
        {
            if (text.Length <= MaxResultChars) return text;
            string note = CapNote(text.Length);
            return text[..CapKeeps(text, note)] + note;
        }

        private static string CapNote(int length) =>
            "\n[MicaStats shortened this result from " + length.ToString(CultureInfo.InvariantCulture) +
            " characters. Call the tool again if the rest is needed.]";

        /// <summary>How many characters of a <paramref name="text"/> over the cap stay in front of <paramref name="note"/>.</summary>
        private static int CapKeeps(string text, string note)
        {
            int keep = MaxResultChars - note.Length;
            if (char.IsHighSurrogate(text[keep - 1])) keep--;   // never split a character in two
            return keep;
        }

        /// <summary>What stands in a conversation for an answer that was taken back.</summary>
        internal const string RemovedAnswer = "(Removed: this answer used your notes, and notes access has changed.)";

        /// <summary>The error that stands for a tool result taken back because it may repeat what the model wrote after it read a note.</summary>
        internal const string RemovedResult = "Removed: notes access has changed.";

        /// <summary>What a tool call was for, as far as taking notes back goes.</summary>
        [Flags]
        private enum Called
        {
            Other = 0,
            NoteTool = 1,
            Suggestion = 2,
        }

        /// <summary>
        /// Takes back, in place, what the note tools returned and what the model made of it. For
        /// when note text already read may not be sent again: Ask MicaStats may no longer read
        /// notes, or the next request goes somewhere other than where the notes were read to.
        /// A conversation is sent again with every later request, and this is what keeps the
        /// notes out of it.
        ///
        /// <para>
        /// Every result of <c>search_notes</c> and <c>get_note</c> becomes the refusal a call gets
        /// while notes access is off (<see cref="NoteTools.Off"/>). From the message after the
        /// first of them that held notes, whatever the model wrote may quote a note, so that goes
        /// too: the text of every assistant message becomes <see cref="RemovedAnswer"/>; the
        /// arguments of every tool call are emptied (a query or a reason can quote a note as
        /// well as an answer can); and a result of another tool stays only when it is what the
        /// PC measured. One that can repeat the call's arguments becomes
        /// <see cref="RemovedResult"/>: an error, which names the argument it could not use, and
        /// <c>suggest_action</c>'s, which names the button. What stays is the user's own
        /// questions, what the PC tools measured, and every call with its result, so a provider
        /// still takes the request.
        /// </para>
        ///
        /// <para>
        /// After this no result holds notes, so doing it again changes nothing more: an answer
        /// given after a take-back used no note, and it stays.
        /// </para>
        ///
        /// <para>
        /// A provider may give two calls the same id, so a result is matched to the latest call
        /// of its id, message by message; when two calls of one message share an id and either is
        /// a note tool, both results count as note results.
        /// </para>
        /// </summary>
        internal static void TakeBackNotes(IEnumerable<ChatMessage> messages)
        {
            var calls = new Dictionary<string, Called>(StringComparer.Ordinal);
            bool afterNotes = false;   // an earlier message holds a result that held notes
            foreach (ChatMessage message in messages)
            {
                Dictionary<string, Called>? here = null;
                foreach (AIContent content in message.Contents)
                {
                    if (content is not FunctionCallContent call) continue;
                    here ??= new Dictionary<string, Called>(StringComparer.Ordinal);
                    here.TryGetValue(call.CallId, out Called already);
                    here[call.CallId] = already | call.Name switch
                    {
                        ToolNames.SearchNotes or ToolNames.GetNote => Called.NoteTool,
                        ToolNames.SuggestAction => Called.Suggestion,
                        _ => Called.Other,
                    };
                }
                if (here != null)
                    foreach (KeyValuePair<string, Called> pair in here) calls[pair.Key] = pair.Value;

                bool assistant = message.Role == ChatRole.Assistant;
                bool heldNotes = false, answered = false, changed = false;
                var now = new List<AIContent>(message.Contents.Count);
                foreach (AIContent content in message.Contents)
                {
                    AIContent? kept = content;
                    switch (content)
                    {
                        case FunctionResultContent result:
                            calls.TryGetValue(result.CallId, out Called called);
                            if ((called & Called.NoteTool) != 0)
                            {
                                heldNotes |= !IsError(result);
                                kept = Refusal(result.CallId, NoteTools.Off);
                            }
                            else if (afterNotes && ((called & Called.Suggestion) != 0 || !IsData(result)))
                            {
                                kept = Refusal(result.CallId, RemovedResult);
                            }
                            break;
                        case FunctionCallContent call when afterNotes && call.Arguments is { Count: > 0 }:
                            kept = new FunctionCallContent(call.CallId, call.Name, new Dictionary<string, object?>());
                            break;
                        case TextContent text when afterNotes && assistant:
                            // One sentence for the whole answer, however many pieces it came in.
                            kept = answered || string.IsNullOrEmpty(text.Text) ? null : new TextContent(RemovedAnswer);
                            answered |= kept != null;
                            break;
                        case TextReasoningContent when afterNotes && assistant:
                            kept = null;
                            break;
                    }
                    changed |= !ReferenceEquals(kept, content);
                    if (kept != null) now.Add(kept);
                }

                if (changed)
                {
                    if (now.Count == 0) now.Add(new TextContent(RemovedAnswer));   // never a message with nothing in it
                    message.Contents = now;
                }
                afterNotes |= heldNotes;
            }
        }

        /// <summary>How every sentence begins that the function-calling loop writes in place of a result: a function it does not have, one that failed.</summary>
        private const string LoopError = "Error:";

        /// <summary>
        /// True for a result that says only what went wrong, and so holds no note text: the
        /// <c>{"error": ...}</c> every MicaStats tool gives for a problem, a function that threw,
        /// or the function-calling loop's own sentence. The loop writes one when the model calls
        /// a note tool that was not offered (the Ask switch is off, and the system prompt still
        /// names the tools): <c>Error: Requested function "search_notes" not found.</c> No note
        /// was read then, and the answer after it is not taken out.
        ///
        /// <para>
        /// A note tool's result that is anything else held notes. That is the strict side on
        /// purpose: another plain string counts too, because a result shortened to text
        /// (<see cref="Cap"/>) is one, and it begins with the tool's JSON, never with "Error:".
        /// </para>
        /// </summary>
        private static bool IsError(FunctionResultContent result) =>
            result.Exception != null ||
            (result.Result is JsonElement { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out _)) ||
            (result.Result is string said && said.StartsWith(LoopError, StringComparison.Ordinal));

        /// <summary>True for a result that is a tool's own data as it gave it: a JSON object, and no error.</summary>
        private static bool IsData(FunctionResultContent result) =>
            result.Exception == null && result.Result is JsonElement { ValueKind: JsonValueKind.Object } json &&
            !json.TryGetProperty("error", out _);

        private static FunctionResultContent Refusal(string callId, string error) =>
            new(callId, AiToolFunctions.ToElement(ToolJson.Error(error)));

        /// <summary>
        /// The same conversation with every tool call and result written as text, for a request
        /// that offers no tools. System messages pass through unchanged (they may carry a cache
        /// breakpoint); tool results become user text.
        /// </summary>
        public static List<ChatMessage> Flatten(IEnumerable<ChatMessage> messages, int maxResultTokens = 0)
        {
            var names = new Dictionary<string, string>();
            var flat = new List<ChatMessage>();
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.System)
                {
                    flat.Add(message);
                    continue;
                }

                var parts = new List<string>();
                foreach (AIContent content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            parts.Add(text.Text);
                            break;
                        case FunctionCallContent call:
                            names[call.CallId] = call.Name;
                            parts.Add("[Called MicaStats tool " + call.Name + " with " + ArgsJson(call.Arguments) + "]");
                            break;
                        case FunctionResultContent result:
                            string name = names.TryGetValue(result.CallId, out string? n) ? n : "a MicaStats tool";
                            string full = ResultText(result.Result);
                            parts.Add("[Result of " + name + ": " +
                                      (maxResultTokens > 0 ? CapTokens(full, maxResultTokens) : Cap(full)) + "]");
                            break;
                    }
                }
                if (parts.Count == 0) continue;
                ChatRole role = message.Role == ChatRole.Tool ? ChatRole.User : message.Role;
                flat.Add(new ChatMessage(role, string.Join("\n", parts)));
            }
            return flat;
        }

        /// <summary>Tool arguments as compact JSON, <c>{}</c> when there are none.</summary>
        public static string ArgsJson(IDictionary<string, object?>? arguments) =>
            arguments == null || arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments, ToolJson.TextOptions);

        private static string ResultText(object? result) => result switch
        {
            null => "null",
            JsonElement element => element.GetRawText(),
            string s => s,
            _ => JsonSerializer.Serialize(result, ToolJson.TextOptions),
        };
    }
}
