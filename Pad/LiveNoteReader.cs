using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The notes of the running app for the two note tools (<see cref="NoteTools"/>): a search the
    /// way the Search notes pane runs it, and one note's text as it is now.
    ///
    /// <para>
    /// It needs no MicaPad window: the workspace, the search and its feeder belong to the app and
    /// stay after the last window hides. They are asked for at each call, because the app builds
    /// them when MicaPad first opens. Until then there is nothing to search and no note to read.
    /// </para>
    ///
    /// <para>
    /// Tool calls arrive on any thread. The open notes and their editors belong to the UI thread
    /// (<see cref="OpenNote.TextProvider"/>), so they are read through the dispatcher; the search
    /// and the store are read off it. The vault is never read: a note's text holds credential
    /// references, not credentials, and <see cref="NoteTools"/> cleans those too.
    /// </para>
    /// </summary>
    internal sealed class LiveNoteReader : INoteReader
    {
        private static readonly NoteSearchResult Nothing = new(Array.Empty<NoteHit>(), false);

        private readonly Func<PadWorkspace?> _workspace;
        private readonly Func<NoteSearchService?> _search;
        private readonly Func<SearchFeeder?> _feeder;
        private readonly Dispatcher _ui;

        /// <param name="workspace">The app's workspace, or null before MicaPad first opened.</param>
        /// <param name="search">The app's search, or null before it started.</param>
        /// <param name="feeder">What hands the open notes to the search index, or null.</param>
        /// <param name="ui">The thread the open notes live on.</param>
        public LiveNoteReader(Func<PadWorkspace?> workspace, Func<NoteSearchService?> search, Func<SearchFeeder?> feeder, Dispatcher ui)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _feeder = feeder ?? throw new ArgumentNullException(nameof(feeder));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        /// <summary>
        /// The passages the Search notes pane would find, words and (when set up) meaning. The
        /// notes are searched as they are now: an edit reaches the index two seconds after it, so
        /// the feeder is flushed and the index waited for first, as Ask your notes does. This
        /// waits for the notes' words only, never for the embedding server.
        /// </summary>
        public async Task<NoteSearchResult> SearchAsync(string query, CancellationToken ct)
        {
            NoteSearchService? service = _search();
            if (service == null) return Nothing;

            await OnUiAsync(() =>
            {
                _feeder()?.FlushPending();
                return true;
            }, ct).ConfigureAwait(false);
            await service.Indexer.WhenApplied().WaitAsync(ct).ConfigureAwait(false);

            // Off the UI thread, as the pane does: the keyword search and the vector scan take a while over a large index.
            SearchOutcome outcome = await Task.Run(() => service.Search.SearchAsync(query, ct), ct).ConfigureAwait(false);
            if (outcome.Hits.Count == 0) return new NoteSearchResult(Array.Empty<NoteHit>(), outcome.UsedMeaning);

            HashSet<string> open = await OnUiAsync(OpenIds, ct).ConfigureAwait(false);
            var hits = new List<NoteHit>(outcome.Hits.Count);
            foreach (Passage p in outcome.Hits)
                hits.Add(new NoteHit(p.NoteId, p.Title, p.Heading, p.FirstLine, p.LastLine, open.Contains(p.NoteId), p.Body));
            return new NoteSearchResult(hits, outcome.UsedMeaning);
        }

        /// <summary>
        /// One note's title and whole text: an open note from its editor, unsaved edits included,
        /// with the title it would have if it were saved now; a closed note from the store. Null
        /// for an id no note has, and for text that is not an id at all. Throws when a note is
        /// there but its text cannot be read right now.
        /// </summary>
        public async Task<NoteText?> ReadAsync(string noteId, CancellationToken ct)
        {
            // An id names a folder under the store. Only the exact form the store gives its notes
            // goes any further: no other letter case, no device name, no separator reaches a path.
            string id = (noteId ?? "").Trim();
            if (!NoteStore.IsNoteId(id)) return null;
            PadWorkspace? workspace = _workspace();
            if (workspace == null) return null;

            NoteText? open = await OnUiAsync(() => ReadOpen(workspace, id), ct).ConfigureAwait(false);
            if (open != null) return open;
            return await Task.Run(() => ReadStored(workspace.Store, id), ct).ConfigureAwait(false);
        }

        private HashSet<string> OpenIds()
        {
            var open = new HashSet<string>(StringComparer.Ordinal);
            if (_workspace() is { } workspace)
                foreach (OpenNote note in workspace.Open) open.Add(note.Id);
            return open;
        }

        /// <summary>UI thread. The title is worked out as the search index does (<see cref="SearchFeeder"/>): a tab's title is only refreshed when its note is saved.</summary>
        private static NoteText? ReadOpen(PadWorkspace workspace, string id)
        {
            OpenNote? note = workspace.Open.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
            if (note == null) return null;
            string text = note.TextProvider();
            string title = note.Meta.IsFileBacked || note.Meta.TitleIsCustom
                ? note.Title
                : NoteTitle.FromText(text, note.Meta.UntitledNumber);
            return new NoteText(note.Id, title, text);
        }

        /// <summary>
        /// Any thread. Through the store's read-only reads: a tool that reads must never change a
        /// note, so nothing here rebuilds a <c>meta.json</c>, finishes a write or makes a folder.
        /// </summary>
        private static NoteText? ReadStored(NoteStore store, string id)
        {
            NoteMeta? meta = store.PeekMeta(id);
            if (meta == null) return null;
            if (!store.TryPeekText(id, out string? text))
                throw new IOException("The text of a note cannot be read right now");
            return new NoteText(meta.Id, meta.Title, text ?? "");
        }

        /// <summary>Runs <paramref name="read"/> on the UI thread: at once when already there, else through the dispatcher.</summary>
        private Task<T> OnUiAsync<T>(Func<T> read, CancellationToken ct)
        {
            if (!_ui.CheckAccess()) return _ui.InvokeAsync(read, DispatcherPriority.Normal, ct).Task;
            try
            {
                return Task.FromResult(read());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }
    }
}
