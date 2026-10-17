using System;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Localization;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>
    /// Where MicaPad's AI text goes, from the settings alone (MicaPad AI spec 1 and 2): the line
    /// under "Use AI in MicaPad", and the short name of the destination and of the model shown
    /// where an action runs. All read the provider as <see cref="AiProviderFactory"/> does.
    /// </summary>
    public static class PadAiPrivacy
    {
        private const string Anthropic = "api.anthropic.com";
        private const string ThisPc = "this PC";
        private const string Sent = "Text you run an AI action on, and passages found for a question, go to ";
        private const string Never = " Stored credentials are never sent.";

        public static string Describe(string provider, string? compatibleBaseUrl)
        {
            if (!IsCompatible(provider)) return UiText.Format("Text you run an AI action on, and passages found for a question, go to Anthropic ({0}). Stored credentials are never sent.", Anthropic);

            if (Endpoint(compatibleBaseUrl) is not { } uri)
                return UiText.Tr("The base URL is not a valid http or https address, so nothing can be sent.");

            if (uri.IsLoopback) return UiText.Format("Everything stays on this PC ({0}).", uri.Host);

            return UiText.Format("Text you run an AI action on, and passages found for a question, go to {0}. Stored credentials are never sent.", uri.Host);
        }

        /// <summary>The line under "Let Ask MicaStats search your notes": where what Ask looks up goes.</summary>
        public static string NotesInAsk(string provider, string? compatibleBaseUrl)
        {
            if (!IsCompatible(provider)) return UiText.Format("When a question needs them, passages and notes Ask looks up go to Anthropic ({0}). Stored credentials are never sent.", Anthropic);

            if (Endpoint(compatibleBaseUrl) is not { } uri)
                return UiText.Tr("The base URL is not a valid http or https address, so nothing can be sent.");

            if (uri.IsLoopback) return UiText.Format("Passages and notes Ask looks up stay on this PC ({0}).", uri.Host);

            return UiText.Format("When a question needs them, passages and notes Ask looks up go to {0}. Stored credentials are never sent.", uri.Host);
        }

        /// <summary>The line under "Let MCP clients search your notes".</summary>
        public static string NotesInMcp => UiText.Tr("Programs you connected through MCP (Settings → AI) can search and read your notes. What they do with the text is up to them. Stored credentials are never given out.");

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
            return uri.IsLoopback ? UiText.Tr(ThisPc) : uri.Host;
        }

        /// <summary>
        /// The id of the model a request goes to, trimmed, for the AI pane's source line and its
        /// "Waiting for …" line: the Claude model for Claude, the compatible server's model for
        /// that provider, never the other one's. "" when none is set. It is only shown.
        /// </summary>
        public static string Model(string provider, string? claudeModel, string? compatibleModel) =>
            ((IsCompatible(provider) ? compatibleModel : claudeModel) ?? "").Trim();

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
