# MicaPad encrypted store and vault service — Implementation Plan (Parts 1–2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Encrypt every file in MicaPad's store at rest (AES-256-GCM under a DPAPI-protected notes key, with migration of existing plain files), and build the credential vault service (public-key vault, PIN, wrong-PIN waits, references) that Part 3's editor will use.

**Architecture:** `StoreCipher` defines the file format; `NotesKey` loads or creates the DPAPI-protected key; `NoteStore` (new partial `NoteStore.Encryption.cs`) wraps every store read and write and migrates plain files; `CredentialVault` holds RSA-3072 + AES-GCM credentials in a DPAPI-protected `vault.bin`; `SecretTokens`/`SecretScrubber` handle `{{secret:ID}}` references. All under `Services/Pad/`, no WPF. Parts 3–4 (editor, Settings, guide) follow in a second plan, `docs/superpowers/plans/2026-10-01-micapad-vault-part3-4.md`, written after this one lands.

**Tech Stack:** .NET 8 WPF app; `System.Security.Cryptography` (AesGcm, RSA, ProtectedData, RandomNumberGenerator, PbeParameters); System.Text.Json; xUnit.

**Spec:** `docs/superpowers/specs/2026-10-01-micapad-vault-design.md`

## Global Constraints

- Repo `C:\AIProject\kil0bit-system-monitor`, branch `feat/micapad-vault`. Commit on it; never push, merge or tag.
- Build and test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Full suite: `timeout 300 env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -5`. Known flaky: `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn` — re-run once if it alone fails.
- Never build or publish into `bin\Release` (the controller deploys).
- Tests: temp folders only (`PadTempDir`), never the real `%APPDATA%`; never launch MicaStats; no network; UI tests only on the shared `UiThread.Run`, never shown.
- New code under `Services/` holds no WPF types. Files in this repo use LF line endings; keep them.
- Nothing secret — note text, credential values, PINs, keys — is ever part of a log line, a warning, an exception message or a status message. Warnings name files and note ids only.
- Exact values (verbatim from the spec): store file header `FF 4D 50 45` + version `01`, 12-byte nonce, 16-byte tag, header bytes as associated data, overhead 33 bytes; key 32 bytes; DPAPI current user with entropy `"MicaStats.MicaPad.NotesKey.v1"` (notes key, `key.bin`) and `"MicaStats.MicaPad.Vault.v1"` (`vault.bin`); RSA 3072, OAEP-SHA256; private key as encrypted PKCS#8, PBES2 PBKDF2-HMAC-SHA256, 600,000 rounds, AES-256-CBC; PIN 6–12 ASCII digits; waits from the 5th wrong PIN: 30 s doubling, capped at 15 min; unlock lasts 5 minutes; reference `{{secret:ID}}`, ID = 8 chars of `0123456789ABCDEFGHJKMNPQRSTVWXYZ`; moved-aside names `<root>-locked-yyyyMMdd-HHmmss` and `vault-locked-yyyyMMdd-HHmmss.bin` (InvariantCulture — the owner's machine runs th-TH, whose default calendar is Buddhist).
- C# string escapes: write non-ASCII as `\uXXXX` escapes. The Write/Edit tools can decode them into raw characters; after writing, verify with `Select-String -SimpleMatch` (PowerShell) or `grep -F`.
- Commits: `git add` exact paths, `git commit -m "..."` (no heredoc), never amend, end with the trailer `Co-Authored-By: <your model name> <noreply@anthropic.com>`.
- Never weaken an existing assertion. When an existing test reads a store file directly with `File.ReadAllText`, change only how it reads (through `NoteStore.ReadStoreText`).
- Do not dispatch subagents.

## Rulings on the spec

- **R1 — rebuilt metadata may overwrite a damaged file.** Spec 1.2 says a damaged file is handled "exactly as today (meta is rebuilt, session is rebuilt)" and also "an unreadable file is never overwritten by a read path". `meta.json` and `session.json` are derived data that today's rebuild rewrites; the never-overwrite rule applies to note text (`current.txt`) and versions. Cost if wrong: a damaged meta.json is replaced by a rebuilt one (it held nothing the text does not).
- **R2 — a key or vault file that cannot be read right now throws.** Only DPAPI refusing it, a wrong length or invalid JSON counts as "cannot be used" and moves things aside. An I/O error (another program holding the file) propagates, so the store or vault is tried again later instead of being moved aside over a passing lock.
- **R3 — a failed vault save removes its `.ready`.** `AtomicFile` keeps a complete `.ready` for the next read; for the vault that would resurrect a change the user was told failed. `Save` deletes it on failure.
- **R4 — locked-folder notice wording:** "MicaPad could not decrypt the notes saved before on this Windows account, so they were moved to {folder}. Nothing was deleted." with the button "Show folder".

## Review Focus

1. Another program (antivirus, backup) holding `key.bin` or `vault.bin` open at startup: the store/vault must not be moved aside — the open fails and is retried later (Task 2, Task 6 tests).
2. The autosave writer saving a note while the migration pass runs: the latest save wins and ends up encrypted (Task 4 test).
3. Plain files left by v1.13 — Thai text, a UTF-8 BOM — read exactly as `File.ReadAllText` read them (Task 3 test).
4. A crash after the encrypted copy is complete but before it replaces the plain original: the next read returns the text (Task 4 test).
5. A vault save that fails (file locked): memory and disk both keep the old state, and no leftover `.ready` brings the change back (Task 6 test).

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| Create `Services/Pad/StoreCipher.cs` | 1 | The store file format: encrypt, decrypt, detect |
| Create `Services/Pad/NotesKey.cs` | 2 | Load or create the DPAPI-protected notes key |
| Modify `Services/Pad/AtomicFile.cs` | 2, 4 | `ReadBytes`; `Write(…, beforeReplace)` |
| Create `Services/Pad/NoteStore.Encryption.cs` | 3, 4 | Key opening, move-aside, encrypted read/write helpers, file enumeration, migration pass |
| Modify `Services/Pad/NoteStore.cs` | 3, 5 | Every read/write through the helpers; `ScrubSnapshots` |
| Modify `App.xaml.cs` | 4 | Migration in the maintenance pass |
| Modify `Services/Pad/PadWorkspace.cs`, `Pad/MicaPadWindow.xaml.cs` | 4 | Locked-folder notice, once per run |
| Create `Services/Pad/SecretTokens.cs`, `Services/Pad/SecretScrubber.cs` | 5 | References and replacement |
| Create `Services/Pad/CredentialVault.cs` | 6 | The vault |
| Tests (create) | | `StoreCipherTests`, `NotesKeyTests`, `PadEncryptionTests`, `PadMigrationTests`, `SecretTokensTests`, `SecretScrubberTests`, `CredentialVaultTests` |
| Tests (modify) | 3, 4 | `PadStoreTests.cs` (two direct reads), `PadTestEnv.cs` (`DiskText`), `PadWindowTests.cs` (notice), `PadConfigTests.cs` (maintenance order) |

---

### Task 1: StoreCipher — the store file format

**Files:**
- Create: `Services/Pad/StoreCipher.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/StoreCipherTests.cs`

**Interfaces:**
- Produces: `public sealed class StoreCipher` with `public const int KeyLength = 32`, `public const int Overhead = 33`, `public StoreCipher(byte[] key)`, `public static bool IsEncrypted(ReadOnlySpan<byte> data)`, `public byte[] Encrypt(ReadOnlySpan<byte> plain)`, `public bool TryDecrypt(ReadOnlySpan<byte> data, out byte[] plain)`.

- [ ] **Step 1: Write the failing tests**

```csharp
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
        public void Only_data_starting_with_0xFF_counts_as_encrypted()
        {
            Assert.True(StoreCipher.IsEncrypted(Cipher.Encrypt(Array.Empty<byte>())));
            Assert.False(StoreCipher.IsEncrypted(Encoding.UTF8.GetBytes("{ \"Id\": 1 }")));
            // Thai and the replacement character are valid UTF-8, which never holds 0xFF.
            Assert.False(StoreCipher.IsEncrypted(Encoding.UTF8.GetBytes("\u0E01\uFFFD")));
            Assert.False(StoreCipher.IsEncrypted(ReadOnlySpan<byte>.Empty));
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~StoreCipherTests" 2>&1 | tail -5`
Expected: build error, `StoreCipher` does not exist.

- [ ] **Step 3: Implement**

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~StoreCipherTests" 2>&1 | tail -5`
Expected: all pass. Verify the `\u` escapes in the test file survived (`grep -F '\u0E2A' tests/Kil0bitSystemMonitor.Tests/StoreCipherTests.cs`).

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/StoreCipher.cs tests/Kil0bitSystemMonitor.Tests/StoreCipherTests.cs
git commit -m "feat(pad): StoreCipher, the encrypted store file format (AES-256-GCM, 0xFF header)" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 2: NotesKey and AtomicFile.ReadBytes

**Files:**
- Create: `Services/Pad/NotesKey.cs`
- Modify: `Services/Pad/AtomicFile.cs` (add `ReadBytes` after `ReadText`)
- Test: `tests/Kil0bitSystemMonitor.Tests/NotesKeyTests.cs`

**Interfaces:**
- Consumes: `StoreCipher.KeyLength` (Task 1).
- Produces: `public static byte[]? AtomicFile.ReadBytes(string path)`; `public enum NotesKeyStatus { Ready, Created, Unreadable }`; `public static class NotesKey` with `public const string FileName = "key.bin"` and `public static NotesKeyStatus Load(string root, Func<bool> storeHasEncryptedFiles, out byte[]? key)` — throws `IOException`/`UnauthorizedAccessException` when `key.bin` exists but cannot be read right now (ruling R2).

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~NotesKeyTests" 2>&1 | tail -5`
Expected: build error (`NotesKey`, `AtomicFile.ReadBytes` missing).

- [ ] **Step 3: Implement `AtomicFile.ReadBytes`** (insert after `ReadText` in `Services/Pad/AtomicFile.cs`; `ReadText` itself is unchanged)

