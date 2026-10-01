using System;
using System.Collections.Generic;
using System.Globalization;
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
                                 .Where(f => !NotesKey.IsKeyFile(f)).ToList();
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
            File.Delete(Path.Combine(_dir.Root, NotesKey.BackupFileName));   // both copies lost (item 11: one alone is restored)

            NoteStore store;
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                store = Open();
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }

            Assert.NotNull(store.LockedFolder);
            Assert.Matches(@"-locked-20[0-9]{6}-[0-9]{6}$", store.LockedFolder);
            Assert.StartsWith(_dir.Root + "-locked-", store.LockedFolder, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(Path.Combine(store.LockedFolder!, "notes", meta.Id)));   // nothing deleted
            Assert.Empty(store.LoadAllMetas());
            Assert.True(File.Exists(Path.Combine(_dir.Root, NotesKey.FileName)));
            Assert.Contains(_warnings, w => w.Contains(store.LockedFolder!, StringComparison.Ordinal));
        }

        /// <summary>Both copies of the key sealed for another account, as on another PC.</summary>
        private void SealBothKeysForAnotherAccount()
        {
            byte[] foreign = ProtectedData.Protect(new byte[32], Encoding.UTF8.GetBytes("another account"), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(_dir.Root, NotesKey.FileName), foreign);
            File.WriteAllBytes(Path.Combine(_dir.Root, NotesKey.BackupFileName), foreign);
        }

        private NoteStore OpenMovedAside()
        {
            Save(Open(), "text");
            SealBothKeysForAnotherAccount();
            return Open();
        }

        [Fact]
        public void A_later_start_still_reports_where_the_notes_were_moved()
        {
            string folder = OpenMovedAside().LockedFolder!;

            Assert.Equal(folder, Open().LockedFolder);
            Assert.Equal(folder, Open().LockedFolder);
            byte[] marker = File.ReadAllBytes(Path.Combine(_dir.Root, "notice-locked.txt"));
            Assert.Equal(0xFF, marker[0]);
        }

        [Fact]
        public void A_folder_the_user_was_told_about_is_forgotten()
        {
            var store = OpenMovedAside();

            store.ForgetLockedFolder();

            Assert.Null(store.LockedFolder);
            Assert.Null(Open().LockedFolder);
            Assert.False(File.Exists(Path.Combine(_dir.Root, "notice-locked.txt")));
        }

        [Fact]
        public void A_key_this_account_cannot_open_moves_the_notes_aside_too()
        {
            Save(Open(), "text");
            SealBothKeysForAnotherAccount();

            Assert.NotNull(Open().LockedFolder);
        }

        // ---- final review: key.bak (item 11) and the 4-byte magic (item 5) --------------------

        [Fact]
        public void A_store_created_now_has_both_copies_of_its_key()
        {
            Save(Open(), "text");

            byte[] key = File.ReadAllBytes(Path.Combine(_dir.Root, NotesKey.FileName));
            Assert.Equal(key, File.ReadAllBytes(Path.Combine(_dir.Root, NotesKey.BackupFileName)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_lost_or_damaged_key_is_restored_from_its_backup_and_nothing_moves(bool damaged)
        {
            var meta = Save(Open(), "written under the key");
            string keyPath = Path.Combine(_dir.Root, NotesKey.FileName);
            byte[] sealedKey = File.ReadAllBytes(keyPath);
            if (damaged) File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3 });
            else File.Delete(keyPath);

            var store = Open();

            Assert.Null(store.LockedFolder);
            Assert.Equal("written under the key", store.LoadText(meta.Id));
            Assert.Equal(sealedKey, File.ReadAllBytes(keyPath));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_dir.Root)!, Path.GetFileName(_dir.Root) + "-locked-*"));
        }

        [Fact]
        public void An_existing_store_without_a_backup_gains_one_on_its_next_load()
        {
            var meta = Save(Open(), "text");
            string keyPath = Path.Combine(_dir.Root, NotesKey.FileName);
            string backup = Path.Combine(_dir.Root, NotesKey.BackupFileName);
            File.Delete(backup);
            byte[] sealedKey = File.ReadAllBytes(keyPath);

            var store = Open();

            Assert.Equal("text", store.LoadText(meta.Id));
            Assert.Equal(sealedKey, File.ReadAllBytes(keyPath));
            Assert.Equal(sealedKey, File.ReadAllBytes(backup));
        }

        [Fact]
        public void A_plain_utf16_file_with_a_bom_reads_as_its_text_and_moves_nothing()
        {
            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(_dir.Root, "notes", id);
            Directory.CreateDirectory(folder);
            string current = Path.Combine(folder, "current.txt");
            File.WriteAllText(current, "\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 UTF-16", Encoding.Unicode);   // FF FE first
            Assert.Equal(new byte[] { 0xFF, 0xFE }, File.ReadAllBytes(current).Take(2).ToArray());

            var store = Open();   // no key yet: a keyless store of plain files

            Assert.Null(store.LockedFolder);
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_dir.Root)!, Path.GetFileName(_dir.Root) + "-locked-*"));
            Assert.Equal("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 UTF-16", store.LoadText(id));

            Assert.Equal(1, store.EncryptPlainFiles());
            Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(current)));
            Assert.Equal("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 UTF-16", Open().LoadText(id));
        }

        [Fact]
        public void A_store_that_opens_normally_has_no_locked_folder()
        {
            Save(Open(), "text");

            Assert.Null(Open().LockedFolder);
        }

        [Fact]
        public void A_keyless_plain_store_with_a_file_held_open_is_not_moved_aside()
        {
            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(_dir.Root, "notes", id);
            Directory.CreateDirectory(Path.Combine(folder, "history"));
            File.WriteAllText(Path.Combine(folder, "current.txt"), "plain text");
            string version = Path.Combine(folder, "history", "20261001-090000-000.txt");
            File.WriteAllText(version, "plain version");

            using (new FileStream(version, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => Open());
                Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_dir.Root)!, Path.GetFileName(_dir.Root) + "-locked-*"));
                Assert.False(File.Exists(Path.Combine(_dir.Root, NotesKey.FileName)));
            }

            Assert.Null(Open().LockedFolder);
        }

        [Fact]
        public void A_key_file_held_open_fails_the_open_and_nothing_is_moved()
        {
            var meta = Save(Open(), "kept");
            string keyPath = Path.Combine(_dir.Root, NotesKey.FileName);

            using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => Open());
                Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_dir.Root)!, Path.GetFileName(_dir.Root) + "-locked-*"));
            }

            var again = Open();
            Assert.Null(again.LockedFolder);
            Assert.Equal("kept", again.LoadText(meta.Id));
        }

        [Fact]
        public void Reading_a_version_does_not_commit_its_ready_file()
        {
            var store = Open();
            var meta = Save(store, "x");
            var first = store.WriteSnapshot(meta.Id, "first", new DateTime(2026, 10, 1, 9, 0, 0));
            var second = store.WriteSnapshot(meta.Id, "second", new DateTime(2026, 10, 1, 9, 0, 1));
            string ready = first.FilePath + AtomicFile.ReadySuffix;
            File.Copy(second.FilePath, ready);

            Assert.Equal("second", store.ReadSnapshot(first));
            Assert.True(File.Exists(ready));
        }

        [Fact]
        public void Versions_holding_a_secret_are_rewritten_with_its_reference()
        {
            var store = Open();
            var meta = Save(store, "now");
            var a = store.WriteSnapshot(meta.Id, "pw=hunter2", new DateTime(2026, 10, 1, 9, 0, 0));
            var b = store.WriteSnapshot(meta.Id, "no secret here", new DateTime(2026, 10, 1, 9, 1, 0));
            var c = store.WriteSnapshot(meta.Id, "hunter2 and hunter2", new DateTime(2026, 10, 1, 9, 2, 0));
            byte[] untouched = File.ReadAllBytes(b.FilePath);

            Assert.Equal(0, store.ScrubSnapshots(meta.Id, "hunter2", "{{secret:K7Q2M9XD}}"));

            var versions = store.ListSnapshots(meta.Id);
            Assert.Equal(new[] { c.Stamp, b.Stamp, a.Stamp }, versions.Select(v => v.Stamp).ToArray());
            Assert.Equal("{{secret:K7Q2M9XD}} and {{secret:K7Q2M9XD}}", store.ReadSnapshot(versions[0]));
            Assert.Equal("pw={{secret:K7Q2M9XD}}", store.ReadSnapshot(versions[2]));
            Assert.Equal(untouched, File.ReadAllBytes(b.FilePath));
            Assert.All(versions, v => Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(v.FilePath))));
        }

        [Fact]
        public void A_version_that_cannot_be_read_counts_as_still_holding_the_secret()
        {
            var store = Open();
            var meta = Save(store, "now");
            var damaged = store.WriteSnapshot(meta.Id, "pw=hunter2", new DateTime(2026, 10, 1, 9, 0, 0));
            Damage(damaged.FilePath, -1);

            Assert.Equal(1, store.ScrubSnapshots(meta.Id, "hunter2", "{{secret:K7Q2M9XD}}"));
            Assert.DoesNotContain(_warnings, w => w.Contains("hunter2", StringComparison.Ordinal));
        }
    }
}
