using System;
using System.Security.Cryptography;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The format of every file in MicaPad's store: AES-256-GCM under the notes key.
    ///
    /// <code>
    /// FF 4D 50 45 | 01 | nonce (12 bytes) | ciphertext | tag (16 bytes)
    /// </code>
    ///
    /// <para>
    /// The five header bytes are the associated data, so the header cannot be changed either.
    /// NoteStore only ever wrote valid UTF-8, which never holds the byte 0xFF, so data starting with
    /// it is encrypted and anything else is plain text from an earlier version.
    /// </para>
    /// </summary>
    public sealed class StoreCipher
    {
        /// <summary>The notes key's length: AES-256.</summary>
        public const int KeyLength = 32;

        private const int NonceLength = 12;
        private const int TagLength = 16;
        private static readonly byte[] Header = { 0xFF, 0x4D, 0x50, 0x45, 0x01 };

        /// <summary>How many bytes an encrypted file holds beyond its text.</summary>
        public const int Overhead = 5 + NonceLength + TagLength;

        private readonly byte[] _key;

        /// <param name="key">The 32-byte notes key; copied, so the caller may wipe its array.</param>
        public StoreCipher(byte[] key)
        {
            if (key == null || key.Length != KeyLength)
                throw new ArgumentException("The notes key must be 32 bytes.", nameof(key));
            _key = (byte[])key.Clone();
        }

        /// <summary>Whether <paramref name="data"/> is in this format rather than an earlier version's plain text.</summary>
        public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length > 0 && data[0] == 0xFF;

        /// <summary>The bytes to write for <paramref name="plain"/>, under a fresh random nonce.</summary>
        public byte[] Encrypt(ReadOnlySpan<byte> plain)
        {
            var output = new byte[Overhead + plain.Length];
            Header.CopyTo(output, 0);
            var nonce = output.AsSpan(Header.Length, NonceLength);
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(_key, TagLength);
            aes.Encrypt(nonce, plain, output.AsSpan(Header.Length + NonceLength, plain.Length),
                        output.AsSpan(output.Length - TagLength), Header);
            return output;
        }

        /// <summary>
        /// The text bytes of <paramref name="data"/>. False when it is not in this format, is cut
        /// short, was changed, or was written under another key.
        /// </summary>
        public bool TryDecrypt(ReadOnlySpan<byte> data, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            if (data.Length < Overhead || !data.Slice(0, Header.Length).SequenceEqual(Header)) return false;

            var output = new byte[data.Length - Overhead];
            try
            {
                using var aes = new AesGcm(_key, TagLength);
                aes.Decrypt(data.Slice(Header.Length, NonceLength), data.Slice(Header.Length + NonceLength, output.Length),
                            data.Slice(data.Length - TagLength), output, Header);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(output);
                return false;
            }

            plain = output;
            return true;
        }
    }
}