```csharp
        /// <summary>
        /// The bytes of a store file written by <see cref="Write"/>, finishing an interrupted write
        /// first. Null when neither the file nor a completed write exists.
        /// </summary>
        public static byte[]? ReadBytes(string path)
        {
            string ready = path + ReadySuffix;
            if (File.Exists(ready))
            {
                try
                {
                    Commit(ready, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return File.ReadAllBytes(ready);
                }
            }

            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
```

- [ ] **Step 4: Implement `Services/Pad/NotesKey.cs`**

```csharp
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
```

- [ ] **Step 5: Run to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~NotesKeyTests|FullyQualifiedName~PadStoreTests" 2>&1 | tail -5`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/NotesKey.cs Services/Pad/AtomicFile.cs tests/Kil0bitSystemMonitor.Tests/NotesKeyTests.cs
git commit -m "feat(pad): NotesKey, the DPAPI-protected notes key; AtomicFile.ReadBytes" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 3: NoteStore reads and writes through the cipher

**Files:**
- Create: `Services/Pad/NoteStore.Encryption.cs`
- Modify: `Services/Pad/NoteStore.cs` (constructor, class summary, `SaveNote`, `TryLoadText`, `LoadMeta`, `LoadSession`, `WriteSessionJson`, `WriteSnapshot`, `ListSnapshots`, `ReadSnapshot`, `RebuildMeta`)
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadStoreTests.cs` (the two `File.ReadAllText(_store.MetaPath(...))` reads), `tests/Kil0bitSystemMonitor.Tests/PadTestEnv.cs` (`DiskText`)
- Test: `tests/Kil0bitSystemMonitor.Tests/PadEncryptionTests.cs`

**Interfaces:**
- Consumes: `StoreCipher` (Task 1), `NotesKey`, `AtomicFile.ReadBytes` (Task 2).
- Produces (used by Tasks 4–5 and Part 3):
  - `public sealed class StoreFileUnreadableException : IOException` with `string FilePath`.
  - On `NoteStore`: `public string? LockedFolder { get; internal set; }`; `internal string? ReadStoreText(string path)`; private `WriteData(string path, byte[] plain)`, `NoteIds()`, `NoteFiles(string id)`, `DataFiles()`, `AnyEncryptedFile()`, `TextLength(FileInfo)`.
  - The `NoteStore(string root, Action<string>? warn = null)` signature is unchanged; it now throws `IOException` when `key.bin` exists but cannot be read right now.

- [ ] **Step 1: Write the failing tests** — `tests/Kil0bitSystemMonitor.Tests/PadEncryptionTests.cs`

```csharp
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~PadEncryptionTests" 2>&1 | tail -5`
Expected: build error (`LockedFolder` missing).

- [ ] **Step 3: Create `Services/Pad/NoteStore.Encryption.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A store file that is encrypted but does not decrypt: damaged, changed by hand, or written under another key.</summary>
    public sealed class StoreFileUnreadableException : IOException
    {
        /// <param name="path">The file.</param>
        public StoreFileUnreadableException(string path)
            : base("The file " + path + " is encrypted but does not decrypt")
        {
            FilePath = path;
        }

        /// <summary>The file.</summary>
        public string FilePath { get; }
    }

    public sealed partial class NoteStore
    {
        private StoreCipher _cipher = null!;

        /// <summary>
        /// Where the store was moved because its key could not be used (another PC or Windows
        /// account, a lost <c>key.bin</c>), or null. Set by the constructor; the first MicaPad
        /// window tells the user once.
        /// </summary>
        public string? LockedFolder { get; internal set; }

        /// <summary>
        /// Loads the notes key, or creates it for a store with no encrypted file. A key that cannot
        /// be used moves the whole folder aside, so nothing is lost, and starts an empty store.
        /// </summary>
        private void OpenKey()
        {
            var status = NotesKey.Load(Root, AnyEncryptedFile, out byte[]? key);
            if (status == NotesKeyStatus.Unreadable)
            {
                LockedFolder = MoveAside();
                _warn("MicaPad's notes could not be decrypted by this Windows account; they were moved to " + LockedFolder);
                Directory.CreateDirectory(NotesDir);
                NotesKey.Load(Root, () => false, out key);
            }

            _cipher = new StoreCipher(key!);
            CryptographicOperations.ZeroMemory(key);
        }

        private string MoveAside()
        {
            string root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = root + "-locked-" + stamp;
            for (int n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = root + "-locked-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);

            Directory.Move(root, target);
            return target;
        }

        /// <summary>Encrypts and writes one store file; the plain bytes are wiped afterwards.</summary>
        private void WriteData(string path, byte[] plain)
        {
            try
            {
                AtomicFile.Write(path, _cipher.Encrypt(plain));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        /// <summary>
        /// A store file's text: decrypted, or read as before when an earlier version wrote it plain.
        /// Null when the file does not exist. A file that is encrypted but does not decrypt throws
        /// <see cref="StoreFileUnreadableException"/>, an <see cref="IOException"/>, so every caller
        /// treats it like any other file it cannot read.
        /// </summary>
        internal string? ReadStoreText(string path)
        {
            byte[]? bytes = AtomicFile.ReadBytes(path);
            if (bytes == null) return null;
            if (!StoreCipher.IsEncrypted(bytes)) return ReadPlain(bytes);

            if (!_cipher.TryDecrypt(bytes, out byte[] plain)) throw new StoreFileUnreadableException(path);
            try
            {
                return Utf8NoBom.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        /// <summary>An earlier version's plain file, decoded as <c>File.ReadAllText</c> did: UTF-8 unless a BOM says otherwise.</summary>
        private static string ReadPlain(byte[] bytes)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>The size of the text a store file holds: its length, less the format's overhead when encrypted.</summary>
        private static long TextLength(FileInfo file)
        {
            if (file.Length < StoreCipher.Overhead) return file.Length;
            try
            {
                using var stream = file.OpenRead();
                return stream.ReadByte() == 0xFF ? file.Length - StoreCipher.Overhead : file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return file.Length;
            }
        }

        /// <summary>
        /// Whether any file holding notes, versions or the session is encrypted. A file that cannot
        /// be read counts as encrypted, so a passing lock never gets a second key made.
        /// </summary>
        private bool AnyEncryptedFile()
        {
            foreach (string path in DataFiles())
            {
                foreach (string file in new[] { path, path + AtomicFile.ReadySuffix })
                {
                    if (!File.Exists(file)) continue;
                    try
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (stream.ReadByte() == 0xFF) return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>The session and every note's files, as the paths <see cref="AtomicFile"/> reads (a lone <c>.ready</c> is listed by its target).</summary>
        private IEnumerable<string> DataFiles()
        {
            if (Exists(SessionPath)) yield return SessionPath;
            foreach (string id in NoteIds())
                foreach (string path in NoteFiles(id))
                    yield return path;
        }

        /// <summary>The ids of the note folders on disk.</summary>
        private IReadOnlyList<string> NoteIds()
        {
            try
            {
                return Directory.Exists(NotesDir)
                    ? Directory.GetDirectories(NotesDir).Select(Path.GetFileName).OfType<string>().ToList()
                    : Array.Empty<string>();
            }
            catch (DirectoryNotFoundException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>A note's text, metadata and versions that exist on disk, finished writes included.</summary>
        private IReadOnlyList<string> NoteFiles(string id)
        {
            var files = new List<string>();
            if (Exists(CurrentPath(id))) files.Add(CurrentPath(id));
            if (Exists(MetaPath(id))) files.Add(MetaPath(id));

            try
            {
                if (Directory.Exists(HistoryDir(id)))
                {
                    var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in Directory.GetFiles(HistoryDir(id)))
                    {
                        if (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) versions.Add(file);
                        else if (file.EndsWith(".txt" + AtomicFile.ReadySuffix, StringComparison.OrdinalIgnoreCase))
                            versions.Add(file.Substring(0, file.Length - AtomicFile.ReadySuffix.Length));
                    }
                    files.AddRange(versions);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The note was deleted meanwhile.
            }
            return files;
        }

        private static bool Exists(string path) => File.Exists(path) || File.Exists(path + AtomicFile.ReadySuffix);
    }
}
```

- [ ] **Step 4: Wire `NoteStore.cs`**

Constructor (and its doc):

```csharp
        /// <summary>Initializes a note store rooted at the given folder.</summary>
        /// <param name="root">The MicaPad folder. Created if missing.</param>
        /// <param name="warn">Where recoverable problems are reported; the diagnostics log by default.</param>
        /// <exception cref="IOException"><c>key.bin</c> exists but cannot be read right now; try again later.</exception>
        public NoteStore(string root, Action<string>? warn = null)
        {
            Root = root;
            _warn = warn ?? (message => DiagnosticsLog.Warn("pad", message));
            Directory.CreateDirectory(NotesDir);
            OpenKey();
        }
```

Class summary: replace "plain UTF-8 text a user can open in Explorer if MicaStats itself is broken." with "each file encrypted with the notes key (<see cref=\"StoreCipher\"/>, <see cref=\"NotesKey\"/>); files an earlier version wrote plain are read as before until <see cref=\"EncryptPlainFiles\"/> rewrites them." (Task 4 adds `EncryptPlainFiles`; until then write the sentence without the cref to keep the build warning-free, and Task 4 restores it.)

Replace each store read and write (and nothing else in these methods):

