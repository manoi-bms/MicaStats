using System;
using System.Collections.Generic;
using System.Threading;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The one thread that writes MicaPad's files, so typing never waits on the disk.
    ///
    /// <para>
    /// Work runs in the order it was queued. Work queued under a key that is still waiting
    /// replaces the waiting work in place — only the newest text of a note matters — and keeps its
    /// position, so a delete queued after a save still runs after it. Work that throws is retried
    /// after 1, 2, 5, then every 10 seconds; the error is reported through <see cref="Completed"/>
    /// each time, and other keys keep flowing meanwhile.
    /// </para>
    /// </summary>
    public sealed class AutosaveWriter : IDisposable
    {
        private sealed class Entry
        {
            public string Key = "";
            public Action Work = () => { };
            public int Failures;
            public DateTime RetryAtUtc;
        }

        private readonly object _gate = new();
        private readonly LinkedList<Entry> _queue = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new();
        private readonly Func<int, TimeSpan> _backoff;
        private readonly Thread _thread;
        private bool _busy;
        private bool _stopping;

        /// <summary>Raised on the writer thread after each attempt: the key, and the error or null.</summary>
        public event Action<string, Exception?>? Completed;

        /// <param name="backoff">Delay before retry number n (1-based); <see cref="DefaultBackoff"/> when null. Tests pass milliseconds.</param>
        public AutosaveWriter(Func<int, TimeSpan>? backoff = null)
        {
            _backoff = backoff ?? DefaultBackoff;
            _thread = new Thread(Run) { IsBackground = true, Name = "MicaPad writer" };
            _thread.Start();
        }

        /// <summary>1 s, 2 s, 5 s, then 10 s for every later retry.</summary>
        public static TimeSpan DefaultBackoff(int failures) => failures switch
        {
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(2),
            3 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };

        /// <summary>Queues work, replacing work still waiting under the same key.</summary>
        public void Enqueue(string key, Action work)
        {
            lock (_gate)
            {
                if (_stopping) return;
                if (_byKey.TryGetValue(key, out var node)) node.Value.Work = work;
                else _byKey[key] = _queue.AddLast(new Entry { Key = key, Work = work });
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Work queued or running.</summary>
        public int PendingCount
        {
            get { lock (_gate) return _queue.Count + (_busy ? 1 : 0); }
        }

        /// <summary>
        /// Waits until everything queued has been written, retrying failed work immediately rather
        /// than after its backoff. False when <paramref name="timeout"/> passes first.
        /// </summary>
        public bool FlushAll(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (_gate)
            {
                foreach (var entry in _queue) entry.RetryAtUtc = DateTime.MinValue;
                Monitor.PulseAll(_gate);

                while (_queue.Count > 0 || _busy)
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(_gate, left);
                }
                return true;
            }
        }

        /// <summary>Stops the thread after the work in progress. Queued work is dropped: call <see cref="FlushAll"/> first.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _stopping = true;
                Monitor.PulseAll(_gate);
            }
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Run()
        {
            while (true)
            {
                Entry entry;
                lock (_gate)
                {
                    while (true)
                    {
                        if (_stopping) return;

                        DateTime now = DateTime.UtcNow;
                        LinkedListNode<Entry>? ready = null;
                        DateTime earliest = DateTime.MaxValue;
                        for (var node = _queue.First; node != null; node = node.Next)
                        {
                            if (node.Value.RetryAtUtc <= now) { ready = node; break; }
                            if (node.Value.RetryAtUtc < earliest) earliest = node.Value.RetryAtUtc;
                        }

                        if (ready != null)
                        {
                            _queue.Remove(ready);
                            _byKey.Remove(ready.Value.Key);
                            entry = ready.Value;
                            _busy = true;
                            break;
                        }

                        if (earliest == DateTime.MaxValue) Monitor.Wait(_gate);
                        else Monitor.Wait(_gate, earliest - now);
                    }
                }

                Exception? error = null;
                try
                {
                    entry.Work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                // Reported while still marked busy, so FlushAll cannot return before listeners hear the outcome.
                try { Completed?.Invoke(entry.Key, error); }
                catch (Exception) { }

                lock (_gate)
                {
                    _busy = false;
                    if (error != null && !_stopping)
                    {
                        entry.Failures++;
                        entry.RetryAtUtc = DateTime.UtcNow + _backoff(entry.Failures);

                        if (_byKey.TryGetValue(entry.Key, out var newer))
                        {
                            // Newer work arrived while this ran: it supersedes the failed work but inherits the backoff.
                            newer.Value.Failures = entry.Failures;
                            newer.Value.RetryAtUtc = entry.RetryAtUtc;
                        }
                        else
                        {
                            _byKey[entry.Key] = _queue.AddFirst(entry);
                        }
                    }
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
