using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// Lists and reads slowdown reports in a report folder, for both the live and the offline
    /// data source. Only <c>slowdown-yyyyMMdd-HHmmss.txt</c> files count: the hardware and
    /// diagnostics reports in the same folder are not slowdowns, and an id is checked against
    /// that exact shape before it becomes a path, so an id can never walk out of the folder.
    /// </summary>
    internal static class SlowdownReportFiles
    {
        // [0-9] rather than \d: \d also matches Thai and other Unicode digits.
        private static readonly Regex IdPattern = new("^slowdown-[0-9]{8}-[0-9]{6}$", RegexOptions.CultureInvariant);

        /// <summary>True for an id the recorder could have written, e.g. <c>slowdown-20260930-140200</c>.</summary>
        public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

        /// <summary>The slowdown reports in <paramref name="folder"/>, newest first; empty when it is missing or unreadable.</summary>
        public static IReadOnlyList<SavedReport> List(string folder)
        {
            var result = new List<SavedReport>();
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) return result;
                foreach (FileInfo file in dir.GetFiles("slowdown-*.txt"))
                {
                    if (!IsValidId(Path.GetFileNameWithoutExtension(file.Name))) continue;
                    result.Add(new SavedReport(file.FullName, file.Name, file.LastWriteTime, file.Length));
                }
                result.Sort((a, b) => b.At.CompareTo(a.At));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        /// <summary>The text of report <paramref name="id"/>, or null when the id is malformed, missing or unreadable.</summary>
        public static string? Read(string folder, string id)
        {
            if (!IsValidId(id)) return null;
            string path = Path.Combine(folder, id + ".txt");
            try
            {
                if (!File.Exists(path)) return null;
                // Shared read: the recorder may be writing or pruning while the bridge reads.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
