using Kil0bitSystemMonitor.Services.Pad.Ai;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A hand-written <see cref="INoteReader"/>: one note with one passage unless the test puts in
    /// something else. It counts how it was asked, so a test can prove the notes were not read.
    /// </summary>
    internal sealed class FakeNoteReader : INoteReader
    {
        public int Searches;
        public int Reads;

        /// <summary>The last query, as the reader received it (already cleaned of credentials).</summary>
        public string? Query;

        public List<NoteHit> Hits = new() { new NoteHit("a1", "Servers", "Production", 3, 9, true, "the vpn gateway") };

        public NoteText? Note = new("a1", "Servers", "line one\nline two");

        /// <summary>Thrown by both members when set.</summary>
        public Exception? Throws;

        /// <summary>Both members answer null: no such note, and a search that hands back nothing at all.</summary>
        public bool AnswersNull;

        public Task<NoteSearchResult> SearchAsync(string query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Searches);
            Query = query;
            if (Throws != null) throw Throws;
            return Task.FromResult(AnswersNull ? null! : new NoteSearchResult(Hits, false));
        }

        public Task<NoteText?> ReadAsync(string noteId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            if (Throws != null) throw Throws;
            return Task.FromResult(AnswersNull ? null : Note);
        }
    }
}
