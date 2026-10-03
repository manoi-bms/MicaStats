using System;
using Kil0bitSystemMonitor.Services.Pad;
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
        public PadWorkspace EnsureStarted() => Start(repairWhileIndexing: true);

        /// <summary>
        /// For a note tool call that passed its permission check. True when there are notes to
        /// read: the workspace is built and restored (if this is the first start), the search is
        /// started, and once per run the index is brought up to date, so stored notes and notes
        /// opened from files are found.
        ///
        /// <para>
        /// False, with nothing built and nothing created on disk, when MicaPad was never used on
        /// this PC (it has no notes folder). Throws once exit has begun, and when the start fails:
        /// a tool then answers with an error result, never with an empty list that reads as "no
        /// such notes".
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

            if (_workspace == null && !NoteStore.ExistsAt(_root)) return false;

            bool hadFeeder = _feeder != null;
            Start(repairWhileIndexing: false);

            if (!_reconciledForNotes && _feeder is { } feeder)
            {
                _reconciledForNotes = true;
                // A feeder built just now has read every note already. One that MicaPad built
                // earlier has not seen what was opened from a file or stored since.
                if (hadFeeder)
                {
                    feeder.FlushPending();
                    feeder.ReconcileAll(readOnly: true);
                }
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

        private PadWorkspace Start(bool repairWhileIndexing)
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
                    workspace.Restore();
                }
                catch
                {
                    workspace.Dispose();
                    throw;
                }
                _workspace = workspace;
            }

            if (_search == null) StartSearch(workspace, repairWhileIndexing);
            return workspace;
        }

        /// <summary>The search, its feeder and whatever the app hangs on them. A failure is logged by its type and leaves no half of it behind.</summary>
        private void StartSearch(PadWorkspace workspace, bool repairWhileIndexing)
        {
            NoteSearchService? service = null;
            SearchFeeder? feeder = null;
            try
            {
                service = _newSearch(workspace.Store);
                feeder = new SearchFeeder(workspace, service.Indexer, readOnly: !repairWhileIndexing);
                _searchStarted?.Invoke(service, feeder);
                _feeder = feeder;
                _search = service;
            }
            catch (Exception ex)
            {
                _warn("Search notes could not start (" + ex.GetType().Name + "); MicaPad opens without it and tries again next time.");
                feeder?.Dispose();
                service?.Dispose();
            }
        }
    }
}
