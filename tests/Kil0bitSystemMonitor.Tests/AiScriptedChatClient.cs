using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A fake model: answers each request with the next scripted step (a tool call, text, an
    /// exception, or waiting until cancelled), or with <see cref="Otherwise"/> once the script
    /// runs out. Records every request it receives. Text is streamed in two chunks.
    /// </summary>
    internal sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<Request, CancellationToken, Task<ChatMessage>>> _script = new();
        private int _calls;

        /// <summary>One request as the model saw it.</summary>
        public sealed record Request(List<ChatMessage> Messages, ChatOptions? Options)
        {
            public List<string> ToolNames => Options?.Tools?.Select(t => t.Name).ToList() ?? new List<string>();
        }

        public List<Request> Requests { get; } = new();

        /// <summary>The answer once the script is used up; by default the text "Done.".</summary>
        public Func<Request, ChatMessage> Otherwise { get; set; } = _ => new ChatMessage(ChatRole.Assistant, "Done.");

        public ScriptedChatClient Reply(string text)
        {
            _script.Enqueue((_, _) => Task.FromResult(new ChatMessage(ChatRole.Assistant, text)));
            return this;
        }

        public ScriptedChatClient Call(string tool, Dictionary<string, object?>? args = null)
        {
            _script.Enqueue((_, _) => Task.FromResult(CallMessage(tool, args)));
            return this;
        }

        public ScriptedChatClient Fail(Exception ex)
        {
            _script.Enqueue((_, _) => Task.FromException<ChatMessage>(ex));
            return this;
        }

        public ScriptedChatClient Hang()
        {
            _script.Enqueue(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ChatMessage(ChatRole.Assistant, "never");
            });
            return this;
        }

        /// <summary>An assistant message asking for one tool call with a fresh call id.</summary>
        public ChatMessage CallMessage(string tool, Dictionary<string, object?>? args = null)
        {
            string id = "call_" + Interlocked.Increment(ref _calls).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, tool, args ?? new Dictionary<string, object?>())]);
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                         CancellationToken cancellationToken = default) =>
            new ChatResponse(await NextAsync(messages, options, cancellationToken));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatMessage reply = await NextAsync(messages, options, cancellationToken);
            if (reply.Contents.OfType<FunctionCallContent>().Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, reply.Contents.ToList());
                yield break;
            }
            string text = reply.Text;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[..(text.Length / 2)]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[(text.Length / 2)..]);
        }

        private async Task<ChatMessage> NextAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
        {
            var request = new Request(messages.ToList(), options);
            Requests.Add(request);
            await Task.Yield();
            return _script.Count > 0 ? await _script.Dequeue()(request, ct) : Otherwise(request);
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
