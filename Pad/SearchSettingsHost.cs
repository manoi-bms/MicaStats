using System;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What Settings → MicaPad → Search reads and writes, handed in so tests build the panel over temp stores.</summary>
    public sealed class SearchSettingsHost
    {
        public required AppConfig Config { get; init; }

        /// <summary>Writes the config to disk now.</summary>
        public required Action Save { get; init; }

        /// <summary>Where the two keys go; never the config.</summary>
        public required SecretStore Secrets { get; init; }

        /// <summary>The running search, for the index line and Rebuild; null before MicaPad first opens.</summary>
        public Func<NoteSearchService?> Service { get; init; } = () => null;

        /// <summary>For the Test buttons.</summary>
        public IEmbedder Embedder { get; init; } = new EmbeddingClient();

        public IReranker Reranker { get; init; } = new RerankClient();
    }
}
