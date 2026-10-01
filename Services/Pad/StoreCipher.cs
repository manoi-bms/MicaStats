using System;
using System.IO;
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
    /// Data starting with the four-byte magic <c>FF 4D 50 45</c> is encrypted; anything else is plain
    /// text from an earlier version. Valid UTF-8 never holds the byte 0xFF, and a plain file with a
    /// UTF-16 byte order mark starts <c>FF FE</c>, so neither is taken for an encrypted file.
    /// </para>
    /// </summary>
    public sealed class StoreCipher
    {
        /// <summary>The notes key's length: AES-256.</summary>
        public const int KeyLength = 32;

        /// <summary>How many leading bytes tell an encrypted file from a plain one: the magic, <c>FF 4D 50 45</c>.</summary>
        public const int MagicLength = 4;

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
        public static bool IsEncrypted(ReadOnlySpan<byte> data) =>
            data.Length >= MagicLength && data.Slice(0, MagicLength).SequenceEqual(Header.AsSpan(0, MagicLength));

        /// <summary>
        /// Whether the stream, read from where it stands, starts with the magic. Reads at most
        /// <see cref="MagicLength"/> bytes; a shorter stream is plain.
        /// </summary>
        public static bool StartsEncrypted(Stream stream)
        {
            Span<byte> first = stackalloc byte[MagicLength];
            int read = stream.ReadAtLeast(first, MagicLength, throwOnEndOfStream: false);
            return IsEncrypted(first.Slice(0, read));
        }

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
