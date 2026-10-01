using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The key MicaPad's store is encrypted with, and the bytes read under it.</summary>
    public class NotesKeyTests : IDisposable
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.NotesKey.v1");
        private readonly PadTempDir _dir = new();

        public void Dispose() => _dir.Dispose();

        private string KeyPath => Path.Combine(_dir.Root, NotesKey.FileName);

        [Fact]
        public void A_new_store_gets_a_32_byte_key_sealed_for_this_account()
        {
            Assert.Equal(NotesKeyStatus.Created, NotesKey.Load(_dir.Root, () => false, out byte[]? key));

            Assert.Equal(32, key!.Length);
            byte[] onDisk = File.ReadAllBytes(KeyPath);
            Assert.True(onDisk.AsSpan().IndexOf(key) < 0);   // the key itself is never on disk
            Assert.Equal(key, ProtectedData.Unprotect(onDisk, Entropy, DataProtectionScope.CurrentUser));
        }

        [Fact]
        public void The_same_key_comes_back()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? first);

            Assert.Equal(NotesKeyStatus.Ready,
                NotesKey.Load(_dir.Root, () => throw new InvalidOperationException("not asked when the key exists"), out byte[]? second));
            Assert.Equal(first, second);
        }

        [Fact]
        public void Two_stores_get_different_keys()
        {
            using var other = new PadTempDir();
            NotesKey.Load(_dir.Root, () => false, out byte[]? a);
            NotesKey.Load(other.Root, () => false, out byte[]? b);

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void No_key_is_made_while_encrypted_files_exist()
        {
            Assert.Equal(NotesKeyStatus.Unreadable, NotesKey.Load(_dir.Root, () => true, out byte[]? key));

            Assert.Null(key);
            Assert.False(File.Exists(KeyPath));
        }

        [Fact]
        public void A_key_sealed_for_someone_else_is_unreadable()
        {
            // Another account's DPAPI cannot be faked in a test; another entropy fails the same way.
            File.WriteAllBytes(KeyPath, ProtectedData.Protect(new byte[32], Encoding.UTF8.GetBytes("someone else"), DataProtectionScope.CurrentUser));

            Assert.Equal(NotesKeyStatus.Unreadable, NotesKey.Load(_dir.Root, () => false, out byte[]? key));
            Assert.Null(key);
        }

        [Fact]
        public void Garbage_or_a_key_of_the_wrong_length_is_unreadable()
        {
            File.WriteAllBytes(KeyPath, new byte[] { 1, 2, 3 });
            Assert.Equal(NotesKeyStatus.Unreadable, NotesKey.Load(_dir.Root, () => false, out _));

            File.WriteAllBytes(KeyPath, ProtectedData.Protect(new byte[16], Entropy, DataProtectionScope.CurrentUser));
            Assert.Equal(NotesKeyStatus.Unreadable, NotesKey.Load(_dir.Root, () => false, out _));
        }

        [Fact]
        public void A_key_file_held_open_by_another_program_throws_and_is_not_unreadable()
        {
            NotesKey.Load(_dir.Root, () => false, out _);
            using (new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => NotesKey.Load(_dir.Root, () => false, out _));
            }

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, () => false, out _));
        }

        [Fact]
        public void An_interrupted_key_write_is_finished()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? key);
            File.Move(KeyPath, KeyPath + AtomicFile.ReadySuffix);

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, () => throw new InvalidOperationException(), out byte[]? back));
            Assert.Equal(key, back);
            Assert.True(File.Exists(KeyPath));
        }

        // ---- key.bak: a second sealed copy (final review, item 11) ------------------------------

        private string BackupPath => Path.Combine(_dir.Root, NotesKey.BackupFileName);

        private static Func<bool> NotAsked => () => throw new InvalidOperationException("not asked while a copy of the key can be used");

        [Fact]
        public void A_new_key_is_written_twice()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? key);

            Assert.Equal(File.ReadAllBytes(KeyPath), File.ReadAllBytes(BackupPath));
            Assert.Equal(key, ProtectedData.Unprotect(File.ReadAllBytes(BackupPath), Entropy, DataProtectionScope.CurrentUser));
        }

        [Fact]
        public void A_damaged_key_with_a_good_backup_loads_ready_and_is_repaired()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? key);
            byte[] sealedKey = File.ReadAllBytes(KeyPath);
            File.WriteAllBytes(KeyPath, new byte[] { 1, 2, 3 });

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, NotAsked, out byte[]? back));

            Assert.Equal(key, back);
            Assert.Equal(sealedKey, File.ReadAllBytes(KeyPath));
            Assert.Equal(sealedKey, File.ReadAllBytes(BackupPath));
        }

        [Fact]
        public void A_missing_key_with_a_good_backup_loads_ready_and_is_repaired()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? key);
            byte[] sealedKey = File.ReadAllBytes(KeyPath);
            File.Delete(KeyPath);

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, NotAsked, out byte[]? back));

            Assert.Equal(key, back);
            Assert.Equal(sealedKey, File.ReadAllBytes(KeyPath));
        }

        [Fact]
        public void An_existing_key_without_a_backup_gains_one_and_is_left_as_it_is()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? key);
            File.Delete(BackupPath);   // a store from before key.bak
            byte[] sealedKey = File.ReadAllBytes(KeyPath);
            DateTime written = File.GetLastWriteTimeUtc(KeyPath);

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, NotAsked, out byte[]? back));

            Assert.Equal(key, back);
            Assert.Equal(sealedKey, File.ReadAllBytes(BackupPath));
            Assert.Equal(sealedKey, File.ReadAllBytes(KeyPath));
            Assert.Equal(written, File.GetLastWriteTimeUtc(KeyPath));
        }

        [Fact]
        public void A_backup_that_differs_from_a_good_key_is_rewritten()
        {
            NotesKey.Load(_dir.Root, () => false, out _);
            File.WriteAllBytes(BackupPath, new byte[] { 9, 9, 9 });

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, NotAsked, out _));

            Assert.Equal(File.ReadAllBytes(KeyPath), File.ReadAllBytes(BackupPath));
        }

        [Fact]
        public void Both_copies_damaged_is_unreadable()
        {
            NotesKey.Load(_dir.Root, () => false, out _);
            File.WriteAllBytes(KeyPath, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(BackupPath, ProtectedData.Protect(new byte[32], Encoding.UTF8.GetBytes("someone else"), DataProtectionScope.CurrentUser));

            Assert.Equal(NotesKeyStatus.Unreadable, NotesKey.Load(_dir.Root, () => false, out byte[]? key));
            Assert.Null(key);
        }

        [Fact]
        public void A_backup_held_open_while_it_is_needed_throws_and_is_not_unreadable()
        {
            NotesKey.Load(_dir.Root, () => false, out byte[]? key);
            File.Delete(KeyPath);
            using (new FileStream(BackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => NotesKey.Load(_dir.Root, () => true, out _));
            }

            Assert.Equal(NotesKeyStatus.Ready, NotesKey.Load(_dir.Root, NotAsked, out byte[]? back));
            Assert.Equal(key, back);
        }

        [Fact]
        public void Both_key_files_count_as_key_files()
        {
            Assert.True(NotesKey.IsKeyFile(KeyPath));
            Assert.True(NotesKey.IsKeyFile(BackupPath));
            Assert.False(NotesKey.IsKeyFile(_dir.PathOf("session.json")));
        }

        [Fact]
        public void ReadBytes_returns_null_for_a_missing_file_and_the_bytes_otherwise()
        {
            string path = _dir.PathOf("data.bin");
            Assert.Null(AtomicFile.ReadBytes(path));

            AtomicFile.Write(path, new byte[] { 0xFF, 0, 7 });

            Assert.Equal(new byte[] { 0xFF, 0, 7 }, AtomicFile.ReadBytes(path));
        }

        [Fact]
        public void ReadBytes_prefers_a_completed_write()
        {
            string path = _dir.PathOf("data.bin");
            File.WriteAllBytes(path, new byte[] { 1 });
            File.WriteAllBytes(path + AtomicFile.ReadySuffix, new byte[] { 2 });

            Assert.Equal(new byte[] { 2 }, AtomicFile.ReadBytes(path));
            Assert.False(File.Exists(path + AtomicFile.ReadySuffix));
        }
    }
}
