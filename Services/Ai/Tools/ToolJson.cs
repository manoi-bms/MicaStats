using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The two shapes every tool shares, so the model and MCP clients learn them once (a reading
    /// that could not be taken, a tool call that failed), and the one way tool JSON becomes text.
    /// </summary>
    public static class ToolJson
    {
        /// <summary>
        /// Relaxed escaping: the default encoder writes <c>&lt;user&gt;</c>, <c>+07:00</c> and Thai
        /// text as <c>\uXXXX</c> escapes, which a model reads badly and pays for in tokens. Safe
        /// here because tool text never goes into HTML.
        /// </summary>
        internal static readonly JsonSerializerOptions TextOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// <c>{"unavailable": true, "reason": "..."}</c>: a reading that was not measured. It is
        /// never written as 0, because a missing temperature probe must not read as a cold CPU.
        /// </summary>
        public static JsonObject Unavailable(string reason) => new()
        {
            ["unavailable"] = true,
            ["reason"] = reason,
        };

        /// <summary><c>{"error": "..."}</c>: the tool could not answer; the message says why.</summary>
        public static JsonObject Error(string message) => new()
        {
            ["error"] = message,
        };

        /// <summary>
        /// Compact JSON text for a model or an MCP client, with <see cref="TextOptions"/>
        /// escaping; <c>null</c> for a null node.
        /// </summary>
        public static string ToText(JsonNode? node) => node == null ? "null" : node.ToJsonString(TextOptions);
    }
}
