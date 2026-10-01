using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The byte format of every file in MicaPad's store.</summary>
    public class StoreCipherTests
    {
        private static readonly StoreCipher Cipher = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

        [Theory]
        [InlineData("")]
        [InlineData("hello")]
        [InlineData("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 line\r\nsecond")]
        public void Text_round_trips(string text)
        {
            byte[] sealedBytes = Cipher.Encrypt(Encoding.UTF8.GetBytes(text));

            Assert.True(Cipher.TryDecrypt(sealedBytes, out byte[] plain));
            Assert.Equal(text, Encoding.UTF8.GetString(plain));
        }

        [Fact]
        public void A_file_starts_with_the_header_and_is_33_bytes_longer_than_its_text()
        {
            byte[] plain = Encoding.UTF8.GetBytes("twelve bytes");
            byte[] sealedBytes = Cipher.Encrypt(plain);

            Assert.Equal(new byte[] { 0xFF, 0x4D, 0x50, 0x45, 0x01 }, sealedBytes.Take(5).ToArray());
            Assert.Equal(plain.Length + 33, sealedBytes.Length);
            Assert.Equal(33, StoreCipher.Overhead);
        }

        [Fact]
        public void The_same_text_encrypts_differently_each_time()
        {
            byte[] plain = Encoding.UTF8.GetBytes("same");

            Assert.NotEqual(Cipher.Encrypt(plain), Cipher.Encrypt(plain));
        }

        [Theory]
        [InlineData(0)]    // magic
        [InlineData(4)]    // version
        [InlineData(10)]   // nonce
        [InlineData(20)]   // ciphertext
        [InlineData(-1)]   // tag
        public void One_changed_byte_anywhere_fails(int index)
        {
            byte[] sealedBytes = Cipher.Encrypt(Encoding.UTF8.GetBytes("a secret long enough"));
            sealedBytes[index < 0 ? sealedBytes.Length + index : index] ^= 0x01;

            Assert.False(Cipher.TryDecrypt(sealedBytes, out _));
        }

        [Fact]
        public void A_truncated_file_fails()
        {
            byte[] sealedBytes = Cipher.Encrypt(Encoding.UTF8.GetBytes("text"));

            Assert.False(Cipher.TryDecrypt(sealedBytes.AsSpan(0, sealedBytes.Length - 1), out _));
            Assert.False(Cipher.TryDecrypt(sealedBytes.AsSpan(0, 20), out _));
            Assert.False(Cipher.TryDecrypt(ReadOnlySpan<byte>.Empty, out _));
        }

        [Fact]
        public void Another_key_fails()
        {
            byte[] sealedBytes = Cipher.Encrypt(Encoding.UTF8.GetBytes("text"));

            Assert.False(new StoreCipher(RandomNumberGenerator.GetBytes(32)).TryDecrypt(sealedBytes, out _));
        }

        [Fact]
        public void Only_data_starting_with_the_four_byte_magic_counts_as_encrypted()
        {
            Assert.True(StoreCipher.IsEncrypted(Cipher.Encrypt(Array.Empty<byte>())));
            Assert.False(StoreCipher.IsEncrypted(Encoding.UTF8.GetBytes("{ \"Id\": 1 }")));
            // Thai and the replacement character are valid UTF-8, which never holds 0xFF.
            Assert.False(StoreCipher.IsEncrypted(Encoding.UTF8.GetBytes("\u0E01\uFFFD")));
            Assert.False(StoreCipher.IsEncrypted(ReadOnlySpan<byte>.Empty));
            // A plain file written as UTF-16 LE starts with its BOM, FF FE: plain, not encrypted.
            Assert.False(StoreCipher.IsEncrypted(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }));
            Assert.False(StoreCipher.IsEncrypted(new byte[] { 0xFF, 0x4D, 0x50 }));   // cut short of the magic
            Assert.True(StoreCipher.IsEncrypted(new byte[] { 0xFF, 0x4D, 0x50, 0x45 }));
        }

        [Fact]
        public void The_key_must_be_32_bytes()
        {
            Assert.Throws<ArgumentException>(() => new StoreCipher(new byte[16]));
        }

        [Fact]
        public void Clearing_the_callers_key_array_later_does_not_affect_the_cipher()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            var cipher = new StoreCipher(key);
            byte[] sealedBytes = cipher.Encrypt(Encoding.UTF8.GetBytes("x"));

            Array.Clear(key);

            Assert.True(cipher.TryDecrypt(sealedBytes, out _));
        }
    }
}
