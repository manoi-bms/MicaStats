using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Merging the keyword and vector lists, and the per-note and total caps (spec 3.5).</summary>
    public static class SearchFusion
    {
        public const int K = 60;

        /// <summary>
        /// Reciprocal Rank Fusion: each list gives a passage 1 / (K + rank), rank from 1; a passage
        /// is identified by note and first line. Ties go to the passage ranked first sooner.
        /// </summary>
        public static IReadOnlyList<Passage> Fuse(IReadOnlyList<Passage> first, IReadOnlyList<Passage> second)
        {
            var scored = new Dictionary<(string, int), (Passage Passage, double Score, int Best, int Order)>();
            int order = 0;
            void Add(IReadOnlyList<Passage> list)
            {
                for (int rank = 0; rank < list.Count; rank++)
                {
                    var key = (list[rank].NoteId, list[rank].FirstLine);
                    double score = 1.0 / (K + rank + 1);
                    scored[key] = scored.TryGetValue(key, out var e)
                        ? (e.Passage, e.Score + score, Math.Min(e.Best, rank), e.Order)
                        : (list[rank], score, rank, order++);
                }
            }
            Add(first);
            Add(second);
            return scored.Values
                .OrderByDescending(e => e.Score)
                .ThenBy(e => e.Best)
                .ThenBy(e => e.Order)
                .Select(e => e.Passage)
                .ToList();
        }

        /// <summary>At most <paramref name="perNote"/> passages of one note and <paramref name="total"/> in all, order kept.</summary>
        public static IReadOnlyList<Passage> Cap(IEnumerable<Passage> ordered, int perNote, int total)
        {
            var perNoteCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<Passage>();
            foreach (var passage in ordered)
            {
                int n = perNoteCount.TryGetValue(passage.NoteId, out int c) ? c : 0;
                if (n >= perNote) continue;
                perNoteCount[passage.NoteId] = n + 1;
                result.Add(passage);
                if (result.Count == total) break;
            }
            return result;
        }
    }
}
