using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Keeps the keyword index and the vectors in step with the notes (spec 3.5). Every change is
    /// queued and applied in order on one background worker, which also sends the passages that
    /// have no vector to the embedding server, <see cref="BatchSize"/> at a time, newest note
    /// first, retrying a failed request after <c>retryDelay(n)</c>. Queued changes keep being
    /// applied while a request is out, so words search never waits for the server. A batch the
    /// server refuses for its content is halved until the passage it will not take is found; that
    /// passage is set aside (found by words only) until Settings change or the index is rebuilt.
    /// When the notes have more passages than the store holds, the least recently modified notes'
    /// passages go without vectors (spec 3.4): their vectors make room for newer ones, and come
    /// back when room frees up.
    /// </summary>
    public sealed class SearchIndexer : IDisposable
    {
        /// <summary>Passages per embedding request.</summary>
        public const int BatchSize = 16;

        /// <summary>
        /// Sent alone when the server refused a single passage, to tell a passage it will not take
        /// from a server that refuses everything (a wrong model name, a proxy whose server is down).
        /// The text the Settings Test button sends.
        /// </summary>
        public const string KnownGoodText = "MicaPad test";

        /// <summary>How long one embedding request may take.</summary>
        public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The vectors are written at most this often while indexing.</summary>
        public static readonly TimeSpan DefaultSaveEvery = TimeSpan.FromSeconds(10);

        /// <summary>The longest the worker sleeps through a retry wait before it looks again.</summary>
        private const long MaxSleepMs = 60_000;

        /// <summary>The pause after an unexpected fault, so a fault that repeats cannot spin the worker.</summary>
        private static readonly TimeSpan FaultPause = TimeSpan.FromSeconds(1);

        /// <summary>1, 2, 5, 10, then every 30 minutes.</summary>
        public static TimeSpan DefaultRetryDelay(int failures) => TimeSpan.FromMinutes(failures switch
        {
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            _ => 30,
        });

        private sealed record NoteInfo(string Title, DateTime ModifiedUtc);

        private readonly ConcurrentQueue<Action> _work = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private readonly IEmbedder _embedder;
        private readonly Func<SearchSettings> _settings;
        private readonly Func<string, string?> _loadStoredText;
        private readonly Action<string> _warn;
        private readonly Func<int, TimeSpan> _retryDelay;
        private readonly TimeSpan _saveEvery;
        private readonly Task _worker;
        private readonly object _idleGate = new();
        private readonly List<TaskCompletionSource> _idleWaiters = new();   // under _idleGate
        private readonly List<TaskCompletionSource> _appliedWaiters = new(); // under _idleGate
        private bool _stopped;                                              // under _idleGate
        private int _disposed;

        // Worker-only state.
        private readonly Dictionary<string, NoteInfo> _notes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unfed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);   // hashes of passages the server will not take
        private readonly List<IReadOnlyList<Passage>> _splits = new();            // halves of a refused batch, sent next
        private readonly List<TaskCompletionSource> _armed = new();
        private List<Passage>? _ranked;   // Ranked(), until the passages change
        private string? _loadedFingerprint;
        private int _failures;
        private long _retryAtMs;
        private long _lastSaveMs;
        private bool _unsaved;
        private SearchFailure _lastFailure;
        private int? _lastStatus;

        private IndexProgress _progress = IndexProgress.Empty;
        private int _generation;   // written on the worker only

        /// <param name="vectors">The store; loaded for the settings' server and model on the worker.</param>
        /// <param name="loadStoredText">A closed note's stored text, or null when it has none (read on the worker).</param>
        /// <param name="warn">Log lines without note text, queries or keys.</param>
        /// <param name="retryDelay">How long to wait after the n-th failed request in a row; <see cref="DefaultRetryDelay"/> when null.</param>
        /// <param name="saveEvery">The least time between two writes of the vectors while indexing; <see cref="DefaultSaveEvery"/> when null.</param>
        public SearchIndexer(VectorStore vectors, IEmbedder embedder, Func<SearchSettings> settings, Func<string, string?> loadStoredText,
                             Action<string>? warn = null, Func<int, TimeSpan>? retryDelay = null, TimeSpan? saveEvery = null)
        {
            Vectors = vectors;
            _embedder = embedder;
            _settings = settings;
            _loadStoredText = loadStoredText;
            _warn = warn ?? (_ => { });
            _retryDelay = retryDelay ?? DefaultRetryDelay;
            _saveEvery = saveEvery ?? DefaultSaveEvery;
            Enqueue(ApplySettings);
            _worker = Task.Run(RunAsync);
        }

        /// <summary>The passages of every indexed note; searched from any thread.</summary>
        public KeywordIndex Keywords { get; } = new();

        /// <summary>The passages' vectors; searched from any thread.</summary>
        public VectorStore Vectors { get; }

        /// <summary>The latest progress; read from any thread.</summary>
        public IndexProgress Progress => Volatile.Read(ref _progress);

        /// <summary>
        /// Changes whenever the vectors may have been started over or made by another model:
        /// Settings applied (meaning search off, another server or model), Rebuild, vectors of
        /// another length. A query vector cached under an older value is not used again.
        /// </summary>
        public int Generation => Volatile.Read(ref _generation);

        /// <summary>Raised on the worker thread after progress changes.</summary>
        public event Action? ProgressChanged;

        /// <summary>The note's current text; cut and indexed on the worker.</summary>
        public void SetNote(string noteId, string title, string text, DateTime modifiedUtc) =>
            Enqueue(() => Index(noteId, title, text, modifiedUtc));

        /// <summary>A note not open in MicaPad: its stored text is read on the worker; a note with none is removed.</summary>
        public void IndexStored(string noteId, string title, DateTime modifiedUtc) => Enqueue(() =>
        {
            string? text = _loadStoredText(noteId);
            if (text == null) Forget(noteId);
            else Index(noteId, title, text, modifiedUtc);
        });

        /// <summary>The note was deleted: its passages leave the index.</summary>
        public void RemoveNote(string noteId) => Enqueue(() => Forget(noteId));

        /// <summary>
        /// Removes every indexed note not in <paramref name="existingIds"/>. The ids not indexed yet
        /// are expected next: until each has been indexed or removed, saves keep every stored vector,
        /// since those notes' vectors are still in the store with no passage to claim them.
        /// </summary>
        public void Reconcile(IReadOnlyCollection<string> existingIds)
        {
            var keep = new HashSet<string>(existingIds, StringComparer.Ordinal);
            Enqueue(() =>
            {
                foreach (string id in _notes.Keys.Where(id => !keep.Contains(id)).ToList()) Forget(id);
                _unfed.Clear();
                _unfed.UnionWith(keep.Where(id => !_notes.ContainsKey(id)));
                _unsaved = true;
            });
        }

        /// <summary>Settings → Search changed: applied in order with the notes.</summary>
        public void SettingsChanged() => Enqueue(ApplySettings);

        /// <summary>Drops every vector and makes them again.</summary>
        public void Rebuild() => Enqueue(() =>
        {
            Vectors.Delete();
            _loadedFingerprint = null;
            ApplySettings();
        });

        /// <summary>
        /// Completes once the worker has run everything queued before this call and has nothing it can
        /// run now (tests; a retry wait counts as nothing). Completes at once after <see cref="Dispose"/>.
        /// </summary>
        public Task WhenIdle()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_idleGate)
            {
                if (_stopped || Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
                _idleWaiters.Add(waiter);
            }
            _signal.Release();
            return waiter.Task;
        }

        /// <summary>
        /// Completes once the worker has applied everything queued before this call: every note
        /// handed over so far is in the keyword index as it was handed over. Unlike
        /// <see cref="WhenIdle"/> it does not wait for vectors, so a slow or absent embedding
        /// server never holds it up. Completes at once after <see cref="Dispose"/>.
        /// </summary>
        public Task WhenApplied()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_idleGate)
            {
                if (_stopped || Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
                _appliedWaiters.Add(waiter);
            }
            _signal.Release();
            return waiter.Task;
        }

        private void Enqueue(Action work)
        {
            _work.Enqueue(work);
            _signal.Release();
        }

        private async Task RunAsync()
        {
            var token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                bool faulted = false;
                try
                {
                    // This pass covers every wake-up already signalled. Idle waiters are taken before
                    // the queue is drained, so the work queued ahead of each has run when it is released;
                    // a waiter that arrives later waits for the next pass.
                    while (_signal.Wait(0)) { }
                    ArmIdleWaiters();

                    if (ApplyQueuedWork()) Publish();
                    if (!_work.IsEmpty) continue;

                    var settings = _settings();
                    var batch = NextBatch(settings);
                    if (batch.Count > 0)
                    {
                        await EmbedBatchAsync(settings, batch, token).ConfigureAwait(false);
                        continue;
                    }

                    SaveIfDue(idle: true);
                    ReleaseArmedWaiters();
                    await _signal.WaitAsync(NextWake(), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _warn("Search indexing failed (" + ex.GetType().Name + ").");
                    faulted = true;
                }

                if (faulted)
                {
                    try { await Task.Delay(FaultPause, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            lock (_idleGate)
            {
                _stopped = true;
                _armed.AddRange(_idleWaiters);
                _idleWaiters.Clear();
                _armed.AddRange(_appliedWaiters);   // no worker is left to apply anything: nobody waits for ever
                _appliedWaiters.Clear();
            }
            ReleaseArmedWaiters();
        }

        private void RunWork(Action work)
        {
            try { work(); }
            catch (Exception ex)
            {
                _warn("Search indexing failed (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>
        /// Runs the queued work; true when there was any. The waiters of <see cref="WhenApplied"/>
        /// are taken before the queue is drained, so the work queued ahead of each has run when it
        /// is released; one that arrives later waits for the next pass. They are released whatever
        /// happens while the work runs: a throw here (work that fails and cannot even be reported)
        /// must not leave a question waiting for the index for ever.
        /// </summary>
        private bool ApplyQueuedWork()
        {
            List<TaskCompletionSource>? waiters = null;
            lock (_idleGate)
            {
                if (_appliedWaiters.Count > 0)
                {
                    waiters = new List<TaskCompletionSource>(_appliedWaiters);
                    _appliedWaiters.Clear();
                }
            }

            bool didWork = false;
            try
            {
                while (_work.TryDequeue(out var work))
                {
                    RunWork(work);
                    didWork = true;
                }
            }
            finally
            {
                if (waiters != null)
                    foreach (var waiter in waiters) waiter.TrySetResult();
            }
            return didWork;
        }

        private void ArmIdleWaiters()
        {
            lock (_idleGate)
            {
                _armed.AddRange(_idleWaiters);
                _idleWaiters.Clear();
            }
        }

        private void ReleaseArmedWaiters()
        {
            foreach (var waiter in _armed) waiter.TrySetResult();
            _armed.Clear();
        }

        /// <summary>How long the idle worker may sleep: until a retry or a held-back save is due, else until signalled.</summary>
        private TimeSpan NextWake()
        {
            long now = Environment.TickCount64;
            long wait = long.MaxValue;
            if (_retryAtMs > now) wait = Math.Min(_retryAtMs - now, MaxSleepMs);
            if (_unsaved) wait = Math.Min(wait, Math.Max(1, _lastSaveMs + (long)_saveEvery.TotalMilliseconds - now));
            return wait == long.MaxValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(wait);
        }

        private void Index(string noteId, string title, string text, DateTime modifiedUtc)
        {
            _notes[noteId] = new NoteInfo(title, modifiedUtc);
            Keywords.Set(noteId, NotePassages.Cut(noteId, title, text));
            _unfed.Remove(noteId);
            _ranked = null;
            _unsaved = true;
        }

        private void Forget(string noteId)
        {
            _notes.Remove(noteId);
            Keywords.Remove(noteId);
            _unfed.Remove(noteId);
            _ranked = null;
            _unsaved = true;
        }

        private void ApplySettings()
        {
            var settings = _settings();
            Interlocked.Increment(ref _generation);   // an answer still out is dropped; cached query vectors are made again
            _failures = 0;
            _retryAtMs = 0;
            _lastFailure = SearchFailure.None;
            _lastStatus = null;
            _refused.Clear();                          // another server, model or key may take them
            _splits.Clear();

            if (!settings.Meaning)
            {
                if (_loadedFingerprint != null || Vectors.Count > 0 || System.IO.File.Exists(Vectors.FilePath)) Vectors.Delete();
                _loadedFingerprint = null;
                return;
            }
            if (!settings.CanEmbed) return;   // meaning on but no server yet: keep what is stored

            if (_loadedFingerprint != settings.Fingerprint)
            {
                if (_loadedFingerprint != null) Vectors.Delete();
                Vectors.Load(settings.Fingerprint);
                _loadedFingerprint = settings.Fingerprint;
            }
        }

        /// <summary>Every passage in use, one per hash, newest note first (the hash's newest use counts).</summary>
        private List<Passage> Ranked()
        {
            if (_ranked != null) return _ranked;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            _ranked = Keywords.AllPassages()
                .OrderByDescending(p => _notes.TryGetValue(p.NoteId, out var info) ? info.ModifiedUtc : DateTime.MinValue)
                .ThenBy(p => p.NoteId, StringComparer.Ordinal)
                .ThenBy(p => p.FirstLine)
                .Where(p => seen.Add(p.Hash))
                .ToList();
            return _ranked;
        }

        /// <summary>The passages that may have a vector: the newest notes' first, as many as the store holds.</summary>
        private IEnumerable<Passage> Eligible() => Ranked().Take(Vectors.MaxCount);

        /// <summary>Eligible passages with no vector, newest note first; those the server refused are not waiting.</summary>
        private List<Passage> WaitingPassages() => Eligible().Where(p => !Vectors.Has(p.Hash) && !_refused.Contains(p.Hash)).ToList();

        /// <summary>Passages in use that the server refused.</summary>
        private int Refused() => _refused.Count == 0 ? 0 : Ranked().Count(p => _refused.Contains(p.Hash));

        private bool MeaningLoaded() => _settings().CanEmbed && _loadedFingerprint != null;

        private int Waiting() => MeaningLoaded() ? WaitingPassages().Count : 0;

        /// <summary>More passages in use than the store holds: the oldest notes' go without vectors.</summary>
        private bool Full() => MeaningLoaded() && Ranked().Count > Vectors.MaxCount;

        /// <summary>
        /// Drops every vector no eligible passage uses: those of passages gone, and those of the least
        /// recently modified notes beyond the store's capacity; refusals of such passages are
        /// forgotten too. Nothing goes while notes a reconcile named are still unfed: their vectors
        /// are in the store with no passage to claim them yet.
        /// </summary>
        private void Evict()
        {
            if (_unfed.Count > 0) return;
            var eligible = Eligible().Select(p => p.Hash).ToHashSet(StringComparer.Ordinal);
            if (Vectors.Keep(eligible) > 0) _unsaved = true;
            _refused.IntersectWith(eligible);
        }

        /// <summary>The next passages to send, or none when nothing may be sent now.</summary>
        private IReadOnlyList<Passage> NextBatch(SearchSettings settings)
        {
            if (!settings.CanEmbed || _loadedFingerprint != settings.Fingerprint || Environment.TickCount64 < _retryAtMs)
                return Array.Empty<Passage>();
            var waiting = WaitingPassages();
            if (waiting.Count == 0)
            {
                _splits.Clear();
                return waiting;
            }

            // After the eviction there is room for every waiting passage, unless unfed notes still
            // hold their vectors: then only what fits is sent, so a full store is never asked again and again.
            Evict();
            int room = Vectors.MaxCount - Vectors.Count;

            // The halves of a batch the server refused go first, without what no longer waits.
            if (_splits.Count > 0)
            {
                var stillWaiting = waiting.Select(p => p.Hash).ToHashSet(StringComparer.Ordinal);
                while (_splits.Count > 0)
                {
                    var part = _splits[0];
                    _splits.RemoveAt(0);
                    var send = part.Where(p => stillWaiting.Contains(p.Hash)).Take(room).ToList();
                    if (send.Count > 0) return send;
                }
            }
            return waiting.Take(Math.Min(BatchSize, room)).ToList();
        }

        /// <summary>A failure that may be this batch's content (a passage too long for the model, say) rather than the server's state.</summary>
        private static bool MayBeTheContent(SearchFailure failure) =>
            failure is SearchFailure.Rejected or SearchFailure.BadAnswer or SearchFailure.ServerError;

        private async Task EmbedBatchAsync(SearchSettings settings, IReadOnlyList<Passage> batch, CancellationToken token)
        {
            int generation = _generation;
            var result = await WhileApplyingWork(SendAsync(settings.Embedding!, batch.Select(p => p.SentText).ToList(), token), token).ConfigureAwait(false);
            if (Withdrawn(settings, generation)) return;
            if (result.Vectors != null && result.Vectors.Count != batch.Count)
                result = new EmbeddingResult(null, SearchFailure.BadAnswer, result.Status);

            if (result.Vectors == null)
            {
                if (!MayBeTheContent(result.Failure)) BackOff(result.Failure, result.Status);
                else if (batch.Count > 1) Split(batch);   // no back-off: the halves go next
                else await RefuseAsync(settings, generation, batch[0], result, token).ConfigureAwait(false);
                return;
            }

            _failures = 0;
            _lastFailure = SearchFailure.None;
            _lastStatus = null;
            _unsaved = true;
            // Only what is still in use: an edit applied while the request was out may have replaced some.
            var eligible = Eligible().Select(p => p.Hash).ToHashSet(StringComparer.Ordinal);
            int full = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                if (!eligible.Contains(batch[i].Hash)) continue;
                var put = Vectors.Put(batch[i].Hash, result.Vectors[i]);
                if (put == PutResult.WrongDimension)
                {
                    _warn("The embedding server now gives vectors of another length; the search vectors are made again.");
                    Vectors.Delete();
                    Vectors.Load(settings.Fingerprint);
                    Interlocked.Increment(ref _generation);
                    put = Vectors.Put(batch[i].Hash, result.Vectors[i]);
                }
                if (put == PutResult.Full) full++;   // stays waiting; NextBatch sends nothing more until there is room
            }
            if (full > 0) _warn("The search vectors are full; " + full + " passages wait for room.");
            SaveIfDue(idle: false);
            Publish();
        }

        /// <summary>The two halves of a batch the server refused go next, the first half first.</summary>
        private void Split(IReadOnlyList<Passage> batch)
        {
            int half = batch.Count / 2;
            _splits.Insert(0, batch.Skip(half).ToList());
            _splits.Insert(0, batch.Take(half).ToList());
        }

        /// <summary>
        /// The server refused one passage. When it still takes <see cref="KnownGoodText"/>, the
        /// passage is set aside; when it refuses that too, the server is at fault: indexing waits
        /// and tries again (spec 3.5) and no passage is blamed.
        /// </summary>
        private async Task RefuseAsync(SearchSettings settings, int generation, Passage passage, EmbeddingResult result, CancellationToken token)
        {
            var known = await WhileApplyingWork(SendAsync(settings.Embedding!, new[] { KnownGoodText }, token), token).ConfigureAwait(false);
            if (Withdrawn(settings, generation)) return;
            if (known.Vectors is not { Count: 1 })
            {
                BackOff(known.Vectors == null ? known.Failure : SearchFailure.BadAnswer, known.Status);
                return;
            }

            _failures = 0;
            _refused.Add(passage.Hash);
            _warn("The embedding server " + SearchFailureText.Describe(result.Failure, result.Status) + " for one passage; it is found by words only.");
            Publish();
        }

        /// <summary>A failed request that is the server's: the waiting passages wait, and the next request goes after the retry delay.</summary>
        private void BackOff(SearchFailure failure, int? status)
        {
            _splits.Clear();
            _failures++;
            _lastFailure = failure;
            _lastStatus = status;
            _retryAtMs = Environment.TickCount64 + (long)_retryDelay(_failures).TotalMilliseconds;
            _warn("The embedding server " + SearchFailureText.Describe(failure, status) + "; indexing waits and tries again.");
            Publish();
        }

        /// <summary>Settings changed, or the vectors were started over, while the request was out: its answer is dropped.</summary>
        private bool Withdrawn(SearchSettings sent, int generation)
        {
            if (generation != _generation) return true;
            var now = _settings();
            return !now.CanEmbed || now.Fingerprint != sent.Fingerprint;
        }

        /// <summary>The embedder's answer; an embedder that throws anyway counts as a failed request, so it waits out the retry delay.</summary>
        private async Task<EmbeddingResult> SendAsync(SearchServer server, IReadOnlyList<string> texts, CancellationToken token)
        {
            try
            {
                return await _embedder.EmbedAsync(server, texts, BatchTimeout, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _warn("The embedding request failed (" + ex.GetType().Name + ").");
                return new EmbeddingResult(null, ex is OperationCanceledException ? SearchFailure.TimedOut : SearchFailure.Unreachable, null);
            }
        }

        /// <summary>
        /// Waits for a request to the embedding server (up to <see cref="BatchTimeout"/>) while still
        /// applying the queued work: note texts, removals and settings, so words search is never held
        /// up by the server. Settings that withdraw the request are seen by <see cref="Withdrawn"/> after.
        /// </summary>
        private async Task<EmbeddingResult> WhileApplyingWork(Task<EmbeddingResult> request, CancellationToken token)
        {
            while (!request.IsCompleted)
            {
                using (var wake = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var signal = _signal.WaitAsync(wake.Token);
                    if (await Task.WhenAny(request, signal).ConfigureAwait(false) != signal)
                    {
                        // The answer came first: this wait must not take a later wake-up from the main loop.
                        wake.Cancel();
                        try { await signal.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
                token.ThrowIfCancellationRequested();

                if (ApplyQueuedWork()) Publish();
            }
            return await request.ConfigureAwait(false);
        }

        /// <summary>
        /// Evicts (see <see cref="Evict"/>) and writes the file, when anything changed since the last
        /// save: while indexing at most every <c>saveEvery</c>, and at once when the worker is idle
        /// with nothing waiting.
        /// </summary>
        private void SaveIfDue(bool idle)
        {
            if (!_unsaved) return;
            if (_loadedFingerprint == null)
            {
                _unsaved = false;
                return;
            }
            long now = Environment.TickCount64;
            bool due = now - _lastSaveMs >= _saveEvery.TotalMilliseconds;
            if (!due && !(idle && Waiting() == 0)) return;

            Evict();
            if (Vectors.Save()) _lastSaveMs = now;
            _unsaved = false;
        }

        private void Publish()
        {
            var passages = Keywords.AllPassages();
            int withVectors = passages.Count(p => Vectors.Has(p.Hash));
            var progress = new IndexProgress(Keywords.NoteCount, passages.Count, withVectors, Waiting(), _lastFailure, _lastStatus, Full(), Refused());
            if (progress == Volatile.Read(ref _progress)) return;
            Volatile.Write(ref _progress, progress);
            ProgressChanged?.Invoke();
        }

        /// <summary>Stops the worker (an embedding request out is cancelled); waits up to 2 s for it.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _signal.Release();
            try { _worker.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
