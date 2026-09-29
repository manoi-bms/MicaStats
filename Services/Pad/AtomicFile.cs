using System;
using System.IO;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Writes that a crash cannot half-finish.
    ///
    /// <para>
    /// Store files use two phases: the bytes go to <c>name.tmp</c> and are flushed to the
    /// physical disk; only then is the temp renamed to <c>name.ready</c>; then it replaces the
    /// target. A crash while writing leaves a partial <c>.tmp</c>, which nothing ever reads. A
    /// crash after the rename leaves a <c>.ready</c>, which is always complete and is preferred on
    /// the next read. A single temp file cannot tell those two cases apart.
    /// </para>
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>Suffix of a write in progress; never read.</summary>
        public const string TempSuffix = ".tmp";

        /// <summary>Suffix of a complete write whose final replace has not happened yet.</summary>
        public const string ReadySuffix = ".ready";

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>Writes a store file in two phases.</summary>
        public static void Write(string path, byte[] bytes)
        {
            string temp = path + TempSuffix;
            string ready = path + ReadySuffix;

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, ready, overwrite: true);
            Commit(ready, path);
        }

        /// <summary>
        /// Reads a store file written by <see cref="Write"/>, finishing an interrupted write first.
        /// Null when neither the file nor a completed write exists.
        /// </summary>
        public static string? ReadText(string path)
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
                    return File.ReadAllText(ready, Utf8NoBom);
                }
            }

            return File.Exists(path) ? File.ReadAllText(path, Utf8NoBom) : null;
        }

        /// <summary>
        /// Writes a user's file: a temp file in the same folder, flushed, then swapped in with
        /// <see cref="File.Replace(string, string, string?)"/>. Win32 <c>ReplaceFile</c> keeps the
        /// target's ACLs, attributes and creation time, where delete-then-move would reset them.
        /// </summary>
        public static void WriteSource(string path, byte[] bytes)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            string temp = Path.Combine(folder,
                "." + Path.GetFileName(path) + ".micapad-" + Guid.NewGuid().ToString("N").Substring(0, 8) + TempSuffix);

            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    File.Move(temp, path);
                    return;
                }

                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                catch (IOException)
                {
                    // A few file systems (FAT volumes, some network shares) refuse ReplaceFile.
                    // Overwriting in place is the best they allow; a locked target throws again here.
                    File.Copy(temp, path, overwrite: true);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static void Commit(string ready, string path)
        {
            if (File.Exists(path)) File.Replace(ready, path, destinationBackupFileName: null);
            else File.Move(ready, path);
        }
    }
}
