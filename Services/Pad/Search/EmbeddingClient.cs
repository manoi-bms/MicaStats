using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// <c>POST {server}/embeddings</c> in the OpenAI format: <c>{"model", "input": [texts]}</c>
    /// (no <c>model</c> when Settings has none), answered with <c>data[].embedding</c> placed by
    /// <c>data[].index</c>. A count or dimension that does not match is a bad answer.
    /// </summary>
    public sealed class EmbeddingClient : IEmbedder, IDisposable
    {
        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake server; null uses the system's network settings without redirects.</param>
        public EmbeddingClient(HttpMessageHandler? handler = null) => _http = SearchHttp.CreateClient(handler);

        public async Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel)
        {
            var body = new Dictionary<string, object> { ["input"] = texts };
            if (server.Model.Length > 0) body["model"] = server.Model;

            var (json, failure, status) = await SearchHttp.PostAsync(_http, server, "/embeddings", body, timeout, cancel).ConfigureAwait(false);
            if (json == null) return new EmbeddingResult(null, failure, status);
            using (json) return Parse(json.RootElement, texts.Count, status);
        }

        private static EmbeddingResult Parse(JsonElement root, int count, int? status)
        {
            var bad = new EmbeddingResult(null, SearchFailure.BadAnswer, status);
            try
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != count)
                    return bad;

                var vectors = new float[count][];
                int position = 0, dimension = -1;
                foreach (var item in data.EnumerateArray())
                {
                    int index = item.TryGetProperty("index", out var ix) && ix.TryGetInt32(out int i) ? i : position;
                    position++;
                    if (index < 0 || index >= count || vectors[index] != null) return bad;
                    if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array) return bad;

                    var vector = new float[embedding.GetArrayLength()];
                    int k = 0;
                    foreach (var x in embedding.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Number) return bad;
                        vector[k++] = x.GetSingle();
                    }
                    if (vector.Length == 0 || (dimension >= 0 && vector.Length != dimension)) return bad;
                    dimension = vector.Length;
                    vectors[index] = vector;
                }
                return new EmbeddingResult(vectors, SearchFailure.None, status);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return bad;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
