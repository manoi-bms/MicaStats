using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// The decision: which processes are runaway orphaned searches.
    ///
    /// <para>
    /// Pure by construction. No process API, no clock, no configuration read — records and
    /// options in, verdicts out. Everything that could fail, block or require a privilege
    /// happens in <see cref="OrphanWatchdog"/> before this is called, which is what makes the
    /// rule that ends processes testable against synthetic input.
    /// </para>
    /// </summary>
    public static class OrphanScan
    {
        /// <summary>Verdicts for every record, in input order.</summary>
        public static IReadOnlyList<OrphanVerdict> Decide(
            IReadOnlyList<ProcessRecord> records, OrphanScanOptions options, DateTime now)
        {
            if (records == null || records.Count == 0) return Array.Empty<OrphanVerdict>();

            var verdicts = new OrphanVerdict[records.Count];
            for (int i = 0; i < records.Count; i++) verdicts[i] = DecideOne(records[i], options, now);
            return verdicts;
        }

        /// <summary>
        /// The five rules, in order. The first one that fails produces the keep reason, so the
        /// log says which guard saved the process rather than merely that it survived.
        /// </summary>
        public static OrphanVerdict DecideOne(ProcessRecord record, OrphanScanOptions options, DateTime now)
        {
            // 1 — image allowlist, on the full path.
            if (!IsAllowlisted(record.ImagePath, options.BinarySuffixes))
                return new OrphanVerdict(record.Pid, false,
                    "image is not an allowlisted search binary");

            // 2 — unbounded scan. Checked only on a command line that was actually read:
            // ProcessDetails can resolve the image and still come back with no command line,
            // and "no path argument is not a filesystem root" would then assert a fact about a
            // command line nobody saw. Kept, with the reason saying what really happened.
            if (string.IsNullOrWhiteSpace(record.CommandLine))
                return new OrphanVerdict(record.Pid, false, "command line unreadable");

            // Only a shallow -maxdepth is a bound; a deep one falls through.
            if (!SearchCommandLine.IsUnbounded(record.CommandLine, options.TrustedMaxDepth))
            {
                int? depth = SearchCommandLine.MaxDepth(record.CommandLine);
                string reason = depth is int trusted && trusted <= options.TrustedMaxDepth
                    ? "bounded by -maxdepth " + trusted.ToString(CultureInfo.InvariantCulture)
                    : "bounded: " + (SearchCommandLine.ScanRoot(record.CommandLine) ?? "no path argument")
                      + " is not a filesystem root";
                return new OrphanVerdict(record.Pid, false, reason);
            }

            // 3 — CPU.
            if (record.CpuSeconds <= options.CpuSecondsThreshold)
                return new OrphanVerdict(record.Pid, false,
                    Seconds(record.CpuSeconds) + " CPU is under the "
                    + Seconds(options.CpuSecondsThreshold) + " threshold");

            // 4 — age.
            TimeSpan age = now - record.StartTime;
            if (age <= options.Grace)
                return new OrphanVerdict(record.Pid, false,
                    Seconds(age.TotalSeconds) + " old, inside the "
                    + options.Grace.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)
                    + " minute grace period");

            // 5 — parent. Two branches, and the reason must say which one fired.
            if (!record.ParentExists)
                return new OrphanVerdict(record.Pid, true,
                    "parent " + record.ParentPid.ToString(CultureInfo.InvariantCulture)
                    + " has exited, so nothing is receiving the output");

            string parentName = FileName(record.ParentImagePath);
            if (IsExpectedParent(parentName, options.ExpectedParents))
                return new OrphanVerdict(record.Pid, false,
                    "parent " + record.ParentPid.ToString(CultureInfo.InvariantCulture)
                    + " (" + parentName + ") is a live shell");

            return new OrphanVerdict(record.Pid, true,
                "parent " + record.ParentPid.ToString(CultureInfo.InvariantCulture)
                + " (" + (parentName.Length == 0 ? "unknown" : parentName)
                + ") is not a recognised shell");
        }

        /// <summary>
        /// Whether this image path ends with one of the allowlisted suffixes.
        ///
        /// <para>
        /// Suffix matching on the full path is the whole protection against
        /// <c>C:\Windows\System32\find.exe</c>, a Microsoft string filter used by batch scripts
        /// that merely shares a file name with the GNU tool that leaks. Slashes are normalised
        /// first because the kernel and the shell disagree about which way they lean.
        /// </para>
        /// </summary>
        public static bool IsAllowlisted(string imagePath, IReadOnlyList<string> suffixes)
        {
            if (string.IsNullOrEmpty(imagePath) || suffixes == null) return false;

            string path = imagePath.Replace('/', '\\');
            foreach (string suffix in suffixes)
            {
                if (string.IsNullOrEmpty(suffix)) continue;
                if (path.EndsWith(suffix.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsExpectedParent(string parentName, IReadOnlyList<string> expected)
        {
            if (parentName.Length == 0 || expected == null) return false;
            foreach (string name in expected)
            {
                if (string.Equals(parentName, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path.Replace('/', '\\')); }
            catch { return ""; }
        }

        /// <summary>Seconds rendered the way the reason strings read, e.g. "2258s".</summary>
        private static string Seconds(double value) =>
            value.ToString("0", CultureInfo.InvariantCulture) + "s";
    }
}
