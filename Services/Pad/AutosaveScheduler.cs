using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Decides when a changed note is due to be saved: one second after the last keystroke, or
    /// five seconds after the first unsaved one if the typing never stops. Pure and single-threaded
    /// (UI thread only); the clock is passed in.
    /// </summary>
    public sealed class AutosaveScheduler
    {
        /// <summary>Quiet time after the last change before a save.</summary>
        public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

        /// <summary>Longest a change may wait during continuous typing.</summary>
        public static readonly TimeSpan MaxLatency = TimeSpan.FromSeconds(5);

        private readonly Dictionary<string, (DateTime First, DateTime Last)> _pending = new();

        /// <summary>Records a change to a note.</summary>
        public void MarkChanged(string id, DateTime now)
        {
            _pending[id] = _pending.TryGetValue(id, out var p) ? (p.First, now) : (now, now);
        }

        /// <summary>True while the note has changes not yet handed to the writer.</summary>
        public bool IsPending(string id) => _pending.ContainsKey(id);

        /// <summary>The notes due now; they stop being pending.</summary>
        public IReadOnlyList<string> TakeDue(DateTime now)
        {
            var due = _pending
                .Where(kv => now - kv.Value.Last >= Debounce || now - kv.Value.First >= MaxLatency)
                .Select(kv => kv.Key)
                .ToList();
            foreach (string id in due) _pending.Remove(id);
            return due;
        }

        /// <summary>Every pending note, due or not; they stop being pending.</summary>
        public IReadOnlyList<string> TakeAll()
        {
            var all = _pending.Keys.ToList();
            _pending.Clear();
            return all;
        }

        /// <summary>Drops a note's pending change, when it was saved some other way.</summary>
        public void Forget(string id) => _pending.Remove(id);
    }
}
