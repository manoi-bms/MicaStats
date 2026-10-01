using System;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The line under the Ask window's title naming the model in use: "Claude \u00B7 claude-haiku-4-5",
    /// or "llama3.2 \u00B7 localhost" for an OpenAI-compatible server. Only the server's host is shown,
    /// never its path, port or anything that could carry a key.
    /// </summary>
    public static class AiModelLabel
    {
        private const string Dot = " \u00B7 ";

        /// <summary>The label for these settings, or null when there is nothing to name.</summary>
        public static string? For(string? provider, string? claudeModel, string? compatibleModel, string? compatibleBaseUrl)
        {
            if (string.Equals(provider, AiProviders.OpenAiCompatible, StringComparison.OrdinalIgnoreCase))
            {
                string model = compatibleModel?.Trim() ?? "";
                string host = Uri.TryCreate(compatibleBaseUrl?.Trim(), UriKind.Absolute, out Uri? uri) ? uri.Host : "";
                if (model.Length > 0 && host.Length > 0) return model + Dot + host;
                if (model.Length > 0) return model;
                return host.Length > 0 ? host : null;
            }

            string claude = claudeModel?.Trim() ?? "";
            return claude.Length > 0 ? "Claude" + Dot + claude : "Claude";
        }
    }
}
