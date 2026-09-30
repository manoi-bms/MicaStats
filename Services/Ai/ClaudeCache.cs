using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Anthropic prompt caching for the assistant, kept in its own file because
    /// <c>Anthropic.Models.Messages</c> declares names such as <c>Type</c> that clash with
    /// <c>System</c> wherever both are imported.
    ///
    /// <para>
    /// One breakpoint at the end of the system prompt caches the tool list too: the API caches
    /// the prefix tools, then system. A second breakpoint on the tools is avoided on purpose,
    /// because the API rejects a request whose later breakpoint has a longer TTL than an earlier one.
    /// </para>
    /// </summary>
    internal static class ClaudeCache
    {
        /// <summary>The system message with a one-hour cache breakpoint at its end.</summary>
        public static ChatMessage SystemMessage(string prompt) =>
            new(ChatRole.System, [new TextContent(prompt).WithCacheControl(Ttl.Ttl1h)]);
    }
}
