using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

            string? key = ClaudeKey(secrets);
            if (key == null) return new AiClientResult(null, NoKey, IsClaude: true);

            AnthropicClient anthropic = NewAnthropicClient(key, NewHttpClient(handler));
            IChatClient client = anthropic.AsIChatClient(config.AiClaudeModel, MaxOutputTokens);
            return new AiClientResult(client, null, IsClaude: true);
        }

        private static AiClientResult CreateCompatible(AppConfig config, SecretStore secrets, HttpMessageHandler? handler)
        {
            string model = (config.AiCompatibleModel ?? "").Trim();
            if (model.Length == 0) return new AiClientResult(null, NoModel, IsClaude: false);

            if (!TryCompatibleEndpoint(config, out Uri? endpoint))
                return new AiClientResult(null, BadUrl, IsClaude: false);

            // The key is optional (a local Ollama has none), but the SDK rejects an empty one.
            var credential = new ApiKeyCredential(CompatibleKey(secrets) ?? "none");
            var openAi = new OpenAIClient(credential, new OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new HttpClientPipelineTransport(NewHttpClient(handler)),
                // Retried twice like Claude; the default is four attempts.
                RetryPolicy = new ClientRetryPolicy(maxRetries: 2),
            });

            IChatClient client = openAi.GetChatClient(model).AsIChatClient();
            // OpenAI itself wants max_completion_tokens; most other servers only know max_tokens.
            if (!IsOpenAiHost(endpoint)) client = new MaxTokensChatClient(client);
            return new AiClientResult(client, null, IsClaude: false);
        }

        /// <summary>
        /// True for OpenAI itself (<c>openai.com</c> or a <c>.openai.com</c> subdomain) and Azure
        /// OpenAI (a <c>.openai.azure.com</c> subdomain), matched on whole labels so a host such as
        /// <c>notopenai.com</c> is another server and gets <c>max_tokens</c>.
        /// </summary>
        internal static bool IsOpenAiHost(Uri endpoint)
        {
            string host = endpoint.Host;
            return string.Equals(host, "openai.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
        }

        // ----- Shared with ModelCatalog: a list of models is asked for with the same key, at the
        // same address and through the same kind of client as a question. ---------------------

        /// <summary>The one address a Claude request goes to.</summary>
        internal const string ClaudeBaseUrl = "https://api.anthropic.com";

        /// <summary>The saved Claude key, trimmed; null when none is saved.</summary>
        internal static string? ClaudeKey(SecretStore secrets) => Trimmed(secrets.Get(SecretNames.ClaudeKey));

        /// <summary>The saved key of the compatible server, trimmed; null when none is saved (a local server needs none).</summary>
        internal static string? CompatibleKey(SecretStore secrets) => Trimmed(secrets.Get(SecretNames.CompatibleKey));

        private static string? Trimmed(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

        /// <summary>
        /// The compatible server's address from the settings. False when the base URL is not an
        /// absolute http or https address: nothing is sent then, and <see cref="BadUrl"/> says so.
        /// </summary>
        internal static bool TryCompatibleEndpoint(AppConfig config, [NotNullWhen(true)] out Uri? endpoint) =>
            Uri.TryCreate((config.AiCompatibleBaseUrl ?? "").Trim(), UriKind.Absolute, out endpoint) &&
            (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps);

        /// <summary>
        /// The Anthropic client every Claude request is made with: the user's own key, to
        /// <see cref="ClaudeBaseUrl"/> and nowhere else, over <paramref name="http"/>.
        /// </summary>
        internal static AnthropicClient NewAnthropicClient(string key, HttpClient http)
        {
            // MicaStats must talk only to api.anthropic.com with the user's own key. These variables
            // belong to Claude Code gateways; the SDK would read them at construction and could
            // redirect the traffic, add an Authorization header, or override anthropic-version.
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", null);

            return new AnthropicClient
            {
                ApiKey = key,
                // The SDK would otherwise read ANTHROPIC_BASE_URL and ANTHROPIC_AUTH_TOKEN from the
                // environment and could send the key and every question to another host.
                BaseUrl = ClaudeBaseUrl,
                AuthToken = null,
                HttpClient = http,
                // 429 and 529 are retried twice by the SDK before the Ask window says "busy".
                MaxRetries = 2,
            };
        }

        /// <summary>The HTTP client of a question: the network, or a test's handler in its place.</summary>
        private static HttpClient NewHttpClient(HttpMessageHandler? handler) =>
            NewHttpClient(handler ?? new SocketsHttpHandler(), owned: handler == null, RequestTimeout);

        /// <summary>
        /// An HTTP client over <paramref name="handler"/> that gives up after <paramref name="timeout"/>.
        /// <paramref name="owned"/> is true for a handler made here, which goes with the client, and
        /// false for a test's, which is never disposed here.
        /// </summary>
        internal static HttpClient NewHttpClient(HttpMessageHandler handler, bool owned, TimeSpan timeout) =>
            new(handler, disposeHandler: owned) { Timeout = timeout };
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
