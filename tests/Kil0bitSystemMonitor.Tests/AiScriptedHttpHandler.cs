using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// An offline HTTP endpoint for the provider SDKs: records every request and answers from a
    /// script. No test reaches the network or a real provider.
    /// </summary>
    internal sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Func<Sent, (HttpStatusCode Status, string ContentType, string Body)> _respond;

        /// <summary>One request: URL, the key header (x-api-key or Authorization) and the body.</summary>
        public sealed record Sent(string Url, string Auth, string Body, string Authorization = "",
                                  IReadOnlyDictionary<string, string>? Headers = null)
        {
            public bool Streaming => Body.Contains("\"stream\":true", StringComparison.Ordinal);
        }

        public ScriptedHttpHandler(Func<Sent, (HttpStatusCode Status, string ContentType, string Body)> respond) => _respond = respond;

        public List<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string auth = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? key)
                ? "x-api-key=" + string.Join(",", key)
                : request.Headers.Authorization?.ToString() ?? "";
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var sent = new Sent(request.RequestUri!.ToString(), auth, body, request.Headers.Authorization?.ToString() ?? "", headers);
            lock (Requests) Requests.Add(sent);
            (HttpStatusCode status, string contentType, string text) = _respond(sent);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, contentType) };
        }

        // ----- Canned provider replies -------------------------------------------------------

        public static string ClaudeText(string text) =>
            "{\"id\":\"msg_2\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"" + text +
            "\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}";

        public const string ClaudeStreamText =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_3\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Stream\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ed ok\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        public const string ClaudeStreamToolUse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_01\",\"name\":\"get_live_status\",\"input\":{}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        public const string ClaudeUnauthorized =
            "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}";

        public const string OpenAiText =
            "{\"id\":\"c2\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"llama3.2\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Core 4 is idle.\"},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":5,\"total_tokens\":10}}";

        public const string OpenAiStreamText =
            "data: {\"id\":\"c3\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"llama3.2\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Str\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c3\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"llama3.2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"eamed\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        public const string OllamaNoTools =
            "{\"error\":{\"message\":\"registry.ollama.ai/library/gemma:2b does not support tools\",\"type\":\"api_error\",\"param\":null,\"code\":null}}";
    }
}
