using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What <see cref="NotesKey.Load"/> found.</summary>
    public enum NotesKeyStatus
    {
        /// <summary>The key was read.</summary>
        Ready,

        /// <summary>There was none and nothing was encrypted yet, so a new one was made.</summary>
        Created,

        /// <summary>
        /// The key cannot be used: DPAPI refuses both copies (another PC or Windows account, a reset
        /// password), both are damaged, or both are missing while encrypted files exist.
        /// </summary>
        Unreadable,
    }

    /// <summary>
    /// The key every MicaPad store file is encrypted with: 32 random bytes in <c>key.bin</c> and a
    /// second copy in <c>key.bak</c>, protected by DPAPI for the current Windows user with a
    /// MicaPad-specific entropy, so only this account on this PC can open it. A copied store folder
    /// is useless elsewhere.
    /// </summary>
    public static class NotesKey
    {
        /// <summary>The key's file name in the store's root folder.</summary>
        public const string FileName = "key.bin";

        /// <summary>The second sealed copy of the key, beside <see cref="FileName"/>: the same bytes.</summary>
        public const string BackupFileName = "key.bak";

        /// <summary>Whether <paramref name="path"/> names one of the key's two files (not a store file holding notes).</summary>
        public static bool IsKeyFile(string path)
        {
            string name = Path.GetFileName(path);
            return string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, BackupFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.NotesKey.v1");

        /// <summary>
        /// Reads the store's key, or makes one when there is none and
        /// <paramref name="storeHasEncryptedFiles"/> says nothing was encrypted with an earlier one.
        ///
        /// <para>
        /// The key is kept twice, in <c>key.bin</c> and <c>key.bak</c> (the same sealed bytes), so one
        /// damaged or lost file never costs every note. A usable <c>key.bin</c> is read and its copy
        /// written when missing or different; otherwise a usable <c>key.bak</c> is read and
        /// <c>key.bin</c> rewritten from it. Only when neither can be used do the first rules apply: a
        /// new key when there is no key file and nothing is encrypted, else Unreadable.
        /// </para>
        /// </summary>
        /// <param name="root">The store's root folder.</param>
        /// <param name="storeHasEncryptedFiles">Asked only when there is no key file at all.</param>
        /// <param name="key">The key when Ready or Created; null when Unreadable.</param>
        /// <exception cref="IOException">A key file exists but cannot be read or written right now (another program holds it): try again later.</exception>
        public static NotesKeyStatus Load(string root, Func<bool> storeHasEncryptedFiles, out byte[]? key)
        {
            key = null;
            string path = Path.Combine(root, FileName);
            string backup = Path.Combine(root, BackupFileName);

            byte[]? sealedKey = AtomicFile.ReadBytes(path);
            if (sealedKey != null && Unseal(sealedKey) is { } fromKey)
            {
                // key.bin is left as it is; only its copy follows it.
                WriteOrWipe(fromKey, () =>
                {
                    byte[]? copy = AtomicFile.ReadBytes(backup);
                    if (copy == null || !copy.AsSpan().SequenceEqual(sealedKey)) AtomicFile.Write(backup, sealedKey);
                });
                key = fromKey;
                return NotesKeyStatus.Ready;
            }

            byte[]? sealedBackup = AtomicFile.ReadBytes(backup);
            if (sealedBackup != null && Unseal(sealedBackup) is { } fromBackup)
            {
                WriteOrWipe(fromBackup, () => AtomicFile.Write(path, sealedBackup));   // key.bin repaired from its copy
                key = fromBackup;
                return NotesKeyStatus.Ready;
            }

            // Neither copy can be used: a key file that exists but does not open is never replaced.
            if (sealedKey != null || sealedBackup != null) return NotesKeyStatus.Unreadable;
            if (storeHasEncryptedFiles()) return NotesKeyStatus.Unreadable;

            byte[] created = RandomNumberGenerator.GetBytes(StoreCipher.KeyLength);
            byte[] sealedCreated = ProtectedData.Protect(created, Entropy, DataProtectionScope.CurrentUser);
            WriteOrWipe(created, () =>
            {
                Directory.CreateDirectory(root);
                AtomicFile.Write(path, sealedCreated);
                AtomicFile.Write(backup, sealedCreated);
            });
            key = created;
            return NotesKeyStatus.Created;
        }

        /// <summary>
        /// The store's key for a reader that must never change the store: <see cref="Load"/>
        /// without anything it writes. Each copy is read where it lies, a finished write beside it
        /// first; no key is made, no copy is repaired from the other, no finished write is swapped
        /// in. Null when no copy can be used (there is none, or DPAPI refuses them). The caller
        /// wipes the key.
        /// </summary>
        /// <exception cref="IOException">A key file exists but cannot be read right now.</exception>
        public static byte[]? Peek(string root)
        {
            foreach (string name in new[] { FileName, BackupFileName })
            {
                string path = Path.Combine(root, name);
                foreach (string file in new[] { path + AtomicFile.ReadySuffix, path })
                {
                    byte[]? sealedKey;
                    try
                    {
                        sealedKey = File.Exists(file) ? File.ReadAllBytes(file) : null;
                    }
                    catch (FileNotFoundException)
                    {
                        sealedKey = null;   // gone between the two calls
                    }
                    if (sealedKey != null && Unseal(sealedKey) is { } key) return key;
                }
            }
            return null;
        }

        /// <summary>The key sealed in <paramref name="sealedBytes"/>, or null when DPAPI refuses it or it is not 32 bytes.</summary>
        private static byte[]? Unseal(byte[] sealedBytes)
        {
            try
            {
                byte[] plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
                if (plain.Length == StoreCipher.KeyLength) return plain;
                CryptographicOperations.ZeroMemory(plain);
                return null;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>Runs a key file write; when it throws, <paramref name="key"/> is wiped before the exception goes on.</summary>
        private static void WriteOrWipe(byte[] key, Action write)
        {
            try
            {
                write();
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
    }
}
