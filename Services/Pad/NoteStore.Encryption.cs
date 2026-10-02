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
        /// account, both copies of the key lost), or null. Set by the constructor; the first MicaPad
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
                _cipher = new StoreCipher(key!);
                CryptographicOperations.ZeroMemory(key);
                try
                {
                    WriteData(LockedMarkerPath, Utf8NoBom.GetBytes(LockedFolder));   // a later start, in a run with no window yet, still tells the user
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("Could not write " + LockedMarkerPath + ": " + ex.Message);
                }
                return;
            }

            _cipher = new StoreCipher(key!);
            CryptographicOperations.ZeroMemory(key);
            LockedFolder = ReadLockedMarker();
        }

        private string LockedMarkerPath => Path.Combine(Root, "notice-locked.txt");

        /// <summary>The folder an earlier start moved the store to and nobody was told about yet; null when none or unreadable.</summary>
        private string? ReadLockedMarker()
        {
            try
            {
                string? folder = ReadStoreText(LockedMarkerPath);
                return string.IsNullOrWhiteSpace(folder) ? null : folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The user has been told about <see cref="LockedFolder"/>: forgets it, here and on disk, so no later start repeats it.</summary>
        public void ForgetLockedFolder()
        {
            LockedFolder = null;
            try
            {
                File.Delete(LockedMarkerPath);
                File.Delete(LockedMarkerPath + AtomicFile.ReadySuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not remove the locked-folder marker: " + ex.Message);
            }
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

        /// <summary>
        /// Encrypts bytes with the notes key, for MicaPad's own files beside the notes (the search
        /// vectors). Readable back only with <see cref="TryDecryptBytes"/> on this Windows account.
        /// </summary>
        public byte[] EncryptBytes(byte[] plain) => _cipher.Encrypt(plain);

        /// <summary>Decrypts bytes from <see cref="EncryptBytes"/>; false for anything else, plain bytes included.</summary>
        public bool TryDecryptBytes(byte[] data, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            return StoreCipher.IsEncrypted(data) && _cipher.TryDecrypt(data, out plain);
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
            byte[]? bytes = ReadIfExists(ready) ?? ReadIfExists(path);

            if (bytes != null)
                bytes = NewerThanPlain(bytes, () => ReadIfExists(ready), () => ReadIfExists(path));
            return TextOf(path, bytes);
        }

        /// <summary>
        /// After a plain read, the migration may have moved a finished copy to <c>.ready</c> and zeroed
        /// the target. Only evidence of a newer complete copy replaces the first bytes: a <c>.ready</c>,
        /// or a target that now starts encrypted. Zeros in the target are never evidence.
        /// </summary>
        internal static byte[] NewerThanPlain(byte[] first, Func<byte[]?> readReady, Func<byte[]?> readTarget)
        {
            if (StoreCipher.IsEncrypted(first)) return first;

            byte[]? ready = readReady();
            if (ready != null) return ready;

            byte[]? target = readTarget();
            return target != null && StoreCipher.IsEncrypted(target) ? target : first;
        }

        private static byte[]? ReadIfExists(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
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
                return StoreCipher.StartsEncrypted(stream) ? file.Length - StoreCipher.Overhead : file.Length;
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
                    if (StoreCipher.StartsEncrypted(stream)) return true;
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
                DiscardTemp(SessionPath + AtomicFile.TempSuffix);
            }

            foreach (string id in ListNoteIds())
            {
                // One file at a time under the note's lock, so a note with hundreds of versions
                // never keeps the UI from loading it for the whole pass.
                foreach (string path in ListNoteFiles(id))
                {
                    lock (LockFor(id))
                    {
                        if (EncryptIfPlain(path)) encrypted++;
                    }
                }

                foreach (string temp in ListTempFiles(id))
                {
                    lock (LockFor(id))
                    {
                        DiscardTemp(temp);
                    }
                }
            }
            return encrypted;
        }

        // NoteIds and NoteFiles throw on I/O errors (AnyEncryptedFile relies on that, so a locked
        // folder never reads as "no encrypted file"); the pass reports and skips instead.
        private IReadOnlyList<string> ListNoteIds()
        {
            try
            {
                return NoteIds();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not list the notes in " + NotesDir + ": " + ex.Message);
                return Array.Empty<string>();
            }
        }

        private IReadOnlyList<string> ListNoteFiles(string id)
        {
            try
            {
                return NoteFiles(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not list the files of note " + id + ": " + ex.Message);
                return Array.Empty<string>();
            }
        }

        /// <summary>The unfinished writes <see cref="AtomicFile"/> left in a note's folder; nothing ever reads them.</summary>
        private IReadOnlyList<string> ListTempFiles(string id)
        {
            var temps = new List<string>
            {
                CurrentPath(id) + AtomicFile.TempSuffix,
                MetaPath(id) + AtomicFile.TempSuffix,
            };
            try
            {
                if (Directory.Exists(HistoryDir(id)))
                    temps.AddRange(Directory.GetFiles(HistoryDir(id), "*.txt" + AtomicFile.TempSuffix));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not list the unfinished writes of note " + id + ": " + ex.Message);
            }
            return temps;
        }

        /// <summary>Best effort: zeros an unfinished write, which may hold plain text, and deletes it.</summary>
        private void DiscardTemp(string temp)
        {
            try
            {
                if (!File.Exists(temp)) return;
                ZeroFill(temp);
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Could not remove the unfinished write " + temp + ": " + ex.Message);
            }
        }

        /// <summary>Whether the file exists and starts with the encrypted format's magic.</summary>
        private static bool StartsEncrypted(string path)
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return StoreCipher.StartsEncrypted(stream);
        }

        private bool EncryptIfPlain(string path)
        {
            byte[]? bytes = null;
            byte[]? text = null;
            try
            {
                if (File.Exists(path + AtomicFile.ReadySuffix))
                {
                    // A finished write is about to replace the target (ReadBytes commits it): a plain
                    // target is zeroed first, as the .ready is complete and preferred.
                    if (File.Exists(path) && !StartsEncrypted(path)) WipePlain(path);
                }
                else if (StartsEncrypted(path))
                {
                    return false;   // the common case at every later start: no need to read the file
                }

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
