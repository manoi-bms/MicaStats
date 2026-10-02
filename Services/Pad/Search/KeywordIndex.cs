using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>A passage and its BM25 score for one query.</summary>
    public readonly record struct KeywordHit(Passage Passage, double Score);

    /// <summary>
    /// BM25 over passages (spec 3.2), in memory, rebuilt from the notes at start and replaced one
    /// note at a time. Any query token may match (OR). The query's last word, while still being
    /// typed, also matches as a prefix: at most <see cref="MaxPrefixExpansions"/> indexed words, the
    /// best of them counted once per passage. Thread-safe: every member takes one lock.
    /// </summary>
    public sealed class KeywordIndex
    {
        public const double K1 = 1.2;
        public const double B = 0.75;
        public const int MaxPrefixExpansions = 50;

        private sealed class Entry
        {
            public required Passage Passage { get; init; }
            public required Dictionary<string, int> Counts { get; init; }
            public required int Length { get; init; }
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, List<Entry>> _byNote = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
        private long _totalLength;
        private int _count;

        /// <summary>Passages indexed.</summary>
        public int PassageCount { get { lock (_gate) return _count; } }

        /// <summary>Notes with at least one passage.</summary>
        public int NoteCount { get { lock (_gate) return _byNote.Count; } }

        /// <summary>Replaces the note's passages; an empty list removes the note.</summary>
        public void Set(string noteId, IReadOnlyList<Passage> passages)
        {
            var entries = new List<Entry>(passages.Count);
            foreach (var passage in passages)
            {
                var tokens = SearchTokens.Of(passage.SentText);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string token in tokens) counts[token] = counts.TryGetValue(token, out int n) ? n + 1 : 1;
                entries.Add(new Entry { Passage = passage, Counts = counts, Length = tokens.Count });
            }

            lock (_gate)
            {
                RemoveLocked(noteId);
                if (entries.Count == 0) return;
                foreach (var entry in entries)
                {
                    foreach (string term in entry.Counts.Keys)
                        _documentFrequency[term] = _documentFrequency.TryGetValue(term, out int df) ? df + 1 : 1;
                    _totalLength += entry.Length;
                    _count++;
                }
                _byNote[noteId] = entries;
            }
        }

        /// <summary>Forgets the note.</summary>
        public void Remove(string noteId)
        {
            lock (_gate) RemoveLocked(noteId);
        }

        private void RemoveLocked(string noteId)
        {
            if (!_byNote.Remove(noteId, out var entries)) return;
            foreach (var entry in entries)
            {
                foreach (string term in entry.Counts.Keys)
                {
                    int df = _documentFrequency[term] - 1;
                    if (df == 0) _documentFrequency.Remove(term);
                    else _documentFrequency[term] = df;
                }
                _totalLength -= entry.Length;
                _count--;
            }
        }

        /// <summary>Every passage, note by note.</summary>
        public IReadOnlyList<Passage> AllPassages()
        {
            lock (_gate) return _byNote.Values.SelectMany(list => list.Select(e => e.Passage)).ToList();
        }

        /// <summary>The note's passages, or none.</summary>
        public IReadOnlyList<Passage> PassagesOf(string noteId)
        {
            lock (_gate)
                return _byNote.TryGetValue(noteId, out var list) ? list.Select(e => e.Passage).ToList() : Array.Empty<Passage>();
        }

        /// <summary>The indexed notes.</summary>
        public IReadOnlyCollection<string> NoteIds()
        {
            lock (_gate) return _byNote.Keys.ToList();
        }

        /// <summary>Best first; ties by note and line so results are stable.</summary>
        public IReadOnlyList<KeywordHit> Search(string query, int limit)
        {
            var terms = SearchTokens.Of(query).Distinct(StringComparer.Ordinal).ToList();
            string? prefix = SearchTokens.LastWordPrefix(query);
            if (terms.Count == 0) return Array.Empty<KeywordHit>();

            lock (_gate)
            {
                if (_count == 0) return Array.Empty<KeywordHit>();
                double average = (double)_totalLength / _count;

                List<string>? expansions = null;
                if (prefix != null)
                {
                    expansions = _documentFrequency.Keys
                        .Where(t => t.Length > prefix.Length && t.StartsWith(prefix, StringComparison.Ordinal))
                        .OrderByDescending(t => _documentFrequency[t])
                        .ThenBy(t => t, StringComparer.Ordinal)
                        .Take(MaxPrefixExpansions)
                        .ToList();
                }

                var hits = new List<KeywordHit>();
                foreach (var list in _byNote.Values)
                {
                    foreach (var entry in list)
                    {
                        double score = 0;
                        foreach (string term in terms) score += Weight(entry, term, average);
                        if (expansions != null)
                        {
                            double best = 0;
                            foreach (string term in expansions) best = Math.Max(best, Weight(entry, term, average));
                            score += best;
                        }
                        if (score > 0) hits.Add(new KeywordHit(entry.Passage, score));
                    }
                }

                return hits
                    .OrderByDescending(h => h.Score)
                    .ThenBy(h => h.Passage.NoteId, StringComparer.Ordinal)
                    .ThenBy(h => h.Passage.FirstLine)
                    .Take(limit)
                    .ToList();
            }
        }

        private double Weight(Entry entry, string term, double average)
        {
            if (!entry.Counts.TryGetValue(term, out int tf)) return 0;
            int df = _documentFrequency[term];
            double idf = Math.Log(1 + (_count - df + 0.5) / (df + 0.5));
            return idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * entry.Length / average));
        }
    }
}
