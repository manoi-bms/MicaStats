using System;
using System.IO;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Settings → MicaPad → Search as <see cref="SearchSettings"/>; keys come from the secret store.</summary>
    public static class PadSearchSettings
    {
        public static SearchSettings From(AppConfig config, Func<string, string?> secret)
        {
            SearchServer? embedding = config.PadEmbeddingServer.Length == 0 ? null
                : new SearchServer(config.PadEmbeddingServer, config.PadEmbeddingModel, secret(SecretNames.PadEmbeddingKey));
            SearchServer? reranker = config.PadRerankServer.Length == 0 ? null
                : new SearchServer(config.PadRerankServer, config.PadRerankModel, secret(SecretNames.PadRerankKey));
            return new SearchSettings(config.PadSemanticSearch, embedding, config.PadRerank, reranker);
        }

        /// <summary>True for the config properties that change how search runs.</summary>
        public static bool IsSearchProperty(string? propertyName) => propertyName is
            nameof(AppConfig.PadSemanticSearch) or nameof(AppConfig.PadEmbeddingServer) or nameof(AppConfig.PadEmbeddingModel)
            or nameof(AppConfig.PadRerank) or nameof(AppConfig.PadRerankServer) or nameof(AppConfig.PadRerankModel);
    }

    /// <summary>
    /// MicaPad's search, one per app (spec 3.5): the vectors beside the notes in <c>search\</c>,
    /// the indexer and the query pipeline, sharing the two clients.
    /// </summary>
    public sealed class NoteSearchService : IDisposable
    {
        /// <summary>The folder beside the notes that holds the vectors.</summary>
        public const string FolderName = "search";

        private readonly IDisposable? _ownedEmbedder;
        private readonly IDisposable? _ownedReranker;

        public NoteSearchService(NoteStore store, Func<SearchSettings> settings, IEmbedder? embedder = null, IReranker? reranker = null, Action<string>? warn = null)
        {
            warn ??= message => DiagnosticsLog.Warn("search", message);
            if (embedder == null) { var client = new EmbeddingClient(); embedder = client; _ownedEmbedder = client; }
            if (reranker == null) { var client = new RerankClient(); reranker = client; _ownedReranker = client; }

            Settings = settings;
            var vectors = new VectorStore(Path.Combine(store.Root, FolderName), store.EncryptBytes, store.TryDecryptBytes, warn);
            Indexer = new SearchIndexer(vectors, embedder, settings, store.LoadText, warn);
            Search = new NoteSearch(Indexer, embedder, reranker, settings);
        }

        public Func<SearchSettings> Settings { get; }

        public SearchIndexer Indexer { get; }

        public NoteSearch Search { get; }

        public void Dispose()
        {
            Indexer.Dispose();
            _ownedEmbedder?.Dispose();
            _ownedReranker?.Dispose();
        }
    }
}