| Method | Before | After |
|---|---|---|
| `SaveNote` | `AtomicFile.Write(CurrentPath(meta.Id), Utf8NoBom.GetBytes(text))` | `WriteData(CurrentPath(meta.Id), Utf8NoBom.GetBytes(text))` |
| `SaveNote` | `AtomicFile.Write(MetaPath(meta.Id), JsonSerializer.SerializeToUtf8Bytes(meta, Json))` | `WriteData(MetaPath(meta.Id), JsonSerializer.SerializeToUtf8Bytes(meta, Json))` |
| `TryLoadText` | `text = AtomicFile.ReadText(CurrentPath(id));` | `text = ReadStoreText(CurrentPath(id));` |
| `LoadMeta` | `string? json = AtomicFile.ReadText(MetaPath(id));` | `string? json = ReadStoreText(MetaPath(id));` |
| `LoadSession` | `string? json = AtomicFile.ReadText(SessionPath);` | `string? json = ReadStoreText(SessionPath);` |
| `WriteSessionJson` | `AtomicFile.Write(SessionPath, Utf8NoBom.GetBytes(json));` | `WriteData(SessionPath, Utf8NoBom.GetBytes(json));` |
| `RebuildMeta` | `AtomicFile.Write(MetaPath(id), JsonSerializer.SerializeToUtf8Bytes(meta, Json));` | `WriteData(MetaPath(id), JsonSerializer.SerializeToUtf8Bytes(meta, Json));` |
| `ListSnapshots` | `list.Add(new SnapshotInfo(file.FullName, stamp, file.Length));` | `list.Add(new SnapshotInfo(file.FullName, stamp, TextLength(file)));` |
| `ReadSnapshot` | `return File.ReadAllText(snapshot.FilePath, Utf8NoBom);` | `return ReadStoreText(snapshot.FilePath);` |

`WriteSnapshot` becomes (size taken before `WriteData` wipes the bytes):

```csharp
                byte[] bytes = Utf8NoBom.GetBytes(text);
                long size = bytes.Length;
                WriteData(path, bytes);
                return new SnapshotInfo(path, stamp, size);
```

Update the `SnapshotInfo.Size` doc in `Services/Pad/NoteMeta.cs` from "File size in bytes." to "Size of the version's text in bytes (UTF-8), whether or not the file is encrypted."

The existing `catch (… IOException …)` blocks already cover `StoreFileUnreadableException`; do not add new catches.

- [ ] **Step 5: Update the two tests that read store files directly**

In `PadStoreTests.cs`, `Corrupt_meta_is_rebuilt_from_the_text`: `File.ReadAllText(_store.MetaPath(meta.Id))` → `_store.ReadStoreText(_store.MetaPath(meta.Id))`; `Meta_json_uses_readable_enum_names`: `string json = File.ReadAllText(_store.MetaPath(meta.Id));` → `string json = _store.ReadStoreText(_store.MetaPath(meta.Id))!;`.

In `PadTestEnv.cs`, `DiskText`:

```csharp
        /// <summary>The note's <c>current.txt</c>, decrypted, or null when it has none.</summary>
        public string? DiskText(OpenNote note) => Store.ReadStoreText(Store.CurrentPath(note.Id));
```

- [ ] **Step 6: Run the new tests and the full suite**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~PadEncryptionTests" 2>&1 | tail -5` — expected: all pass.
Then the full suite (Global Constraints). Any other failure that comes from a test reading a store file with `File.ReadAllText`/`ReadAllBytes`: switch that read to `ReadStoreText`; never change what it asserts. Report each such change.

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/NoteStore.Encryption.cs Services/Pad/NoteStore.cs Services/Pad/NoteMeta.cs tests/Kil0bitSystemMonitor.Tests/PadEncryptionTests.cs tests/Kil0bitSystemMonitor.Tests/PadStoreTests.cs tests/Kil0bitSystemMonitor.Tests/PadTestEnv.cs
git commit -m "feat(pad): MicaPad's store is encrypted at rest; plain files from earlier versions still read; an unusable key moves the store aside" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```
(Add any other test file Step 6 touched, by exact path.)

---

### Task 4: Migration pass, maintenance wiring, locked-folder notice

**Files:**
- Modify: `Services/Pad/AtomicFile.cs` (`Write` gains `beforeReplace`)
- Modify: `Services/Pad/NoteStore.Encryption.cs` (`EncryptPlainFiles`, `EncryptIfPlain`, `WipePlain`, `ZeroFill`), `Services/Pad/NoteStore.cs` (class summary cref)
- Modify: `App.xaml.cs` (`RunPadMaintenance`)
- Modify: `Services/Pad/PadWorkspace.cs` (`LockedNoticeShown`), `Pad/MicaPadWindow.xaml.cs` (`Open`, `ShowLockedFolderNotice`)
- Test: create `tests/Kil0bitSystemMonitor.Tests/PadMigrationTests.cs`; modify `PadWindowTests.cs`, `PadConfigTests.cs`

**Interfaces:**
- Consumes: Task 3's `NoteIds()`, `NoteFiles(id)`, `ReadStoreText`, `_cipher`, `LockedFolder`.
- Produces: `public static void AtomicFile.Write(string path, byte[] bytes, Action? beforeReplace = null)`; `public int NoteStore.EncryptPlainFiles()`; `internal static void NoteStore.ZeroFill(string path)`; `public bool PadWorkspace.LockedNoticeShown { get; set; }`; `internal void MicaPadWindow.ShowLockedFolderNotice()`.

- [ ] **Step 1: Write the failing tests** — `tests/Kil0bitSystemMonitor.Tests/PadMigrationTests.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
```

In `PadWindowTests.cs` (inside the class, using its `WithWindow`):

```csharp
        [Fact]
        public void The_locked_folder_notice_shows_once() => WithWindow((window, env, config) =>
        {
            env.Store.LockedFolder = @"C:\Users\x\AppData\Roaming\MicaStats\MicaPad-locked-20261001-090000";

            window.ShowLockedFolderNotice();

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("MicaPad could not decrypt the notes saved before on this Windows account, so they were moved to "
                         + env.Store.LockedFolder + ". Nothing was deleted.", window.InfoText.Text);
            Assert.Equal("Show folder", window.InfoPrimary.Content);

            window.HideInfo();
            window.ShowLockedFolderNotice();
            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
        });

        [Fact]
        public void No_notice_when_the_store_opened_normally() => WithWindow((window, env, config) =>
        {
            window.ShowLockedFolderNotice();

            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
        });
```

In `PadConfigTests.cs` (next to the test that already reads `App.xaml.cs`):

```csharp
        [Fact]
        public void Maintenance_encrypts_plain_files_before_pruning()
        {
            string app = System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "App.xaml.cs"));
            int encrypt = app.IndexOf("store.EncryptPlainFiles()", StringComparison.Ordinal);
            int prune = app.IndexOf("store.PruneAll(", StringComparison.Ordinal);

            Assert.True(encrypt > 0 && encrypt < prune);
        }
```

- [ ] **Step 2: Run to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~PadMigrationTests|FullyQualifiedName~PadWindowTests|FullyQualifiedName~PadConfigTests" 2>&1 | tail -5`
Expected: build error.

- [ ] **Step 3: `AtomicFile.Write` gains `beforeReplace`**

```csharp
        /// <summary>
        /// Writes a store file in two phases. <paramref name="beforeReplace"/> runs once the
        /// complete write is on disk as <c>.ready</c> and before it replaces the target.
        /// </summary>
        public static void Write(string path, byte[] bytes, Action? beforeReplace = null)
        {
            string temp = path + TempSuffix;
            string ready = path + ReadySuffix;

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, ready, overwrite: true);
            beforeReplace?.Invoke();
            Commit(ready, path);
        }
```

- [ ] **Step 4: The migration pass** (append to `NoteStore.Encryption.cs`)

```csharp
        /// <summary>
        /// Encrypts every file an earlier version wrote plain: notes, versions, metadata and the
        /// session. Each plain original is overwritten with zeros once its encrypted copy is
        /// complete on disk and before that copy replaces it, so a crash at any point leaves either
        /// the plain file or a complete encrypted one. Encrypted files are skipped, so it can run at
        /// every start and resumes where a crash stopped it. Never throws: a file it cannot encrypt
        /// is reported and tried again next time.
        /// </summary>
        /// <returns>How many files were encrypted.</returns>
        public int EncryptPlainFiles()
        {
            int encrypted = 0;
            lock (_sessionLock)
            {
                if (EncryptIfPlain(SessionPath)) encrypted++;
            }

            foreach (string id in NoteIds())
            {
                lock (LockFor(id))
                {
                    foreach (string path in NoteFiles(id))
                        if (EncryptIfPlain(path)) encrypted++;
                }
            }
            return encrypted;
        }

        private bool EncryptIfPlain(string path)
        {
            byte[]? bytes = null;
            try
            {
                bytes = AtomicFile.ReadBytes(path);   // finishes an interrupted write first
                if (bytes == null || StoreCipher.IsEncrypted(bytes)) return false;

                AtomicFile.Write(path, _cipher.Encrypt(bytes), beforeReplace: () => WipePlain(path));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not encrypt " + path + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
            }
        }

        /// <summary>Best effort: zeros over a plain file before its encrypted copy replaces it. An SSD may still keep the old blocks.</summary>
        private void WipePlain(string path)
        {
            try
            {
                ZeroFill(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not overwrite the plain copy of " + path + ": " + ex.Message);
            }
        }

        /// <summary>Overwrites a file with zeros in place, keeping its length, and flushes it to disk.</summary>
        internal static void ZeroFill(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var zeros = new byte[64 * 1024];
            for (long left = stream.Length; left > 0; left -= zeros.Length)
                stream.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
            stream.Flush(flushToDisk: true);
        }
```

Restore the `<see cref="EncryptPlainFiles"/>` in `NoteStore`'s class summary (Task 3, Step 4).

- [ ] **Step 5: Maintenance runs the pass first** — in `App.xaml.cs`, `RunPadMaintenance`, the `Task.Run` body becomes:

```csharp
                    try
                    {
                        int encrypted = store.EncryptPlainFiles();
                        if (encrypted > 0)
                            Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("pad", "Encrypted " + encrypted + " MicaPad files written by an earlier version");
                        store.PruneAll(DateTime.UtcNow, days, Kil0bitSystemMonitor.Services.Pad.RecycleBin.Instance);
                    }
```

