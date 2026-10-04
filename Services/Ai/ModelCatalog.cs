using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Models;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// One model of a provider's list, after cleaning: what a provider says is data, not trusted text.
    /// </summary>
    /// <param name="Id">The model's id as plain text: at most 200 characters, no control or format characters, never empty.</param>
    /// <param name="ContextTokens">Its context window in tokens, 1,024 to 2,000,000; 0 when the provider reported none.</param>
    /// <param name="MaxOutputTokens">Its largest output in tokens, 256 to 2,000,000 and never above the window; 0 when not reported.</param>
    public sealed record AiModelInfo(string Id, int ContextTokens, int MaxOutputTokens);

    /// <summary>A provider's models, or the reason there are none.</summary>
    /// <param name="Models">In the provider's order, at most 500, each id once. Empty when there is a <paramref name="Problem"/>.</param>
    /// <param name="Problem">A sentence for the user, ending in a full stop, or null. It never holds a key, an address or the server's own words.</param>
    /// <param name="Host">The host that was asked (or would have been): "api.anthropic.com", or the base URL's host; "" when the base URL is no address.</param>
    public sealed record AiModelList(IReadOnlyList<AiModelInfo> Models, string? Problem, string Host);

    /// <summary>
    /// Asks the provider of the AI settings for its models and their limits (spec 2026-10-05, 1.1 and
    /// 1.2). The request goes only to the configured provider, with the saved key, and what comes
    /// back is cleaned before anything else sees it: the server may be any machine the user pointed
    /// the app at. Nothing here decides when to ask: the caller does, and never while AI is off.
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>
        /// What no server can exceed: the models kept of a list, the characters of an id, and the
        /// bytes of an answer that are read (a longer answer is a failure, not a longer read).
        /// </summary>
        public const int MaxModels = 500, MaxIdChars = 200, MaxAnswerBytes = 4 * 1024 * 1024;

        /// <summary>The whole listing, every page and retry of it, gives up after this.</summary>
        public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

        internal const string NotAList = "The server's answer was not a list of models.";
        internal const string TooLong = "The server's answer was too long to read: over 4 MB.";
        internal const string TimedOut = "The AI service did not answer for 15 seconds. Try again.";
        internal const string Cancelled = "Loading the model list was cancelled.";
        internal const string Failed = "The model list could not be loaded.";

        private const string ClaudeHost = "api.anthropic.com";

        // A window is a whole number of tokens from 1,024; over 2,000,000 counts as 2,000,000.
        // A largest output is one from 256. Anything else is "not reported".
        private const int MinWindow = 1024, MinOutput = 256, MaxTokens = 2_000_000;

        // Claude's list: models asked for per page, and pages followed at most.
        private const int ClaudePageSize = 100, MaxPages = 10;

        // The window is the first of these a model has a usable number for, then meta.n_ctx_train
        // (spec 1.1); the output the first of the next, then top_provider.max_completion_tokens.
        private static readonly string[] WindowFields =
            { "max_model_len", "context_length", "context_window", "max_context_length", "max_input_tokens" };
        private static readonly string[] OutputFields = { "max_output_tokens", "max_completion_tokens" };

        /// <summary>
        /// Asks the provider of these settings for its models. Never throws for anything the settings,
        /// the network or the server do: a failure is a <see cref="AiModelList.Problem"/>. (A null
        /// <paramref name="config"/> or <paramref name="secrets"/> is the caller's mistake and throws.)
        /// <paramref name="handler"/> replaces the network for tests and is never disposed here.
        /// </summary>
        public static Task<AiModelList> ListAsync(AppConfig config, SecretStore secrets, HttpMessageHandler? handler, CancellationToken ct) =>
            ListAsync(config, secrets, handler, null, ListTimeout, ct);

        /// <summary>
        /// The same, and <paramref name="warn"/> is told of a failure in one line for the log: the
        /// host and the kind of failure only, never a key, a path or query, or anything the server said.
        /// </summary>
        public static Task<AiModelList> ListAsync(AppConfig config, SecretStore secrets, HttpMessageHandler? handler,
                                                  Action<string>? warn, CancellationToken ct) =>
            ListAsync(config, secrets, handler, warn, ListTimeout, ct);

        /// <summary>The same with another deadline, for tests.</summary>
        internal static async Task<AiModelList> ListAsync(AppConfig config, SecretStore secrets, HttpMessageHandler? handler,
                                                          Action<string>? warn, TimeSpan timeout, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(secrets);

            string host = "";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                // The settings and the key are read here, before the first wait, so one listing asks
                // one provider even if the settings change while it runs.
                bool claude = config.AiProvider != AiProviders.OpenAiCompatible;
                Uri? endpoint = null;
                if (claude) host = ClaudeHost;
                else if (AiProviderFactory.TryCompatibleEndpoint(config, out endpoint)) host = endpoint.Host;
                else return new AiModelList(Array.Empty<AiModelInfo>(), AiProviderFactory.BadUrl, "");

                string? key = claude ? AiProviderFactory.ClaudeKey(secrets) : AiProviderFactory.CompatibleKey(secrets);
                if (claude && key == null) return new AiModelList(Array.Empty<AiModelInfo>(), AiProviderFactory.NoKey, host);
                if (ct.IsCancellationRequested) return new AiModelList(Array.Empty<AiModelInfo>(), Cancelled, host);

                deadline.CancelAfter(timeout);
                // Redirects are not followed, and no answer is read past MaxAnswerBytes, whoever reads it.
                var capped = new CappedAnswers(handler ?? NewNetworkHandler());
                using HttpClient http = AiProviderFactory.NewHttpClient(capped, owned: handler == null, timeout);

                IReadOnlyList<AiModelInfo> models = claude
                    ? await ListClaudeAsync(key!, http, deadline.Token).ConfigureAwait(false)
                    : await ListCompatibleAsync(endpoint!, key, http, deadline.Token).ConfigureAwait(false);
                return new AiModelList(models, null, host);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return new AiModelList(Array.Empty<AiModelInfo>(), Cancelled, host);

                (string problem, string kind) = Explain(ex, deadline.IsCancellationRequested);
                Tell(warn, "Listing the models" + (host.Length > 0 ? " of " + host : "") + " failed (" + kind + ")");
                return new AiModelList(Array.Empty<AiModelInfo>(), problem, host);
            }
        }

        /// <summary>
        /// The network for a list request: as a question's, except that no redirect is followed. A
        /// redirect could carry the key to another host (Claude's travels in a header of its own,
        /// which the handler would not drop), so one is a failure here.
        /// </summary>
        internal static HttpMessageHandler NewNetworkHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

        // ----- Claude: through the SDK ------------------------------------------------------------

        private static async Task<IReadOnlyList<AiModelInfo>> ListClaudeAsync(string key, HttpClient http, CancellationToken ct)
        {
            // The same client a question uses: this key, api.anthropic.com only, two retries.
            using AnthropicClient client = AiProviderFactory.NewAnthropicClient(key, http);
            var kept = new List<AiModelInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            ModelListPage page = await client.Models.List(new ModelListParams { Limit = ClaudePageSize }, ct).ConfigureAwait(false);
            for (int pages = 1; ; pages++)
            {
                int before = kept.Count;
                foreach (ModelInfo model in page.Items)
                {
                    if (kept.Count >= MaxModels) break;
                    Keep(kept, seen, IdOf(model), Read(model, m => m.MaxInputTokens), Read(model, m => m.MaxTokens));
                }

                // A page that adds nothing new ends it too: a list that only repeats itself is not followed.
                // (The SDK's HasNext is false for a page that does not say whether more follows, or after which id.)
                if (kept.Count >= MaxModels || kept.Count == before || pages >= MaxPages || !page.HasNext()) break;
                page = await page.Next(ct).ConfigureAwait(false);
            }
            return kept;
        }

        // The SDK reads a member of a model when it is asked for, and throws AnthropicInvalidDataException
        // if it is missing or not what the API promises (an id that is a number, a limit that is text
        // or a fraction). One odd model must not spoil the list: its id is then none, its number 0.

        private static string? IdOf(ModelInfo model)
        {
            try { return model.ID; }
            catch (Exception ex) when (IsBadData(ex)) { return null; }
        }

        private static long Read(ModelInfo model, Func<ModelInfo, long?> number)
        {
            try { return number(model) ?? 0; }
            catch (Exception ex) when (IsBadData(ex)) { return 0; }
        }

        // The last two are what System.Text.Json itself throws for a value of another kind.
        private static bool IsBadData(Exception ex) =>
            ex is AnthropicInvalidDataException or JsonException or InvalidOperationException;

        // ----- A compatible server: plain JSON ----------------------------------------------------

        private static async Task<IReadOnlyList<AiModelInfo>> ListCompatibleAsync(Uri endpoint, string? key, HttpClient http, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsAddress(endpoint));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Only a saved key is sent: a server without one gets no Authorization header at all.
            if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // The status alone decides: the body of a refusal is the server's own words, and none of it is used.
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(null, null, response.StatusCode);

            // Already whole and at most MaxAnswerBytes: CappedAnswers read it.
            byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (!TryParseCompatible(body, out IReadOnlyList<AiModelInfo> models)) throw new NotAListException();
            return models;
        }

        /// <summary>The base URL with one trailing slash removed, then "/models"; a query stays, a fragment goes.</summary>
        private static Uri ModelsAddress(Uri endpoint)
        {
            string left = endpoint.GetLeftPart(UriPartial.Path);
            if (left.EndsWith('/')) left = left[..^1];
            return new Uri(left + "/models" + endpoint.Query);
        }

        /// <summary>
        /// The compatible server's list as it is parsed: pure, for tests. The models under <c>data</c>,
        /// or of an answer that is itself an array; none when the text is not such a list. Never throws.
        /// </summary>
        internal static IReadOnlyList<AiModelInfo> ParseCompatible(string json)
        {
            TryParseCompatible(Encoding.UTF8.GetBytes(json ?? ""), out IReadOnlyList<AiModelInfo> models);
            return models;
        }

        /// <summary>
        /// Reads a compatible server's answer. False when it is not a list of models at all (not
        /// JSON, or JSON of another shape); true with what could be kept otherwise, which may be nothing.
        /// </summary>
        internal static bool TryParseCompatible(ReadOnlyMemory<byte> utf8, out IReadOnlyList<AiModelInfo> models)
        {
            models = Array.Empty<AiModelInfo>();
            // A byte order mark some servers put first is not part of the JSON.
            ReadOnlySpan<byte> start = utf8.Span;
            if (start.Length >= 3 && start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF) utf8 = utf8[3..];

            try
            {
                using JsonDocument document = JsonDocument.Parse(utf8);
                JsonElement root = document.RootElement;
                JsonElement list;
                if (root.ValueKind == JsonValueKind.Array) list = root;
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement data) &&
                         data.ValueKind == JsonValueKind.Array) list = data;
                else return false;

                var kept = new List<AiModelInfo>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (kept.Count >= MaxModels) break;
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement id)) continue;
                    Keep(kept, seen, TextOf(id), FirstTokens(item, WindowFields, "meta", "n_ctx_train", MinWindow),
                         FirstTokens(item, OutputFields, "top_provider", "max_completion_tokens", MinOutput));
                }
                models = kept;
                return true;
            }
            catch (JsonException)
            {
                // Not JSON, or nested deeper than a model list ever is.
                return false;
            }
        }

        /// <summary>
        /// The first number of at least <paramref name="least"/> among <paramref name="fields"/>, then
        /// under <paramref name="parent"/>.<paramref name="child"/>; 0 when the model has none. A field
        /// that holds something else is passed over, not a failure.
        /// </summary>
        private static long FirstTokens(JsonElement model, string[] fields, string parent, string child, int least)
        {
            foreach (string field in fields)
            {
                if (!model.TryGetProperty(field, out JsonElement value)) continue;
                long tokens = TokensOf(value);
                if (tokens >= least) return tokens;
            }
            if (model.TryGetProperty(parent, out JsonElement inner) && inner.ValueKind == JsonValueKind.Object &&
                inner.TryGetProperty(child, out JsonElement nested))
            {
                long tokens = TokensOf(nested);
                if (tokens >= least) return tokens;
            }
            return 0;
        }

        /// <summary>
        /// A token count as a server wrote it: a JSON whole number, or a string of nothing but the
        /// digits 0 to 9. 0 for anything else: a fraction, an exponent, a number past a long, a sign,
        /// white space, text.
        /// </summary>
        private static long TokensOf(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out long number) && number > 0 ? number : 0;
            if (value.ValueKind != JsonValueKind.String) return 0;
            return long.TryParse(TextOf(value), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0 ? parsed : 0;
        }

        /// <summary>The text of a JSON string; null for anything else, and for a string that is not text (half a surrogate pair).</summary>
        private static string? TextOf(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String) return null;
            try { return value.GetString(); }
            catch (InvalidOperationException) { return null; }
        }

        // ----- What is kept of a model, whichever provider gave it --------------------------------

        private static void Keep(List<AiModelInfo> kept, HashSet<string> seen, string? id, long window, long output)
        {
            string clean = CleanId(id);
            // No usable id: the model is dropped. An id seen before: the first one stays.
            if (clean.Length == 0 || !seen.Add(clean)) return;

            int context = window < MinWindow ? 0 : (int)Math.Min(window, MaxTokens);
            int most = output < MinOutput ? 0 : (int)Math.Min(output, MaxTokens);
            if (context > 0 && most > context) most = context;
            kept.Add(new AiModelInfo(clean, context, most));
        }

        /// <summary>
        /// A model id as plain text for a list: control characters, format characters (the
        /// right-to-left override is one), line and paragraph separators and halves of surrogate
        /// pairs are removed, surrounding white space is trimmed, and at most
        /// <see cref="MaxIdChars"/> characters are kept, never ending in half a pair. "" when nothing is left.
        /// </summary>
        internal static string CleanId(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            var text = new StringBuilder(Math.Min(raw.Length, MaxIdChars));
            Span<char> units = stackalloc char[2];
            for (int i = 0; i < raw.Length;)
            {
                if (!Rune.TryGetRuneAt(raw, i, out Rune rune))
                {
                    i++;        // half a surrogate pair
                    continue;
                }
                i += rune.Utf16SequenceLength;

                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate) continue;
                if (text.Length == 0 && Rune.IsWhiteSpace(rune)) continue;
                // Whole characters only: one that does not fit is left out, and nothing after it is read.
                if (text.Length + rune.Utf16SequenceLength > MaxIdChars) break;
                ReadOnlySpan<char> written = units[..rune.EncodeToUtf16(units)];
                text.Append(written);
            }

            int end = text.Length;
            while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
            return text.ToString(0, end);
        }

        // ----- What limits belong to -------------------------------------------------------------

        /// <summary>
        /// What the limits belong to, in one string for <see cref="AppConfig.AiModelLimitsOf"/>: the
        /// provider, the address's scheme, host and port (lower case, a default port left out, never
        /// the path, the query or a user name), and the model exactly as configured. Compare with
        /// <see cref="StringComparison.Ordinal"/>. It holds no key, and is never empty.
        /// </summary>
        public static string KeyOf(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (config.AiProvider != AiProviders.OpenAiCompatible)
                return AiProviders.Claude + "|" + AiProviderFactory.ClaudeBaseUrl + "|" + (config.AiClaudeModel ?? "");

            string address = "";
            if (AiProviderFactory.TryCompatibleEndpoint(config, out Uri? endpoint))
            {
                address = endpoint.Scheme.ToLowerInvariant() + "://" + endpoint.Host.ToLowerInvariant();
                if (!endpoint.IsDefaultPort) address += ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
            }
            return AiProviders.OpenAiCompatible + "|" + address + "|" + (config.AiCompatibleModel ?? "");
        }

        // ----- Failures --------------------------------------------------------------------------

        /// <summary>The answer was JSON of another shape, or not JSON.</summary>
        private sealed class NotAListException : Exception
        {
        }

        /// <summary>The answer was longer than <see cref="MaxAnswerBytes"/>.</summary>
        private sealed class AnswerTooLongException : Exception
        {
        }

        /// <summary>
        /// The sentence for the user and the word for the log. Neither is built from an exception's
        /// message or a response body: those can quote an address, a key or the server's own text.
        /// </summary>
        private static (string Problem, string Kind) Explain(Exception ex, bool deadlinePassed)
        {
            string kind = ex.GetType().Name;
            if (Find<AnswerTooLongException>(ex) != null) return (TooLong, "over 4 MB");
            if (deadlinePassed || Find<TimeoutException>(ex) != null || Find<OperationCanceledException>(ex) != null) return (TimedOut, kind);

            // A status: the provider's sentence for it, from the number alone.
            HttpStatusCode? status = Find<AnthropicApiException>(ex)?.StatusCode ?? Find<HttpRequestException>(ex)?.StatusCode;
            if (status is HttpStatusCode code)
            {
                string said = AiErrorText.Describe(new HttpRequestException(null, null, code));
                return (said.EndsWith('.') ? said : said + ".", "HTTP " + ((int)code).ToString(CultureInfo.InvariantCulture));
            }

            // Ours for a compatible server; the other two are the SDK failing to read a page of Claude's models.
            if (ex is NotAListException || Find<AnthropicInvalidDataException>(ex) != null || Find<JsonException>(ex) != null)
                return (NotAList, "not a list");
            // Nothing answered, or the connection broke while the answer was read.
            if (Find<HttpRequestException>(ex) != null || Find<AnthropicIOException>(ex) != null || Find<IOException>(ex) != null)
                return (AiErrorText.Unreachable, kind);
            return (Failed, kind);
        }

        /// <summary>The first <typeparamref name="T"/> in <paramref name="ex"/> or under it.</summary>
        private static T? Find<T>(Exception ex) where T : Exception
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is T found) return found;
                if (e is AggregateException many)
                {
                    foreach (Exception inner in many.InnerExceptions)
                    {
                        if (Find<T>(inner) is T deeper) return deeper;
                    }
                }
            }
            return null;
        }

        private static void Tell(Action<string>? warn, string line)
        {
            if (warn == null) return;
            try { warn(line); }
            catch (Exception)
            {
                // A log that fails must not turn a listing into an exception.
            }
        }

        /// <summary>
        /// Reads every answer whole, up to <see cref="MaxAnswerBytes"/>, before anything else sees it:
        /// a longer one is a failure and is not read further. It sits under the HTTP client, so the
        /// Anthropic SDK is held to the same limit as the plain request.
        /// </summary>
        private sealed class CappedAnswers : DelegatingHandler
        {
            public CappedAnswers(HttpMessageHandler inner) : base(inner)
            {
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                try
                {
                    HttpContent original = response.Content;
                    var whole = new ByteArrayContent(await ReadCappedAsync(original, cancellationToken).ConfigureAwait(false));
                    foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
                    {
                        if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                            whole.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    response.Content = whole;
                    original.Dispose();
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
            {
                // A length declared over the limit is not read at all.
                if (content.Headers.ContentLength is long declared && declared > MaxAnswerBytes) throw new AnswerTooLongException();

                using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var whole = new MemoryStream();
                byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        int read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                        if (read == 0) return whole.ToArray();
                        if (whole.Length + read > MaxAnswerBytes) throw new AnswerTooLongException();
                        whole.Write(chunk, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                }
            }
        }
    }
}
