using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchVectorStoreTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        private readonly List<string> _warnings = new();

        public void Dispose() => _dir.Dispose();

        /// <summary>A store whose "encryption" is a byte flip with a marker, so tests can tell protected bytes.</summary>
        private VectorStore NewStore(int capacity = VectorStore.Capacity) => new(
            Path.Combine(_dir.Root, "search"),
            plain => new byte[] { 0xEE }.Concat(plain.Select(b => (byte)~b)).ToArray(),
            (byte[] data, out byte[] plain) =>
            {
                plain = Array.Empty<byte>();
                if (data.Length == 0 || data[0] != 0xEE) return false;
                plain = data.Skip(1).Select(b => (byte)~b).ToArray();
                return true;
            },
            _warnings.Add,
            capacity);

        private static string H(int i) => i.ToString("x64");

        [Fact]
        public void Vectors_survive_a_save_and_load_and_the_file_is_protected()
        {
            var store = NewStore();
            store.Load("http://gpu/v1|bge");
            Assert.Equal(PutResult.Stored, store.Put(H(1), new[] { 3f, 4f }));
            Assert.True(store.Save());

            byte[] raw = File.ReadAllBytes(store.FilePath);
            Assert.Equal(0xEE, raw[0]);

            var again = NewStore();
            Assert.True(again.Load("http://gpu/v1|bge"));
            Assert.True(again.Has(H(1)));
            Assert.Equal(2, again.Dimension);
            var hit = Assert.Single(again.Nearest(new[] { 3f, 4f }, new[] { H(1) }, 5));
            Assert.Equal(1.0, hit.Score, 5);   // stored unit length: cosine of a vector with itself
        }

        [Fact]
        public void Another_fingerprint_starts_empty()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Save();

            var again = NewStore();
            Assert.False(again.Load("b|m"));
            Assert.Equal(0, again.Count);
        }

        [Fact]
        public void A_damaged_or_foreign_file_starts_empty_with_a_warning()
        {
            var store = NewStore();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);

            File.WriteAllBytes(store.FilePath, new byte[] { 0xEE, 1, 2, 3 });
            Assert.False(store.Load("a|m"));
            Assert.Equal(0, store.Count);

            File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3 });
            Assert.False(store.Load("a|m"));

            Assert.Equal(2, _warnings.Count);
            Assert.All(_warnings, w => Assert.DoesNotContain("\\", w, StringComparison.Ordinal));   // no paths, no contents
        }

        [Fact]
        public void Put_refuses_another_dimension()
        {
            var store = NewStore();
            store.Load("a|m");
            Assert.Equal(PutResult.Stored, store.Put(H(1), new[] { 1f, 0f }));
            Assert.Equal(PutResult.WrongDimension, store.Put(H(2), new[] { 1f, 0f, 0f }));
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void A_full_store_takes_no_more()
        {
            var store = NewStore(capacity: 2);
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Put(H(2), new[] { 1f });
            Assert.Equal(PutResult.Full, store.Put(H(3), new[] { 1f }));
            Assert.Equal(PutResult.Stored, store.Put(H(2), new[] { 0.5f }));   // replacing is not growing
        }

        [Fact]
        public void Keep_drops_unused_vectors()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Put(H(2), new[] { 1f });

            Assert.Equal(1, store.Keep(new HashSet<string> { H(2) }));
            Assert.False(store.Has(H(1)));
            Assert.True(store.Has(H(2)));
        }

        [Fact]
        public void Nearest_ranks_by_cosine_among_the_candidates_only()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f, 0f });
            store.Put(H(2), new[] { 0.7f, 0.7f });
            store.Put(H(3), new[] { 0f, 1f });

            var hits = store.Nearest(new[] { 1f, 0.1f }, new[] { H(1), H(2), H(9) }, 5);

            Assert.Equal(new[] { H(1), H(2) }, hits.Select(h => h.Hash).ToArray());
        }

        [Fact]
        public void Delete_removes_the_file_and_the_vectors()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Save();

            store.Delete();

            Assert.False(File.Exists(store.FilePath));
            Assert.Equal(0, store.Count);
            Assert.Equal(0, store.Dimension);
        }

        [Fact]
        public void Save_writes_only_after_a_change()
        {
            var store = NewStore();
            store.Load("a|m");
            Assert.False(store.Save());
            store.Put(H(1), new[] { 1f });
            Assert.True(store.Save());
            Assert.False(store.Save());
        }

        [Fact]
        public void The_notes_key_round_trips_bytes_and_refuses_plain_ones()
        {
            using var env = new PadTestEnv();
            byte[] secret = { 1, 2, 3, 4 };

            byte[] sealed_ = env.Store.EncryptBytes(secret);

            Assert.True(env.Store.TryDecryptBytes(sealed_, out byte[] plain));
            Assert.Equal(secret, plain);
            Assert.False(env.Store.TryDecryptBytes(secret, out _));
        }
    }
}
