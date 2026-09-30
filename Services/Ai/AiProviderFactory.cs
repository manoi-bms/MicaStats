using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Kil0bitSystemMonitor.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The chat client for the current settings, or the reason there is none. <see cref="IsClaude"/>
    /// tells the assistant it may mark the system prompt for Anthropic prompt caching.
    /// </summary>
    /// <param name="Client">The ready client, or null when <paramref name="Problem"/> says what is missing.</param>
    /// <param name="Problem">A sentence for the Ask window, e.g. "Add an API key in Settings > AI.".</param>
    /// <param name="IsClaude">True for the Claude provider.</param>
    public sealed record AiClientResult(IChatClient? Client, string? Problem, bool IsClaude);

    /// <summary>
    /// Builds the <see cref="IChatClient"/> for AI settings: Claude through the official
    /// Anthropic SDK, or any OpenAI-compatible endpoint (OpenAI, Azure, OpenRouter, Ollama,
    /// LM Studio). Built per question, so a provider change applies from the next question.
    /// </summary>
    public static class AiProviderFactory
    {
        /// <summary>No reply within this time fails the request.</summary>
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Answer length cap, sent as the default output limit.</summary>
        internal const int MaxOutputTokens = 2000;

        internal const string NoKey = "Add an API key in Settings > AI.";
        internal const string NoModel = "Choose a model in Settings > AI.";
        internal const string BadUrl = "Enter a valid http or https base URL in Settings > AI.";

        /// <summary>
        /// Creates the client for <paramref name="config"/>. <paramref name="handler"/> replaces
        /// the network for tests (it is never disposed here); the app passes null.
        /// </summary>
        public static AiClientResult Create(AppConfig config, SecretStore secrets, HttpMessageHandler? handler = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(secrets);

            if (config.AiProvider == AiProviders.OpenAiCompatible) return CreateCompatible(config, secrets, handler);

            string? key = secrets.Get(SecretNames.ClaudeKey);
            if (string.IsNullOrWhiteSpace(key)) return new AiClientResult(null, NoKey, IsClaude: true);

            var anthropic = new AnthropicClient
            {
                ApiKey = key.Trim(),
                HttpClient = NewHttpClient(handler),
                // 429 and 529 are retried twice by the SDK before the Ask window says "busy".
                MaxRetries = 2,
            };
            IChatClient client = anthropic.AsIChatClient(config.AiClaudeModel, MaxOutputTokens);
            return new AiClientResult(client, null, IsClaude: true);
        }

        private static AiClientResult CreateCompatible(AppConfig config, SecretStore secrets, HttpMessageHandler? handler)
        {
            string model = (config.AiCompatibleModel ?? "").Trim();
            if (model.Length == 0) return new AiClientResult(null, NoModel, IsClaude: false);

            if (!Uri.TryCreate((config.AiCompatibleBaseUrl ?? "").Trim(), UriKind.Absolute, out Uri? endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                return new AiClientResult(null, BadUrl, IsClaude: false);

            // The key is optional (a local Ollama has none), but the SDK rejects an empty one.
            string? key = secrets.Get(SecretNames.CompatibleKey);
            var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "none" : key.Trim());
            var openAi = new OpenAIClient(credential, new OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new HttpClientPipelineTransport(NewHttpClient(handler)),
            });

            IChatClient client = openAi.GetChatClient(model).AsIChatClient();
            // OpenAI itself wants max_completion_tokens; most other servers only know max_tokens.
            if (!IsOpenAiHost(endpoint)) client = new MaxTokensChatClient(client);
            return new AiClientResult(client, null, IsClaude: false);
        }

        private static bool IsOpenAiHost(Uri endpoint) =>
            endpoint.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.EndsWith("openai.azure.com", StringComparison.OrdinalIgnoreCase);

        private static HttpClient NewHttpClient(HttpMessageHandler? handler) =>
            new(handler ?? new SocketsHttpHandler(), disposeHandler: handler == null) { Timeout = RequestTimeout };
    }

    /// <summary>
    /// Sends the output limit as <c>max_tokens</c> instead of <c>max_completion_tokens</c>:
    /// Microsoft.Extensions.AI.OpenAI writes the newer name, which older OpenAI-compatible
    /// servers ignore, leaving answers unbounded.
    /// </summary>
    internal sealed class MaxTokensChatClient : DelegatingChatClient
    {
        public MaxTokensChatClient(IChatClient inner) : base(inner) { }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                            CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, Patch(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, Patch(options), cancellationToken);

        private static ChatOptions? Patch(ChatOptions? options)
        {
            if (options?.MaxOutputTokens is not int max || options.RawRepresentationFactory != null) return options;
            ChatOptions patched = options.Clone();
            patched.MaxOutputTokens = null;
            patched.RawRepresentationFactory = _ =>
            {
                var raw = new OpenAI.Chat.ChatCompletionOptions();
                raw.Patch.Set("$.max_tokens"u8, max);
                return raw;
            };
            return patched;
        }
    }
}
