using System;
using System.ClientModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Anthropic.Exceptions;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Turns a provider failure into one sentence for the Ask window. Both SDKs throw their own
    /// types (<see cref="AnthropicApiException"/> with the HTTP status and body,
    /// <see cref="ClientResultException"/> for OpenAI-compatible endpoints), so the status and
    /// the server's own message are read from whichever arrived.
    /// </summary>
    public static class AiErrorText
    {
        /// <summary>Shown for a 401.</summary>
        internal const string KeyRejected = "The key was rejected. Check it in Settings > AI.";

        /// <summary>Shown when nothing answered in time.</summary>
        internal const string TimedOut = "The AI service did not answer for 60 seconds. Try again.";

        /// <summary>Shown when the endpoint could not be reached at all.</summary>
        internal const string Unreachable = "Could not reach the AI service. Check the network, or the base URL in Settings > AI, and try again.";

        /// <summary>Shown for 429, 503 and 529 after the SDK's own retries.</summary>
        internal const string Busy = "The AI service is busy or rate-limited. Wait a minute and try again.";

        private const int MaxServerMessage = 300;

        // A server saying it cannot do tool calls: Ollama "does not support tools", vLLM
        // "tool choice requires --enable-auto-tool-choice", llama.cpp "tools param requires --jinja".
        private static readonly Regex ToolsRejected = new(
            "\\btools?\\b.*\\b(support|supported|enable|enabled|requires?|not allowed|unrecognized|unknown)\\b|" +
            "\\b(support|supported|enable|enabled)\\b.*\\btools?\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        /// <summary>A user-facing sentence for <paramref name="ex"/>; never the raw stack or type name.</summary>
        public static string Describe(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            ex = Unwrap(ex);
            if (IsTimeout(ex)) return TimedOut;

            int status = StatusOf(ex);
            if (status == 0)
            {
                if (ex is HttpRequestException || ex is AnthropicIOException || ex is ClientResultException)
                    return Unreachable;
                return "The AI request failed: " + Shorten(FirstLine(ex.Message));
            }

            string server = ServerMessage(ex);
            return status switch
            {
                401 => KeyRejected,
                403 => "The AI service refused the request (403). Check that this key may use this model.",
                429 or 503 or 529 => Busy,
                _ => "The AI service said: " + (server.Length > 0 ? server : "HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
        }

        /// <summary>
        /// True when an endpoint rejected the request because it cannot call tools (some local
        /// models), which switches the assistant to limited mode instead of failing.
        /// </summary>
        internal static bool IsToolsUnsupported(Exception ex)
        {
            ex = Unwrap(ex);
            if (ex is OperationCanceledException || IsTimeout(ex)) return false;
            int status = StatusOf(ex);
            if (status is 401 or 403 or 429 || status >= 502) return false;
            if (status == 0 && (ex is HttpRequestException || ex is AnthropicIOException)) return false;
            string text = ServerMessage(ex);
            return ToolsRejected.IsMatch(text.Length > 0 ? text : ex.Message);
        }

        /// <summary>
        /// The OpenAI SDK's retry policy reports a network failure as an AggregateException of the
        /// attempts; the last attempt's own exception is the one to classify.
        /// </summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException aggregate)
            {
                Exception? inner = aggregate.Flatten().InnerExceptions.LastOrDefault();
                if (inner == null) break;
                ex = inner;
            }
            return ex;
        }

        private static bool IsTimeout(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is TimeoutException || e is TaskCanceledException) return true;
            }
            return false;
        }

        private static int StatusOf(Exception ex) => ex switch
        {
            AnthropicApiException api => (int)api.StatusCode,
            ClientResultException result => result.Status,
            HttpRequestException http when http.StatusCode.HasValue => (int)http.StatusCode.Value,
            _ => 0,
        };

        /// <summary>The server's own error message from the response body, or empty.</summary>
        private static string ServerMessage(Exception ex)
        {
            string? body = null;
            try
            {
                body = ex switch
                {
                    AnthropicApiException api => api.ResponseBody,
                    ClientResultException result => result.GetRawResponse()?.Content?.ToString(),
                    _ => null,
                };
            }
            catch (Exception readFailure) when (readFailure is InvalidOperationException or ObjectDisposedException)
            {
                body = null;
            }
            if (string.IsNullOrWhiteSpace(body)) return "";

            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                // Anthropic and OpenAI both use {"error": {"message": "..."}}; some servers send {"error": "..."}.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String) return Shorten(error.GetString() ?? "");
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) &&
                        message.ValueKind == JsonValueKind.String)
                        return Shorten(message.GetString() ?? "");
                }
            }
            catch (JsonException)
            {
                // Not JSON: fall through to the raw text.
            }
            return Shorten(FirstLine(body));
        }

        private static string FirstLine(string text)
        {
            string trimmed = text.Trim();
            int newline = trimmed.IndexOf('\n');
            return (newline < 0 ? trimmed : trimmed[..newline]).Trim();
        }

        private static string Shorten(string text) =>
            text.Length <= MaxServerMessage ? text : text[..MaxServerMessage] + "...";
    }
}