and its summary becomes "Encrypts files an earlier version left plain, prunes history and purges long-closed notes, off the UI thread."

- [ ] **Step 6: The notice** — in `Services/Pad/PadWorkspace.cs`, after `Store`:

```csharp
        /// <summary>Whether a window already told the user about <see cref="NoteStore.LockedFolder"/>: once per run.</summary>
        public bool LockedNoticeShown { get; set; }
```

In `Pad/MicaPadWindow.xaml.cs`, `Open` calls the notice after opening the path:

```csharp
        public static MicaPadWindow Open(PadWorkspace workspace, AppConfig config, Action? openSettings, string? path)
        {
            var window = ShowOrActivate(workspace, config, openSettings, path);
            if (!string.IsNullOrWhiteSpace(path)) window.OpenPath(path);
            window.ShowLockedFolderNotice();
            return window;
        }
```

and, next to `ShowInfo`:

```csharp
        /// <summary>
        /// Once per run: the notes this Windows account could not decrypt were moved to
        /// <see cref="NoteStore.LockedFolder"/>. Nothing was deleted.
        /// </summary>
        internal void ShowLockedFolderNotice()
        {
            string? folder = _workspace.Store.LockedFolder;
            if (folder == null || _workspace.LockedNoticeShown) return;

            _workspace.LockedNoticeShown = true;
            ShowInfo("MicaPad could not decrypt the notes saved before on this Windows account, so they were moved to "
                     + folder + ". Nothing was deleted.", null, "Show folder", () => ShowInFolder(folder));
        }
```

- [ ] **Step 7: Run the new tests and the full suite**

Run the Step 2 filter (expected: all pass), then the full suite (Global Constraints).

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/AtomicFile.cs Services/Pad/NoteStore.Encryption.cs Services/Pad/NoteStore.cs App.xaml.cs Services/Pad/PadWorkspace.cs Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadMigrationTests.cs tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs
git commit -m "feat(pad): existing notes are encrypted on the first start, plain copies zeroed; a notice when notes had to be moved aside" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

**Controller, after Task 4's review:** Part 1 is complete — deploy for the owner's e2e (standing rule) and tell them what to check (spec Testing → Manual 1).

---

### Task 5: References, scrubbing, and NoteStore.ScrubSnapshots

**Files:**
- Create: `Services/Pad/SecretTokens.cs`, `Services/Pad/SecretScrubber.cs`
- Modify: `Services/Pad/NoteStore.cs` (add `ScrubSnapshots` after `ReadSnapshot`)
- Test: create `tests/Kil0bitSystemMonitor.Tests/SecretTokensTests.cs`, `tests/Kil0bitSystemMonitor.Tests/SecretScrubberTests.cs`; add to `PadEncryptionTests.cs`

**Interfaces:**
- Consumes: `NoteStore.ReadStoreText`, `WriteData`, `ListSnapshots`, `LockFor` (Task 3).
- Produces:
  - `public readonly record struct SecretReference(int Offset, int Length, string Id)`
  - `public static class SecretTokens`: `const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"`, `const int IdLength = 8`, `const string Pattern = @"\{\{secret:([0-9A-HJKMNP-TV-Z]{8})\}\}"`, `string Format(string id)`, `bool IsId(string? id)`, `IReadOnlyList<SecretReference> Find(string text)`, `bool Contains(string text)`, `string NewId(Func<string, bool> taken)`.
  - `public static class SecretScrubber`: `int Count(string text, string value)`, `string Replace(string text, string value, string reference, out int count)`.
  - `public int NoteStore.ScrubSnapshots(string id, string value, string reference)` — returns how many versions may still hold the value (could not be read or rewritten).

- [ ] **Step 1: Write the failing tests**

`SecretTokensTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The text that stands in a note for a stored credential.</summary>
    public class SecretTokensTests
    {
        [Fact]
        public void A_reference_is_the_id_in_double_braces() =>
            Assert.Equal("{{secret:K7Q2M9XD}}", SecretTokens.Format("K7Q2M9XD"));

        [Fact]
        public void References_are_found_with_their_offsets()
        {
            string text = "user: admin\npass: {{secret:K7Q2M9XD}} \u0E44\u0E17\u0E22 {{secret:00000000}}";

            var found = SecretTokens.Find(text);

            Assert.Equal(2, found.Count);
            Assert.Equal(new SecretReference(text.IndexOf("{{", System.StringComparison.Ordinal), SecretTokens.Format("K7Q2M9XD").Length, "K7Q2M9XD"), found[0]);
            Assert.Equal("00000000", found[1].Id);
            Assert.True(SecretTokens.Contains(text));
        }

        [Theory]
        [InlineData("{{secret:k7q2m9xd}}")]   // lower case
        [InlineData("{{secret:K7Q2M9XI}}")]   // I, L, O and U are not in the alphabet
        [InlineData("{{secret:K7Q2M9XL}}")]
        [InlineData("{{secret:K7Q2M9XO}}")]
        [InlineData("{{secret:K7Q2M9XU}}")]
        [InlineData("{{secret:K7Q2M9X}}")]    // 7 characters
        [InlineData("{{secret:K7Q2M9XDD}}")]  // 9
        [InlineData("{secret:K7Q2M9XD}")]
        [InlineData("{{Secret:K7Q2M9XD}}")]
        public void Near_misses_are_not_references(string text)
        {
            Assert.Empty(SecretTokens.Find(text));
            Assert.False(SecretTokens.Contains(text));
        }

        [Fact]
        public void New_ids_use_the_alphabet_and_skip_taken_ones()
        {
            var seen = new List<string>();

            string id = SecretTokens.NewId(candidate =>
            {
                seen.Add(candidate);
                return seen.Count < 3;
            });

            Assert.Equal(3, seen.Count);
            Assert.Equal(seen[2], id);
            Assert.All(seen, s => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{8}$", s));
        }

        [Fact]
        public void The_alphabet_is_crockford_base32()
        {
            Assert.Equal(32, SecretTokens.Alphabet.Distinct().Count());
            Assert.Equal(32, SecretTokens.Alphabet.Length);
            foreach (char c in "ILOU") Assert.DoesNotContain(c, SecretTokens.Alphabet);
        }

        [Theory]
        [InlineData("K7Q2M9XD", true)]
        [InlineData("k7q2m9xd", false)]
        [InlineData("K7Q2M9X", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Ids_are_checked(string? id, bool valid) => Assert.Equal(valid, SecretTokens.IsId(id));
    }
}
```

`SecretScrubberTests.cs`:

```csharp
using System;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Replacing a secret's copies with its reference.</summary>
    public class SecretScrubberTests
    {
        private const string Reference = "{{secret:K7Q2M9XD}}";

        [Fact]
        public void Every_copy_is_replaced_and_counted()
        {
            string text = SecretScrubber.Replace("a=hunter2\nb=hunter2\r\nc=hunter", "hunter2", Reference, out int count);

            Assert.Equal(2, count);
            Assert.Equal("a=" + Reference + "\nb=" + Reference + "\r\nc=hunter", text);
        }

        [Fact]
        public void Copies_do_not_overlap() => Assert.Equal(2, SecretScrubber.Count("aaaaa", "aa"));

        [Fact]
        public void Matching_is_exact() => Assert.Equal(0, SecretScrubber.Count("Hunter2", "hunter2"));

        [Fact]
        public void A_multi_line_secret_is_matched_with_its_line_breaks()
        {
            string key = "-----BEGIN KEY-----\r\nabc\r\n-----END KEY-----";

            Assert.Equal(1, SecretScrubber.Count("x\r\n" + key + "\r\ny", key));
        }

        [Fact]
        public void No_copy_leaves_the_text_as_it_is()
        {
            string text = "nothing here";

            Assert.Same(text, SecretScrubber.Replace(text, "secret", Reference, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void An_empty_value_is_refused() => Assert.Throws<ArgumentException>(() => SecretScrubber.Count("x", ""));
    }
}
```

Add to `PadEncryptionTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SecretTokensTests|FullyQualifiedName~SecretScrubberTests|FullyQualifiedName~PadEncryptionTests" 2>&1 | tail -5`
Expected: build error.

- [ ] **Step 3: Implement `Services/Pad/SecretTokens.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One reference in a note's text: where it is and which credential it names.</summary>
    public readonly record struct SecretReference(int Offset, int Length, string Id);

    /// <summary>
    /// The text that stands in a note for a stored credential, <c>{{secret:K7Q2M9XD}}</c>: eight
    /// random characters of Crockford base32, which has no I, L, O or U, so an id read aloud or
    /// typed again is not misread. Upper case only.
    /// </summary>
    public static class SecretTokens
    {
        /// <summary>Crockford base32.</summary>
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Characters in an id.</summary>
        public const int IdLength = 8;

        /// <summary>A reference; group 1 is the id.</summary>
        public const string Pattern = @"\{\{secret:([0-9A-HJKMNP-TV-Z]{8})\}\}";

        private static readonly Regex Reference = new(Pattern, RegexOptions.CultureInvariant);

        /// <summary>The reference text for <paramref name="id"/>.</summary>
        public static string Format(string id) => "{{secret:" + id + "}}";

        /// <summary>Whether <paramref name="id"/> is eight characters of the alphabet.</summary>
        public static bool IsId(string? id)
        {
            if (id == null || id.Length != IdLength) return false;
            foreach (char c in id)
                if (Alphabet.IndexOf(c) < 0) return false;
            return true;
        }

        /// <summary>Every reference in <paramref name="text"/>, in order.</summary>
        public static IReadOnlyList<SecretReference> Find(string text)
        {
            var found = new List<SecretReference>();
            foreach (Match match in Reference.Matches(text))
                found.Add(new SecretReference(match.Index, match.Length, match.Groups[1].Value));
            return found;
        }

        /// <summary>Whether <paramref name="text"/> holds a reference.</summary>
        public static bool Contains(string text) => Reference.IsMatch(text);

        /// <summary>A random id that <paramref name="taken"/> does not claim.</summary>
        public static string NewId(Func<string, bool> taken)
        {
            Span<char> chars = stackalloc char[IdLength];
            while (true)
            {
                for (int i = 0; i < IdLength; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                string id = new(chars);
                if (!taken(id)) return id;
            }
        }
    }
}
```

