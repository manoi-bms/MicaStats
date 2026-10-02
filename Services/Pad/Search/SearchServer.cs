using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Why a call to the embedding server or the reranker gave no answer (spec 3.3).</summary>
    public enum SearchFailure { None, Unreachable, KeyRefused, RateLimited, TimedOut, Rejected, ServerError, BadAnswer }

    /// <summary>
    /// A server from Settings: its base address (MicaPad adds the route), the model (may be empty)
    /// and the key (null when none is saved). Printed, the key shows as <c>***</c> (spec 4: keys are
    /// never logged or shown).
    /// </summary>
    public sealed record SearchServer(string BaseUrl, string Model, string? Key)
    {
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append("BaseUrl = ").Append(BaseUrl)
                   .Append(", Model = ").Append(Model)
                   .Append(", Key = ").Append(Key == null ? "" : "***");
            return true;
        }
    }

    /// <summary>One vector per text, in the texts' order, or the failure.</summary>
    public sealed record EmbeddingResult(IReadOnlyList<float[]>? Vectors, SearchFailure Failure, int? Status);

    /// <summary>A document's index in the request and its relevance.</summary>
    public readonly record struct RerankScore(int Index, double Score);

    /// <summary>The documents best first, or the failure.</summary>
    public sealed record RerankResult(IReadOnlyList<RerankScore>? Ranked, SearchFailure Failure, int? Status);

    /// <summary>Turns texts into vectors. Throws only <see cref="OperationCanceledException"/>, when the caller cancels.</summary>
    public interface IEmbedder
    {
        Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel);
    }

    /// <summary>Orders documents by relevance to a query. Throws only <see cref="OperationCanceledException"/>, when the caller cancels.</summary>
    public interface IReranker
    {
        Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel);
    }

    /// <summary>How the status line and Settings name a failure, after "the embedding server" or "the reranker".</summary>
    public static class SearchFailureText
    {
        public static string Describe(SearchFailure failure, int? status) => failure switch
        {
            SearchFailure.Unreachable => "could not be reached",
            SearchFailure.KeyRefused => "refused the key",
            SearchFailure.RateLimited => "is busy (too many requests)",
            SearchFailure.TimedOut => "timed out",
            SearchFailure.Rejected => "refused the request (HTTP " + status + ")",
            SearchFailure.ServerError => "failed (HTTP " + status + ")",
            SearchFailure.BadAnswer => "gave an answer MicaPad could not read",
            _ => "answered",
        };
    }
}
