using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The 7-day history on disk: <c>yyyyMMdd.csv</c> per UTC day in <see cref="Folder"/>.
    ///
    /// <para>
    /// The last 24 to 48 hours stay per minute; <see cref="Maintain"/> rewrites a day that ended
    /// more than 24 hours ago as 5-minute rows and deletes a day that ended more than
    /// <see cref="RetentionDays"/> days ago, which keeps the folder near 10 MB at most.
    /// </para>
    ///
    /// <para>
    /// Files are opened with <see cref="FileShare.ReadWrite"/> so the <c>--mcp</c> bridge can read
    /// while the app appends. Every public member takes one lock and never throws for I/O: a
    /// failure is reported through <c>warn</c> once per kind until that kind works again. The
    /// constructor touches no disk; the folder appears with the first row.
    /// </para>
    /// </summary>
    public sealed class HistoryStore
    {
        /// <summary>Days a day file is kept after the day ended.</summary>
        public const int RetentionDays = 7;

        private const int ThinnedSeconds = 300;
        private static readonly TimeSpan KeepMinuteRowsFor = TimeSpan.FromHours(24);
        private static readonly Regex DayFileName = new(@"^\d{8}\.csv$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly Func<DateTime> _utcClock;
        private readonly Action<string> _warn;
        private readonly object _gate = new();
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

        /// <param name="folder">Where the day files live; created by the first <see cref="Append"/>.</param>
        /// <param name="utcClock">"Now" for <see cref="Maintain"/>.</param>
        /// <param name="warn">Told about I/O failures, once per kind.</param>
        public HistoryStore(string folder, Func<DateTime> utcClock, Action<string>? warn = null)
        {
            Folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _utcClock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
            _warn = warn ?? (_ => { });
        }

        /// <summary><c>%APPDATA%\MicaStats\history</c>.</summary>
        public static string DefaultFolder => Path.Combine(DiagnosticsLog.DataDir, "history");

        /// <summary>The folder holding the day files.</summary>
        public string Folder { get; }

        /// <summary>
        /// Adds a row to the file of its UTC day, writing the two header lines when the file is new
        /// or empty. A line cut short by an earlier crash is ended first, so it can never swallow
        /// this row.
        /// </summary>
        public void Append(HistoryRow row)
        {
            if (row == null) return;

            lock (_gate)
            {
                string path = "";
                try
                {
                    path = PathFor(row.Utc);
                    Directory.CreateDirectory(Folder);
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

                    var text = new StringBuilder();
                    if (stream.Length == 0)
                    {
                        text.Append(Header());
                    }
                    else
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        if (stream.ReadByte() != '\n') text.Append('\n');
                    }
                    text.Append(HistoryCsv.Format(row)).Append('\n');

                    byte[] bytes = Utf8NoBom.GetBytes(text.ToString());
                    stream.Seek(0, SeekOrigin.End);
                    stream.Write(bytes, 0, bytes.Length);
                    Succeeded("write");
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    Failed("write", "History could not be written to " + (path.Length > 0 ? path : Folder) + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Every row from <paramref name="fromUtc"/> to <paramref name="toUtc"/>, both included,
        /// sorted by time. Damaged lines and a last line still being written are skipped.
        /// </summary>
        public IReadOnlyList<HistoryRow> Read(DateTime fromUtc, DateTime toUtc)
        {
            DateTime from = MinuteAggregator.ToUtc(fromUtc);
            DateTime to = MinuteAggregator.ToUtc(toUtc);
            var rows = new List<HistoryRow>();
            if (from > to) return rows;

            lock (_gate)
            {
                bool anyFailed = false;
                foreach (var file in DayFiles())
                {
                    if (file.Day < from.Date || file.Day > to.Date) continue;
                    List<HistoryRow> fileRows = ReadFile(file.FilePath, out bool failed);
                    anyFailed |= failed;
                    foreach (HistoryRow row in fileRows)
                        if (row.Utc >= from && row.Utc <= to) rows.Add(row);
                }
                // A kind is reset only after a whole pass without a failure of that kind, so one
                // stuck file next to healthy ones does not warn again on every read.
                if (!anyFailed) Succeeded("read");
            }
            return rows.OrderBy(r => r.Utc).ToList();
        }

        /// <summary>
        /// Thins every day that ended more than 24 hours ago to 5-minute rows and deletes every day
        /// that ended more than <see cref="RetentionDays"/> days ago. Files that are not day files
        /// are left alone. Safe to run at any time; a day already thinned is not rewritten.
        /// </summary>
        public void Maintain()
        {
            DateTime now = MinuteAggregator.ToUtc(_utcClock());
            lock (_gate)
            {
                bool anyFailed = false;
                foreach (var file in DayFiles())
                {
                    TimeSpan sinceDayEnded = now - file.Day.AddDays(1);
                    try
                    {
                        if (sinceDayEnded > TimeSpan.FromDays(RetentionDays))
                        {
                            File.Delete(file.FilePath);
                            DeleteIfThere(file.FilePath + AtomicFile.ReadySuffix);
                            DeleteIfThere(file.FilePath + AtomicFile.TempSuffix);
                        }
                        else if (sinceDayEnded > KeepMinuteRowsFor)
                        {
                            Thin(file.FilePath);
                        }
                    }
                    catch (Exception ex) when (IsIo(ex))
                    {
                        anyFailed = true;
                        Failed("maintain", "History maintenance failed on " + file.FilePath + ": " + ex.Message);
                    }
                }
                if (!anyFailed) Succeeded("maintain");
            }
        }

        /// <summary>Bytes on disk in <see cref="Folder"/>, for Settings; 0 when there is no folder.</summary>
        public long SizeBytes()
        {
            lock (_gate)
            {
                try
                {
                    if (!Directory.Exists(Folder)) return 0;
                    return new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    Failed("size", "The size of the history folder could not be read: " + ex.Message);
                    return 0;
                }
            }
        }

        /// <summary>Removes the whole folder (Settings > AI > Delete history). Recording, if on, starts a new one.</summary>
        public void DeleteAll()
        {
            lock (_gate)
            {
                try
                {
                    if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
                    Succeeded("delete");
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    Failed("delete", "The history folder could not be deleted: " + ex.Message);
                }
            }
        }

        private string PathFor(DateTime utc) =>
            Path.Combine(Folder, MinuteAggregator.ToUtc(utc).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv");

        private static string Header() => HistoryCsv.VersionLine + "\n" + HistoryCsv.ColumnLine + "\n";

        /// <summary>The day files present, with the UTC day each one holds.</summary>
        private List<(DateTime Day, string FilePath)> DayFiles()
        {
            var files = new List<(DateTime Day, string FilePath)>();
            if (!Directory.Exists(Folder)) return files;

            try
            {
                foreach (string path in Directory.EnumerateFiles(Folder))
                {
                    string name = Path.GetFileName(path);
                    if (!DayFileName.IsMatch(name)) continue;
                    if (DateTime.TryParseExact(name.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime day))
                        files.Add((day, path));
                }
                Succeeded("list");
            }
            catch (Exception ex) when (IsIo(ex))
            {
                Failed("list", "The history folder could not be listed: " + ex.Message);
            }
            return files;
        }

        /// <summary>
        /// The complete rows of one file. Anything after the last line break is a line still being
        /// written (or cut short by a crash) and is ignored.
        /// </summary>
        private List<HistoryRow> ReadFile(string path, out bool failed)
        {
            failed = false;
            var rows = new List<HistoryRow>();
            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return rows;
            }
            catch (Exception ex) when (IsIo(ex))
            {
                failed = true;
                Failed("read", "History could not be read from " + path + ": " + ex.Message);
                return rows;
            }

            int end = text.LastIndexOf('\n');
            if (end < 0) return rows;
            foreach (string line in text.Substring(0, end).Split('\n'))
                if (HistoryCsv.TryParse(line, out HistoryRow? row) && row != null) rows.Add(row);
            return rows;
        }

        /// <summary>Rewrites one day file as 5-minute rows, atomically; a day with no 1-minute rows is left alone.</summary>
        private void Thin(string path)
        {
            List<HistoryRow> rows = ReadFile(path, out _);
            if (rows.Count == 0 || rows.All(r => r.Seconds >= ThinnedSeconds)) return;

            var text = new StringBuilder(Header());
            foreach (var bucket in rows.GroupBy(r => BucketOf(r.Utc)).OrderBy(g => g.Key))
                text.Append(HistoryCsv.Format(HistoryCsv.Combine(bucket.ToList(), bucket.Key, ThinnedSeconds))).Append('\n');

            AtomicFile.Write(path, Utf8NoBom.GetBytes(text.ToString()));
        }

        private static DateTime BucketOf(DateTime utc)
        {
            long bucketTicks = TimeSpan.FromSeconds(ThinnedSeconds).Ticks;
            DateTime u = MinuteAggregator.ToUtc(utc);
            return new DateTime(u.Ticks - u.Ticks % bucketTicks, DateTimeKind.Utc);
        }

        private static void DeleteIfThere(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static bool IsIo(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

        private void Failed(string kind, string message)
        {
            if (_failing.Add(kind)) _warn(message);
        }

        private void Succeeded(string kind) => _failing.Remove(kind);
    }
}