- [ ] **Step 4: Implement `Services/Pad/SecretScrubber.cs`**

```csharp
using System;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Finds and replaces every exact copy of a secret, so storing it leaves no copy behind in a note or its versions.</summary>
    public static class SecretScrubber
    {
        /// <summary>How many times <paramref name="value"/> appears in <paramref name="text"/>: exact (ordinal), without overlaps.</summary>
        public static int Count(string text, string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("The value to find is empty.", nameof(value));

            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        /// <summary>
        /// <paramref name="text"/> with every exact copy of <paramref name="value"/> replaced by
        /// <paramref name="reference"/>; the same instance when there is none.
        /// </summary>
        public static string Replace(string text, string value, string reference, out int count)
        {
            count = Count(text, value);
            if (count == 0) return text;

            var result = new StringBuilder(text.Length + count * (reference.Length - value.Length));
            int start = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, start, StringComparison.Ordinal))
            {
                result.Append(text, start, at - start).Append(reference);
                start = at + value.Length;
            }
            return result.Append(text, start, text.Length - start).ToString();
        }
    }
}
```

- [ ] **Step 5: `NoteStore.ScrubSnapshots`** (in `NoteStore.cs`, after `ReadSnapshot`)

```csharp
        /// <summary>
        /// Rewrites every version of the note that holds <paramref name="value"/>, each copy
        /// replaced by <paramref name="reference"/>. Stamps and file names stay; versions without it
        /// are not touched.
        /// </summary>
        /// <returns>How many versions may still hold it, because they could not be read or rewritten.</returns>
        public int ScrubSnapshots(string id, string value, string reference)
        {
            lock (LockFor(id))
            {
                int failed = 0;
                foreach (var snapshot in ListSnapshots(id))
                {
                    try
                    {
                        string? text = ReadStoreText(snapshot.FilePath);
                        if (text == null) continue;

                        string scrubbed = SecretScrubber.Replace(text, value, reference, out int count);
                        if (count > 0) WriteData(snapshot.FilePath, Utf8NoBom.GetBytes(scrubbed));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _warn("Could not remove a stored credential from a version of note " + id + ": " + ex.Message);
                        failed++;
                    }
                }
                return failed;
            }
        }
```

- [ ] **Step 6: Run to verify they pass**, then the full suite.

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/SecretTokens.cs Services/Pad/SecretScrubber.cs Services/Pad/NoteStore.cs tests/Kil0bitSystemMonitor.Tests/SecretTokensTests.cs tests/Kil0bitSystemMonitor.Tests/SecretScrubberTests.cs tests/Kil0bitSystemMonitor.Tests/PadEncryptionTests.cs
git commit -m "feat(pad): secret references, scrubbing a value from a note's versions" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 6: CredentialVault

**Files:**
- Create: `Services/Pad/CredentialVault.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/CredentialVaultTests.cs`

**Interfaces:**
- Consumes: `AtomicFile` (Tasks 2, 4), `SecretTokens.NewId`/`IsId` (Task 5).
- Produces (Part 3 builds on exactly these):
  - `public sealed record CredentialInfo(string Id, string Label, DateTime CreatedUtc, string? NoteId)`
  - `public enum UnlockOutcome { Unlocked, WrongPin, Waiting, NoVault }`
  - `public sealed record UnlockResult(UnlockOutcome Outcome, int TriesBeforeWait = 0, DateTime? WaitUntilUtc = null)`
  - `public enum VaultLoadStatus { Missing, Ready, MovedAside }`
  - `public sealed class CredentialVault : IDisposable` — constants `ProductionRounds = 600_000`, `MinPinLength = 6`, `MaxPinLength = 12`, `TriesBeforeFirstWait = 5`, `MaxLabelLength = 60`, `MaxSecretLength = 65536`, `FileName = "vault.bin"`, `UnlockDuration` (5 min), `FirstWait` (30 s), `LongestWait` (15 min); ctor `(string path, Func<DateTime> utcNow, int rounds = ProductionRounds, Action<string>? warn = null)`; `string FilePath`, `int Rounds`, `string? MovedAsideTo`, `event EventHandler? Changed`, `bool Exists`, `IReadOnlyList<CredentialInfo> Credentials`, `CredentialInfo? Find(string id)`, `bool IsUnlocked`, `DateTime? UnlockedUntilUtc`, `VaultLoadStatus Load()`, `void Create(string pin)`, `string Add(string secret, string? label, string? noteId)`, `void Rename(string id, string? label)`, `UnlockResult Unlock(string pin)`, `string Reveal(string id)`, `void Delete(string id)`, `UnlockResult ChangePin(string currentPin, string newPin)`, `void Reset()`, `void Lock()`, `static bool IsValidPin(string? pin)`, `static TimeSpan? WaitAfter(int failedAttempts)`.

**Contingency (record as a ruling if it happens):** if `ImportEncryptedPkcs8PrivateKey` refuses 600,000 rounds (`The_production_rounds_are_600000_and_work` fails with an iteration-limit error), store the private key as `ExportPkcs8PrivateKey()` bytes sealed with AES-256-GCM under `Rfc2898DeriveBytes.Pbkdf2(pin, salt[16], 600000, SHA256, 32)`, adding a `"salt"` field to the JSON; every test stays as written.

