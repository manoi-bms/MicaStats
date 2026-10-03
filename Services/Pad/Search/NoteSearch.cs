using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>One search's results and how it ran, for the pane's list and status line.</summary>
    public sealed record SearchOutcome(
        string Query,
        IReadOnlyList<Passage> Hits,
        bool UsedMeaning,
        bool Reranked,
        SearchFailure EmbedFailure,
        int? EmbedStatus,
        SearchFailure RerankFailure,
        int? RerankStatus,
        IndexProgress Progress)
    {
        public static SearchOutcome Empty(string query, IndexProgress progress) =>
            new(query, Array.Empty<Passage>(), false, false, SearchFailure.None, null, SearchFailure.None, null, progress);
    }

    /// <summary>
    /// The query pipeline (spec 3.5): keyword top 50; with meaning search, the query is embedded
    /// (5 s, the last 20 cached) and the top 50 passages by cosine join by Reciprocal Rank Fusion;
    /// with reranking, the top 40 are reordered by the reranker (8 s); then 3 per note, 20 in all.
    /// A step that fails is skipped and named in the outcome. Cancelling throws. Credential
    /// references in the query, whole or cut at either end (a query can be a selection that ran
    /// through one), become <c>[credential]</c> before anything (spec 3.1), and Settings
    /// are read again after the query embedding, so what the owner switched off meanwhile is not used.
    /// </summary>
    public sealed class NoteSearch
    {
        public const int KeywordTop = 50;
        public const int VectorTop = 50;
        public const int RerankTop = 40;
        public const int PerNote = 3;
        public const int Total = 20;
        public const int QueryCacheSize = 20;
        public static readonly TimeSpan QueryEmbedTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan RerankTimeout = TimeSpan.FromSeconds(8);

        private readonly SearchIndexer _indexer;
        private readonly IEmbedder _embedder;
        private readonly IReranker _reranker;
        private readonly Func<SearchSettings> _settings;
        private readonly object _cacheGate = new();
        private readonly LinkedList<(string Key, float[] Vector)> _cache = new();

        public NoteSearch(SearchIndexer indexer, IEmbedder embedder, IReranker reranker, Func<SearchSettings> settings)
        {
            _indexer = indexer;
            _embedder = embedder;
            _reranker = reranker;
            _settings = settings;
        }

        public async Task<SearchOutcome> SearchAsync(string query, CancellationToken cancel)
        {
            query = NotePassages.WithoutSecretParts(query.Trim());
            var progress = _indexer.Progress;
            if (query.Length == 0) return SearchOutcome.Empty(query, progress);

            var settings = _settings();
            var keyword = _indexer.Keywords.Search(query, KeywordTop).Select(h => h.Passage).ToList();
            cancel.ThrowIfCancellationRequested();

            var vector = new List<Passage>();
            bool usedMeaning = false;
            SearchFailure embedFailure = SearchFailure.None, rerankFailure = SearchFailure.None;
            int? embedStatus = null, rerankStatus = null;

            // What may be used and sent from here on: Settings as they are once the query vector is back.
            var now = settings;
            if (settings.CanEmbed)
            {
                var (queryVector, failure, status) = await QueryVectorAsync(settings, query, cancel).ConfigureAwait(false);
                embedFailure = failure;
                embedStatus = status;
                now = _settings();
                // Meaning search turned off, or another server or model, while the query was out: words only.
                if (queryVector != null && now.CanEmbed && now.Fingerprint == settings.Fingerprint)
                {
                    usedMeaning = true;
                    var passages = _indexer.Keywords.AllPassages();
                    var byHash = passages.ToLookup(p => p.Hash, StringComparer.Ordinal);
                    foreach (var hit in _indexer.Vectors.Nearest(queryVector, passages.Select(p => p.Hash), VectorTop))
                        vector.AddRange(byHash[hit.Hash]);
                }
            }
            cancel.ThrowIfCancellationRequested();

            IReadOnlyList<Passage> ordered = SearchFusion.Fuse(keyword, vector);
            bool reranked = false;

            if (now.CanRerank && ordered.Count > 0)
            {
                var top = ordered.Take(RerankTop).ToList();
                var result = await _reranker.RerankAsync(now.Reranker!, query, top.Select(p => p.SentText).ToList(), top.Count, RerankTimeout, cancel).ConfigureAwait(false);
                if (result.Ranked != null)
                {
                    reranked = true;
                    ordered = result.Ranked.Select(r => top[r.Index]).ToList();
                }
                else
                {
                    rerankFailure = result.Failure;
                    rerankStatus = result.Status;
                }
            }

            return new SearchOutcome(query, SearchFusion.Cap(ordered, PerNote, Total), usedMeaning, reranked,
                                     embedFailure, embedStatus, rerankFailure, rerankStatus, _indexer.Progress);
        }

        private async Task<(float[]? Vector, SearchFailure Failure, int? Status)> QueryVectorAsync(SearchSettings settings, string query, CancellationToken cancel)
        {
            // After a Rebuild or a reset of the vectors the model may have changed behind the same
            // address: a vector cached before is not used (its length may no longer match).
            string key = _indexer.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + settings.Fingerprint + "\n" + query;
            lock (_cacheGate)
            {
                var node = _cache.First;
                while (node != null && node.Value.Key != key) node = node.Next;
                if (node != null)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return (node.Value.Vector, SearchFailure.None, null);
                }
            }

            var result = await _embedder.EmbedAsync(settings.Embedding!, new[] { query }, QueryEmbedTimeout, cancel).ConfigureAwait(false);
            if (result.Vectors == null) return (null, result.Failure, result.Status);

            lock (_cacheGate)
            {
                _cache.AddFirst((key, result.Vectors[0]));
                while (_cache.Count > QueryCacheSize) _cache.RemoveLast();
            }
            return (result.Vectors[0], SearchFailure.None, null);
        }
    }

    /// <summary>The pane's status line and the Settings index line (spec 1 and 2).</summary>
    public static class SearchStatusText
    {
        public static string For(SearchOutcome outcome, SearchSettings settings)
        {
            string how;
            if (!settings.Meaning) how = "Words";
            else if (settings.Embedding == null) how = "Words only: set the embedding server in Settings";
            else if (!outcome.UsedMeaning) how = "Words only: the embedding server " + SearchFailureText.Describe(outcome.EmbedFailure, outcome.EmbedStatus);
            else if (outcome.Reranked) how = "Meaning + words, reranked";
            else if (settings.CanRerank && outcome.RerankFailure != SearchFailure.None)
                how = "Meaning + words. Not reranked: the reranker " + SearchFailureText.Describe(outcome.RerankFailure, outcome.RerankStatus);
            else how = "Meaning + words";

            return outcome.Progress.Waiting > 0 && settings.CanEmbed ? how + " · " + Index(outcome.Progress, settings) : how;
        }

        public static string Index(IndexProgress p, SearchSettings settings)
        {
            string refused = p.Refused == 0 ? ""
                : "; " + p.Refused + (p.Refused == 1 ? " passage" : " passages") + " the server refused";
            if (settings.CanEmbed && p.Waiting > 0)
            {
                return p.LastFailure != SearchFailure.None
                    ? "Indexing paused: the embedding server " + SearchFailureText.Describe(p.LastFailure, p.LastStatus)
                    : "Indexing: " + p.WithVectors + " of " + p.Passages + " passages" + refused;
            }
            string words = p.Passages + " passages from " + p.Notes + " notes";
            if (!settings.CanEmbed) return words;
            string meaning = words + "; " + p.WithVectors + " with meaning" + refused;
            return p.Full ? meaning + ". The index is full: the rest are found by words only." : meaning;
        }
    }
}
