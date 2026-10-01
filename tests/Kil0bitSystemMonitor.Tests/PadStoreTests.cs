using System;
using System.IO;
using System.Linq;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The store is the last line of defence: whatever the window or the writer thread did, these
    /// tests pin what comes back from disk — including after a crash left files half-written.
    /// </summary>
    public class PadStoreTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 9, 29, 5, 0, 0, DateTimeKind.Utc);
        private readonly PadTempDir _dir = new();
        private readonly NoteStore _store;

        public PadStoreTests()
        {
            _store = new NoteStore(_dir.Root, warn: _ => { });
        }

        public void Dispose() => _dir.Dispose();

        private NoteMeta Note(string? text, DateTime? modifiedUtc = null)
        {
            var meta = NoteStore.NewMeta(T0, 1, null);
            if (modifiedUtc != null) meta.ModifiedUtc = modifiedUtc.Value;
            Assert.True(_store.SaveNote(meta, text, 1));
            return meta;
        }

        private NoteStore Reopened() => new(_dir.Root, warn: _ => { });

        [Fact]
        public void Text_and_meta_round_trip_through_a_fresh_store()
        {
            var meta = Note("hello");
            meta.Title = "Custom";
            meta.TitleIsCustom = true;
            Assert.True(_store.SaveNote(meta, "hello again", 2));

            var store = Reopened();
            Assert.Equal("hello again", store.LoadText(meta.Id));
            var back = store.LoadMeta(meta.Id);
            Assert.NotNull(back);
            Assert.Equal("Custom", back!.Title);
            Assert.True(back.TitleIsCustom);
            Assert.Equal(T0, back.CreatedUtc);
        }

        [Fact]
        public void Versions_only_increase()
        {
            long first = _store.NextVersion();

            Assert.True(_store.NextVersion() > first);
        }

        [Fact]
        public void An_older_version_never_overwrites_a_newer_one()
        {
            var meta = Note("v1");
            Assert.True(_store.SaveNote(meta, "v3", 3));

            Assert.False(_store.SaveNote(meta, "v2", 2));
            Assert.Equal("v3", _store.LoadText(meta.Id));
        }

        [Fact]
        public void A_leftover_ready_file_wins_and_is_committed()
        {
            var meta = Note("old");
            string current = _store.CurrentPath(meta.Id);
            File.WriteAllText(current + AtomicFile.ReadySuffix, "newer");

            Assert.Equal("newer", _store.LoadText(meta.Id));
            Assert.False(File.Exists(current + AtomicFile.ReadySuffix));
            Assert.Equal("newer", File.ReadAllText(current));
        }

        [Fact]
        public void A_partial_tmp_file_is_ignored()
        {
            var meta = Note("old");
            File.WriteAllText(_store.CurrentPath(meta.Id) + AtomicFile.TempSuffix, "parti");

            Assert.Equal("old", _store.LoadText(meta.Id));
        }

        [Fact]
        public void Missing_text_falls_back_to_the_newest_snapshot()
        {
            var meta = Note(text: null);
            _store.WriteSnapshot(meta.Id, "first", new DateTime(2026, 9, 29, 10, 0, 0));
            _store.WriteSnapshot(meta.Id, "second", new DateTime(2026, 9, 29, 11, 0, 0));

            Assert.Equal("second", _store.LoadText(meta.Id));
        }

        [Fact]
        public void Text_that_exists_but_cannot_be_read_is_not_replaced_by_an_older_snapshot()
        {
            var meta = Note("newest");
            _store.WriteSnapshot(meta.Id, "older", new DateTime(2026, 9, 29, 10, 0, 0));

            using (new FileStream(_store.CurrentPath(meta.Id), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(_store.TryLoadText(meta.Id, out string? text));
                Assert.Null(text);
                Assert.Null(_store.LoadText(meta.Id));
            }

            Assert.True(_store.TryLoadText(meta.Id, out string? after));
            Assert.Equal("newest", after);
        }

        [Fact]
        public void A_note_with_no_text_anywhere_loads_as_null_rather_than_unreadable()
        {
            var meta = Note(text: null);

            Assert.True(_store.TryLoadText(meta.Id, out string? text));
            Assert.Null(text);
        }

        [Fact]
        public void Corrupt_meta_is_rebuilt_from_the_text()
        {
            var meta = Note("First line\nmore");
            File.WriteAllText(_store.MetaPath(meta.Id), "{not json");

            var rebuilt = Reopened().LoadMeta(meta.Id);

            Assert.NotNull(rebuilt);
            Assert.Equal(meta.Id, rebuilt!.Id);
            Assert.Equal("First line", rebuilt.Title);
            Assert.Null(rebuilt.ClosedAtUtc);
            Assert.Contains("\"Id\"", _store.ReadStoreText(_store.MetaPath(meta.Id)));
        }

        [Fact]
        public void A_folder_with_neither_text_nor_meta_is_not_a_note()
        {
            string id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_store.NoteDir(id));

            Assert.Null(_store.LoadMeta(id));
            Assert.Empty(_store.LoadAllMetas());
        }

        [Fact]
        public void Meta_json_uses_readable_enum_names()
        {
            var meta = Note("x");
            meta.Encoding = PadEncoding.Utf16Le;
            meta.LineEnding = LineEnding.Lf;
            _store.SaveNote(meta, null, 2);

            string json = _store.ReadStoreText(_store.MetaPath(meta.Id))!;
            Assert.Contains("\"Utf16Le\"", json);
            Assert.Contains("\"Lf\"", json);
        }

        [Fact]
        public void The_session_round_trips()
        {
            var a = Note("a");
            var session = new SessionState
            {
                WindowOpen = true,
                Top = 40,
                Width = 800,
                Height = 500,
                Zoom = 1.2,
                ActiveNoteId = a.Id,
            };
            session.OpenNoteIds.Add(a.Id);
            session.Tabs[a.Id] = new TabViewState { CaretOffset = 3, VerticalOffset = 12.5 };
            _store.SaveSession(session);

            var back = Reopened().LoadSession();

            Assert.True(back.WindowOpen);
            Assert.Null(back.Left);
            Assert.Equal(40, back.Top);
            Assert.Equal(1.2, back.Zoom);
            Assert.Equal(new[] { a.Id }, back.OpenNoteIds);
            Assert.Equal(a.Id, back.ActiveNoteId);
            Assert.Equal(3, back.Tabs[a.Id].CaretOffset);
            Assert.Equal(12.5, back.Tabs[a.Id].VerticalOffset);
        }

        [Fact]
        public void A_corrupt_session_is_rebuilt_from_open_notes_by_modified_time()
        {
            var a = Note("a", T0.AddMinutes(1));
            var b = Note("b", T0.AddMinutes(3));
            var c = Note("c", T0.AddMinutes(2));
            b.ClosedAtUtc = T0.AddMinutes(4);
            _store.SaveNote(b, null, 2);
            File.WriteAllText(_store.SessionPath, "garbage");

            var session = _store.LoadSession();

            Assert.Equal(new[] { a.Id, c.Id }, session.OpenNoteIds);
            Assert.Equal(c.Id, session.ActiveNoteId);
            Assert.False(session.WindowOpen);
        }

        [Fact]
        public void A_missing_session_is_rebuilt_too()
        {
            var a = Note("a");

            Assert.Equal(new[] { a.Id }, _store.LoadSession().OpenNoteIds);
        }

        [Fact]
        public void The_session_drops_notes_whose_folder_is_gone()
        {
            var a = Note("a");
            var session = new SessionState();
            session.OpenNoteIds.Add(a.Id);
            session.OpenNoteIds.Add(Guid.NewGuid().ToString("N"));
            _store.SaveSession(session);

            Assert.Equal(new[] { a.Id }, _store.LoadSession().OpenNoteIds);
        }

        [Fact]
        public void Snapshots_list_newest_first_and_read_back()
        {
            var meta = Note("x");
            _store.WriteSnapshot(meta.Id, "older", new DateTime(2026, 9, 29, 10, 0, 0));
            _store.WriteSnapshot(meta.Id, "newer", new DateTime(2026, 9, 29, 11, 0, 0));

            var list = _store.ListSnapshots(meta.Id);

            Assert.Equal(2, list.Count);
            Assert.Equal(new DateTime(2026, 9, 29, 11, 0, 0), list[0].Stamp);
            Assert.Equal("newer", _store.ReadSnapshot(list[0]));
            Assert.Equal("older", _store.ReadSnapshot(list[1]));
            Assert.Equal(5, list[1].Size);
        }

        [Fact]
        public void Snapshots_in_the_same_millisecond_get_distinct_names()
        {
            var meta = Note("x");
            var when = new DateTime(2026, 9, 29, 10, 0, 0, 5);

            var first = _store.WriteSnapshot(meta.Id, "1", when);
            var second = _store.WriteSnapshot(meta.Id, "2", when);

            Assert.NotEqual(first.FilePath, second.FilePath);
            Assert.Equal(2, _store.ListSnapshots(meta.Id).Count);
        }

        [Fact]
        public void Deleting_an_empty_note_removes_its_folder()
        {
            var meta = Note("");

            _store.DeleteEmptyNote(meta.Id);

            Assert.False(Directory.Exists(_store.NoteDir(meta.Id)));
        }

        [Fact]
        public void A_save_queued_before_a_delete_does_not_bring_the_note_back()
        {
            var meta = Note("");
            long queuedBeforeDelete = _store.NextVersion();

            _store.DeleteEmptyNote(meta.Id);

            Assert.False(_store.SaveNote(meta, "late", queuedBeforeDelete));
            Assert.False(Directory.Exists(_store.NoteDir(meta.Id)));
            Assert.True(_store.SaveNote(meta, "reopened", _store.NextVersion()));
        }

        [Fact]
        public void A_source_write_replaces_the_file_and_leaves_no_temporary_file()
        {
            string file = _dir.PathOf("config.ini");
            File.WriteAllText(file, "old");

            AtomicFile.WriteSource(file, Encoding.UTF8.GetBytes("new"));
            AtomicFile.WriteSource(_dir.PathOf("fresh.txt"), Encoding.UTF8.GetBytes("created"));

            Assert.Equal("new", File.ReadAllText(file));
            Assert.Equal("created", File.ReadAllText(_dir.PathOf("fresh.txt")));
            // The store's own key sits in the same folder; only user files are counted.
            Assert.Equal(2, Directory.GetFiles(_dir.Root).Count(f => Path.GetFileName(f) != NotesKey.FileName));
        }
    }
}
