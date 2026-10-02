namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The values <see cref="Kil0bitSystemMonitor.Models.AppConfig.AiProvider"/> may hold. Strings,
    /// like every other choice in <c>config.json</c>, so the file stays readable and a value this
    /// build does not know falls back to Claude instead of failing to load.
    /// </summary>
    public static class AiProviders
    {
        /// <summary>Anthropic's Claude, through the official Anthropic SDK.</summary>
        public const string Claude = "Claude";

        /// <summary>Any endpoint that speaks the OpenAI chat API: OpenAI, Azure, OpenRouter, Ollama, LM Studio.</summary>
        public const string OpenAiCompatible = "OpenAiCompatible";
    }

    /// <summary>How MCP clients reach MicaStats, stored in <see cref="Kil0bitSystemMonitor.Models.AppConfig.AiMcpMode"/>.</summary>
    public static class AiMcpModes
    {
        /// <summary>No pipe, no HTTP server; the <c>--mcp</c> bridge answers every call with a refusal.</summary>
        public const string Off = "Off";

        /// <summary><c>MicaStats.exe --mcp</c> on stdio, forwarding to the running app over a per-user named pipe.</summary>
        public const string Stdio = "Stdio";

        /// <summary>The running app serves MCP on 127.0.0.1 with a bearer token.</summary>
        public const string Http = "Http";
    }

    /// <summary>
    /// Names of the values <see cref="SecretStore"/> keeps. Secrets never go into <c>config.json</c>.
    /// </summary>
    public static class SecretNames
    {
        /// <summary>The Anthropic API key.</summary>
        public const string ClaudeKey = "claude-key";

        /// <summary>The optional key of the OpenAI-compatible endpoint.</summary>
        public const string CompatibleKey = "compatible-key";

        /// <summary>The bearer token local-HTTP MCP clients must send.</summary>
        public const string McpToken = "mcp-token";

        /// <summary>MicaPad's embedding server key (Settings → MicaPad → Search).</summary>
        public const string PadEmbeddingKey = "pad-embedding-key";

        /// <summary>MicaPad's reranker key.</summary>
        public const string PadRerankKey = "pad-rerank-key";
    }
}
