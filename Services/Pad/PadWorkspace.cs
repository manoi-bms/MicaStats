using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Why opening a file did or did not produce a tab.</summary>
    public enum OpenFileStatus
    {
        /// <summary>The file was read and a tab holds it.</summary>
        Opened,
        /// <summary>A tab already holds this file; it was activated instead.</summary>
        AlreadyOpen,
        /// <summary>The file does not exist.</summary>
        NotFound,
        /// <summary>The file is over the size limit.</summary>
        TooLarge,
        /// <summary>The file is not text.</summary>
        Binary,
        /// <summary>The file could not be read (locked, no permission, bad path).</summary>
        Failed,
        /// <summary>Reloading would discard unsaved edits too large to keep as a version; nothing was changed.</summary>
        EditsWouldBeLost,
        /// <summary>
        /// The file has a note whose text cannot be read right now: a closed note with unsaved
        /// edits, or a note that could not be read at restore (see <see cref="PadWorkspace.Restore"/>).
        /// Opening the file fresh beside it would leave two notes for one file, so nothing was opened.
        /// </summary>
        ClosedNoteUnreadable,
    }

    /// <summary>Collaborators for <see cref="PadWorkspace"/>; tests replace every one of them.</summary>
    public sealed class PadWorkspaceOptions
    {
        /// <summary>The writer to share; a private one is created (and disposed) when null.</summary>
        public AutosaveWriter? Writer { get; init; }

        /// <summary>The time source; tests move it by hand.</summary>
        public Func<DateTime> UtcClock { get; init; } = () => DateTime.UtcNow;

        /// <summary>Where deleted notes go, so a delete can be undone from the Recycle Bin.</summary>
        public IRecycleBin RecycleBin { get; init; } = Kil0bitSystemMonitor.Services.Pad.RecycleBin.Instance;

        /// <summary>Runs an action on the UI thread. The app passes the dispatcher; tests run inline.</summary>
        public Action<Action> Post { get; init; } = action => action();

        /// <summary>Reports a recoverable problem to the diagnostics log.</summary>
        public Action<string> Warn { get; init; } = message => DiagnosticsLog.Warn("pad", message);

        /// <summary>Reports a failure, with its exception, to the diagnostics log.</summary>
        public Action<string, Exception> Error { get; init; } = (message, ex) => DiagnosticsLog.Error("pad", message, ex);

        /// <summary>Code page for files that are neither Unicode-marked nor valid UTF-8.</summary>
        public int AnsiCodePage { get; init; } = TextFileCodec.SystemAnsiCodePage;

        /// <summary>The credential vault MicaPad's windows use; null leaves the credential commands disabled. The app passes its one vault; tests pass their own.</summary>
        public CredentialVault? Vault { get; init; }
    }

    /// <summary>
    /// MicaPad's open notes and every rule about them: when text reaches disk, when a version is
    /// kept, what closing a tab means, what comes back after a restart.
    ///
    /// <para>
    /// Holds no WPF types, so all of it is tested directly. The window only reports changes
    /// (<see cref="NotifyChanged"/>), drives the clock (<see cref="Tick"/>), and points each note's
    /// <see cref="OpenNote.TextProvider"/> at its document. Everything here runs on the UI thread;
    /// disk writes go to the <see cref="AutosaveWriter"/> as closures over copies.
    /// </para>
    /// </summary>
    public sealed partial class PadWorkspace : IDisposable
    {
        /// <summary>Writer key of the session file.</summary>
        public const string SessionKey = "session";

        private readonly NoteStore _store;
        private readonly AutosaveWriter _writer;
        private readonly bool _ownsWriter;
        private readonly AutosaveScheduler _scheduler = new();
        private readonly Func<DateTime> _clock;
        private readonly IRecycleBin _recycleBin;
        private readonly Action<Action> _post;
        private readonly Action<string> _warn;
        private readonly Action<string, Exception> _error;
        private readonly int _ansiCodePage;
        private readonly Dictionary<string, OpenNote> _byId = new();
        private readonly Dictionary<string, NoteMeta> _recentlyClosed = new();

        /// <summary>
        /// Open notes whose text could not be read at <see cref="Restore"/>. Not shown, never
        /// written, but kept in the session so they come back once they can be read.
        /// </summary>
        private readonly List<string> _unreadable = new();

        /// <summary>When each write failure was last logged (<see cref="Environment.TickCount64"/>), per log key.</summary>
        private readonly ConcurrentDictionary<string, long> _failureLoggedAt = new();

        /// <summary>A stuck write retries every 10 s; the log hears about it at most this often.</summary>
        private const long FailureLogIntervalMs = 60_000;

        private bool _restored;

        /// <summary>A save handed to the writer: the meta copy, text (null: meta only) and store version it was queued with.</summary>
        private sealed record PendingSave(NoteMeta Meta, string? Text, long Version);

        /// <summary>
        /// The last queued save of every note, removed once that exact save lands. Read by
        /// <see cref="FlushAll"/> and <see cref="Reopen"/> when the writer has not caught up.
        /// </summary>
        private readonly ConcurrentDictionary<string, PendingSave> _unconfirmed = new();

        /// <summary>Creates a workspace over <paramref name="store"/>; call <see cref="Restore"/> to load the saved session.</summary>
        public PadWorkspace(NoteStore store, PadWorkspaceOptions? options = null)
        {
            options ??= new PadWorkspaceOptions();
            _store = store;
            _ownsWriter = options.Writer == null;
            _writer = options.Writer ?? new AutosaveWriter();
            _clock = options.UtcClock;
            _recycleBin = options.RecycleBin;
            _post = options.Post;
            _warn = options.Warn;
            _error = options.Error;
            _ansiCodePage = options.AnsiCodePage;
            Vault = options.Vault;
            Open.CollectionChanged += (s, e) => SyncTabs();   // first subscriber: every window's tab list is in step before a window's own handler runs
        }

        /// <summary>The store this workspace reads and writes.</summary>
        public NoteStore Store => _store;

        /// <summary>The credential vault, shared by every window and by Settings; null when the app gave none.</summary>
        public CredentialVault? Vault { get; }

        /// <summary>Whether a window already told the user the vault was moved aside: once per run.</summary>
        public bool VaultNoticeShown { get; set; }

        /// <summary>Whether a window already told the user about <see cref="NoteStore.LockedFolder"/>: once per run.</summary>
        public bool LockedNoticeShown { get; set; }

        /// <summary>Open notes in tab order. Changed on the UI thread only.</summary>
        public ObservableCollection<OpenNote> Open { get; } = new();

        /// <summary>Windows, placement, zoom and per-tab view state, saved with <see cref="SaveSession"/>. One window until <see cref="Restore"/> loads the saved ones.</summary>
        public SessionState Session { get; private set; } = SessionWindows.Normalize(new SessionState());

        /// <summary>
        /// Loads the saved session's notes into <see cref="Open"/>. Only the first call does
        /// anything, so a window recreated later finds the same notes without reading them again.
        /// A note whose text cannot be read (a locked <c>current.txt</c>, say) is skipped and its
        /// folder left untouched; it stays in the session, so it returns at the next launch.
        /// </summary>
        public IReadOnlyList<OpenNote> Restore()
        {
            if (_restored) return Open.ToList();
            _restored = true;

            Session = _store.LoadSession();   // always at least one window (SessionWindows.Normalize)

            // Notes opened before the restore (only tests do that) join the first window.
            _active.Clear();
            foreach (var note in Open)
            {
                note.IsActive = false;
                if (WindowStateOf(note.WindowId) == null) note.WindowId = Windows[0].Id;
            }
            SyncTabs();

            foreach (var window in Windows)
            {
                foreach (string id in window.NoteIds.ToList())
                {
                    if (_byId.ContainsKey(id) || _unreadable.Contains(id)) continue;
                    var meta = _store.LoadMeta(id);
                    if (meta == null)
                    {
                        _warn("The session lists note " + id + " but it could not be loaded");
                        continue;
                    }
                    if (meta.IsClosed) continue;
                    if (!TryLoadInitialText(meta, out string text))
                    {
                        // Opening it empty (or from an older snapshot) would let the next autosave
                        // overwrite the newer text that could not be read.
                        _warn("The text of note " + id + " could not be read; it was left as it is and will be tried again next time");
                        _unreadable.Add(id);
                        _unreadableWindow[id] = window.Id;
                        continue;
                    }
                    AddOpen(meta, text, Open.Count, window.Id);
                }
            }

            // A window none of whose notes came back is not reopened, unless it is the only one;
            // its unreadable notes are kept for the first window (UnreadableWindowOf).
            foreach (var empty in Windows.Where(w => TabsOf(w.Id).Count == 0).ToList())
            {
                if (Windows.Count == 1) break;
                Session.Windows!.Remove(empty);
                _tabs.Remove(empty.Id);
            }

            foreach (var window in Windows)
            {
                var tabs = TabsOf(window.Id);
                if (tabs.Count > 0) SetActive(tabs.FirstOrDefault(n => n.Id == window.ActiveNoteId) ?? tabs[tabs.Count - 1]);
            }
            _activation.Clear();
            _activation.AddRange(SessionWindows.ByLastActive(Windows));
            return Open.ToList();
        }

        /// <summary>
        /// Moves a tab to <paramref name="index"/> among its own window's tabs (clamped); other
        /// windows' tabs keep their places. The caller saves the session when the drag ends.
        /// </summary>
        public void MoveTab(OpenNote note, int index)
        {
            var tabs = TabsOf(note.WindowId);
            int from = tabs.IndexOf(note);
            if (from < 0) return;
            int to = Math.Clamp(index, 0, tabs.Count - 1);
            if (to == from) return;
            // Moving onto the Open position of the tab now at "to" lands it before that tab when
            // moving left and after it when moving right: exactly "to" among this window's tabs.
            Open.Move(Open.IndexOf(note), Open.IndexOf(tabs[to]));
        }

        /// <summary>A new empty scratch note in the most recently active window.</summary>
        public OpenNote NewNote() => NewNote(MostRecentWindowId);

        /// <summary>A new empty scratch note, opened after the window's active tab and made active there.</summary>
        public OpenNote NewNote(string windowId)
        {
            if (WindowStateOf(windowId) == null) windowId = MostRecentWindowId;
            int number = Open
                .Where(n => !n.Meta.IsFileBacked)
                .Select(n => n.Meta.UntitledNumber)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var note = AddOpen(NoteStore.NewMeta(_clock(), number, null), "", InsertIndexAfterActive(windowId), windowId);
            SetActive(note);
            EnqueueSave(note);
            SaveSession();
            return note;
        }

        /// <summary>Makes <paramref name="note"/> its window's shown tab and remembers it for the next launch.</summary>
        public void SetActive(OpenNote note)
        {
            if (_active.TryGetValue(note.WindowId, out var previous) && !ReferenceEquals(previous, note)) previous.IsActive = false;
            _active[note.WindowId] = note;
            note.IsActive = true;
            if (WindowStateOf(note.WindowId) is { } state) state.ActiveNoteId = note.Id;
        }

        /// <summary>Remembers where the user was in a tab, for the next switch back or the next launch.</summary>
        public void SetTabViewState(OpenNote note, int caretOffset, double verticalOffset)
        {
            Session.Tabs.TryGetValue(note.Id, out var previous);
            Session.Tabs[note.Id] = new TabViewState
            {
                CaretOffset = caretOffset,
                VerticalOffset = verticalOffset,
                Bookmarks = previous?.Bookmarks,
            };
        }

        /// <summary>Records a tab's bookmarked lines (1-based) for the session; none clears them.</summary>
        public void SetBookmarks(OpenNote note, IReadOnlyList<int> lines)
        {
            if (!Session.Tabs.TryGetValue(note.Id, out var view))
                Session.Tabs[note.Id] = view = new TabViewState();
            view.Bookmarks = lines.Count == 0 ? null : lines.ToList();
        }

        /// <summary>
        /// The note's text changed. Cheap: it only records the time. Pass
        /// <paramref name="markUnsaved"/> false when the change is a reload from the file itself.
        /// </summary>
        public void NotifyChanged(OpenNote note, bool markUnsaved = true)
        {
            if (!_byId.ContainsKey(note.Id)) return;

            DateTime now = _clock();
            note.LastEditUtc = now;
            note.ChangedSinceSnapshot = true;
            note.EverHadText = true;
            if (markUnsaved && note.Meta.IsFileBacked) note.HasUnsavedEdits = true;
            _scheduler.MarkChanged(note.Id, now);
        }

        /// <summary>True while the note has changes not yet handed to the writer.</summary>
        public bool HasPendingChanges(OpenNote note) => _scheduler.IsPending(note.Id);

        /// <summary>Hands due notes to the writer and takes pause snapshots. The window calls this four times a second.</summary>
        public void Tick()
        {
            DateTime now = _clock();
            foreach (string id in _scheduler.TakeDue(now))
                if (_byId.TryGetValue(id, out var note)) EnqueueSave(note);

            foreach (var note in Open)
                if (note.ChangedSinceSnapshot && HistoryPolicy.IsDueOnPause(now, note.LastEditUtc, note.SnapshotClockUtc))
                    SnapshotNow(note, SnapshotReason.Pause);
        }

        /// <summary>Hands every pending change to the writer now: on deactivation and tab switch.</summary>
        public void FlushPending()
        {
            foreach (string id in _scheduler.TakeAll())
                if (_byId.TryGetValue(id, out var note)) EnqueueSave(note);
        }

        /// <summary>Waits for the writer, for readers that need the files current (the history list).</summary>
        public bool FlushWrites(TimeSpan timeout) => _writer.FlushAll(timeout);

        /// <summary>
        /// Everything to disk, for exit and session end: snapshots of changed notes, pending saves,
        /// the session. Waits up to <paramref name="timeout"/>; whatever is still unwritten then is
        /// written directly on this thread. Never throws for an I/O failure and never asks anything.
        /// </summary>
        /// <returns>True when the writer finished in time.</returns>
        public bool FlushAll(TimeSpan timeout)
        {
            var dirty = new List<OpenNote>();
            foreach (string id in _scheduler.TakeAll())
                if (_byId.TryGetValue(id, out var note)) dirty.Add(note);

            foreach (var note in Open)
            {
                if (note.ChangedSinceSnapshot)
                {
                    SnapshotNow(note, SnapshotReason.Exiting);
                    if (!dirty.Contains(note)) dirty.Add(note);
                }
                if (note.SaveState == SaveState.Failed && !dirty.Contains(note)) dirty.Add(note);
            }

            foreach (var note in dirty) EnqueueSave(note);
            SaveSession();

            if (_writer.FlushAll(timeout)) return true;

            _warn("Autosave did not finish in time; writing what is left directly");
            // Every save still queued, including those of notes closed this session. Each is
            // written under the version it was queued with, so if the queued write lands later
            // the store skips it as not newer.
            foreach (var entry in _unconfirmed.ToArray())
            {
                try
                {
                    _store.SaveNote(entry.Value.Meta, entry.Value.Text, entry.Value.Version);
                    _unconfirmed.TryRemove(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _error("Writing note " + entry.Key + " directly failed", ex);
                }
            }

            try
            {
                _store.SaveSession(PrepareSession());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error("Writing the session directly failed", ex);
            }
            return false;
        }

        /// <summary>
        /// Keeps a version of the note's current text, unless it equals the newest version, is
        /// empty and has never been snapshotted, or is over <see cref="HistoryPolicy.MaxSnapshotChars"/>.
        /// </summary>
        /// <returns>True when a version was queued.</returns>
        public bool SnapshotNow(OpenNote note, SnapshotReason reason)
        {
            note.ChangedSinceSnapshot = false;
            string text = note.TextProvider();
            if (text.Length > HistoryPolicy.MaxSnapshotChars) return false;
            if (text.Length == 0 && note.Meta.LastSnapshotHash == null) return false;

            string hash = HistoryPolicy.Hash(text);
            if (hash == note.Meta.LastSnapshotHash) return false;

            DateTime now = _clock();
            note.Meta.LastSnapshotHash = hash;
            note.Meta.LastSnapshotUtc = now;
            note.SnapshotClockUtc = now;

            EnqueueSnapshot(note.Id, text, now);
            EnqueueSave(note);   // persists LastSnapshotHash
            return true;
        }

        /// <summary>
        /// Closes a tab without asking anything. The note is snapshotted and kept as a closed note;
        /// a scratch note that never held text is deleted instead. Its window's active tab falls to
        /// the neighbour in that window.
        /// </summary>
        public void Close(OpenNote note)
        {
            if (!_byId.ContainsKey(note.Id)) return;

            string windowId = note.WindowId;
            int index = TabsOf(windowId).IndexOf(note);
            _scheduler.Forget(note.Id);

            if (!note.Meta.IsFileBacked && !note.EverHadText)
            {
                string id = note.Id;
                // Same key as its saves, so it replaces any still waiting and runs after one in progress.
                _unconfirmed.TryRemove(id, out _);   // being deleted, not saved
                _writer.Enqueue(id, Logged(id + "#delete", "Removing empty note " + id + " failed; it will be retried",
                    () => _store.DeleteEmptyNote(id)));
            }
            else
            {
                SnapshotNow(note, SnapshotReason.Closing);
                note.Meta.ClosedAtUtc = _clock();
                EnqueueSave(note);
                _recentlyClosed[note.Id] = note.Meta.Clone();
            }

            _byId.Remove(note.Id);
            Open.Remove(note);
            Session.Tabs.Remove(note.Id);
            note.IsActive = false;

            if (ReferenceEquals(ActiveIn(windowId), note))
            {
                _active.Remove(windowId);
                var tabs = TabsOf(windowId);
                if (tabs.Count > 0) SetActive(tabs[Math.Min(index, tabs.Count - 1)]);
                else if (WindowStateOf(windowId) is { } state) state.ActiveNoteId = null;
            }
            SaveSession();
        }

        /// <summary>
        /// Closed notes, most recently closed first. Notes closed in this session are taken from
        /// memory, because their close may still be in the writer queue.
        /// </summary>
        public IReadOnlyList<NoteMeta> ClosedNotes()
        {
            var byId = new Dictionary<string, NoteMeta>();
            foreach (var meta in _store.LoadAllMetas())
                if (meta.IsClosed) byId[meta.Id] = meta;
            foreach (var pair in _recentlyClosed) byId[pair.Key] = pair.Value;

            return byId.Values
                .Where(m => !_byId.ContainsKey(m.Id))
                .OrderByDescending(m => m.ClosedAtUtc)
                .ToList();
        }

        /// <summary>Ctrl+Shift+T, into the most recently active window.</summary>
        public OpenNote? ReopenLastClosed() => ReopenLastClosed(MostRecentWindowId);

        /// <summary>Ctrl+Shift+T in a window: the last closed note comes back there.</summary>
        public OpenNote? ReopenLastClosed(string windowId)
        {
            var last = ClosedNotes().FirstOrDefault();
            return last == null ? null : Reopen(last.Id, windowId);
        }

        /// <summary>Reopens a closed note in the most recently active window; see <see cref="Reopen(string, string)"/>.</summary>
        public OpenNote? Reopen(string id) => Reopen(id, MostRecentWindowId);

        /// <summary>
        /// Reopens a closed note after the active tab of <paramref name="windowId"/>; an open one is
        /// just activated, in the window that shows it. Null when the note cannot be loaded,
        /// including when its text exists but cannot be read right now: then nothing is written and
        /// the note stays in the closed list. A file note whose file another tab already holds comes
        /// back as a note of its own.
        /// </summary>
        public OpenNote? Reopen(string id, string windowId)
        {
            if (_byId.TryGetValue(id, out var open))
            {
                SetActive(open);
                return open;
            }
            if (WindowStateOf(windowId) == null) windowId = MostRecentWindowId;

            // Its close may still be queued; normally this lets it land first.
            _writer.FlushAll(TimeSpan.FromSeconds(2));

            NoteMeta? meta;
            string text;
            if (_unconfirmed.TryGetValue(id, out var pending))
            {
                // The writer has not caught up: the newest state is the one still queued, not the
                // one on disk. Reopening from disk here would bring back older text.
                meta = pending.Meta.Clone();
                if (pending.Text != null) text = pending.Text;
                else if (!TryLoadInitialText(meta, out text)) return UnreadableOnReopen(id);
            }
            else
            {
                meta = _store.LoadMeta(id);
                if (meta == null) return null;
                // Reopening writes the text straight back, so text that cannot be read must stop it here.
                if (!TryLoadInitialText(meta, out text)) return UnreadableOnReopen(id);
            }

            if (meta.IsFileBacked && Open.Any(n => SamePath(n.Meta.SourcePath, meta.SourcePath!)))
            {
                // Another tab holds this file now (a Save As onto it while this note was closed).
                // Two tabs saving one file would overwrite each other, so this note comes back on
                // its own, under the file's name, keeping its text and history.
                meta.TitleIsCustom = true;
                meta.SourcePath = null;
                meta.SourceStamp = null;
                meta.HasUnsavedEdits = false;
            }

            _recentlyClosed.Remove(id);
            meta.ClosedAtUtc = null;

            // Written now, not queued: the daily purge re-reads closedAt on disk under the note lock,
            // so a reopened note must stop looking closed at once.
            try
            {
                bool writeText = !meta.IsFileBacked || meta.HasUnsavedEdits;
                _store.SaveNote(meta.Clone(), writeText ? text : null, _store.NextVersion());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error("Reopening note " + id + " could not be written yet; autosave will retry", ex);
            }

            var note = AddOpen(meta, text, InsertIndexAfterActive(windowId), windowId);
            SetActive(note);
            EnqueueSave(note);
            SaveSession();
            return note;
        }

        /// <summary>
        /// Sends a closed note to the Recycle Bin. False when it could not; the note is then kept.
        /// Also false while the writer is behind: recycling a note with writes still queued would
        /// let a late snapshot recreate its folder as an orphan.
        /// </summary>
        public bool DeleteClosed(string id)
        {
            if (_byId.ContainsKey(id)) return false;
            if (!_writer.FlushAll(TimeSpan.FromSeconds(2))) return false;
            _recentlyClosed.Remove(id);
            return _store.DeleteNote(id, _recycleBin);
        }

        /// <summary>Sets a custom tab title; a blank one returns to the automatic title.</summary>
        public void Rename(OpenNote note, string? title)
        {
            string trimmed = (title ?? "").Trim();
            if (trimmed.Length == 0)
            {
                note.Meta.TitleIsCustom = false;
                note.Title = note.Meta.IsFileBacked
                    ? NoteTitle.ForFile(note.Meta.SourcePath!)
                    : NoteTitle.FromText(note.TextProvider(), note.Meta.UntitledNumber);
            }
            else
            {
                note.Meta.TitleIsCustom = true;
                note.Title = trimmed;
            }
            EnqueueSave(note);
        }

        /// <summary>
        /// Sets the language a note is shown in; null or an unknown id returns it to Auto. Saved with
        /// the note like a rename: it is not an edit of a file.
        /// </summary>
        public void SetLanguage(OpenNote note, string? languageId)
        {
            note.Meta.Language = PadLanguages.ById(languageId)?.Id;
            EnqueueSave(note);
        }

        /// <summary>The note's versions, newest first.</summary>
        public IReadOnlyList<SnapshotInfo> History(OpenNote note) => _store.ListSnapshots(note.Id);

        /// <summary>The text of one version, or null when it can no longer be read.</summary>
        public string? ReadSnapshot(SnapshotInfo snapshot) => _store.ReadSnapshot(snapshot);

        /// <summary>Queues a write of the session, serialized now on the UI thread.</summary>
        public void SaveSession()
        {
            string json = NoteStore.SerializeSession(PrepareSession());
            _writer.Enqueue(SessionKey, Logged(SessionKey, "Writing the MicaPad session failed; it will be retried",
                () => _store.WriteSessionJson(json)));
        }

        /// <summary>Releases the writer when this workspace created it; a shared one is left to its owner.</summary>
        public void Dispose()
        {
            if (_ownsWriter) _writer.Dispose();
        }

        private SessionState PrepareSession()
        {
            foreach (var window in Windows)
            {
                window.NoteIds = TabsOf(window.Id).Select(n => n.Id)
                    // Notes that could not be read at restore go last in their window, so they return at the next launch.
                    .Concat(_unreadable.Where(id => !_byId.ContainsKey(id) && UnreadableWindowOf(id) == window.Id))
                    .ToList();
                window.ActiveNoteId = ActiveIn(window.Id)?.Id;
            }
            SessionWindows.WriteMirror(Session);      // the old fields, for older MicaPads
            foreach (string stale in Session.Tabs.Keys.Where(k => !_byId.ContainsKey(k)).ToList())
                Session.Tabs.Remove(stale);
            return Session;
        }

        private OpenNote? UnreadableOnReopen(string id)
        {
            _warn("Note " + id + " was not reopened: its text could not be read, so it was left as it is");
            return null;
        }

        /// <summary>
        /// Queues a version of <paramref name="text"/> under a unique key: snapshots must never
        /// replace one another in the writer queue.
        /// </summary>
        private void EnqueueSnapshot(string id, string text, DateTime utcNow)
        {
            DateTime local = utcNow.ToLocalTime();
            string key = id + "#snapshot#" + _store.NextVersion().ToString(CultureInfo.InvariantCulture);
            _writer.Enqueue(key, Logged(id + "#snapshot", "Keeping a version of note " + id + " failed; it will be retried",
                () => _store.WriteSnapshot(id, text, local)));
        }

        /// <summary>
        /// Wraps writer work so a failure is logged (throttled by <see cref="LogWriteFailure"/>)
        /// and then rethrown, which is what makes the writer retry it.
        /// </summary>
        private Action Logged(string logKey, string message, Action work) => () =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                LogWriteFailure(logKey, message, ex);
                throw;
            }
        };

        /// <summary>
        /// Reports a failed write at most once per <paramref name="key"/> every
        /// <see cref="FailureLogIntervalMs"/>: a write that stays stuck is retried every 10 s, and
        /// the log should say so without filling up. Runs on the writer thread. Uses
        /// <see cref="Environment.TickCount64"/>, so a clock change cannot silence it.
        /// </summary>
        private void LogWriteFailure(string key, string message, Exception ex)
        {
            long now = Environment.TickCount64;
            if (_failureLoggedAt.TryGetValue(key, out long last) && now - last < FailureLogIntervalMs) return;
            _failureLoggedAt[key] = now;
            _error(message, ex);
        }

        private OpenNote AddOpen(NoteMeta meta, string text, int index, string windowId)
        {
            var note = new OpenNote(meta, text)
            {
                SnapshotClockUtc = _clock(),
                EverHadText = text.Length > 0 || meta.LastSnapshotHash != null,
                WindowId = windowId,                  // before the insert: the tab lists sync on it
            };
            _byId[meta.Id] = note;
            Open.Insert(Math.Clamp(index, 0, Open.Count), note);
            return note;
        }

        /// <summary>
        /// Queues a save of the note's meta, and of its text when the text is authoritative (always
        /// for a scratch note; for a file-backed one only while it has unsaved edits).
        /// </summary>
        private void EnqueueSave(OpenNote note)
        {
            _scheduler.Forget(note.Id);

            var meta = note.Meta;
            bool writeText = !meta.IsFileBacked || meta.HasUnsavedEdits;
            string? text = writeText ? note.TextProvider() : null;
            if (text != null && !meta.IsFileBacked && !meta.TitleIsCustom)
                note.Title = NoteTitle.FromText(text, meta.UntitledNumber);
            meta.ModifiedUtc = _clock();

            NoteMeta copy = meta.Clone();
            long version = _store.NextVersion();
            note.SaveSequence = version;
            note.SaveState = SaveState.Saving;

            string id = note.Id;
            var pending = new PendingSave(copy, text, version);
            _unconfirmed[id] = pending;
            _writer.Enqueue(id, () =>
            {
                try
                {
                    _store.SaveNote(copy, text, version);
                    _unconfirmed.TryRemove(new KeyValuePair<string, PendingSave>(id, pending));
                }
                catch (Exception ex)
                {
                    LogWriteFailure(id, "Autosave of note " + id + " failed; it will be retried", ex);
                    _post(() => note.SaveState = SaveState.Failed);
                    throw;
                }
                _post(() => OnSaved(note, version));
            });
        }

        private void OnSaved(OpenNote note, long version)
        {
            note.LastSavedUtc = _clock();
            if (version == note.SaveSequence) note.SaveState = SaveState.Saved;
        }

        /// <summary>
        /// A note's text on restore or reopen: a file-backed note with no unsaved edits is read
        /// fresh from its file; every other note (and a file that cannot be read) from the store.
        /// A clean file note read from the store is marked <see cref="SourceStamp.Unverified"/>.
        /// False when the store holds text that cannot be read right now; the note must then not
        /// be opened, or its next save would overwrite that text.
        /// </summary>
        private bool TryLoadInitialText(NoteMeta meta, out string text)
        {
            if (meta.IsFileBacked && !meta.HasUnsavedEdits)
            {
                // The stamp first: if the file changes while it is read, the next check sees a change.
                var stamp = SourceStamp.Read(meta.SourcePath!);
                var decoded = ReadSource(meta.SourcePath!, out _);
                if (decoded != null)
                {
                    meta.Encoding = decoded.Encoding;
                    meta.CodePage = decoded.CodePage;
                    meta.LineEnding = decoded.LineEnding;
                    meta.SourceStamp = stamp;
                    text = decoded.Text;
                    return true;
                }

                // MicaPad's copy stands in for a file it cannot read now (an offline share, a lock).
                // That copy may be older than the file, or empty, so it must never pass for it.
                meta.SourceStamp = SourceStamp.Unverified;
            }

            if (!_store.TryLoadText(meta.Id, out string? stored))
            {
                text = "";
                return false;
            }
            text = stored ?? "";
            return true;
        }

        /// <summary>Reads and decodes a user's file, sharing it with any program writing to it (a log, say).</summary>
        private DecodedText? ReadSource(string path, out OpenFileStatus status)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    status = OpenFileStatus.NotFound;
                    return null;
                }
                if (info.Length > TextFileCodec.MaxFileBytes)
                {
                    status = OpenFileStatus.TooLarge;
                    return null;
                }

                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }

                if (!TextFileCodec.TryDecode(bytes, _ansiCodePage, out var decoded, out var failure))
                {
                    status = failure == DecodeFailure.TooLarge ? OpenFileStatus.TooLarge : OpenFileStatus.Binary;
                    return null;
                }

                status = OpenFileStatus.Opened;
                return decoded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _warn("Could not read " + path + ": " + ex.Message);
                status = OpenFileStatus.Failed;
                return null;
            }
        }
    }
}
