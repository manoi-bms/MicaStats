using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>What MicaPad's store leaves on disk: encrypted files, read back the same; damage handled like before.</summary>
    public class PadEncryptionTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);
        private readonly PadTempDir _dir = new();
        private readonly List<string> _warnings = new();

        public void Dispose()
        {
            _dir.Dispose();
            string parent = Path.GetDirectoryName(_dir.Root)!;
            foreach (string aside in Directory.GetDirectories(parent, Path.GetFileName(_dir.Root) + "-locked-*"))
                Directory.Delete(aside, recursive: true);
        }

        private NoteStore Open() => new(_dir.Root, warn: _warnings.Add);

        private static NoteMeta Save(NoteStore store, string text, string? title = null)
        {
            var meta = NoteStore.NewMeta(T0, 1, null);
            if (title != null)
            {
                meta.Title = title;
                meta.TitleIsCustom = true;
            }
            Assert.True(store.SaveNote(meta, text, store.NextVersion()));
            return meta;
        }

        private static void Damage(string path, int index)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[index < 0 ? bytes.Length + index : index] ^= 0x40;
            File.WriteAllBytes(path, bytes);
        }

        [Fact]
        public void Nothing_mica_pad_writes_holds_the_text_in_the_clear()
        {
            var store = Open();
            var meta = Save(store, "correct horse battery \u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35", "Bank login");
            store.WriteSnapshot(meta.Id, "correct horse battery v1", new DateTime(2026, 10, 1, 9, 0, 0));
            store.SaveSession(new SessionState { OpenNoteIds = new List<string> { meta.Id }, ActiveNoteId = meta.Id });

            var files = Directory.EnumerateFiles(_dir.Root, "*", SearchOption.AllDirectories)
                                 .Where(f => Path.GetFileName(f) != NotesKey.FileName).ToList();
            Assert.Equal(4, files.Count);   // current.txt, meta.json, one version, session.json
            var needles = new[] { "correct horse", "Bank login", "\u0E2A\u0E27\u0E31\u0E2A" }.Select(Encoding.UTF8.GetBytes).ToList();
            foreach (string file in files)
            {
                byte[] bytes = File.ReadAllBytes(file);
                Assert.True(StoreCipher.IsEncrypted(bytes), file);
                foreach (byte[] needle in needles) Assert.True(bytes.AsSpan().IndexOf(needle) < 0, file);
            }
        }

        [Fact]
        public void A_fresh_store_on_the_same_folder_reads_everything_back()
        {
            var meta = Save(Open(), "line one\nline two", "Title");

            var again = Open();

            Assert.Equal("line one\nline two", again.LoadText(meta.Id));
            Assert.Equal("Title", again.LoadMeta(meta.Id)!.Title);
        }

        [Fact]
        public void Plain_files_from_an_earlier_version_are_read_as_before_and_saved_encrypted()
        {
            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(_dir.Root, "notes", id);
            Directory.CreateDirectory(Path.Combine(folder, "history"));
            File.WriteAllText(Path.Combine(folder, "current.txt"), "\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 plain", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "history", "20261001-090000-000.txt"), "with a BOM", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(_dir.Root, "session.json"), "{ \"OpenNoteIds\": [\"" + id + "\"] }");

            var store = Open();

            Assert.Equal("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 plain", store.LoadText(id));
            Assert.Equal("with a BOM", store.ReadSnapshot(Assert.Single(store.ListSnapshots(id))));
            Assert.Equal(new[] { id }, store.LoadSession().OpenNoteIds);

            var meta = store.LoadMeta(id)!;
            Assert.True(store.SaveNote(meta, "edited", store.NextVersion()));
            Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(store.CurrentPath(id))));
            Assert.Equal("edited", Open().LoadText(id));
        }

        [Fact]
        public void A_damaged_note_text_is_unreadable_and_left_as_it_is()
        {
            var store = Open();
            var meta = Save(store, "the only copy of this text");
            string path = store.CurrentPath(meta.Id);
            Damage(path, 29);   // inside the ciphertext
            byte[] damaged = File.ReadAllBytes(path);

            var again = Open();

            Assert.False(again.TryLoadText(meta.Id, out string? text));
            Assert.Null(text);
            Assert.Equal(damaged, File.ReadAllBytes(path));
            Assert.Contains(_warnings, w => w.Contains(meta.Id, StringComparison.Ordinal));
            Assert.DoesNotContain(_warnings, w => w.Contains("only copy", StringComparison.Ordinal));
        }

        [Fact]
        public void A_damaged_version_reads_as_missing_but_is_still_listed()
        {
            var store = Open();
            var meta = Save(store, "x");
            var info = store.WriteSnapshot(meta.Id, "old text", new DateTime(2026, 10, 1, 9, 0, 0));
            Damage(info.FilePath, -1);

            Assert.Null(store.ReadSnapshot(Assert.Single(store.ListSnapshots(meta.Id))));
        }

        [Fact]
        public void A_damaged_meta_is_rebuilt_from_the_text()
        {
            var store = Open();
            var meta = Save(store, "First line\nrest");
            Damage(store.MetaPath(meta.Id), -1);

            Assert.Equal("First line", Open().LoadMeta(meta.Id)!.Title);
        }

        [Fact]
        public void A_damaged_session_is_rebuilt_from_the_notes()
        {
            var store = Open();
            var meta = Save(store, "open note");
            store.SaveSession(new SessionState { OpenNoteIds = new List<string> { meta.Id }, ActiveNoteId = meta.Id });
            Damage(store.SessionPath, -1);

            Assert.Contains(meta.Id, Open().LoadSession().OpenNoteIds);
        }

        [Fact]
        public void Version_sizes_are_the_size_of_the_text()
        {
            var store = Open();
            var meta = Save(store, "x");
            var written = store.WriteSnapshot(meta.Id, "\u0E01\u0E02 twelve", new DateTime(2026, 10, 1, 9, 0, 0));

            Assert.Equal(Encoding.UTF8.GetByteCount("\u0E01\u0E02 twelve"), written.Size);
            Assert.Equal(written.Size, Assert.Single(store.ListSnapshots(meta.Id)).Size);
        }

        [Fact]
        public void A_two_megabyte_note_round_trips()
        {
            string text = string.Concat(Enumerable.Repeat("0123456789abcdef\u0E01\n", 2 * 1024 * 1024 / 20));

            var meta = Save(Open(), text);

            Assert.Equal(text, Open().LoadText(meta.Id));
        }

        [Fact]
        public void When_the_key_is_lost_the_notes_are_moved_aside_and_a_new_store_starts()
        {
            var meta = Save(Open(), "written under the old key");
            File.Delete(Path.Combine(_dir.Root, NotesKey.FileName));

            var store = Open();

            Assert.NotNull(store.LockedFolder);
            Assert.StartsWith(_dir.Root + "-locked-", store.LockedFolder, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(Path.Combine(store.LockedFolder!, "notes", meta.Id)));   // nothing deleted
            Assert.Empty(store.LoadAllMetas());
            Assert.True(File.Exists(Path.Combine(_dir.Root, NotesKey.FileName)));
            Assert.Contains(_warnings, w => w.Contains(store.LockedFolder!, StringComparison.Ordinal));
        }

        [Fact]
        public void A_key_this_account_cannot_open_moves_the_notes_aside_too()
        {
            Save(Open(), "text");
            File.WriteAllBytes(Path.Combine(_dir.Root, NotesKey.FileName),
                ProtectedData.Protect(new byte[32], Encoding.UTF8.GetBytes("another account"), DataProtectionScope.CurrentUser));

            Assert.NotNull(Open().LockedFolder);
        }

        [Fact]
        public void A_store_that_opens_normally_has_no_locked_folder()
        {
            Save(Open(), "text");

            Assert.Null(Open().LockedFolder);
        }
    }
}
