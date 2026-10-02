using System;
using System.Collections.Generic;
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
    /// sooner, and deletions at once.
    /// </summary>
    internal sealed class SearchFeeder : IDisposable
    {
        public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

        private readonly PadWorkspace _workspace;
        private readonly SearchIndexer _indexer;
        private readonly TimeSpan _debounce;
        private readonly Dictionary<string, DispatcherTimer> _timers = new(StringComparer.Ordinal);

        public SearchFeeder(PadWorkspace workspace, SearchIndexer indexer, TimeSpan? debounce = null)
        {
            _workspace = workspace;
            _indexer = indexer;
            _debounce = debounce ?? DefaultDebounce;
            ReconcileAll();   // first: when it throws, nothing is left subscribed
            _workspace.NoteTextChanged += OnTextChanged;
            _workspace.NoteClosing += OnClosing;
            _workspace.NoteDeleted += OnDeleted;
        }

        /// <summary>Every note again: open ones from their editors, the rest from the store; vanished ones removed.</summary>
        public void ReconcileAll()
        {
            var metas = _workspace.Store.LoadAllMetas();
            var open = _workspace.Open.ToDictionary(n => n.Id, StringComparer.Ordinal);
            _indexer.Reconcile(metas.Select(m => m.Id).Concat(open.Keys).Distinct().ToList());
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
            // The tab title is only refreshed when the note is saved (PadWorkspace.EnqueueSave); an
            // automatic one is worked out from the text here so a search never finds the old first line.
            string title = note.Meta.IsFileBacked || note.Meta.TitleIsCustom
                ? note.Title
                : NoteTitle.FromText(text, note.Meta.UntitledNumber);
            _indexer.SetNote(note.Id, title, text, note.Meta.ModifiedUtc);
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
            foreach (var timer in _timers.Values) timer.Stop();
            _timers.Clear();
        }
    }
}
