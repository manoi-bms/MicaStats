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
    /// MicaPad's notes on disk: one folder per note, plain UTF-8 text a user can open in Explorer
    /// if MicaStats itself is broken.
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
        public NoteStore(string root, Action<string>? warn = null)
        {
            Root = root;
            _warn = warn ?? (message => DiagnosticsLog.Warn("pad", message));
            Directory.CreateDirectory(NotesDir);
        }

        /// <summary><c>%APPDATA%\MicaStats\MicaPad</c>, beside <c>config.json</c>.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicaStats", "MicaPad");

        /// <summary>The root folder where all notes are stored.</summary>
        public string Root { get; }

        /// <summary>The <c>notes</c> subfolder containing all note folders.</summary>
        public string NotesDir => Path.Combine(Root, "notes");

        /// <summary>Path to <c>session.json</c>, the window and tab state.</summary>
        public string SessionPath => Path.Combine(Root, "session.json");

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
                if (text != null) AtomicFile.Write(CurrentPath(meta.Id), Utf8NoBom.GetBytes(text));
                AtomicFile.Write(MetaPath(meta.Id), JsonSerializer.SerializeToUtf8Bytes(meta, Json));
                _written[meta.Id] = version;
                return true;
            }
        }

        /// <summary>
        /// The note's text, most trusted source first: a completed write not yet swapped in,
        /// <c>current.txt</c>, then the newest snapshot. Null when none exists.
        /// </summary>
        public string? LoadText(string id)
        {
            lock (LockFor(id))
            {
                try
                {
                    string? text = AtomicFile.ReadText(CurrentPath(id));
                    if (text != null) return text;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("Could not read the text of note " + id + ": " + ex.Message);
                }

                var newest = ListSnapshots(id).FirstOrDefault();
                return newest == null ? null : ReadSnapshot(newest);
            }
        }

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
                    string? json = AtomicFile.ReadText(MetaPath(id));
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
        /// closed, oldest change first; ids whose folder is gone are dropped.
        /// </summary>
        public SessionState LoadSession()
        {
            lock (_sessionLock)
            {
                try
                {
                    string? json = AtomicFile.ReadText(SessionPath);
                    if (json != null)
                    {
                        var session = JsonSerializer.Deserialize<SessionState>(json, Json);
                        if (session != null)
                        {
                            session.OpenNoteIds = (session.OpenNoteIds ?? new List<string>())
                                .Where(id => Directory.Exists(NoteDir(id)))
                                .Distinct()
                                .ToList();
                            session.Tabs ??= new Dictionary<string, TabViewState>();
                            return session;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _warn("session.json is unreadable; rebuilding it from the notes: " + ex.Message);
                }

                return RebuildSession();
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
                AtomicFile.Write(SessionPath, Utf8NoBom.GetBytes(json));
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
                AtomicFile.Write(path, bytes);
                return new SnapshotInfo(path, stamp, bytes.Length);
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
                list.Add(new SnapshotInfo(file.FullName, stamp, file.Length));
            }

            list.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));
            return list;
        }

        /// <summary>A version's text, or null when it cannot be read.</summary>
        public string? ReadSnapshot(SnapshotInfo snapshot)
        {
            try
            {
                return File.ReadAllText(snapshot.FilePath, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not read snapshot " + snapshot.FilePath + ": " + ex.Message);
                return null;
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
                AtomicFile.Write(MetaPath(id), JsonSerializer.SerializeToUtf8Bytes(meta, Json));
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