- [ ] **Step 1: Write the failing tests** — `tests/Kil0bitSystemMonitor.Tests/CredentialVaultTests.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The credential vault: store without the PIN, reveal with it, wrong PINs wait.</summary>
    public class CredentialVaultTests : IDisposable
    {
        private const int TestRounds = 1000;
        private const string Pin = "246810";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.Vault.v1");
        private readonly PadTempDir _dir = new();
        private readonly FakeClock _clock = new();
        private readonly List<string> _warnings = new();

        public void Dispose() => _dir.Dispose();

        private string VaultPath => Path.Combine(_dir.Root, CredentialVault.FileName);

        private CredentialVault New() => new(VaultPath, () => _clock.UtcNow, TestRounds, _warnings.Add);

        private CredentialVault Loaded()
        {
            var vault = New();
            vault.Load();
            return vault;
        }

        private CredentialVault Created()
        {
            var vault = Loaded();
            vault.Create(Pin);
            return vault;
        }

        private static byte[] Unseal(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);

        private static byte[] Seal(byte[] json) => ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

        [Fact]
        public void Without_a_file_there_is_no_vault()
        {
            var vault = New();

            Assert.Equal(VaultLoadStatus.Missing, vault.Load());
            Assert.False(vault.Exists);
            Assert.Empty(vault.Credentials);
            Assert.Equal(UnlockOutcome.NoVault, vault.Unlock(Pin).Outcome);
            Assert.False(File.Exists(VaultPath));
        }

        [Fact]
        public void A_stored_value_comes_back_only_with_the_pin()
        {
            var vault = Created();
            string secret = "p@ss \u0E23\u0E2B\u0E31\u0E2A\r\nline two";

            string id = vault.Add(secret, "Bank", "note-1");

            Assert.True(SecretTokens.IsId(id));
            Assert.Equal(new CredentialInfo(id, "Bank", _clock.UtcNow, "note-1"), Assert.Single(vault.Credentials));
            Assert.False(vault.IsUnlocked);
            Assert.Throws<InvalidOperationException>(() => vault.Reveal(id));

            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            Assert.Equal(secret, vault.Reveal(id));
        }

        [Fact]
        public void A_fresh_vault_object_reads_the_file_back()
        {
            string id = Created().Add("s3cret!", "Label", null);

            var again = New();
            Assert.Equal(VaultLoadStatus.Ready, again.Load());
            Assert.Equal("Label", again.Find(id)!.Label);
            again.Unlock(Pin);
            Assert.Equal("s3cret!", again.Reveal(id));
        }

        [Fact]
        public void Wrong_pins_count_down_then_wait_doubling_up_to_fifteen_minutes()
        {
            var vault = Created();
            for (int left = 4; left >= 1; left--)
                Assert.Equal(new UnlockResult(UnlockOutcome.WrongPin, left), vault.Unlock("000000"));

            var fifth = vault.Unlock("000000");
            Assert.Equal(UnlockOutcome.WrongPin, fifth.Outcome);
            Assert.Equal(_clock.UtcNow.AddSeconds(30), fifth.WaitUntilUtc);

            // During a wait even the right PIN is not checked, and nothing counts.
            Assert.Equal(new UnlockResult(UnlockOutcome.Waiting, 0, _clock.UtcNow.AddSeconds(30)), vault.Unlock(Pin));

            double wait = 30;
            foreach (double expected in new double[] { 60, 120, 240, 480, 900, 900 })
            {
                _clock.Advance(wait);
                var result = vault.Unlock("000000");
                Assert.Equal(UnlockOutcome.WrongPin, result.Outcome);
                Assert.Equal(_clock.UtcNow.AddSeconds(expected), result.WaitUntilUtc);
                wait = expected;
            }

            _clock.Advance(wait);
            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            vault.Lock();
            Assert.Equal(new UnlockResult(UnlockOutcome.WrongPin, 4), vault.Unlock("000000"));   // the right PIN restarted the count
        }

        [Theory]
        [InlineData(1, null)]
        [InlineData(4, null)]
        [InlineData(5, 30)]
        [InlineData(6, 60)]
        [InlineData(9, 480)]
        [InlineData(10, 900)]
        [InlineData(50, 900)]
        public void The_wait_schedule(int failed, int? seconds) =>
            Assert.Equal(seconds == null ? null : TimeSpan.FromSeconds(seconds.Value), CredentialVault.WaitAfter(failed));

        [Fact]
        public void A_wait_survives_a_restart()
        {
            var vault = Created();
            for (int i = 0; i < 5; i++) vault.Unlock("000000");

            Assert.Equal(UnlockOutcome.Waiting, Loaded().Unlock(Pin).Outcome);
        }

        [Fact]
        public void An_unlock_lasts_five_minutes()
        {
            var vault = Created();
            string id = vault.Add("x1234", null, null);
            vault.Unlock(Pin);

            Assert.Equal(_clock.UtcNow.AddMinutes(5), vault.UnlockedUntilUtc);
            _clock.Advance(299);
            Assert.Equal("x1234", vault.Reveal(id));
            _clock.Advance(1);
            Assert.False(vault.IsUnlocked);
            Assert.Null(vault.UnlockedUntilUtc);
            Assert.Throws<InvalidOperationException>(() => vault.Reveal(id));
        }

        [Fact]
        public void Lock_now_locks_at_once_and_says_so_once()
        {
            var vault = Created();
            vault.Unlock(Pin);
            int changed = 0;
            vault.Changed += (s, e) => changed++;

            vault.Lock();
            vault.Lock();

            Assert.False(vault.IsUnlocked);
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Changing_the_pin_keeps_every_value()
        {
            var vault = Created();
            string a = vault.Add("first secret", "A", null);
            string b = vault.Add("second secret", "B", null);

            Assert.Throws<ArgumentException>(() => vault.ChangePin(Pin, "12"));
            Assert.Equal(UnlockOutcome.WrongPin, vault.ChangePin("111111", "13579135").Outcome);
            Assert.Equal(UnlockOutcome.Unlocked, vault.ChangePin(Pin, "13579135").Outcome);

            var again = Loaded();
            Assert.Equal(UnlockOutcome.WrongPin, again.Unlock(Pin).Outcome);
            Assert.Equal(UnlockOutcome.Unlocked, again.Unlock("13579135").Outcome);
            Assert.Equal("first secret", again.Reveal(a));
            Assert.Equal("second secret", again.Reveal(b));
        }

        [Fact]
        public void Reset_deletes_every_credential()
        {
            var vault = Created();
            vault.Add("gone soon", null, null);

            vault.Reset();

            Assert.False(vault.Exists);
            Assert.Empty(vault.Credentials);
            Assert.False(File.Exists(VaultPath));
            Assert.False(Loaded().Exists);
        }

        [Fact]
        public void Delete_needs_the_vault_unlocked()
        {
            var vault = Created();
            string id = vault.Add("delete me", null, null);

            Assert.Throws<InvalidOperationException>(() => vault.Delete(id));
            vault.Unlock(Pin);
            vault.Delete(id);

            Assert.Null(vault.Find(id));
            Assert.Null(Loaded().Find(id));
        }

        [Fact]
        public void Labels_are_trimmed_kept_on_one_line_and_capped()
        {
            var vault = Created();
            string id = vault.Add("value", "  two\r\nlines\t", null);
            Assert.Equal("two lines", vault.Find(id)!.Label);

            vault.Rename(id, new string('x', 80));
            Assert.Equal(new string('x', 60), vault.Find(id)!.Label);

            vault.Rename(id, null);
            Assert.Equal("", vault.Find(id)!.Label);
            Assert.Throws<KeyNotFoundException>(() => vault.Rename("ZZZZZZZZ", "x"));
        }

        [Fact]
        public void Nothing_readable_is_on_disk()
        {
            Created().Add("hunter2-value", "Bank login", null);

            byte[] onDisk = File.ReadAllBytes(VaultPath);
            Assert.True(onDisk.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Bank login")) < 0);   // the DPAPI layer hides even labels
            string json = Encoding.UTF8.GetString(Unseal(onDisk));
            Assert.Contains("\"label\":\"Bank login\"", json);
            Assert.DoesNotContain("hunter2", json);
        }

        [Fact]
        public void A_value_cannot_be_moved_to_another_id()
        {
            var vault = Created();
            string a = vault.Add("value of a", null, null);
            string b = vault.Add("value of b", null, null);

            var doc = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            var entries = doc["credentials"]!.AsArray();
            entries[0]!["id"] = b;
            entries[1]!["id"] = a;
            File.WriteAllBytes(VaultPath, Seal(Encoding.UTF8.GetBytes(doc.ToJsonString())));

            var again = Loaded();
            again.Unlock(Pin);
            Assert.ThrowsAny<CryptographicException>(() => again.Reveal(a));
        }

        [Fact]
        public void A_private_key_from_another_vault_does_not_unlock()
        {
            using var otherDir = new PadTempDir();
            var other = new CredentialVault(Path.Combine(otherDir.Root, CredentialVault.FileName), () => _clock.UtcNow, TestRounds);
            other.Load();
            other.Create(Pin);
            Created();

            var mine = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            var theirs = JsonNode.Parse(Unseal(File.ReadAllBytes(other.FilePath)))!;
            mine["privateKey"] = theirs["privateKey"]!.GetValue<string>();
            File.WriteAllBytes(VaultPath, Seal(Encoding.UTF8.GetBytes(mine.ToJsonString())));

            Assert.Equal(UnlockOutcome.WrongPin, Loaded().Unlock(Pin).Outcome);
        }

        [Fact]
        public void A_vault_this_account_cannot_open_is_moved_aside()
        {
            File.WriteAllBytes(VaultPath, new byte[] { 1, 2, 3, 4 });
            var vault = New();

            Assert.Equal(VaultLoadStatus.MovedAside, vault.Load());

            Assert.False(vault.Exists);
            Assert.False(File.Exists(VaultPath));
            Assert.StartsWith("vault-locked-", Path.GetFileName(vault.MovedAsideTo));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(vault.MovedAsideTo!));
            Assert.Contains(_warnings, w => w.Contains(vault.MovedAsideTo!, StringComparison.Ordinal));
        }

        [Fact]
        public void A_vault_file_held_open_elsewhere_throws_and_is_not_moved()
        {
            Created();
            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => New().Load());
            }

            Assert.True(File.Exists(VaultPath));
            Assert.True(Loaded().Exists);
        }

        [Fact]
        public void A_failed_save_changes_nothing_in_memory_or_on_disk()
        {
            var vault = Created();
            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => vault.Add("not stored", null, null));
            }

            Assert.Empty(vault.Credentials);
            Assert.False(File.Exists(VaultPath + AtomicFile.ReadySuffix));
            Assert.Empty(Loaded().Credentials);
        }

        [Theory]
        [InlineData("123456", true)]
        [InlineData("123456789012", true)]
        [InlineData("12345", false)]
        [InlineData("1234567890123", false)]
        [InlineData("12345a", false)]
        [InlineData(" 123456", false)]
        [InlineData("\u0E51\u0E52\u0E53\u0E54\u0E55\u0E56", false)]   // Thai digits
        [InlineData("\u0661\u0662\u0663\u0664\u0665\u0666", false)]   // Arabic-Indic digits
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Pins_are_six_to_twelve_ascii_digits(string? pin, bool valid) =>
            Assert.Equal(valid, CredentialVault.IsValidPin(pin));

        [Fact]
        public void Create_refuses_a_bad_pin_and_a_second_vault()
        {
            var vault = Loaded();
            Assert.Throws<ArgumentException>(() => vault.Create("123"));

            vault.Create(Pin);

            Assert.Throws<InvalidOperationException>(() => vault.Create(Pin));
            Assert.Throws<InvalidOperationException>(() => New().Create("999999"));   // not loaded, but the file exists
        }

        [Fact]
        public void Add_refuses_empty_and_oversized_values()
        {
            var vault = Created();

            Assert.Throws<ArgumentException>(() => vault.Add("", null, null));
            Assert.Throws<ArgumentException>(() => vault.Add(new string('x', CredentialVault.MaxSecretLength + 1), null, null));
            Assert.True(SecretTokens.IsId(vault.Add(new string('x', CredentialVault.MaxSecretLength), null, null)));
        }

        [Fact]
        public void Adding_never_needs_the_pin_and_ids_are_unique()
        {
            var vault = Created();

            var ids = Enumerable.Range(0, 50).Select(i => vault.Add("v" + i, null, null)).ToList();

            Assert.Equal(50, ids.Distinct().Count());
            Assert.False(vault.IsUnlocked);
        }

        [Fact]
        public void Changes_are_announced()
        {
            var vault = Created();
            int changed = 0;
            vault.Changed += (s, e) => changed++;

            string id = vault.Add("x", null, null);
            vault.Rename(id, "y");
            vault.Unlock(Pin);
            vault.Delete(id);

            Assert.Equal(4, changed);
        }

        [Fact]
        public void The_production_rounds_are_600000_and_work()
        {
            Assert.Equal(600_000, CredentialVault.ProductionRounds);
            var vault = new CredentialVault(VaultPath, () => _clock.UtcNow);
            Assert.Equal(600_000, vault.Rounds);

            vault.Load();
            vault.Create(Pin);
            string id = vault.Add("slow but sure", null, null);

            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            Assert.Equal("slow but sure", vault.Reveal(id));
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~CredentialVaultTests" 2>&1 | tail -5`
Expected: build error.

