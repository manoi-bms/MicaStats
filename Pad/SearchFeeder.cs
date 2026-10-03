using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Hands the notes to the search indexer (spec 3.5): every note at start and on
    /// <see cref="ReconcileAll"/>, an open note's text 2 s after its last change (read on the UI
    /// thread, where <see cref="OpenNote.TextProvider"/> lives) or at once when its tab closes
    /// sooner, a note that joins the open tabs (a file opened, a closed note reopened) the same
    /// way, and deletions at once.
    /// </summary>
    internal sealed class SearchFeeder : IDisposable
    {
        public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

        private readonly PadWorkspace _workspace;
        private readonly SearchIndexer _indexer;
        private readonly TimeSpan _debounce;
        private readonly Dictionary<string, DispatcherTimer> _timers = new(StringComparer.Ordinal);

        /// <param name="workspace">The open notes, and the store of the rest.</param>
        /// <param name="indexer">Where the notes go.</param>
        /// <param name="debounce">How long after an edit a note is sent; <see cref="DefaultDebounce"/> when null.</param>
        /// <param name="reconcile">
        /// False leaves the first <see cref="ReconcileAll"/> to the caller: a start for a note
        /// tool, which lists the stored notes without the store's repairs and tries again when
        /// they cannot be listed.
        /// </param>
        public SearchFeeder(PadWorkspace workspace, SearchIndexer indexer, TimeSpan? debounce = null, bool reconcile = true)
        {
            _workspace = workspace;
            _indexer = indexer;
            _debounce = debounce ?? DefaultDebounce;
            if (reconcile) ReconcileAll();   // first: when it throws, nothing is left subscribed
            _workspace.NoteTextChanged += OnTextChanged;
            _workspace.NoteClosing += OnClosing;
            _workspace.NoteDeleted += OnDeleted;
            _workspace.Open.CollectionChanged += OnOpenChanged;
        }

        /// <summary>
        /// Every note again: open ones from their editors, the rest from the store; vanished ones removed.
        /// <paramref name="readOnly"/> lists the stored notes without the store's repairs
        /// (<see cref="NoteStore.PeekAllMetas"/>): for a reconcile a note tool asked for, which must
        /// not change the store. A note whose <c>meta.json</c> is damaged is then left out until
        /// MicaPad itself has loaded it; one whose <c>meta.json</c> cannot be read right now keeps
        /// what the index has for it; and when the notes cannot be listed at all, this throws
        /// before the index is touched.
        /// </summary>
        public void ReconcileAll(bool readOnly = false)
        {
            IReadOnlyList<string> unknown = Array.Empty<string>();
            var metas = readOnly ? _workspace.Store.PeekAllMetas(out unknown) : _workspace.Store.LoadAllMetas();
            var open = _workspace.Open.ToDictionary(n => n.Id, StringComparer.Ordinal);
            _indexer.Reconcile(metas.Select(m => m.Id).Concat(open.Keys).Distinct().ToList(), unknown);
            foreach (var note in open.Values) Send(note);
            foreach (var meta in metas.Where(m => !open.ContainsKey(m.Id)))
                _indexer.IndexStored(meta.Id, meta.Title, meta.ModifiedUtc);
        }

        /// <summary>Sends every note still waiting for its debounce now (tests, and before a search).</summary>
        public void FlushPending()
        {
            foreach (string id in _timers.Keys.ToList()) Fire(id);
        }

        private void OnTextChanged(OpenNote note)
        {
            if (!_timers.TryGetValue(note.Id, out var timer))
            {
                timer = new DispatcherTimer { Interval = _debounce };
                string id = note.Id;
                timer.Tick += (_, _) => Fire(id);
                _timers[note.Id] = timer;
            }
            timer.Stop();
            timer.Start();
        }

        /// <summary>
        /// A note joined the open tabs: a file opened in a tab, a closed note reopened, a new note.
        /// The workspace raises no text change for those, so without this the index would not know
        /// a file's text until its first edit. It waits for the debounce like an edit; a search
        /// flushes it first. Raised inside the workspace's own change, so it must not throw: a tab
        /// always opens, and a note not sent here is sent at its next edit or reconcile.
        /// </summary>
        private void OnOpenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems == null || e.Action == NotifyCollectionChangedAction.Move) return;
            try
            {
                foreach (OpenNote note in e.NewItems.OfType<OpenNote>()) OnTextChanged(note);
            }
            catch (Exception)
            {
                // Nothing to do here; see the summary.
            }
        }

        /// <summary>The tab closes while an edit still waits for its debounce: sent now, while the text can still be read.</summary>
        private void OnClosing(OpenNote note)
        {
            if (_timers.ContainsKey(note.Id)) Fire(note.Id);
        }

        private void Fire(string id)
        {
            if (_timers.Remove(id, out var timer)) timer.Stop();
            var note = _workspace.Open.FirstOrDefault(n => n.Id == id);
            if (note != null) Send(note);
        }

        private void Send(OpenNote note)
        {
            string text = note.TextProvider();
            // The tab title is only refreshed when the note is saved (PadWorkspace.EnqueueSave):
            // the live one, so a search never finds the old first line.
            _indexer.SetNote(note.Id, note.LiveTitle(text), text, note.Meta.ModifiedUtc);
        }

        private void OnDeleted(string id)
        {
            if (_timers.Remove(id, out var timer)) timer.Stop();
            _indexer.RemoveNote(id);
        }

        public void Dispose()
        {
            _workspace.NoteTextChanged -= OnTextChanged;
            _workspace.NoteClosing -= OnClosing;
            _workspace.NoteDeleted -= OnDeleted;
            _workspace.Open.CollectionChanged -= OnOpenChanged;
            foreach (var timer in _timers.Values) timer.Stop();
            _timers.Clear();
        }
    }
}
