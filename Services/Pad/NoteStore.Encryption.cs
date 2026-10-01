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
        internal string? ReadStoreText(string path) => TextOf(path, AtomicFile.ReadBytes(path));

        /// <summary>
        /// Like <see cref="ReadStoreText"/> but never commits a finished write: a <c>.ready</c> is read
        /// where it lies. For readers that run without the note's lock.
        /// </summary>
        private string? ReadStoreTextInPlace(string path)
        {
            string ready = path + AtomicFile.ReadySuffix;
            byte[]? bytes = File.Exists(ready) ? File.ReadAllBytes(ready) : File.Exists(path) ? File.ReadAllBytes(path) : null;
            return TextOf(path, bytes);
        }

        private string? TextOf(string path, byte[]? bytes)
        {
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
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return stream.ReadByte() == 0xFF ? file.Length - StoreCipher.Overhead : file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return file.Length;
            }
        }

        /// <summary>
        /// Whether any file holding notes, versions or the session is encrypted. A file that cannot
        /// be read right now throws, so the store is opened again later rather than moved aside or
        /// given a second key.
        /// </summary>
        private bool AnyEncryptedFile()
        {
            foreach (string path in DataFiles())
            {
                foreach (string file in new[] { path, path + AtomicFile.ReadySuffix })
                {
                    if (!File.Exists(file)) continue;
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.ReadByte() == 0xFF) return true;
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
            byte[]? text = null;
            try
            {
                bytes = AtomicFile.ReadBytes(path);   // finishes an interrupted write first
                if (bytes == null || StoreCipher.IsEncrypted(bytes)) return false;

                // The decoded text, so a BOM an earlier version wrote does not come back as U+FEFF.
                text = Utf8NoBom.GetBytes(ReadPlain(bytes));
                AtomicFile.Write(path, _cipher.Encrypt(text), beforeReplace: () => WipePlain(path));
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
                if (text != null) CryptographicOperations.ZeroMemory(text);
            }
        }

        /// <summary>Best effort: zeros over a plain file before its encrypted copy replaces it. An SSD may still keep the old blocks.</summary>
        private void WipePlain(string path)
        {
            try
            {
                // Only while the encrypted copy still waits as .ready: if anything committed it early,
                // the target is the encrypted file and must not be zeroed.
                if (File.Exists(path + AtomicFile.ReadySuffix)) ZeroFill(path);
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
    }
}
