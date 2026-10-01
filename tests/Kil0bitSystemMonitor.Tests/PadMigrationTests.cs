using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The first start after the update: an earlier version's plain store becomes encrypted, losing nothing.</summary>
    public class PadMigrationTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        private readonly List<string> _warnings = new();

        public void Dispose() => _dir.Dispose();

        private string NotePath(string id, params string[] parts) => Path.Combine(new[] { _dir.Root, "notes", id }.Concat(parts).ToArray());

        /// <summary>A store as v1.13 left it: plain UTF-8 everywhere, and one write that was interrupted after it completed.</summary>
        private string PlainStore()
        {
            var meta = NoteStore.NewMeta(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 1, null);
            meta.Title = "Plain title";
            string id = meta.Id;
            Directory.CreateDirectory(NotePath(id, "history"));
            File.WriteAllText(NotePath(id, "meta.json"), JsonSerializer.Serialize(meta));
            File.WriteAllText(NotePath(id, "current.txt"), "plain current");
            File.WriteAllText(NotePath(id, "current.txt") + AtomicFile.ReadySuffix, "plain newer, not yet swapped in");
            File.WriteAllText(NotePath(id, "history", "20260901-090000-000.txt"), "plain version one");
            File.WriteAllText(NotePath(id, "history", "20260902-090000-000.txt"), "plain version two");
            File.WriteAllText(Path.Combine(_dir.Root, "session.json"), "{ \"OpenNoteIds\": [\"" + id + "\"] }");
            return id;
        }

        private NoteStore Open() => new(_dir.Root, warn: _warnings.Add);

        [Fact]
        public void Every_plain_file_is_encrypted_and_reads_the_same()
        {
            string id = PlainStore();
            var store = Open();

            Assert.Equal(5, store.EncryptPlainFiles());   // session, current (its .ready first), meta, two versions

            foreach (string file in Directory.EnumerateFiles(_dir.Root, "*", SearchOption.AllDirectories)
                                             .Where(f => Path.GetFileName(f) != NotesKey.FileName))
                Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(file)), file);
            Assert.False(File.Exists(NotePath(id, "current.txt") + AtomicFile.ReadySuffix));

            Assert.Equal("plain newer, not yet swapped in", store.LoadText(id));
            Assert.Equal("Plain title", store.LoadMeta(id)!.Title);
            Assert.Equal(new[] { "plain version two", "plain version one" },
                         store.ListSnapshots(id).Select(s => store.ReadSnapshot(s)).ToArray());
            Assert.Equal(new[] { id }, store.LoadSession().OpenNoteIds);
        }

        [Fact]
        public void A_second_run_finds_nothing_to_do()
        {
            PlainStore();
            var store = Open();
            store.EncryptPlainFiles();

            Assert.Equal(0, store.EncryptPlainFiles());
            Assert.Equal(0, Open().EncryptPlainFiles());
        }

        [Fact]
        public void A_file_it_cannot_open_is_reported_and_done_next_time()
        {
            string id = PlainStore();
            var store = Open();
            using (new FileStream(NotePath(id, "history", "20260901-090000-000.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(4, store.EncryptPlainFiles());
            }

            Assert.Contains(_warnings, w => w.Contains("20260901-090000-000.txt", StringComparison.Ordinal));
            Assert.DoesNotContain(_warnings, w => w.Contains("plain version", StringComparison.Ordinal));
            Assert.Equal(1, store.EncryptPlainFiles());
        }

        [Fact]
        public void A_utf8_bom_does_not_survive_into_the_encrypted_text()
        {
            string id = PlainStore();
            File.Delete(NotePath(id, "current.txt") + AtomicFile.ReadySuffix);
            var bom = new UTF8Encoding(true);
            File.WriteAllText(NotePath(id, "current.txt"), "bom current", bom);
            File.WriteAllText(NotePath(id, "history", "20260901-090000-000.txt"), "bom version", bom);
            var store = Open();

            store.EncryptPlainFiles();

            Assert.Equal("bom current", store.LoadText(id));
            Assert.Equal("bom version", store.ReadSnapshot(store.ListSnapshots(id).Last()));
            Assert.All(store.ListSnapshots(id), s => Assert.NotEqual('﻿', store.ReadSnapshot(s)[0]));
        }

        [Fact]
        public void Zero_fill_keeps_the_length_and_leaves_only_zeros()
        {
            string path = _dir.PathOf("plain.txt");
            File.WriteAllText(path, "hunter2 and more");

            NoteStore.ZeroFill(path);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(16, bytes.Length);
            Assert.All(bytes, b => Assert.Equal(0, b));
        }

        [Fact]
        public void Before_replace_runs_once_the_new_copy_is_complete_and_the_old_is_still_there()
        {
            string path = _dir.PathOf("store-file.txt");
            File.WriteAllText(path, "old");
            bool ran = false;

            AtomicFile.Write(path, new byte[] { 1, 2, 3 }, beforeReplace: () =>
            {
                ran = true;
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path + AtomicFile.ReadySuffix));
                Assert.Equal("old", File.ReadAllText(path));
            });

            Assert.True(ran);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }

        [Fact]
        public void A_crash_after_the_encrypted_copy_and_the_wipe_still_reads_the_text()
        {
            string id = PlainStore();
            var store = Open();
            string current = NotePath(id, "current.txt");
            File.Delete(current + AtomicFile.ReadySuffix);
            // The encrypted copy is complete as .ready and the plain file was zeroed; the swap never happened.
            var donor = NoteStore.NewMeta(DateTime.UtcNow, 2, null);
            store.SaveNote(donor, "the encrypted copy", store.NextVersion());
            File.Copy(store.CurrentPath(donor.Id), current + AtomicFile.ReadySuffix);
            NoteStore.ZeroFill(current);

            Assert.Equal("the encrypted copy", Open().LoadText(id));
        }

        [Fact]
        public void Saves_during_the_pass_win()
        {
            string id = PlainStore();
            var store = Open();
            var meta = store.LoadMeta(id)!;
            string last = "";

            var saver = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    last = "save " + i;
                    store.SaveNote(meta, last, store.NextVersion());
                }
            });
            store.EncryptPlainFiles();
            saver.Wait();

            Assert.Equal(last, store.LoadText(id));
            Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(store.CurrentPath(id))));
        }
    }
}
