using System;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The line under the provider settings that says where questions go: "stays on this PC"
    /// for a server on this machine, otherwise the host that receives them. Worked out from the
    /// settings alone, so it is true before anything has been sent.
    /// </summary>
    public static class AiPrivacyNote
    {
        private const string Removed = " Your profile folder, computer name, user name and IP addresses are removed first.";

        /// <summary>The note for a provider and, for an OpenAI-compatible one, its base URL.</summary>
        /// <param name="provider">An <see cref="AiProviders"/> value.</param>
        /// <param name="compatibleBaseUrl">The compatible endpoint's base URL; ignored for Claude.</param>
        public static string Describe(string provider, string? compatibleBaseUrl)
        {
            if (!string.Equals(provider, AiProviders.OpenAiCompatible, StringComparison.Ordinal))
                return "Questions and the PC data they need go to Anthropic (api.anthropic.com)." + Removed;

            if (!Uri.TryCreate(compatibleBaseUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "The base URL is not a valid http or https address, so nothing can be sent.";

            if (uri.IsLoopback)
                return "Everything stays on this PC (" + uri.Host + ").";

            return "Questions and the PC data they need go to " + uri.Host + "." + Removed;
        }
    }
}
