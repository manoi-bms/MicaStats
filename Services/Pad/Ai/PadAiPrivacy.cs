using System;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>
    /// Where MicaPad's AI text goes, from the settings alone (MicaPad AI spec 1 and 2): the line
    /// under "Use AI in MicaPad", and the short name of the destination shown where an action
    /// runs. Both read the provider as <see cref="AiProviderFactory"/> does.
    /// </summary>
    public static class PadAiPrivacy
    {
        private const string Anthropic = "api.anthropic.com";
        private const string ThisPc = "this PC";
        private const string Sent = "Text you run an AI action on, and passages found for a question, go to ";
        private const string Never = " Stored credentials are never sent.";

        public static string Describe(string provider, string? compatibleBaseUrl)
        {
            if (!IsCompatible(provider)) return Sent + "Anthropic (" + Anthropic + ")." + Never;

            if (Endpoint(compatibleBaseUrl) is not { } uri)
                return "The base URL is not a valid http or https address, so nothing can be sent.";

            if (uri.IsLoopback) return "Everything stays on this PC (" + uri.Host + ").";

            return Sent + uri.Host + "." + Never;
        }

        /// <summary>
        /// The destination in a word or two, for the AI pane's source line and the notes status:
        /// "api.anthropic.com" for Claude, "this PC" for a loopback address, the host of any
        /// other server (never its port, path or key), or "" when the base URL is not a valid
        /// http or https address, so nothing can be sent.
        /// </summary>
        public static string Destination(string provider, string? compatibleBaseUrl)
        {
            if (!IsCompatible(provider)) return Anthropic;
            if (Endpoint(compatibleBaseUrl) is not { } uri) return "";
            return uri.IsLoopback ? ThisPc : uri.Host;
        }

        /// <summary>Any other value is Claude, as the factory has it.</summary>
        private static bool IsCompatible(string provider) =>
            string.Equals(provider, AiProviders.OpenAiCompatible, StringComparison.Ordinal);

        /// <summary>The OpenAI-compatible server's address, or null when it is not a valid http or https one.</summary>
        private static Uri? Endpoint(string? baseUrl) =>
            Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri
                : null;
    }
}
