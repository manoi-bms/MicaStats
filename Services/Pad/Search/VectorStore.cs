using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Reverses the protection <see cref="VectorStore"/> writes with; false when the bytes are not its own.</summary>
    public delegate bool TryUnprotect(byte[] data, out byte[] plain);

    /// <summary>What <see cref="VectorStore.Put"/> did.</summary>
    public enum PutResult { Stored, Full, WrongDimension }

    /// <summary>A vector's passage hash and its cosine with the query.</summary>
    public readonly record struct VectorHit(string Hash, double Score);

    /// <summary>
    /// Passage vectors by passage hash (spec 3.4), unit length, in memory and in
    /// <c>search\vectors.bin</c> protected with the notes key. The file names the fingerprint
    /// (server and model) and the dimension it was made with; anything else, or a file that cannot
    /// be read, starts empty. Thread-safe: every member takes one lock.
    /// </summary>
    public sealed class VectorStore
    {
        public const int Capacity = 20_000;
        public const string FileName = "vectors.bin";

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPVX");
        private const int FormatVersion = 1;
        private const int MaxDimension = 16_384;

        private readonly object _gate = new();
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
        private readonly Func<byte[], byte[]> _protect;
        private readonly TryUnprotect _unprotect;
        private readonly Action<string> _warn;
        private readonly int _capacity;
        private bool _dirty;

        /// <param name="folder">Where <see cref="FileName"/> lives; created on the first save.</param>
        public VectorStore(string folder, Func<byte[], byte[]> protect, TryUnprotect unprotect, Action<string>? warn = null, int capacity = Capacity)
        {
            FilePath = Path.Combine(folder, FileName);
            _protect = protect;
            _unprotect = unprotect;
            _warn = warn ?? (_ => { });
            _capacity = capacity;
        }

        public string FilePath { get; }

        /// <summary>The server and model these vectors belong to.</summary>
        public string Fingerprint { get { lock (_gate) return _fingerprint; } }
        private string _fingerprint = "";

        /// <summary>The vectors' length; 0 while there are none.</summary>
        public int Dimension { get { lock (_gate) return _dimension; } }
        private int _dimension;

        public int Count { get { lock (_gate) return _vectors.Count; } }

        /// <summary>The most vectors this store holds (<see cref="Capacity"/> unless the constructor said otherwise).</summary>
        public int MaxCount => _capacity;

        /// <summary>
        /// Empties the store for <paramref name="fingerprint"/> and reads the file when it was made
        /// for it. True when vectors were read.
        /// </summary>
        public bool Load(string fingerprint)
        {
            lock (_gate)
            {
                _vectors.Clear();
                _fingerprint = fingerprint;
                _dimension = 0;
                _dirty = false;

                byte[]? data;
                try { data = AtomicFile.ReadBytes(FilePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("The search vectors could not be read (" + ex.GetType().Name + "); they will be made again.");
                    return false;
                }
                if (data == null) return false;

                if (!_unprotect(data, out byte[] plain))
                {
                    _warn("The search vectors could not be decrypted; they will be made again.");
                    return false;
                }

                try
                {
                    return ReadLocked(plain, fingerprint);
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException)
                {
                    _vectors.Clear();
                    _dimension = 0;
                    _warn("The search vectors file is damaged (" + ex.GetType().Name + "); they will be made again.");
                    return false;
                }
            }
        }

        private bool ReadLocked(byte[] plain, string fingerprint)
        {
            using var reader = new BinaryReader(new MemoryStream(plain), Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new FormatException("magic");
            if (reader.ReadInt32() != FormatVersion) throw new FormatException("version");
            string fileFingerprint = reader.ReadString();
            int dimension = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (fileFingerprint != fingerprint)
            {
                _warn("The search vectors were made for another server or model; they will be made again.");
                return false;
            }
            if (dimension <= 0 || dimension > MaxDimension || count < 0 || count > _capacity) throw new FormatException("header");

            for (int i = 0; i < count; i++)
            {
                byte[] hash = reader.ReadBytes(32);
                if (hash.Length != 32) throw new EndOfStreamException();
                var vector = new float[dimension];
                for (int k = 0; k < dimension; k++) vector[k] = reader.ReadSingle();
                _vectors[Convert.ToHexString(hash).ToLowerInvariant()] = vector;
            }
            _dimension = dimension;
            return count > 0;
        }

        public bool Has(string hash)
        {
            lock (_gate) return _vectors.ContainsKey(hash);
        }

        /// <summary>Stores the vector at unit length. Refuses a new hash when full, and any other dimension.</summary>
        public PutResult Put(string hash, float[] vector)
        {
            lock (_gate)
            {
                if (_dimension != 0 && vector.Length != _dimension) return PutResult.WrongDimension;
                if (!_vectors.ContainsKey(hash) && _vectors.Count >= _capacity) return PutResult.Full;
                _vectors[hash] = Normalized(vector);
                _dimension = vector.Length;
                _dirty = true;
                return PutResult.Stored;
            }
        }

        /// <summary>Drops every vector no passage uses; returns how many went.</summary>
        public int Keep(IReadOnlySet<string> inUse)
        {
            lock (_gate)
            {
                var unused = _vectors.Keys.Where(h => !inUse.Contains(h)).ToList();
                foreach (string hash in unused) _vectors.Remove(hash);
                if (unused.Count > 0) _dirty = true;
                if (_vectors.Count == 0 && unused.Count > 0) _dimension = 0;
                return unused.Count;
            }
        }

        /// <summary>Writes the file when something changed since the last save; true when it wrote.</summary>
        public bool Save()
        {
            lock (_gate)
            {
                if (!_dirty) return false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    AtomicFile.Write(FilePath, _protect(Serialize()));
                    _dirty = false;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("The search vectors could not be saved (" + ex.GetType().Name + ").");
                    return false;
                }
            }
        }

        private byte[] Serialize()
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(_fingerprint);
                writer.Write(_dimension);
                writer.Write(_vectors.Count);
                foreach (var pair in _vectors)
                {
                    writer.Write(Convert.FromHexString(pair.Key));
                    foreach (float x in pair.Value) writer.Write(x);
                }
            }
            return buffer.ToArray();
        }

        /// <summary>Forgets every vector and deletes the file (meaning search turned off, model changed, Rebuild).</summary>
        public void Delete()
        {
            lock (_gate)
            {
                _vectors.Clear();
                _dimension = 0;
                _dirty = false;
                try
                {
                    File.Delete(FilePath);
                    File.Delete(FilePath + ".ready");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("The search vectors file could not be deleted (" + ex.GetType().Name + ").");
                }
            }
        }

        /// <summary>
        /// Deletes the vectors file in <paramref name="folder"/> and its leftover from a write, with no
        /// store open on it: Settings turning meaning search off before MicaPad opened this session.
        /// </summary>
        public static void DeleteFiles(string folder, Action<string>? warn = null)
        {
            string path = Path.Combine(folder, FileName);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".ready")) File.Delete(path + ".ready");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke("The search vectors file could not be deleted (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>The <paramref name="limit"/> candidates closest to the query by cosine; candidates without a vector are skipped.</summary>
        public IReadOnlyList<VectorHit> Nearest(float[] query, IEnumerable<string> candidates, int limit)
        {
            float[] q = Normalized(query);
            lock (_gate)
            {
                if (_dimension == 0 || q.Length != _dimension) return Array.Empty<VectorHit>();
                var hits = new List<VectorHit>();
                foreach (string hash in candidates.Distinct(StringComparer.Ordinal))
                {
                    if (!_vectors.TryGetValue(hash, out var v)) continue;
                    double dot = 0;
                    for (int i = 0; i < v.Length; i++) dot += v[i] * q[i];
                    hits.Add(new VectorHit(hash, dot));
                }
                return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Hash, StringComparer.Ordinal).Take(limit).ToList();
            }
        }

        private static float[] Normalized(float[] vector)
        {
            double sum = 0;
            foreach (float x in vector) sum += (double)x * x;
            double length = Math.Sqrt(sum);
            var result = new float[vector.Length];
            if (length == 0) return result;
            for (int i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / length);
            return result;
        }
    }
}
