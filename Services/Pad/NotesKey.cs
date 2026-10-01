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
        /// The key cannot be used: DPAPI refuses it (another PC or Windows account, a reset
        /// password), it is damaged, or it is missing while encrypted files exist.
        /// </summary>
        Unreadable,
    }

    /// <summary>
    /// The key every MicaPad store file is encrypted with: 32 random bytes in <c>key.bin</c>,
    /// protected by DPAPI for the current Windows user with a MicaPad-specific entropy, so only
    /// this account on this PC can open it. A copied store folder is useless elsewhere.
    /// </summary>
    public static class NotesKey
    {
        /// <summary>The key's file name in the store's root folder.</summary>
        public const string FileName = "key.bin";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.NotesKey.v1");

        /// <summary>
        /// Reads the store's key, or makes one when there is none and
        /// <paramref name="storeHasEncryptedFiles"/> says nothing was encrypted with an earlier one.
        /// </summary>
        /// <param name="root">The store's root folder.</param>
        /// <param name="storeHasEncryptedFiles">Asked only when there is no key file.</param>
        /// <param name="key">The key when Ready or Created; null when Unreadable.</param>
        /// <exception cref="IOException">The key file exists but cannot be read right now (another program holds it): try again later.</exception>
        public static NotesKeyStatus Load(string root, Func<bool> storeHasEncryptedFiles, out byte[]? key)
        {
            key = null;
            string path = Path.Combine(root, FileName);

            byte[]? sealedBytes = AtomicFile.ReadBytes(path);
            if (sealedBytes == null)
            {
                if (storeHasEncryptedFiles()) return NotesKeyStatus.Unreadable;

                byte[] created = RandomNumberGenerator.GetBytes(StoreCipher.KeyLength);
                Directory.CreateDirectory(root);
                AtomicFile.Write(path, ProtectedData.Protect(created, Entropy, DataProtectionScope.CurrentUser));
                key = created;
                return NotesKeyStatus.Created;
            }

            try
            {
                byte[] plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
                if (plain.Length != StoreCipher.KeyLength)
                {
                    CryptographicOperations.ZeroMemory(plain);
                    return NotesKeyStatus.Unreadable;
                }

                key = plain;
                return NotesKeyStatus.Ready;
            }
            catch (CryptographicException)
            {
                return NotesKeyStatus.Unreadable;
            }
        }
    }
}
