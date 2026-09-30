using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The API keys and the MCP HTTP token, encrypted for the current Windows user.
    ///
    /// <para>
    /// DPAPI with <see cref="DataProtectionScope.CurrentUser"/> and a MicaStats-specific entropy:
    /// only this Windows account on this PC can read the file back, so a copied
    /// <c>secrets.bin</c> is useless elsewhere, and nothing secret ever reaches
    /// <c>config.json</c>, which people attach to bug reports. The file is the base64 text of the
    /// protected bytes of a small JSON object, written through <see cref="AtomicFile"/> so a crash
    /// cannot leave half of it.
    /// </para>
    ///
    /// <para>
    /// Never throws. A failure goes to <c>warn</c> once per kind, and a value is never part of a
    /// message.
    /// </para>
    ///
    /// <para>
    /// Nothing is cached: every call reads the file again, and <see cref="Set"/> and
    /// <see cref="Remove"/> read, change and write it under one lock shared by every instance on
    /// the same path. The app creates short-lived stores (one per MCP HTTP request) while Settings
    /// and the Ask window hold their own, and none of them may drop another's entry.
    /// </para>
    /// </summary>
    public sealed class SecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.Secrets.v1");
        private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly Action<string> _warn;
        private readonly object _gate;
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

        /// <param name="path">The file; its folder is created on the first save.</param>
        /// <param name="warn">Told about failures, never about values.</param>
        public SecretStore(string path, Action<string>? warn = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _warn = warn ?? (_ => { });
            _gate = Gates.GetOrAdd(GateKey(path), _ => new object());
        }

        /// <summary><c>%APPDATA%\MicaStats\secrets.bin</c>, beside <c>config.json</c>.</summary>
        public static string DefaultPath => Path.Combine(DiagnosticsLog.DataDir, "secrets.bin");

        /// <summary>Whether a value is saved under <paramref name="name"/>; Settings shows "Saved" from this.</summary>
        public bool Has(string name) => Get(name) != null;

        /// <summary>
        /// Whether the file can be read right now. <see cref="Has"/> is false both for "nothing
        /// saved" and for a locked file, so Settings asks this before it believes a removal worked.
        /// </summary>
        public bool CanRead()
        {
            lock (_gate) return Load() != null;
        }

        /// <summary>The saved value, or null when there is none or the file cannot be read.</summary>
        public string? Get(string name)
        {
            lock (_gate)
            {
                var all = Load();
                return all != null && all.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value)
                    ? value
                    : null;
            }
        }

        /// <summary>
        /// Saves <paramref name="value"/>, trimmed (a pasted key often carries a space or a line
        /// break). A blank value removes the name instead. When the save fails the old state stays
        /// and <c>warn</c> is told, so <see cref="Has"/> tells the truth afterwards.
        /// </summary>
        public void Set(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Remove(name);
                return;
            }

            lock (_gate)
            {
                var all = Load();
                if (all == null) return;   // the file is there but locked right now: never overwrite it blind
                all[name] = value.Trim();
                Save(all);
            }
        }

        /// <summary>Forgets <paramref name="name"/>; removing the last value deletes the file.</summary>
        public void Remove(string name)
        {
            lock (_gate)
            {
                var all = Load();
                if (all == null || !all.Remove(name)) return;
                Save(all);
            }
        }

        /// <summary>
        /// A fresh bearer token: 32 random bytes as base64url without padding (43 characters), safe
        /// in an HTTP header and a command line.
        /// </summary>
        public static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// Every saved value. Null when the file exists but cannot be opened right now (locked,
        /// access denied), so a write does not replace secrets that are only out of reach for a
        /// moment. A file that can be opened but not decrypted (another account, another PC,
        /// damage) reads as empty, so a new value can replace it.
        /// </summary>
        private Dictionary<string, string>? Load()
        {
            string? text;
            try
            {
                text = AtomicFile.ReadText(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WarnOnce("open", "The saved AI keys could not be opened (" + ex.GetType().Name + ")");
                return null;
            }

            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, string>(StringComparer.Ordinal);

            byte[]? plain = null;
            try
            {
                plain = ProtectedData.Unprotect(Convert.FromBase64String(text.Trim()), Entropy, DataProtectionScope.CurrentUser);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
                _warned.Remove("open");
                _warned.Remove("decrypt");
                return values == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(values, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                WarnOnce("decrypt", "The saved AI keys could not be read (" + ex.GetType().Name + "); enter them again in Settings > AI");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        }

        private void Save(Dictionary<string, string> all)
        {
            byte[]? plain = null;
            try
            {
                if (all.Count == 0)
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    if (File.Exists(_path + AtomicFile.ReadySuffix)) File.Delete(_path + AtomicFile.ReadySuffix);
                }
                else
                {
                    string? folder = Path.GetDirectoryName(Path.GetFullPath(_path));
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    plain = JsonSerializer.SerializeToUtf8Bytes(all);
                    byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                    AtomicFile.Write(_path, Encoding.ASCII.GetBytes(Convert.ToBase64String(protectedBytes)));
                }
                _warned.Remove("write");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                           or NotSupportedException or ArgumentException)
            {
                WarnOnce("write", "The AI keys could not be saved (" + ex.GetType().Name + ")");
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        }

        private void WarnOnce(string kind, string message)
        {
            if (_warned.Add(kind)) _warn(message);
        }

        /// <summary>One lock per file, however the path was spelled.</summary>
        private static string GateKey(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }
}
