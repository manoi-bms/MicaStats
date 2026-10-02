using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// <c>POST {server}/rerank</c> in the Cohere/Jina format: <c>{"model", "query", "documents",
    /// "top_n"}</c>, answered with <c>results[]</c> of <c>index</c> and <c>relevance_score</c>,
    /// returned best first.
    /// </summary>
    public sealed class RerankClient : IReranker, IDisposable
    {
        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake server; null uses the system's network settings without redirects.</param>
        public RerankClient(HttpMessageHandler? handler = null) => _http = SearchHttp.CreateClient(handler);

        public async Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel)
        {
            var body = new Dictionary<string, object> { ["query"] = SearchHttp.Clean(query), ["documents"] = SearchHttp.Clean(documents), ["top_n"] = topN };
            if (server.Model.Length > 0) body["model"] = server.Model;

            var (json, failure, status) = await SearchHttp.PostAsync(_http, server, "/rerank", body, timeout, cancel).ConfigureAwait(false);
            if (json == null) return new RerankResult(null, failure, status);
            using (json) return Parse(json.RootElement, documents.Count, status);
        }

        private static RerankResult Parse(JsonElement root, int count, int? status)
        {
            var bad = new RerankResult(null, SearchFailure.BadAnswer, status);
            try
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    return bad;

                var seen = new HashSet<int>();
                var scores = new List<RerankScore>();
                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out int index)) return bad;
                    if (!item.TryGetProperty("relevance_score", out var sc) || sc.ValueKind != JsonValueKind.Number) return bad;
                    if (index < 0 || index >= count || !seen.Add(index)) return bad;
                    scores.Add(new RerankScore(index, sc.GetDouble()));
                }
                return new RerankResult(scores.OrderByDescending(s => s.Score).ThenBy(s => s.Index).ToList(), SearchFailure.None, status);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return bad;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
