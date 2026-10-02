namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Settings → MicaPad → Search as the search code needs it: meaning search and its server,
    /// reranking and its server. A server is null when Settings has no address for it.
    /// </summary>
    public sealed record SearchSettings(bool Meaning, SearchServer? Embedding, bool Rerank, SearchServer? Reranker)
    {
        /// <summary>Words only: nothing leaves the PC.</summary>
        public static SearchSettings Off { get; } = new(false, null, false, null);

        /// <summary>The server and model the vectors belong to; "" without a server.</summary>
        public string Fingerprint => Embedding == null ? "" : Embedding.BaseUrl + "|" + Embedding.Model;

        /// <summary>Passages and queries may be sent to the embedding server.</summary>
        public bool CanEmbed => Meaning && Embedding != null;

        /// <summary>Results may be sent to the reranker (only with meaning search, spec 2).</summary>
        public bool CanRerank => CanEmbed && Rerank && Reranker != null;
    }

    /// <summary>
    /// What the index holds and what it is waiting for (Settings status line, pane status line).
    /// <see cref="Refused"/>: passages the embedding server would not take on their own (too long
    /// for its model, say); they are found by words only until Settings change or the index is rebuilt.
    /// </summary>
    public sealed record IndexProgress(int Notes, int Passages, int WithVectors, int Waiting, SearchFailure LastFailure, int? LastStatus, bool Full, int Refused = 0)
    {
        /// <summary>Nothing indexed yet.</summary>
        public static IndexProgress Empty { get; } = new(0, 0, 0, 0, SearchFailure.None, null, false);
    }
}
