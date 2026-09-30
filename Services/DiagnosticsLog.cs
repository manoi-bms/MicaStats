using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Append-only diagnostics log at <c>%APPDATA%\MicaStats\logs\micastats.log</c>, kept next
    /// to <c>config.json</c> so every investigation artefact lives in one folder. Plain text,
    /// one timestamped line per event, rotated once past 512 KB (previous file kept as
    /// <c>micastats-1.log</c>).
    ///
    /// <para>
    /// Logging must never hurt the app: every write is wrapped. A line that meets a sharing
    /// violation is skipped; any other failure (no access, a folder that cannot be created)
    /// disables the logger for the process rather than retrying on a hot path. The
    /// <c>--mcp</c> bridge, a second process, writes <see cref="BridgeFileName"/> instead
    /// (<see cref="UseFileName"/>), so each file has a single writing process.
    /// </para>
    /// </summary>
    public static class DiagnosticsLog
    {
        /// <summary>The app's own log file name.</summary>
        public const string AppFileName = "micastats.log";

        /// <summary>
        /// The <c>--mcp</c> bridge's log file name. The bridge is a second process that runs beside
        /// the app, so it keeps its own file and <see cref="AppFileName"/> has a single writer.
        /// </summary>
        public const string BridgeFileName = "mcp-bridge.log";

        private static DiagnosticsLogFile s_file = new(Path.Combine(DataDir, "logs"), AppFileName);

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicaStats");

        /// <summary>The folder this process logs into, <c>%APPDATA%\MicaStats\logs</c> outside tests.</summary>
        public static string LogDir => Volatile.Read(ref s_file).Folder;

        /// <summary>The file this process logs into: <see cref="AppFileName"/>, or <see cref="BridgeFileName"/> in the bridge.</summary>
        public static string LogPath => Volatile.Read(ref s_file).FilePath;

        public static void Log(string area, string message) => WriteLine("INFO ", area, message);
        public static void Warn(string area, string message) => WriteLine("WARN ", area, message);

        public static void Error(string area, string message, Exception? ex = null) =>
            WriteLine("ERROR", area,
                ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);

        /// <summary>
        /// Sends every later line of this process to <paramref name="fileName"/> in
        /// <see cref="LogDir"/>. The <c>--mcp</c> bridge calls it first thing with
        /// <see cref="BridgeFileName"/>, so the app and a bridge never append to the same file.
        /// </summary>
        public static void UseFileName(string fileName) => UseFile(LogDir, fileName);

        /// <summary>
        /// Tests only: sends every later line of this process to <paramref name="fileName"/> in
        /// <paramref name="folder"/>, so a test can check the logger without touching %APPDATA%.
        /// </summary>
        internal static void UseFile(string folder, string fileName) =>
            Volatile.Write(ref s_file, new DiagnosticsLogFile(folder, fileName));

        private static void WriteLine(string level, string area, string message) =>
            // InvariantCulture is load-bearing: locale-default formatting also swaps the
            // CALENDAR, so a Thai locale would stamp Buddhist-era years (2569).
            Volatile.Read(ref s_file).Append(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " [" + level + "] [" + area + "] " + message + Environment.NewLine);
    }

    /// <summary>
    /// One diagnostics log file: appends whole lines, rotates the file once past 512 KB and never
    /// throws. Separate from <see cref="DiagnosticsLog"/> so tests can drive a file in a temporary
    /// folder without changing where the app logs.
    /// </summary>
    internal sealed class DiagnosticsLogFile
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private readonly object _gate = new();
        private bool _dead;

        /// <summary>A log at <paramref name="fileName"/> inside <paramref name="folder"/>; nothing is created until the first line.</summary>
        public DiagnosticsLogFile(string folder, string fileName)
        {
            Folder = folder;
            FilePath = Path.Combine(folder, Path.GetFileName(fileName));
        }

        /// <summary>The folder the file lives in, created on the first write.</summary>
        public string Folder { get; }

        /// <summary>The full path of the file.</summary>
        public string FilePath { get; }

        /// <summary>True once a failure has switched this file off for the rest of the process.</summary>
        public bool IsDead => Volatile.Read(ref _dead);

        /// <summary>
        /// Appends <paramref name="line"/> (already ending in a newline). Never throws. The file is
        /// opened with full sharing, so a reader or another writer does not block it; a line that
        /// still meets a sharing or lock violation is skipped, and only a failure that will not
        /// pass (no access, a folder that cannot be created) switches the file off.
        /// </summary>
        public void Append(string line)
        {
            if (IsDead) return;
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(Folder);
                    RotateIfNeeded();
                    byte[] bytes = Utf8NoBom.GetBytes(line);
                    using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                                                      FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                // Another handle holds the file for a moment: this line is lost, the next one is not.
            }
            catch
            {
                Volatile.Write(ref _dead, true);
            }
        }

        /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION, as the HRESULT an IOException carries.</summary>
        private static bool IsSharingViolation(IOException ex) =>
            ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

        private void RotateIfNeeded()
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            string old = Path.Combine(Folder,
                Path.GetFileNameWithoutExtension(FilePath) + "-1" + Path.GetExtension(FilePath));
            try
            {
                if (File.Exists(old)) File.Delete(old);
                fi.MoveTo(old);
            }
            catch { }
        }
    }
}
