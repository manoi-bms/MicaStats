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
    /// <see cref="WaitUntilUtc"/>: when the running wait ends - set for Waiting, and for WrongPin
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
        private string? _movedAsideTo;

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
        public string? MovedAsideTo
        {
            get { lock (_gate) return _movedAsideTo; }
        }

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
                _movedAsideTo = null;

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

                _movedAsideTo = MoveAside();
                _warn("The credential vault could not be opened by this Windows account; it was moved to " + _movedAsideTo);
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
                foreach (string path in new[] { FilePath + AtomicFile.TempSuffix, FilePath + AtomicFile.ReadySuffix, FilePath })
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
            if (_doc.LockedUntilUtc is DateTime saved)
            {
                // A wait saved further ahead than the longest wait came from a clock that was wrong: cap it, and keep the cap.
                if (saved > now + LongestWait)
                {
                    var capped = _doc.Clone();
                    capped.LockedUntilUtc = saved = now + LongestWait;
                    Save(capped);
                }
                if (saved > now) return new UnlockResult(UnlockOutcome.Waiting, 0, saved);
            }

            // Count first and check second: when the count cannot be saved, the PIN is not checked at all.
            var counted = _doc.Clone();
            counted.FailedAttempts = _doc.FailedAttempts + 1;
            counted.LockedUntilUtc = WaitAfter(counted.FailedAttempts) is TimeSpan wait ? now + wait : null;
            Save(counted);

            RSA? opened = IsValidPin(pin) ? OpenPrivateKey(_doc, pin) : null;
            if (opened != null)
            {
                var reset = _doc.Clone();
                reset.FailedAttempts = 0;
                reset.LockedUntilUtc = null;
                try
                {
                    Save(reset);
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }
                key = opened;
                return new UnlockResult(UnlockOutcome.Unlocked);
            }

            return new UnlockResult(UnlockOutcome.WrongPin, Math.Max(0, TriesBeforeFirstWait - counted.FailedAttempts), counted.LockedUntilUtc);
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

        /// <summary>Writes <paramref name="next"/> and only then makes it current. A failed write removes its own <c>.ready</c> so it cannot resurrect the change.</summary>
        private void Save(VaultDocument next)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            string? folder = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (folder != null) Directory.CreateDirectory(folder);
            bool readyIsOurs = false;
            try
            {
                AtomicFile.Write(FilePath, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser), () => readyIsOurs = true);
            }
            catch
            {
                // Only the .ready this save wrote, and only while vault.bin still exists: after a half-done replace
                // the .ready can be the only copy, and an older .ready is not ours to remove.
                try
                {
                    if (readyIsOurs && File.Exists(FilePath)) File.Delete(FilePath + AtomicFile.ReadySuffix);
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
