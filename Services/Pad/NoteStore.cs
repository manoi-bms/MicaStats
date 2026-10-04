using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// MicaPad's notes on disk: one folder per note, each file encrypted with the notes key
    /// (<see cref="StoreCipher"/>, <see cref="NotesKey"/>); files an earlier version wrote plain are read as before
    /// until <see cref="EncryptPlainFiles"/> encrypts them.
    ///
    /// <code>
    /// Root\session.json
    /// Root\notes\{id}\meta.json
    /// Root\notes\{id}\current.txt
    /// Root\notes\{id}\history\yyyyMMdd-HHmmss-fff.txt
    /// </code>
    ///
    /// <para>
    /// Called from the UI thread and from the autosave writer thread at once, so every operation on
    /// one note holds that note's lock. Saves carry a version number and an older one is ignored:
    /// a background save that finishes late can never overwrite a newer synchronous one.
    /// </para>
    /// </summary>
    public sealed partial class NoteStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly ConcurrentDictionary<string, object> _locks = new();
        private readonly ConcurrentDictionary<string, long> _written = new();
        private readonly object _sessionLock = new();
        private readonly Action<string> _warn;
        private long _version;

        /// <summary>Initializes a note store rooted at the given folder.</summary>
        /// <param name="root">The MicaPad folder. Created if missing.</param>
        /// <param name="warn">Where recoverable problems are reported; the diagnostics log by default.</param>
        /// <exception cref="IOException"><c>key.bin</c>, <c>key.bak</c> or a store file cannot be read right now, or an unusable store cannot be moved aside; try again later.</exception>
        public NoteStore(string root, Action<string>? warn = null)
        {
            Root = root;
            _warn = warn ?? (message => DiagnosticsLog.Warn("pad", message));
            Directory.CreateDirectory(NotesDir);
            OpenKey();
        }

        /// <summary><c>%APPDATA%\MicaStats\MicaPad</c>, beside <c>config.json</c>.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicaStats", "MicaPad");

        /// <summary>The root folder where all notes are stored.</summary>
        public string Root { get; }

        private const string NotesFolder = "notes";

        /// <summary>The <c>notes</c> subfolder containing all note folders.</summary>
        public string NotesDir => Path.Combine(Root, NotesFolder);

        private const string SessionFile = "session.json";

        /// <summary>Path to <c>session.json</c>, the window and tab state.</summary>
        public string SessionPath => Path.Combine(Root, SessionFile);

        /// <summary>The folder for a specific note.</summary>
        public string NoteDir(string id) => Path.Combine(NotesDir, id);

        /// <summary>Path to a note's <c>meta.json</c>.</summary>
        public string MetaPath(string id) => Path.Combine(NoteDir(id), "meta.json");

        /// <summary>Path to a note's <c>current.txt</c>, its working text.</summary>
        public string CurrentPath(string id) => Path.Combine(NoteDir(id), "current.txt");

        /// <summary>The <c>history</c> subfolder for a note's snapshots.</summary>
        public string HistoryDir(string id) => Path.Combine(NoteDir(id), "history");

        /// <summary>A new note's metadata. Nothing is written until the first <see cref="SaveNote"/>.</summary>
        public static NoteMeta NewMeta(DateTime utcNow, int untitledNumber, string? sourcePath) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = sourcePath != null ? NoteTitle.ForFile(sourcePath) : NoteTitle.Untitled(untitledNumber),
            UntitledNumber = untitledNumber,
            SourcePath = sourcePath,
            CreatedUtc = utcNow,
            ModifiedUtc = utcNow,
        };

        /// <summary>
        /// Whether <paramref name="id"/> is exactly what <see cref="NewMeta"/> gives a note: the 32
        /// digits of a GUID, <c>0-9</c> and <c>a-f</c>. An id is a folder name, and Windows finds a
        /// folder under another letter case, opens a device for <c>CON</c> or <c>NUL</c>, and walks
        /// through a separator. An id that comes from outside the app (the note tools) must pass
        /// this before it is made into a path.
        /// </summary>
        public static bool IsNoteId(string? id)
        {
            if (id == null || id.Length != 32) return false;
            foreach (char c in id)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
            return true;
        }

        /// <summary>
        /// The note's metadata for a reader that must never change the store (the note tools).
        /// Unlike <see cref="LoadMeta"/> it writes nothing: a missing or damaged <c>meta.json</c>
        /// is not rebuilt, a finished write is read where it lies, and no folder is made. Null when
        /// there is no such note: an id <see cref="IsNoteId"/> refuses, no <c>meta.json</c>, one
        /// that does not decode, or one that names another id. A file that is there but cannot be
        /// read right now throws, as <see cref="ReadStoreText"/> does. Read under the note's lock,
        /// so a save in progress is waited for; a note with no folder gets no lock.
        /// </summary>
        public NoteMeta? PeekMeta(string id)
        {
            if (!IsNoteId(id)) return null;
            try
            {
                if (!Directory.Exists(NoteDir(id))) return null;
                string? json;
                lock (LockFor(id))
                {
                    json = ReadStoreTextInPlace(MetaPath(id));
                }
                if (json == null) return null;
                var meta = JsonSerializer.Deserialize<NoteMeta>(json, Json);
                return meta != null && string.Equals(meta.Id, id, StringComparison.Ordinal) ? meta : null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;   // the note was deleted meanwhile
            }
        }

        /// <summary>
        /// Every stored note <see cref="PeekMeta"/> can read, for a reader that must never change
        /// the store: <see cref="LoadAllMetas"/> without its repairs. Nothing is rebuilt. A folder
        /// whose name is not a note id, or whose <c>meta.json</c> is missing or damaged, is left
        /// out: for this reader it is no note.
        ///
        /// <para>
        /// What cannot be read right now is never passed off as absent. A note whose
        /// <c>meta.json</c> is there but locked is named in <paramref name="unknown"/>: whether it
        /// is still a note cannot be said, so a caller that drops what is not listed (the search
        /// index) must keep what it has for it. And when the notes cannot be listed at all (the
        /// folder is held by another program, or is not there to ask), this throws: a partial or
        /// empty list would read as "the others are gone".
        /// </para>
        /// </summary>
        /// <param name="unknown">The ids of the notes whose <c>meta.json</c> could not be read right now.</param>
        /// <exception cref="IOException">The notes folder cannot be listed right now.</exception>
        /// <exception cref="UnauthorizedAccessException">The notes folder may not be listed.</exception>
        public IReadOnlyList<NoteMeta> PeekAllMetas(out IReadOnlyList<string> unknown)
        {
            var metas = new List<NoteMeta>();
            var unread = new List<string>();
            unknown = unread;

            // Listed to its end before any note is read: a listing that fails, half-way too, throws here.
            foreach (string folder in Directory.GetDirectories(NotesDir))
            {
                string id = Path.GetFileName(folder);
                try
                {
                    if (PeekMeta(id) is { } meta) metas.Add(meta);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unread.Add(id);   // this note cannot be read right now; the others still can
                }
            }
            return metas;
        }

        /// <summary>
        /// Whether a store was ever made under <paramref name="root"/>, asked without making one:
        /// the constructor creates the folder and the notes key.
        /// </summary>
        public static bool ExistsAt(string root) => Directory.Exists(Path.Combine(root, NotesFolder));

        /// <summary>
        /// Whether the store under <paramref name="root"/> holds any note folder, asked without
        /// making a store. Throws when the notes folder cannot be listed right now.
        /// </summary>
        public static bool HasNotesAt(string root)
        {
            string notes = Path.Combine(root, NotesFolder);
            return Directory.Exists(notes) && Directory.EnumerateDirectories(notes).Any();
        }

        /// <summary>
        /// The session saved under <paramref name="root"/>, for a start that must not change the
        /// store (a note tool's): <see cref="LoadSession"/> without anything it writes and without
        /// its rebuild. Asked without making a store. <c>session.json</c> is read where it lies, a
        /// finished write beside it first and never swapped in; the key is read the same way
        /// (<see cref="NotesKey.Peek"/>). The session comes back checked as
        /// <see cref="LoadSession"/> checks it.
        ///
        /// <para>
        /// Null when the saved session does not load as it is: there is none, it cannot be read
        /// right now (a lock), it does not decrypt or the key cannot be used, or it holds no
        /// session. <see cref="LoadSession"/> would rebuild one from the notes then, reading and
        /// repairing every note's record on the way; a caller of this must start nothing instead.
        /// </para>
        /// </summary>
        public static SessionState? PeekSessionAt(string root)
        {
            string path = Path.Combine(root, SessionFile), ready = path + AtomicFile.ReadySuffix;
            try
            {
                byte[]? bytes = ReadIfExists(ready) ?? ReadIfExists(path);
                if (bytes == null) return null;
                bytes = NewerThanPlain(bytes, () => ReadIfExists(ready), () => ReadIfExists(path));

                string json;
                if (!StoreCipher.IsEncrypted(bytes))
                {
                    json = ReadPlain(bytes);   // written by a version before the store was encrypted
                }
                else
                {
                    byte[]? key = NotesKey.Peek(root);
                    if (key == null) return null;
                    try
                    {
                        if (!new StoreCipher(key).TryDecrypt(bytes, out byte[] plain)) return null;
                        json = Utf8NoBom.GetString(plain);
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                    }
                }

                var session = JsonSerializer.Deserialize<SessionState>(json, Json);
                return session == null ? null : Checked(session, Path.Combine(root, NotesFolder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>A session as it was read, made usable: notes whose folder is gone are dropped, and there is at least one window.</summary>
        private static SessionState Checked(SessionState session, string notesDir)
        {
            bool Exists(string id) => Directory.Exists(Path.Combine(notesDir, id));
            session.OpenNoteIds = (session.OpenNoteIds ?? new List<string>()).Where(Exists).Distinct().ToList();
            session.Tabs ??= new Dictionary<string, TabViewState>();
            return SessionWindows.Normalize(session, Exists);
        }

        /// <summary>
        /// The note's text for a reader that must never change the store: <see cref="TryLoadText"/>
        /// without committing a finished write. <c>current.txt</c> is read where it lies, then the
        /// newest snapshot. True with the text, or with null when the note has none (or the id is
        /// not one <see cref="IsNoteId"/> accepts). False when the text is there but cannot be read
        /// right now.
        /// </summary>
        public bool TryPeekText(string id, out string? text)
        {
            text = null;
            if (!IsNoteId(id)) return true;
            return TryReadTextInPlace(id, out text);
        }

        /// <summary>
        /// The note's text as <see cref="LoadText"/> finds it, read where it lies: no finished
        /// write is committed. It is read under the note's lock, so a save in progress is waited
        /// for; a note with no folder gets no lock. For the search index, which reads every stored
        /// note on its own thread and must not change one. Null when there is no text or it cannot
        /// be read right now. Unlike <see cref="TryPeekText"/> it takes any folder name the store
        /// itself listed, so it is not for an id that comes from outside the app.
        /// </summary>
        internal string? LoadTextInPlace(string id) => TryReadTextInPlace(id, out string? text) ? text : null;

        /// <summary>
        /// Reads only: <c>current.txt</c> or a finished write waiting beside it, else the newest
        /// version. Under the note's lock, so a save in progress is waited for rather than met
        /// half-way; a note with no folder gets no lock (asking for ids that do not exist leaves
        /// nothing behind, in memory either).
        /// </summary>
        private bool TryReadTextInPlace(string id, out string? text)
        {
            text = null;
            try
            {
                if (!Directory.Exists(NoteDir(id))) return true;
                lock (LockFor(id))
                {
                    text = ReadStoreTextInPlace(CurrentPath(id));
                    if (text != null) return true;

                    var newest = ListSnapshots(id).FirstOrDefault();
                    if (newest == null) return true;
                    text = ReadStoreTextInPlace(newest.FilePath);
                    return text != null;
                }
            }
            catch (DirectoryNotFoundException)
            {
                text = null;
                return true;   // the note was deleted meanwhile: no text anywhere
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text = null;
                return false;
            }
        }

        /// <summary>
        /// A save version, increasing across every note this store has seen. Issued by the store
        /// rather than per open tab, so a note closed and reopened keeps getting newer versions
        /// than the ones it was saved with before.
        /// </summary>
        public long NextVersion() => System.Threading.Interlocked.Increment(ref _version);

        /// <summary>
        /// Writes the text (when given) and then the meta. Returns false, writing nothing, when a
        /// save with an equal or higher <paramref name="version"/> has already been written.
        /// </summary>
        public bool SaveNote(NoteMeta meta, string? text, long version)
        {
            lock (LockFor(meta.Id))
            {
                if (_written.TryGetValue(meta.Id, out long done) && version <= done) return false;

                Directory.CreateDirectory(NoteDir(meta.Id));
                if (text != null) WriteData(CurrentPath(meta.Id), Utf8NoBom.GetBytes(text));
                WriteData(MetaPath(meta.Id), JsonSerializer.SerializeToUtf8Bytes(meta, Json));
                _written[meta.Id] = version;
                return true;
            }
        }

        /// <summary>
        /// The note's text, most trusted source first: a completed write not yet swapped in,
        /// <c>current.txt</c>, then the newest snapshot.
        ///
        /// <para>
        /// True with the text, or with null when the note has no text anywhere (no
        /// <c>current.txt</c>, no completed write, no snapshot). False when the text exists but
        /// cannot be read right now (locked, no permission): the caller must then leave the note
        /// alone. The snapshot is used only when <c>current.txt</c> truly does not exist, because a
        /// snapshot may be older; standing it in for a file that is merely locked would let the
        /// next save overwrite the newer text with it.
        /// </para>
        /// </summary>
        public bool TryLoadText(string id, out string? text)
        {
            text = null;
            lock (LockFor(id))
            {
                try
                {
                    text = ReadStoreText(CurrentPath(id));
                    if (text != null) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("Could not read the text of note " + id + ": " + ex.Message);
                    return false;
                }

                try
                {
                    var newest = ListSnapshots(id).FirstOrDefault();
                    if (newest == null) return true;
                    text = ReadSnapshot(newest);
                    return text != null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("Could not list the versions of note " + id + ": " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// The note's text as <see cref="TryLoadText"/> finds it, or null when there is none or it
        /// cannot be read. For readers only: a caller that may write the text back must use
        /// <see cref="TryLoadText"/>, which tells an unreadable note from an empty one.
        /// </summary>
        public string? LoadText(string id) => TryLoadText(id, out string? text) ? text : null;

        /// <summary>
        /// The note's metadata, rebuilt from its text when <c>meta.json</c> is missing or corrupt.
        /// Null when the folder holds neither.
        /// </summary>
        public NoteMeta? LoadMeta(string id)
        {
            lock (LockFor(id))
            {
                if (!Directory.Exists(NoteDir(id))) return null;

                try
                {
                    string? json = ReadStoreText(MetaPath(id));
                    if (json != null)
                    {
                        var meta = JsonSerializer.Deserialize<NoteMeta>(json, Json);
                        if (meta != null && meta.Id == id) return meta;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _warn("meta.json of note " + id + " is unreadable; rebuilding it: " + ex.Message);
                }

                return RebuildMeta(id);
            }
        }

        /// <summary>Every note on disk, open or closed.</summary>
        public IReadOnlyList<NoteMeta> LoadAllMetas()
        {
            var metas = new List<NoteMeta>();
            if (!Directory.Exists(NotesDir)) return metas;

            foreach (string folder in Directory.EnumerateDirectories(NotesDir))
            {
                var meta = LoadMeta(Path.GetFileName(folder));
                if (meta != null) metas.Add(meta);
            }
            return metas;
        }

        /// <summary>
        /// Removes a note that never held text. Permanent on purpose: there is nothing in it to
        /// recover, and a Recycle Bin full of empty folders helps nobody.
        /// </summary>
        public void DeleteEmptyNote(string id)
        {
            lock (LockFor(id))
            {
                if (Directory.Exists(NoteDir(id))) Directory.Delete(NoteDir(id), recursive: true);
                // A tombstone, not a removal: a save queued before the delete carries a lower
                // version and must not recreate the folder. Saves issued afterwards still pass.
                _written[id] = NextVersion();
            }
        }

        /// <summary>
        /// The saved session. A missing or corrupt file is rebuilt from the notes that are not
        /// closed, oldest change first; ids whose folder is gone are dropped. Always returns at least one
        /// window (<see cref="SessionWindows.Normalize"/>).
        /// </summary>
        public SessionState LoadSession()
        {
            lock (_sessionLock)
            {
                try
                {
                    string? json = ReadStoreText(SessionPath);
                    if (json != null)
                    {
                        var session = JsonSerializer.Deserialize<SessionState>(json, Json);
                        if (session != null) return Checked(session, NotesDir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _warn("session.json is unreadable; rebuilding it from the notes: " + ex.Message);
                }

                // Phase 1's rebuild, into one window (spec "Error handling"): also for a v2 file that cannot be read.
                return SessionWindows.Normalize(RebuildSession());
            }
        }

        /// <summary>Serializes a session. Done on the UI thread, so the writer thread never reads a live object.</summary>
        public static string SerializeSession(SessionState session) => JsonSerializer.Serialize(session, Json);

        /// <summary>Writes an already-serialized session.</summary>
        public void WriteSessionJson(string json)
        {
            lock (_sessionLock)
            {
                Directory.CreateDirectory(Root);
                WriteData(SessionPath, Utf8NoBom.GetBytes(json));
            }
        }

        /// <summary>Serializes and writes a session in one call.</summary>
        public void SaveSession(SessionState session) => WriteSessionJson(SerializeSession(session));

        /// <summary>
        /// Writes one version into the note's history, named by <paramref name="localTime"/>. A
        /// name already taken moves on by a millisecond.
        /// </summary>
        public SnapshotInfo WriteSnapshot(string id, string text, DateTime localTime)
        {
            lock (LockFor(id))
            {
                Directory.CreateDirectory(HistoryDir(id));

                var stamp = new DateTime(localTime.Ticks - localTime.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);
                string path;
                while (File.Exists(path = SnapshotPath(id, stamp))) stamp = stamp.AddMilliseconds(1);

                byte[] bytes = Utf8NoBom.GetBytes(text);
                long size = bytes.Length;
                WriteData(path, bytes);
                return new SnapshotInfo(path, stamp, size);
            }
        }

        /// <summary>The note's versions, newest first. Files that are not snapshots are ignored.</summary>
        public IReadOnlyList<SnapshotInfo> ListSnapshots(string id)
        {
            string folder = HistoryDir(id);
            if (!Directory.Exists(folder)) return Array.Empty<SnapshotInfo>();

            var list = new List<SnapshotInfo>();
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (!string.Equals(file.Extension, ".txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (!HistoryPolicy.TryParseStamp(Path.GetFileNameWithoutExtension(file.Name), out DateTime stamp)) continue;
                list.Add(new SnapshotInfo(file.FullName, stamp, TextLength(file)));
            }

            list.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));
            return list;
        }

        /// <summary>A version's text, or null when it cannot be read.</summary>
        public string? ReadSnapshot(SnapshotInfo snapshot)
        {
            try
            {
                return ReadStoreTextInPlace(snapshot.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not read snapshot " + snapshot.FilePath + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Rewrites every version of the note that holds <paramref name="value"/>, each copy
        /// replaced by <paramref name="reference"/>. Stamps and file names stay; versions without it
        /// are not touched.
        /// </summary>
        /// <returns>
        /// How many versions may still hold it, because they could not be read or rewritten; at
        /// least 1 when the versions could not even be listed. Never throws for an I/O failure.
        /// </returns>
        public int ScrubSnapshots(string id, string value, string reference)
        {
            lock (LockFor(id))
            {
                IReadOnlyList<SnapshotInfo> versions;
                try
                {
                    versions = ListSnapshots(id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("Could not list the versions of note " + id + " to remove a stored credential: " + ex.Message);
                    return 1;   // unknown: any of them may still hold it
                }

                int failed = 0;
                foreach (var snapshot in versions)
                {
                    try
                    {
                        string? text = ReadStoreText(snapshot.FilePath);
                        if (text == null) continue;

                        string scrubbed = SecretScrubber.Replace(text, value, reference, out int count);
                        if (count > 0) WriteData(snapshot.FilePath, Utf8NoBom.GetBytes(scrubbed));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _warn("Could not remove a stored credential from a version of note " + id + ": " + ex.Message);
                        failed++;
                    }
                }
                return failed;
            }
        }

        private object LockFor(string id) => _locks.GetOrAdd(id, _ => new object());

        private string SnapshotPath(string id, DateTime stamp) =>
            Path.Combine(HistoryDir(id), HistoryPolicy.FormatStamp(stamp) + ".txt");

        private NoteMeta? RebuildMeta(string id)
        {
            string? text = LoadText(id);
            if (text == null) return null;

            var folder = new DirectoryInfo(NoteDir(id));
            string current = CurrentPath(id);
            var meta = new NoteMeta
            {
                Id = id,
                UntitledNumber = 1,
                Title = NoteTitle.FromText(text, 1),
                CreatedUtc = folder.CreationTimeUtc,
                ModifiedUtc = File.Exists(current) ? File.GetLastWriteTimeUtc(current) : folder.LastWriteTimeUtc,
            };

            try
            {
                WriteData(MetaPath(id), JsonSerializer.SerializeToUtf8Bytes(meta, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not rewrite meta.json of note " + id + ": " + ex.Message);
            }
            return meta;
        }

        private SessionState RebuildSession()
        {
            var open = LoadAllMetas()
                .Where(m => !m.IsClosed)
                .OrderBy(m => m.ModifiedUtc)
                .Select(m => m.Id)
                .ToList();
            return new SessionState { OpenNoteIds = open, ActiveNoteId = open.LastOrDefault() };
        }
    }
}
