using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchPipelineTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        private static Passage P(string note, int line, string body = "x") =>
            new(note, "t", "", line, line, body, body, "t\n\n" + body, NotePassages.HashOf(note + line));

        [Fact]
        public void Fusion_adds_reciprocal_ranks()
        {
            var a = P("a", 1); var b = P("b", 1); var c = P("c", 1);
            // a: 1/61 + 1/62; b: 1/62 + 1/61; c: 1/63 — a and b tie, a was ranked first sooner
            var fused = SearchFusion.Fuse(new[] { a, b, c }, new[] { b, a });
            Assert.Equal(new[] { a, b, c }, fused.ToArray());
        }

        [Fact]
        public void Fusion_keeps_two_pieces_of_one_long_line_apart()
        {
            var pieces = NotePassages.Cut("a", "t", new string('x', 1500) + new string('y', 1500));
            Assert.Equal(2, pieces.Count);
            Assert.Equal(pieces[0].FirstLine, pieces[1].FirstLine);

            // the second piece: 1/62 + 1/61; the first: 1/61
            var fused = SearchFusion.Fuse(new[] { pieces[0], pieces[1] }, new[] { pieces[1] });

            Assert.Equal(new[] { pieces[1], pieces[0] }, fused.ToArray());
        }

        [Fact]
        public void Fusion_with_one_list_keeps_its_order()
        {
            var a = P("a", 1); var b = P("b", 1);
            Assert.Equal(new[] { a, b }, SearchFusion.Fuse(new[] { a, b }, Array.Empty<Passage>()).ToArray());
        }

        [Fact]
        public void Cap_keeps_three_per_note_and_twenty_in_all()
        {
            var many = Enumerable.Range(1, 5).Select(i => P("a", i)).Concat(Enumerable.Range(1, 30).Select(i => P("n" + i, 1))).ToList();
            var capped = SearchFusion.Cap(many, 3, 20);
            Assert.Equal(20, capped.Count);
            Assert.Equal(3, capped.Count(p => p.NoteId == "a"));
        }

        [Fact]
        public void The_snippet_shows_the_best_line_with_matches_bold()
        {
            var runs = SearchSnippet.Make("intro line\nthe VPN does not connect\nother", "vpn connect");
            Assert.Equal("the VPN does not connect", string.Concat(runs.Select(r => r.Text)).Split('\n')[0]);
            Assert.Equal(new[] { "VPN", "connect" }, runs.Where(r => r.Bold).Select(r => r.Text).ToArray());
        }

        [Fact]
        public void A_Thai_match_is_bold_as_one_run()
        {
            var runs = SearchSnippet.Make("เน็ตไม่ติดตอนเช้า", "ไม่ติด");
            Assert.Equal("ไม่ติด", Assert.Single(runs, r => r.Bold).Text);
        }

        [Fact]
        public void A_meaning_only_hit_shows_its_first_lines()
        {
            var runs = SearchSnippet.Make("\nfirst\nsecond\nthird\nfourth", "unrelated");
            Assert.Equal("first\nsecond\nthird", string.Concat(runs.Select(r => r.Text)));
            Assert.DoesNotContain(runs, r => r.Bold);
        }

        [Fact]
        public void The_snippet_is_short()
        {
            var runs = SearchSnippet.Make(new string('a', 500) + " needle " + new string('b', 500), "needle");
            Assert.True(string.Concat(runs.Select(r => r.Text)).Length <= SearchSnippet.MaxChars + 2);   // plus the two ellipses
            Assert.Contains(runs, r => r.Bold && r.Text == "needle");
        }

        [Fact]
        public void The_passage_is_found_again_after_lines_moved()
        {
            string[] lines = { "new line", "another", "# VPN", "body" };
            Assert.Equal(3, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 1, "# VPN"));
            Assert.Equal(3, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 3, "# VPN"));
            Assert.Equal(4, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 9, "gone"));     // clamped
            Assert.Equal(2, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 2, "gone"));
        }

        // ---- the pipeline ----

        private sealed class FakeEmbedder : IEmbedder
        {
            public Func<string, float[]> Vector { get; set; } = t => t.Contains("connect", StringComparison.Ordinal) || t.Contains("vpn", StringComparison.Ordinal) ? new[] { 1f, 0f } : new[] { 0f, 1f };
            public SearchFailure FailWith { get; set; }
            public TimeSpan Delay { get; set; }
            public int Calls;

            /// <summary>Every text sent, in order.</summary>
            public List<string> Texts { get; } = new();

            /// <summary>Runs inside each call, before it answers (the owner changing Settings meanwhile).</summary>
            public Action? OnCall { get; set; }

            public async Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel)
            {
                Interlocked.Increment(ref Calls);
                lock (Texts) Texts.AddRange(texts);
                OnCall?.Invoke();
                if (Delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(Delay, cancel).WaitAsync(timeout, cancel); }
                    catch (TimeoutException) { return new EmbeddingResult(null, SearchFailure.TimedOut, null); }
                }
                if (FailWith != SearchFailure.None) return new EmbeddingResult(null, FailWith, 503);
                return new EmbeddingResult(texts.Select(Vector).ToList(), SearchFailure.None, 200);
            }
        }

        private sealed class FakeReranker : IReranker
        {
            public SearchFailure FailWith { get; set; }
            public List<IReadOnlyList<string>> Seen { get; } = new();
            public List<string> Queries { get; } = new();

            public Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel)
            {
                Seen.Add(documents.ToList());
                Queries.Add(query);
                if (FailWith != SearchFailure.None) return Task.FromResult(new RerankResult(null, FailWith, 500));
                // reverses the order it was given
                var ranked = Enumerable.Range(0, documents.Count).Reverse().Select((index, rank) => new RerankScore(index, 1.0 / (rank + 1))).ToList();
                return Task.FromResult(new RerankResult(ranked, SearchFailure.None, 200));
            }
        }

        private SearchSettings _settings = new(true, new SearchServer("http://gpu/v1", "m", null), true, new SearchServer("http://gpu/v1", "r", null));

        private async Task<(NoteSearch Search, SearchIndexer Indexer)> Build(FakeEmbedder embedder, FakeReranker reranker)
        {
            var indexer = new SearchIndexer(
                new VectorStore(Path.Combine(_dir.Root, "search"), b => b, (byte[] d, out byte[] p) => { p = d; return true; }),
                embedder, () => _settings, _ => null, retryDelay: _ => TimeSpan.FromHours(1));
            indexer.SetNote("a", "VPN notes", "it does not connect after sleep", DateTime.UtcNow);
            indexer.SetNote("b", "Lunch", "noodles on friday", DateTime.UtcNow);
            await indexer.WhenIdle();
            return (new NoteSearch(indexer, embedder, reranker, () => _settings), indexer);
        }

        [Fact]
        public async Task Meaning_and_words_are_fused_then_reranked()
        {
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(new FakeEmbedder(), reranker);
            using (indexer)
            {
                var outcome = await search.SearchAsync("noodles", default);

                Assert.True(outcome.UsedMeaning);
                Assert.True(outcome.Reranked);
                Assert.Equal(2, outcome.Hits.Count);                    // "b" by words, both by meaning
                Assert.Equal("a", outcome.Hits[0].NoteId);              // the fake reranker reverses the fused order
                Assert.Single(reranker.Seen);
                Assert.Equal("Meaning + words, reranked", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task Words_only_when_meaning_is_off()
        {
            _settings = SearchSettings.Off;
            var embedder = new FakeEmbedder();
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(embedder, reranker);
            using (indexer)
            {
                var outcome = await search.SearchAsync("noodles", default);

                Assert.Equal("b", Assert.Single(outcome.Hits).NoteId);
                Assert.False(outcome.UsedMeaning);
                Assert.Equal(0, embedder.Calls);
                Assert.Empty(reranker.Seen);
                Assert.Equal("Words", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task A_failing_embedding_server_leaves_words_and_says_why()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                embedder.FailWith = SearchFailure.Unreachable;
                var outcome = await search.SearchAsync("noodles", default);

                Assert.Equal("b", outcome.Hits[0].NoteId);
                Assert.False(outcome.UsedMeaning);
                Assert.StartsWith("Words only: the embedding server could not be reached", SearchStatusText.For(outcome, _settings), StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_hanging_embedding_server_times_out_to_words_only()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                embedder.Delay = TimeSpan.FromMinutes(5);
                var started = DateTime.UtcNow;
                var outcome = await search.SearchAsync("noodles", default);

                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(7));
                Assert.Equal(SearchFailure.TimedOut, outcome.EmbedFailure);
                Assert.Equal("b", outcome.Hits[0].NoteId);
            }
        }

        [Fact]
        public async Task A_failing_reranker_keeps_the_fused_order()
        {
            var reranker = new FakeReranker { FailWith = SearchFailure.ServerError };
            var (search, indexer) = await Build(new FakeEmbedder(), reranker);
            using (indexer)
            {
                var outcome = await search.SearchAsync("noodles", default);

                Assert.False(outcome.Reranked);
                Assert.Equal("b", outcome.Hits[0].NoteId);
                Assert.Equal("Meaning + words. Not reranked: the reranker failed (HTTP 500)", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task Turning_meaning_off_while_the_query_is_embedded_sends_nothing_to_the_reranker()
        {
            var embedder = new FakeEmbedder();
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(embedder, reranker);
            using (indexer)
            {
                embedder.OnCall = () => _settings = SearchSettings.Off;   // switched off while the query was out
                var outcome = await search.SearchAsync("noodles", default);

                Assert.Empty(reranker.Seen);
                Assert.False(outcome.UsedMeaning);
                Assert.False(outcome.Reranked);
                Assert.Equal("b", Assert.Single(outcome.Hits).NoteId);   // words only: the query vector is not used
            }
        }

        [Fact]
        public async Task Turning_rerank_off_while_the_query_is_embedded_sends_nothing_to_the_reranker()
        {
            var embedder = new FakeEmbedder();
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(embedder, reranker);
            using (indexer)
            {
                embedder.OnCall = () => _settings = _settings with { Rerank = false };
                var outcome = await search.SearchAsync("noodles", default);

                Assert.Empty(reranker.Seen);
                Assert.True(outcome.UsedMeaning);
                Assert.False(outcome.Reranked);
            }
        }

        [Fact]
        public async Task A_credential_reference_in_the_query_goes_out_as_credential()
        {
            var embedder = new FakeEmbedder();
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(embedder, reranker);
            using (indexer)
            {
                await search.SearchAsync("noodles {{secret:K7Q2M9XD}}", default);

                Assert.Contains("noodles [credential]", embedder.Texts);
                Assert.DoesNotContain(embedder.Texts, t => t.Contains("K7Q2M9XD", StringComparison.Ordinal));
                Assert.Equal("noodles [credential]", Assert.Single(reranker.Queries));
            }
        }

        [Fact]
        public async Task The_query_vector_is_cached()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                int before = embedder.Calls;
                await search.SearchAsync("vpn", default);
                await search.SearchAsync("vpn", default);
                Assert.Equal(before + 1, embedder.Calls);
            }
        }

        [Fact]
        public async Task A_newer_query_cancels_the_older_one()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                embedder.Delay = TimeSpan.FromSeconds(3);
                using var older = new CancellationTokenSource();
                var first = search.SearchAsync("slow one", older.Token);
                older.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            }
        }

        [Fact]
        public async Task An_empty_query_finds_nothing_and_sends_nothing()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                int before = embedder.Calls;
                var outcome = await search.SearchAsync("   ", default);
                Assert.Empty(outcome.Hits);
                Assert.Equal(before, embedder.Calls);
            }
        }

        [Fact]
        public void Index_status_names_the_waiting_and_the_failure()
        {
            Assert.Equal("Indexing: 120 of 312 passages",
                SearchStatusText.Index(new IndexProgress(9, 312, 120, 192, SearchFailure.None, null, false), _settings));
            Assert.Equal("Indexing paused: the embedding server refused the key",
                SearchStatusText.Index(new IndexProgress(9, 312, 120, 192, SearchFailure.KeyRefused, 401, false), _settings));
            Assert.Equal("312 passages from 9 notes; 312 with meaning",
                SearchStatusText.Index(new IndexProgress(9, 312, 312, 0, SearchFailure.None, null, false), _settings));
            Assert.Equal("312 passages from 9 notes",
                SearchStatusText.Index(new IndexProgress(9, 312, 0, 0, SearchFailure.None, null, false), SearchSettings.Off));
            // Full = more passages than the store holds (the indexer works it out each pass); Waiting then drains to 0.
            Assert.Equal("25000 passages from 9 notes; 20000 with meaning. The index is full: the rest are found by words only.",
                SearchStatusText.Index(new IndexProgress(9, 25000, 20000, 0, SearchFailure.None, null, true), _settings));
            Assert.Equal("Indexing: 1200 of 25000 passages",
                SearchStatusText.Index(new IndexProgress(9, 25000, 1200, 18800, SearchFailure.None, null, true), _settings));
        }
    }
}
