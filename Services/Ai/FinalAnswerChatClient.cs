using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Sits under the function-invoking client and handles its last request. After
    /// <c>MaximumIterationsPerRequest</c> rounds that client sends one more request with no
    /// tools; the history then still holds tool calls and results, which some APIs reject
    /// without a tool list. This rewrites them as plain text and asks the model to answer now.
    /// Requests that offer tools pass through untouched.
    /// </summary>
    internal sealed class FinalAnswerChatClient : DelegatingChatClient
    {
        public FinalAnswerChatClient(IChatClient inner) : base(inner) { }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                            CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(Prepare(messages, options), options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(Prepare(messages, options), options, cancellationToken);

        private static IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            if (options?.Tools is { Count: > 0 }) return messages;
            List<ChatMessage> flat = ToolHistory.Flatten(messages);
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
        /// <see cref="MaxResultChars"/> is kept shortened (<see cref="Cap"/>): the conversation is
        /// sent again with every later request, so one large lookup would otherwise cost its full
        /// size on every round of every later question.
        /// </summary>
        public static List<ChatMessage> KeepAnswered(IEnumerable<ChatMessage> messages)
        {
            List<ChatMessage> list = messages.ToList();
            var answered = new HashSet<string>(list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId));
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
                            contents.Add(full.Length <= MaxResultChars ? result : new FunctionResultContent(result.CallId, Cap(full)));
                            break;
                    }
                }
                if (contents.Count > 0) kept.Add(new ChatMessage(message.Role, contents));
            }
            return kept;
        }

        /// <summary>
        /// <paramref name="text"/> when it fits in <see cref="MaxResultChars"/>; otherwise its start
        /// followed by a note naming the full length, at most <see cref="MaxResultChars"/> in all, so
        /// shortening twice changes nothing.
        /// </summary>
        internal static string Cap(string text)
        {
            if (text.Length <= MaxResultChars) return text;
            string note = "\n[MicaStats shortened this result from " + text.Length.ToString(CultureInfo.InvariantCulture) +
                          " characters. Call the tool again if the rest is needed.]";
            int keep = MaxResultChars - note.Length;
            if (char.IsHighSurrogate(text[keep - 1])) keep--;   // never split a character in two
            return text[..keep] + note;
        }

        /// <summary>
        /// The same conversation with every tool call and result written as text, for a request
        /// that offers no tools. System messages pass through unchanged (they may carry a cache
        /// breakpoint); tool results become user text.
        /// </summary>
        public static List<ChatMessage> Flatten(IEnumerable<ChatMessage> messages)
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
                            parts.Add("[Result of " + name + ": " + Cap(ResultText(result.Result)) + "]");
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