- [ ] **Step 3: Implement `Services/Pad/CredentialVault.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One stored credential as anyone may see it: never its value.</summary>
    public sealed record CredentialInfo(string Id, string Label, DateTime CreatedUtc, string? NoteId);

    /// <summary>What a PIN did.</summary>
    public enum UnlockOutcome
    {
        /// <summary>The PIN was right.</summary>
        Unlocked,

        /// <summary>The PIN was wrong; it counts toward the wait.</summary>
        WrongPin,

        /// <summary>A wait is running: the PIN was not checked and does not count.</summary>
        Waiting,

        /// <summary>There is no vault yet.</summary>
        NoVault,
    }

    /// <summary>
    /// The outcome of a PIN. <see cref="TriesBeforeWait"/>: wrong PINs left before a wait starts.
    /// <see cref="WaitUntilUtc"/>: when the running wait ends — set for Waiting, and for WrongPin
    /// when this PIN started a wait.
    /// </summary>
    public sealed record UnlockResult(UnlockOutcome Outcome, int TriesBeforeWait = 0, DateTime? WaitUntilUtc = null);

    /// <summary>What <see cref="CredentialVault.Load"/> found.</summary>
    public enum VaultLoadStatus
    {
        /// <summary>No vault yet.</summary>
        Missing,

        /// <summary>Loaded.</summary>
        Ready,

        /// <summary>This Windows account cannot open it; renamed to <see cref="CredentialVault.MovedAsideTo"/>.</summary>
        MovedAside,
    }

    /// <summary>
    /// MicaPad's credentials, in <c>vault.bin</c> beside the notes.
    ///
    /// <para>
    /// Storing needs no PIN, revealing does: each value is sealed with its own AES-256-GCM key
    /// (associated data: its id, so a value cannot be moved to another id), and that key is
    /// wrapped with the vault's RSA-3072 public key (OAEP-SHA256). The private key is kept as
    /// encrypted PKCS#8 under the PIN (PBKDF2-HMAC-SHA256, <see cref="ProductionRounds"/> rounds,
    /// AES-256-CBC). The whole file is the vault's JSON protected by DPAPI for the current Windows
    /// user, so even ids and labels are unreadable elsewhere.
    /// </para>
    ///
    /// <para>
    /// A right PIN keeps the private key in memory for <see cref="UnlockDuration"/>. From the
    /// <see cref="TriesBeforeFirstWait"/>th wrong PIN in a row the vault refuses to check a PIN for
    /// <see cref="WaitAfter"/>; the count and the wait are saved, so a restart does not reset them.
    /// </para>
    ///
    /// <para>
    /// Every change is saved before it takes effect in memory; a failed save changes nothing.
    /// Values, PINs and keys never reach a warning or an exception message.
    /// </para>
    /// </summary>
    public sealed class CredentialVault : IDisposable
    {
        /// <summary>PBKDF2 rounds guarding the private key.</summary>
        public const int ProductionRounds = 600_000;

        /// <summary>Shortest PIN.</summary>
        public const int MinPinLength = 6;

        /// <summary>Longest PIN.</summary>
        public const int MaxPinLength = 12;

        /// <summary>Wrong PINs in a row before the first wait.</summary>
        public const int TriesBeforeFirstWait = 5;

        /// <summary>Longest label.</summary>
        public const int MaxLabelLength = 60;

        /// <summary>Longest value, in characters.</summary>
        public const int MaxSecretLength = 64 * 1024;

        /// <summary>The vault's file name in the MicaPad folder.</summary>
        public const string FileName = "vault.bin";

        /// <summary>How long a right PIN keeps the vault unlocked.</summary>
        public static readonly TimeSpan UnlockDuration = TimeSpan.FromMinutes(5);

        /// <summary>The wait after the 5th wrong PIN.</summary>
        public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);

        /// <summary>The longest wait.</summary>
        public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);

        private const int KeyBits = 3072;
        private const int AesKeyLength = 32;
        private const int NonceLength = 12;
        private const int TagLength = 16;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.Vault.v1");
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly object _gate = new();
        private readonly Func<DateTime> _utcNow;
        private readonly Action<string> _warn;
        private VaultDocument? _doc;
        private RSA? _privateKey;
        private DateTime _unlockedUntilUtc;

        /// <param name="path">The vault file; its folder is created on the first save.</param>
        /// <param name="utcNow">The clock for waits, the unlock window and creation times.</param>
        /// <param name="rounds">PBKDF2 rounds for a new or changed PIN; tests pass fewer.</param>
        /// <param name="warn">Told about a vault moved aside; never about values.</param>
        public CredentialVault(string path, Func<DateTime> utcNow, int rounds = ProductionRounds, Action<string>? warn = null)
        {
            FilePath = path ?? throw new ArgumentNullException(nameof(path));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            if (rounds < 1) throw new ArgumentOutOfRangeException(nameof(rounds));
            Rounds = rounds;
            _warn = warn ?? (message => DiagnosticsLog.Warn("pad", message));
        }

        /// <summary>The vault file.</summary>
        public string FilePath { get; }

        /// <summary>PBKDF2 rounds for a new or changed PIN.</summary>
        public int Rounds { get; }

        /// <summary>Where the last <see cref="Load"/> moved a vault this account could not open, or null.</summary>
        public string? MovedAsideTo { get; private set; }

        /// <summary>After any change of the credentials or the lock, on the thread that made it.</summary>
        public event EventHandler? Changed;

        /// <summary>Whether there is a vault (a PIN has been created).</summary>
        public bool Exists
        {
            get { lock (_gate) return _doc != null; }
        }

        /// <summary>Every credential, without values, oldest first.</summary>
        public IReadOnlyList<CredentialInfo> Credentials
        {
            get
            {
                lock (_gate) return _doc == null ? Array.Empty<CredentialInfo>() : _doc.Credentials.Select(Info).ToList();
            }
        }

        /// <summary>Whether a right PIN was entered less than <see cref="UnlockDuration"/> ago and the vault was not locked since.</summary>
        public bool IsUnlocked
        {
            get { lock (_gate) return UnlockedKey() != null; }
        }

        /// <summary>When the unlock ends, or null while locked.</summary>
        public DateTime? UnlockedUntilUtc
        {
            get { lock (_gate) return UnlockedKey() != null ? _unlockedUntilUtc : null; }
        }

        /// <summary>Whether <paramref name="pin"/> is 6 to 12 ASCII digits.</summary>
        public static bool IsValidPin(string? pin) =>
            pin != null && pin.Length >= MinPinLength && pin.Length <= MaxPinLength && pin.All(char.IsAsciiDigit);

        /// <summary>The wait after this many wrong PINs in a row: none before the 5th, then 30 s doubling to at most 15 minutes.</summary>
        public static TimeSpan? WaitAfter(int failedAttempts)
        {
            if (failedAttempts < TriesBeforeFirstWait) return null;
            var wait = TimeSpan.FromTicks(FirstWait.Ticks << Math.Min(failedAttempts - TriesBeforeFirstWait, 10));
            return wait < LongestWait ? wait : LongestWait;
        }

        /// <summary>The credential with this id, or null.</summary>
        public CredentialInfo? Find(string id)
        {
            lock (_gate)
            {
                var entry = _doc?.Credentials.FirstOrDefault(c => c.Id == id);
                return entry == null ? null : Info(entry);
            }
        }

        /// <summary>
        /// Reads the vault file. One this Windows account cannot open (DPAPI refuses it, or it is
        /// damaged) is renamed to <c>vault-locked-yyyyMMdd-HHmmss.bin</c> beside it, never deleted.
        /// </summary>
        /// <exception cref="IOException">The file exists but cannot be read right now; try again later.</exception>
        public VaultLoadStatus Load()
        {
            lock (_gate)
            {
                DropKey();
                _doc = null;
                MovedAsideTo = null;

                byte[]? sealedBytes = AtomicFile.ReadBytes(FilePath);
                if (sealedBytes == null) return VaultLoadStatus.Missing;

                try
                {
                    var doc = JsonSerializer.Deserialize<VaultDocument>(
                        ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser), Json);
                    if (doc != null && doc.IsValid())
                    {
                        _doc = doc;
                        return VaultLoadStatus.Ready;
                    }
                }
                catch (Exception ex) when (ex is CryptographicException or JsonException)
                {
                    // Falls through to moving it aside.
                }

                MovedAsideTo = MoveAside();
                _warn("The credential vault could not be opened by this Windows account; it was moved to " + MovedAsideTo);
                return VaultLoadStatus.MovedAside;
            }
        }

        /// <summary>Creates the vault with its PIN. Refused when a vault exists, loaded or not.</summary>
        public void Create(string pin)
        {
            if (!IsValidPin(pin)) throw new ArgumentException("A PIN is 6 to 12 digits.", nameof(pin));
            lock (_gate)
            {
                if (_doc != null || File.Exists(FilePath)) throw new InvalidOperationException("The vault already exists.");

                using var rsa = RSA.Create(KeyBits);
                Save(new VaultDocument
                {
                    PublicKey = rsa.ExportSubjectPublicKeyInfo(),
                    PrivateKey = rsa.ExportEncryptedPkcs8PrivateKey(pin.AsSpan(), Pbe()),
                });
            }
            OnChanged();
        }

        /// <summary>Stores a value without the PIN and returns its new id.</summary>
        public string Add(string secret, string? label, string? noteId)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length > MaxSecretLength)
                throw new ArgumentException("A value is 1 to " + MaxSecretLength + " characters.", nameof(secret));

            string id;
            lock (_gate)
            {
                var doc = Require();
                id = SecretTokens.NewId(candidate => doc.Credentials.Any(c => c.Id == candidate));

                byte[] key = RandomNumberGenerator.GetBytes(AesKeyLength);
                byte[] plain = Encoding.UTF8.GetBytes(secret);
                try
                {
                    var entry = new VaultEntry
                    {
                        Id = id,
                        Label = CleanLabel(label),
                        CreatedUtc = _utcNow(),
                        NoteId = noteId,
                        Nonce = RandomNumberGenerator.GetBytes(NonceLength),
                        Cipher = new byte[plain.Length],
                        Tag = new byte[TagLength],
                    };
                    using (var aes = new AesGcm(key, TagLength))
                        aes.Encrypt(entry.Nonce, plain, entry.Cipher, entry.Tag, Encoding.UTF8.GetBytes(id));
                    using (var rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(doc.PublicKey, out _);
                        entry.WrappedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
                    }

                    var next = doc.Clone();
                    next.Credentials.Add(entry);
                    Save(next);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            OnChanged();
            return id;
        }

        /// <summary>Changes a label; no PIN needed, labels are not secret.</summary>
        public void Rename(string id, string? label)
        {
            lock (_gate)
            {
                var next = Require().Clone();
                (next.Credentials.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("No credential " + id)).Label = CleanLabel(label);
                Save(next);
            }
            OnChanged();
        }

        /// <summary>Checks a PIN under the wrong-PIN rules; a right one unlocks for <see cref="UnlockDuration"/>.</summary>
        public UnlockResult Unlock(string pin)
        {
            UnlockResult result;
            lock (_gate)
            {
                result = Check(pin, out RSA? key);
                if (key != null)
                {
                    DropKey();
                    _privateKey = key;
                    _unlockedUntilUtc = _utcNow() + UnlockDuration;
                }
            }
            OnChanged();
            return result;
        }

        /// <summary>A value. Only while unlocked.</summary>
        /// <exception cref="InvalidOperationException">The vault is locked.</exception>
        /// <exception cref="KeyNotFoundException">No credential has this id.</exception>
        /// <exception cref="CryptographicException">The stored value was changed and does not decrypt.</exception>
        public string Reveal(string id)
        {
            lock (_gate)
            {
                var key = UnlockedKey() ?? throw new InvalidOperationException("The vault is locked.");
                var entry = _doc!.Credentials.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("No credential " + id);

                byte[] aesKey = key.Decrypt(entry.WrappedKey, RSAEncryptionPadding.OaepSHA256);
                byte[] plain = new byte[entry.Cipher.Length];
                try
                {
                    using var aes = new AesGcm(aesKey, TagLength);
                    aes.Decrypt(entry.Nonce, entry.Cipher, entry.Tag, plain, Encoding.UTF8.GetBytes(entry.Id));
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(aesKey);
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
        }

        /// <summary>Deletes a credential. Only while unlocked, so nobody at an unlocked PC can wipe credentials without the PIN.</summary>
        public void Delete(string id)
        {
            lock (_gate)
            {
                if (UnlockedKey() == null) throw new InvalidOperationException("The vault is locked.");
                var next = _doc!.Clone();
                if (next.Credentials.RemoveAll(c => c.Id == id) == 0) throw new KeyNotFoundException("No credential " + id);
                Save(next);
            }
            OnChanged();
        }

        /// <summary>Re-seals the private key under a new PIN. The current PIN is checked under the wrong-PIN rules.</summary>
        public UnlockResult ChangePin(string currentPin, string newPin)
        {
            if (!IsValidPin(newPin)) throw new ArgumentException("A PIN is 6 to 12 digits.", nameof(newPin));

            UnlockResult result;
            lock (_gate)
            {
                result = Check(currentPin, out RSA? key);
                if (key != null)
                {
                    using (key)
                    {
                        var next = _doc!.Clone();
                        next.PrivateKey = key.ExportEncryptedPkcs8PrivateKey(newPin.AsSpan(), Pbe());
                        Save(next);
                    }
                }
            }
            OnChanged();
            return result;
        }

        /// <summary>Deletes the vault and every credential: the way out of a forgotten PIN.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                DropKey();
                foreach (string path in new[] { FilePath, FilePath + AtomicFile.ReadySuffix, FilePath + AtomicFile.TempSuffix })
                    if (File.Exists(path)) File.Delete(path);
                _doc = null;
            }
            OnChanged();
        }

        /// <summary>Locks now: the private key leaves memory.</summary>
        public void Lock()
        {
            bool wasUnlocked;
            lock (_gate)
            {
                wasUnlocked = _privateKey != null;
                DropKey();
            }
            if (wasUnlocked) OnChanged();
        }

        /// <inheritdoc />
        public void Dispose() => Lock();

        private UnlockResult Check(string pin, out RSA? key)
        {
            key = null;
            if (_doc == null) return new UnlockResult(UnlockOutcome.NoVault);

            DateTime now = _utcNow();
            if (_doc.LockedUntilUtc is DateTime until && until > now) return new UnlockResult(UnlockOutcome.Waiting, 0, until);

            RSA? opened = IsValidPin(pin) ? OpenPrivateKey(_doc, pin) : null;
            var next = _doc.Clone();
            if (opened != null)
            {
                if (_doc.FailedAttempts != 0 || _doc.LockedUntilUtc != null)
                {
                    next.FailedAttempts = 0;
                    next.LockedUntilUtc = null;
                    try
                    {
                        Save(next);
                    }
                    catch
                    {
                        opened.Dispose();
                        throw;
                    }
                }
                key = opened;
                return new UnlockResult(UnlockOutcome.Unlocked);
            }

            next.FailedAttempts = _doc.FailedAttempts + 1;
            next.LockedUntilUtc = WaitAfter(next.FailedAttempts) is TimeSpan wait ? now + wait : null;
            Save(next);
            return new UnlockResult(UnlockOutcome.WrongPin, Math.Max(0, TriesBeforeFirstWait - next.FailedAttempts), next.LockedUntilUtc);
        }

        /// <summary>The private key under <paramref name="pin"/>, or null when the PIN is wrong or the key is not this vault's.</summary>
        private static RSA? OpenPrivateKey(VaultDocument doc, string pin)
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportEncryptedPkcs8PrivateKey(pin.AsSpan(), doc.PrivateKey, out _);
                if (CryptographicOperations.FixedTimeEquals(rsa.ExportSubjectPublicKeyInfo(), doc.PublicKey)) return rsa;
            }
            catch (Exception ex) when (ex is CryptographicException or System.Formats.Asn1.AsnContentException)
            {
                // A wrong PIN.
            }
            rsa.Dispose();
            return null;
        }

        private PbeParameters Pbe() => new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Rounds);

        private VaultDocument Require() => _doc ?? throw new InvalidOperationException("Create the vault first.");

        private RSA? UnlockedKey()
        {
            if (_privateKey != null && _utcNow() >= _unlockedUntilUtc) DropKey();
            return _privateKey;
        }

        private void DropKey()
        {
            _privateKey?.Dispose();
            _privateKey = null;
        }

        /// <summary>Writes <paramref name="next"/> and only then makes it current. A failed write leaves no <c>.ready</c> behind to resurrect it.</summary>
        private void Save(VaultDocument next)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            string? folder = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (folder != null) Directory.CreateDirectory(folder);
            try
            {
                AtomicFile.Write(FilePath, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
            }
            catch
            {
                try
                {
                    File.Delete(FilePath + AtomicFile.ReadySuffix);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The write's own failure is the one worth reporting.
                }
                throw;
            }
            _doc = next;
        }

        private string MoveAside()
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = Path.Combine(folder, "vault-locked-" + stamp + ".bin");
            for (int n = 2; File.Exists(target); n++)
                target = Path.Combine(folder, "vault-locked-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".bin");
            File.Move(FilePath, target);
            return target;
        }

        private static string CleanLabel(string? label)
        {
            string clean = string.Join(" ", (label ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (clean.Length <= MaxLabelLength) return clean;
            int cut = char.IsHighSurrogate(clean[MaxLabelLength - 1]) ? MaxLabelLength - 1 : MaxLabelLength;
            return clean.Substring(0, cut).TrimEnd();
        }

        private static CredentialInfo Info(VaultEntry entry) => new(entry.Id, entry.Label, entry.CreatedUtc, entry.NoteId);

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><c>vault.bin</c>'s JSON, inside the DPAPI layer.</summary>
    internal sealed class VaultDocument
    {
        public int Version { get; set; } = 1;

        public byte[] PublicKey { get; set; } = Array.Empty<byte>();

        public byte[] PrivateKey { get; set; } = Array.Empty<byte>();

        public int FailedAttempts { get; set; }

        public DateTime? LockedUntilUtc { get; set; }

        public List<VaultEntry> Credentials { get; set; } = new();

        public bool IsValid() =>
            Version == 1 && PublicKey is { Length: > 0 } && PrivateKey is { Length: > 0 } && FailedAttempts >= 0
            && Credentials != null && Credentials.All(c => c != null && c.IsValid())
            && Credentials.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == Credentials.Count;

        public VaultDocument Clone() => new()
        {
            Version = Version,
            PublicKey = PublicKey,
            PrivateKey = PrivateKey,
            FailedAttempts = FailedAttempts,
            LockedUntilUtc = LockedUntilUtc,
            Credentials = Credentials.Select(c => c.Clone()).ToList(),
        };
    }

    /// <summary>One credential in <c>vault.bin</c>.</summary>
    internal sealed class VaultEntry
    {
        public string Id { get; set; } = "";

        public string Label { get; set; } = "";

        public DateTime CreatedUtc { get; set; }

        public string? NoteId { get; set; }

        public byte[] WrappedKey { get; set; } = Array.Empty<byte>();

        public byte[] Nonce { get; set; } = Array.Empty<byte>();

        public byte[] Cipher { get; set; } = Array.Empty<byte>();

        public byte[] Tag { get; set; } = Array.Empty<byte>();

        public bool IsValid() =>
            SecretTokens.IsId(Id) && Label != null && WrappedKey is { Length: > 0 } && Nonce is { Length: 12 }
            && Cipher != null && Tag is { Length: 16 };

        public VaultEntry Clone() => (VaultEntry)MemberwiseClone();
    }
}
```

- [ ] **Step 4: Run to verify they pass**, then the full suite. Verify the `\u` escapes in the test file survived.

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/CredentialVault.cs tests/Kil0bitSystemMonitor.Tests/CredentialVaultTests.cs
git commit -m "feat(pad): CredentialVault - store without a PIN, reveal with it; waits after wrong PINs; DPAPI-sealed vault.bin" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

## After this plan

- Release notes for the version that ships this (not now): "Downgrading MicaStats after this version is not supported: older versions cannot read MicaPad's encrypted notes."
- Next: write `docs/superpowers/plans/2026-10-01-micapad-vault-part3-4.md` (editor: Store as credential, pill, pill menu, PIN dialogs, reveal popup, clipboard, history scrubbing; Settings credentials group; GUIDE.md), from spec Parts 3–4, against the `CredentialVault`/`SecretTokens`/`SecretScrubber`/`ScrubSnapshots` interfaces above.
