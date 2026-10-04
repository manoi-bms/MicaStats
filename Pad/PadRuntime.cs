using System;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's workspace and its search for the whole app, built once per run by whichever
    /// needs them first: the first MicaPad window (<see cref="EnsureStarted"/>), or the first
    /// note tool call (<see cref="StartForNoteTools"/>), which needs no window.
    ///
    /// <para>
    /// There is one construction path, so there is never a second workspace over the store. The
    /// workspace is restored from the saved session in the same call that creates it: until
    /// then its session is an empty one, and the flush at exit would write that over
    /// <c>session.json</c>. Nothing here shows a window, makes a note, loads the vault or tells
    /// the user anything; those belong to a MicaPad window.
    /// </para>
    ///
    /// <para>
    /// The two starts differ in what they may do to the store. A window's start is MicaPad
    /// opening: a session that is missing or unreadable is rebuilt from the notes, and records
    /// are repaired on the way. A note tool's start repairs nothing: it goes ahead only when the
    /// saved session loads as it is, and otherwise starts nothing.
    /// </para>
    ///
    /// <para>
    /// Everything runs on the UI thread, where the workspace lives. The three properties may be
    /// read from any thread.
    /// </para>
    /// </summary>
    internal sealed class PadRuntime : IDisposable
    {
        private readonly string _root;
        private readonly Func<NoteStore> _store;
        private readonly Func<NoteStore, PadWorkspace> _newWorkspace;
        private readonly Func<NoteStore, NoteSearchService> _newSearch;
        private readonly Action<NoteSearchService, SearchFeeder>? _searchStarted;
        private readonly Action<string> _warn;

        private volatile PadWorkspace? _workspace;
        private volatile NoteSearchService? _search;
        private volatile SearchFeeder? _feeder;
        private bool _reconciledForNotes;
        private bool _searchFailed;
        private bool _notReadySaid;
        private bool _exiting;
        private bool _disposed;

        /// <param name="root">The MicaPad folder (<see cref="NoteStore.DefaultRoot"/> in the app).</param>
        /// <param name="store">The app's one store over <paramref name="root"/>. Asked only once a start is decided: making a store creates its folder and its key.</param>
        /// <param name="newWorkspace">Builds the workspace over the store; called at most once.</param>
        /// <param name="newSearch">Builds the search over the store.</param>
        /// <param name="searchStarted">Runs once the search and its feeder exist, before they are published.</param>
        /// <param name="warn">Log lines; never note text.</param>
        public PadRuntime(string root, Func<NoteStore> store, Func<NoteStore, PadWorkspace> newWorkspace,
                          Func<NoteStore, NoteSearchService> newSearch,
                          Action<NoteSearchService, SearchFeeder>? searchStarted = null, Action<string>? warn = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _newWorkspace = newWorkspace ?? throw new ArgumentNullException(nameof(newWorkspace));
            _newSearch = newSearch ?? throw new ArgumentNullException(nameof(newSearch));
            _searchStarted = searchStarted;
            _warn = warn ?? (_ => { });
        }

        /// <summary>The workspace, restored; null until the first start.</summary>
        public PadWorkspace? Workspace => _workspace;

        /// <summary>The search; null until the first start, and when it could not start.</summary>
        public NoteSearchService? Search => _search;

        /// <summary>What hands the notes to the search index; null like <see cref="Search"/>.</summary>
        public SearchFeeder? Feeder => _feeder;

        /// <summary>
        /// For a MicaPad window: the workspace, built and restored if this is the first start, and
        /// the search (best effort: a search that cannot start is logged and tried again at the
        /// next call; MicaPad opens without it). Makes the store, so the MicaPad folder exists
        /// afterwards. Throws when the workspace cannot be built or restored; nothing is kept then.
        /// </summary>
        public PadWorkspace EnsureStarted() => Start(forTools: false, saved: null);

        /// <summary>
        /// For a note tool call that passed its permission check. True when there are notes to
        /// read: the workspace is built and restored (if this is the first start), the search is
        /// started, and once per run the index is brought up to date, so stored notes and notes
        /// opened from files are found.
        ///
        /// <para>
        /// False, with nothing built and nothing created on disk, when there are no notes at all:
        /// MicaPad was never used on this PC (it has no notes folder), or its folder holds no
        /// note and no session.
        /// </para>
        ///
        /// <para>
        /// A first start goes ahead only when the saved session loads as it is. That is asked
        /// first, without writing and before anything is made, the store included
        /// (<see cref="NoteStore.PeekSessionAt"/>). With notes but no session that loads (it is
        /// missing, locked, damaged), this throws <see cref="NotesNotReadyException"/> and
        /// nothing is started: a workspace started then would rebuild the session from the notes,
        /// repairing every note's record on the way, and for a session that was only locked the
        /// rebuilt one would replace the saved layout at exit. The session that was looked at is
        /// the one the workspace is restored from, so nothing can change in between.
        /// </para>
        ///
        /// <para>
        /// Throws also once exit has begun, when the start fails, and when the notes cannot be
        /// listed for the index: a tool then answers with an error result, never with an empty
        /// list that reads as "no such notes". Only a reconcile that ran to its end counts as
        /// this run's; one that failed leaves the index as it was and is tried again at the next
        /// call. A search that could not start is not built again by a note tool in this run.
        /// </para>
        ///
        /// <para>
        /// A read tool does not change notes, so the index is fed here without the store's
        /// repairs: closed notes are read where they lie. Restoring the session is the workspace's
        /// own start and reads its notes as opening MicaPad does.
        /// </para>
        /// </summary>
        public bool StartForNoteTools()
        {
            if (_exiting || _disposed) throw new InvalidOperationException("MicaStats is closing");

            SessionState? saved = null;
            if (_workspace == null)
            {
                if (!NoteStore.ExistsAt(_root)) return false;

                saved = NoteStore.PeekSessionAt(_root);
                if (saved == null)
                {
                    if (!NoteStore.HasNotesAt(_root)) return false;
                    if (!_notReadySaid)
                    {
                        _notReadySaid = true;
                        _warn("MicaPad's notes are not ready for the note tools: the saved session is missing or cannot be read. Opening MicaPad once restores it.");
                    }
                    throw new NotesNotReadyException();
                }
            }

            Start(forTools: true, saved);

            if (!_reconciledForNotes && _feeder is { } feeder)
            {
                // A feeder built just now has read nothing yet; one that MicaPad built earlier has
                // not seen what was stored since. Either way: every note, once.
                feeder.FlushPending();
                feeder.ReconcileAll(readOnly: true);   // throws when the notes cannot be listed: not this run's reconcile yet
                _reconciledForNotes = true;
            }
            return true;
        }

        /// <summary>Exit has begun: no note tool starts or reads anything from now on.</summary>
        public void BeginExit() => _exiting = true;

        /// <summary>Stops the feeder, the search and the workspace, in that order. Safe to call twice.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _exiting = true;
            _feeder?.Dispose();
            _search?.Dispose();
            _workspace?.Dispose();
        }

        /// <param name="forTools">A note tool's start: no repairs while indexing, and no second try at a search that could not start.</param>
        /// <param name="saved">The saved session a note tool's first start has read already; null lets the workspace load it, rebuilt if need be.</param>
        private PadWorkspace Start(bool forTools, SessionState? saved)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PadRuntime));

            PadWorkspace? workspace = _workspace;
            if (workspace == null)
            {
                workspace = _newWorkspace(_store());
                try
                {
                    // In the same call as the creation, before anything else can reach the
                    // workspace: unrestored, its session is empty, and a flush would save that.
                    workspace.Restore(saved);
                }
                catch
                {
                    workspace.Dispose();
                    throw;
                }
                _workspace = workspace;
            }

            // A window tries the search again each time it opens. A note tool does not: a search
            // that could not start would otherwise be built, and fail, at every call.
            if (_search == null && !(forTools && _searchFailed)) StartSearch(workspace, forTools);
            return workspace;
        }

        /// <summary>
        /// The search, its feeder and whatever the app hangs on them. A failure is logged by its
        /// type and leaves no half of it behind. For a note tool the feeder is built without its
        /// first reconcile, which <see cref="StartForNoteTools"/> runs itself: listing the notes
        /// can fail for a moment, and that must not count as a search that cannot start.
        /// </summary>
        private void StartSearch(PadWorkspace workspace, bool forTools)
        {
            NoteSearchService? service = null;
            SearchFeeder? feeder = null;
            try
            {
                service = _newSearch(workspace.Store);
                feeder = new SearchFeeder(workspace, service.Indexer, reconcile: !forTools);
                _searchStarted?.Invoke(service, feeder);
                _feeder = feeder;
                _search = service;
                _searchFailed = false;
            }
            catch (Exception ex)
            {
                _searchFailed = true;
                _warn("Search notes could not start (" + ex.GetType().Name + "); MicaPad opens without it and tries again next time.");
                feeder?.Dispose();
                service?.Dispose();
            }
        }
    }
}
