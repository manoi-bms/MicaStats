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
    public class SearchIndexerTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        /// <summary>Embeds each text as [length, 1], or fails as told; records every batch.</summary>
        internal sealed class FakeEmbedder : IEmbedder
        {
            public List<IReadOnlyList<string>> Batches { get; } = new();
            public SearchFailure FailWith { get; set; } = SearchFailure.None;
            public int Dimension { get; set; } = 2;
            public Func<Task>? Gate { get; set; }

            public async Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel)
            {
                lock (Batches) Batches.Add(texts.ToList());
                if (Gate != null) await Gate();
                if (FailWith != SearchFailure.None) return new EmbeddingResult(null, FailWith, 503);
                return new EmbeddingResult(texts.Select(t => Enumerable.Range(0, Dimension).Select(i => i == 0 ? (float)t.Length : 1f).ToArray()).ToList(), SearchFailure.None, 200);
            }

            public int TextsSent { get { lock (Batches) return Batches.Sum(b => b.Count); } }
        }

        private static readonly SearchServer Server = new("http://gpu/v1", "m", null);
        private SearchSettings _settings = new(true, Server, false, null);
        private readonly Dictionary<string, string> _stored = new();

        private SearchIndexer NewIndexer(FakeEmbedder embedder, Func<int, TimeSpan>? retry = null, int capacity = VectorStore.Capacity) => new(
            new VectorStore(Path.Combine(_dir.Root, "search"), b => b.ToArray(), (byte[] d, out byte[] p) => { p = d; return true; }, capacity: capacity),
            embedder,
            () => _settings,
            id => _stored.TryGetValue(id, out var t) ? t : null,
            retryDelay: retry ?? (_ => TimeSpan.FromMilliseconds(20)),
            saveEvery: TimeSpan.FromMilliseconds(10));

        [Fact]
        public async Task A_note_is_indexed_by_words_and_by_meaning()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("a", "t", "vpn does not connect", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Single(indexer.Keywords.Search("vpn", 10));
            Assert.Equal(1, indexer.Vectors.Count);
            Assert.Equal(new IndexProgress(1, 1, 1, 0, SearchFailure.None, null, false), indexer.Progress);
        }

        [Fact]
        public async Task Only_changed_passages_are_sent_again()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("a", "t", "first paragraph\n\n" + new string('x', 900), DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Equal(2, embedder.TextsSent);

            indexer.SetNote("a", "t", "first paragraph\n\n" + new string('y', 900), DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Equal(3, embedder.TextsSent);
        }

        [Fact]
        public async Task The_latest_text_of_a_note_wins()
        {
            var embedder = new FakeEmbedder();
            var release = new TaskCompletionSource();
            embedder.Gate = () => release.Task;
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("a", "t", "old words", DateTime.UtcNow);
            while (embedder.TextsSent == 0) await Task.Delay(5);      // the old text is being embedded
            indexer.SetNote("a", "t", "new words", DateTime.UtcNow);
            release.SetResult();
            await indexer.WhenIdle();

            Assert.Empty(indexer.Keywords.Search("old", 10));
            Assert.Single(indexer.Keywords.Search("new", 10));
            var inUse = indexer.Keywords.AllPassages().Select(p => p.Hash).ToHashSet();
            Assert.Equal(1, indexer.Vectors.Count);
            Assert.True(indexer.Vectors.Has(inUse.Single()));
        }

        [Fact]
        public async Task Meaning_off_sends_nothing_and_turning_it_off_deletes_the_vectors()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.True(File.Exists(indexer.Vectors.FilePath));

            _settings = SearchSettings.Off;
            indexer.SettingsChanged();
            indexer.SetNote("b", "t", "more words", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Equal(1, embedder.TextsSent);
            Assert.False(File.Exists(indexer.Vectors.FilePath));
            Assert.Equal(0, indexer.Vectors.Count);
            Assert.Equal(2, indexer.Keywords.NoteCount);   // words still work
        }

        [Fact]
        public async Task Another_model_starts_the_vectors_over()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();

            _settings = _settings with { Embedding = Server with { Model = "other" } };
            indexer.SettingsChanged();
            await indexer.WhenIdle();

            Assert.Equal(2, embedder.TextsSent);
            Assert.Equal("http://gpu/v1|other", indexer.Vectors.Fingerprint);
            Assert.Equal(1, indexer.Vectors.Count);
        }

        [Fact]
        public async Task A_dimension_change_starts_the_vectors_over()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();

            embedder.Dimension = 3;   // the server's model was swapped behind the same address
            indexer.SetNote("b", "t", "other words", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Equal(3, indexer.Vectors.Dimension);
            Assert.Equal(2, indexer.Vectors.Count);
        }

        [Fact]
        public async Task A_failing_server_is_retried_and_named()
        {
            var embedder = new FakeEmbedder { FailWith = SearchFailure.Unreachable };
            var delays = new List<int>();
            using var indexer = NewIndexer(embedder, n => { lock (delays) delays.Add(n); return TimeSpan.FromMilliseconds(30); });

            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Equal(SearchFailure.Unreachable, indexer.Progress.LastFailure);
            Assert.Equal(1, indexer.Progress.Waiting);
            Assert.Single(indexer.Keywords.Search("words", 10));

            embedder.FailWith = SearchFailure.None;
            await Task.Delay(100);
            await indexer.WhenIdle();
            Assert.Equal(0, indexer.Progress.Waiting);
            Assert.Equal(SearchFailure.None, indexer.Progress.LastFailure);
            Assert.Equal(1, delays.First());
        }

        [Fact]
        public void Retries_wait_1_2_5_10_then_30_minutes()
        {
            Assert.Equal(new[] { 1, 2, 5, 10, 30, 30 }, Enumerable.Range(1, 6).Select(n => (int)SearchIndexer.DefaultRetryDelay(n).TotalMinutes).ToArray());
        }

        [Fact]
        public async Task Sixteen_passages_go_per_request_newest_note_first()
        {
            var embedder = new FakeEmbedder();
            var release = new TaskCompletionSource();
            embedder.Gate = () => release.Task;
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("blocker", "t", "first", DateTime.UtcNow.AddDays(-10));
            while (embedder.TextsSent == 0) await Task.Delay(5);
            indexer.SetNote("old", "old", string.Join("\n\n", Enumerable.Range(0, 20).Select(i => "old " + i + " " + new string('o', 790))), DateTime.UtcNow.AddDays(-5));
            indexer.SetNote("new", "new", "fresh", DateTime.UtcNow);
            release.SetResult();
            await indexer.WhenIdle();

            Assert.All(embedder.Batches, b => Assert.True(b.Count <= SearchIndexer.BatchSize));
            Assert.StartsWith("new", embedder.Batches[1][0], StringComparison.Ordinal);
        }

        [Fact]
        public async Task Stored_vectors_are_kept_until_the_notes_are_fed()
        {
            using (var first = NewIndexer(new FakeEmbedder()))
            {
                first.SetNote("a", "t", "alpha words", DateTime.UtcNow);
                first.SetNote("b", "t", "beta words", DateTime.UtcNow);
                await first.WhenIdle();
            }

            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            await indexer.WhenIdle();                        // idle before any note arrives
            indexer.Reconcile(new[] { "a", "b" });
            indexer.SetNote("a", "t", "alpha words", DateTime.UtcNow);
            await indexer.WhenIdle();                        // idle again while "b" is not fed yet
            indexer.SetNote("b", "t", "beta words", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Equal(0, embedder.TextsSent);             // nothing unchanged is sent again after a restart
            Assert.Equal(2, indexer.Vectors.Count);
        }

        [Fact]
        public async Task Stored_notes_are_read_on_the_worker_and_missing_ones_removed()
        {
            using var indexer = NewIndexer(new FakeEmbedder());
            _stored["s"] = "stored words";
            indexer.IndexStored("s", "t", DateTime.UtcNow);
            indexer.IndexStored("gone", "t", DateTime.UtcNow);
            indexer.SetNote("x", "t", "x words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Single(indexer.Keywords.Search("stored", 10));

            indexer.Reconcile(new[] { "s" });
            await indexer.WhenIdle();
            Assert.Equal(new[] { "s" }, indexer.Keywords.NoteIds().ToArray());
        }

        [Fact]
        public async Task WhenIdle_never_completes_before_the_work_queued_ahead_of_it()
        {
            _settings = SearchSettings.Off;
            using var indexer = NewIndexer(new FakeEmbedder());
            for (int i = 0; i < 2000; i++)
            {
                indexer.SetNote("n" + i, "t", "word" + i, DateTime.UtcNow);
                await indexer.WhenIdle();
                Assert.Equal(i + 1, indexer.Keywords.NoteCount);
            }
        }

        private static bool HasVector(SearchIndexer indexer, string noteId) =>
            indexer.Vectors.Has(indexer.Keywords.PassagesOf(noteId).Single().Hash);

        [Fact]
        public async Task At_capacity_the_oldest_notes_go_without_vectors_and_the_store_recovers()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder, capacity: 2);
            var start = DateTime.UtcNow;
            indexer.SetNote("a", "t", "alpha words", start.AddMinutes(1));
            indexer.SetNote("b", "t", "beta words", start.AddMinutes(2));
            indexer.SetNote("c", "t", "gamma words", start.AddMinutes(3));
            await indexer.WhenIdle();

            Assert.False(HasVector(indexer, "a"));
            Assert.True(HasVector(indexer, "b"));
            Assert.True(HasVector(indexer, "c"));
            Assert.True(indexer.Progress.Full);
            Assert.Equal(0, indexer.Progress.Waiting);

            indexer.SetNote("a", "t", "alpha words edited", start.AddMinutes(4));   // the oldest note becomes the newest
            await indexer.WhenIdle();

            Assert.True(HasVector(indexer, "a"));
            Assert.False(HasVector(indexer, "b"));                                  // now the oldest
            Assert.True(HasVector(indexer, "c"));
            Assert.True(indexer.Progress.Full);

            indexer.RemoveNote("c");
            await indexer.WhenIdle();

            Assert.False(indexer.Progress.Full);
            Assert.Equal(0, indexer.Progress.Waiting);
            Assert.All(indexer.Keywords.AllPassages(), p => Assert.True(indexer.Vectors.Has(p.Hash)));
            Assert.Equal(2, indexer.Vectors.Count);
        }

        [Fact]
        public async Task While_named_notes_are_unfed_a_full_store_evicts_nothing_and_sends_nothing()
        {
            var start = DateTime.UtcNow;
            using (var first = NewIndexer(new FakeEmbedder(), capacity: 2))
            {
                first.SetNote("a", "t", "alpha words", start.AddMinutes(1));
                first.SetNote("b", "t", "beta words", start.AddMinutes(2));
                await first.WhenIdle();
            }

            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder, capacity: 2);
            indexer.Reconcile(new[] { "a", "b", "c" });
            indexer.SetNote("c", "t", "gamma words", start.AddMinutes(3));
            await indexer.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));   // a resend loop would never go idle

            Assert.Equal(0, embedder.TextsSent);                             // no room while "a" and "b" keep their vectors
            Assert.Equal(2, indexer.Vectors.Count);
            Assert.Equal(1, indexer.Progress.Waiting);

            indexer.SetNote("a", "t", "alpha words", start.AddMinutes(1));
            indexer.SetNote("b", "t", "beta words", start.AddMinutes(2));
            await indexer.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, embedder.TextsSent);
            Assert.True(HasVector(indexer, "c"));
            Assert.True(HasVector(indexer, "b"));
            Assert.False(HasVector(indexer, "a"));
        }

        [Fact]
        public async Task Rebuild_sends_everything_again()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();

            indexer.Rebuild();
            await indexer.WhenIdle();

            Assert.Equal(2, embedder.TextsSent);
            Assert.Equal(1, indexer.Vectors.Count);
        }

        [Fact]
        public async Task Progress_changes_are_announced()
        {
            using var indexer = NewIndexer(new FakeEmbedder());
            int seen = 0;
            indexer.ProgressChanged += () => Interlocked.Increment(ref seen);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.True(seen > 0);
        }
    }
}
