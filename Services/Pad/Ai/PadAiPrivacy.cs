using System;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>The line under "Use AI in MicaPad" that says where text goes (MicaPad AI spec 1), from the settings alone.</summary>
    public static class PadAiPrivacy
    {
        private const string Sent = "Text you run an AI action on, and passages found for a question, go to ";
        private const string Never = " Stored credentials are never sent.";

        public static string Describe(string provider, string? compatibleBaseUrl)
        {
            if (!string.Equals(provider, AiProviders.OpenAiCompatible, StringComparison.Ordinal))
                return Sent + "Anthropic (api.anthropic.com)." + Never;

            if (!Uri.TryCreate(compatibleBaseUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "The base URL is not a valid http or https address, so nothing can be sent.";

            if (uri.IsLoopback) return "Everything stays on this PC (" + uri.Host + ").";

            return Sent + uri.Host + "." + Never;
        }
    }
}
