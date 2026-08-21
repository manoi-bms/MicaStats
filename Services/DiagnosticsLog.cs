using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Append-only diagnostics log at <c>%APPDATA%\MicaStats\logs\micastats.log</c>, kept next
    /// to <c>config.json</c> so every investigation artefact lives in one folder. Plain text,
    /// one timestamped line per event, rotated once past 512 KB (previous file kept as
    /// <c>micastats-1.log</c>).
    ///
    /// <para>
    /// Logging must never hurt the app: every write is wrapped, and the first IO failure
    /// permanently disables the logger for the process rather than retrying on a hot path.
    /// </para>
    /// </summary>
    public static class DiagnosticsLog
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Gate = new();
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static bool _dead;

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicaStats");

        public static string LogDir => Path.Combine(DataDir, "logs");
        public static string LogPath => Path.Combine(LogDir, "micastats.log");

        public static void Log(string area, string message) => WriteLine("INFO ", area, message);
        public static void Warn(string area, string message) => WriteLine("WARN ", area, message);

        public static void Error(string area, string message, Exception? ex = null) =>
            WriteLine("ERROR", area,
                ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);

        private static void WriteLine(string level, string area, string message)
        {
            if (_dead) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDir);
                    RotateIfNeeded();
                    // InvariantCulture is load-bearing: locale-default formatting also swaps
                    // the CALENDAR, so a Thai locale would stamp Buddhist-era years (2569).
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        " [" + level + "] [" + area + "] " + message + Environment.NewLine,
                        Utf8NoBom);
                }
            }
            catch
            {
                _dead = true;
            }
        }

        private static void RotateIfNeeded()
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            string old = Path.Combine(LogDir, "micastats-1.log");
            try
            {
                if (File.Exists(old)) File.Delete(old);
                fi.MoveTo(old);
            }
            catch { }
        }
    }
}
