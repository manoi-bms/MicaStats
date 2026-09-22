using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>One flagged process, as the toast and the kill path need it.</summary>
    public sealed record OrphanFinding(
        ProcessIdentity Identity, string Name, string CommandLine, double CpuSeconds, string Reason);

    /// <summary>
    /// Watches for whole-filesystem searches left running by a tool that did not reap its
    /// children, and ends them once the user says so.
    ///
    /// <para>
    /// This is the impure half: the clock, the snapshot, the log and the kill. The rule itself
    /// lives in <see cref="OrphanScan"/> and is tested there. Nothing here decides anything.
    /// </para>
    ///
    /// <para>
    /// It never kills on its own. Detection is always on and always logged, but termination
    /// waits for a click, because a false positive destroys work somebody intended and they
    /// find out only when the results never arrive.
    /// </para>
    /// </summary>
    public sealed class OrphanWatchdog : IDisposable
    {
        /// <summary>
        /// Scan cadence. Deliberately not configurable: it is the cadence the thresholds were
        /// chosen around, and an orphan wastes the same core whether it is noticed in ten
        /// seconds or sixty.
        /// </summary>
        private const int ScanIntervalMs = 60_000;

        /// <summary>
        /// How long to let a kill land before checking whether it did. A terminate is
        /// asynchronous, and a process wedged in kernel I/O reports success and keeps running.
        /// </summary>
        private const int VerifyDelayMs = 2000;

        private const string Area = "watchdog";

        private readonly OrphanLedger _ledger = new();
        private readonly object _gate = new();
        private System.Threading.Timer? _timer;
        private bool _enabled;
        private bool _disposed;

        /// <summary>Thresholds and allowlists. Replaced wholesale when settings change.</summary>
        public OrphanScanOptions Options { get; set; } = OrphanScanOptions.Defaults;

        /// <summary>Raised on a background thread with processes worth reporting.</summary>
        public event Action<IReadOnlyList<OrphanFinding>>? Found;

        /// <summary>
        /// Whether to scan. Switching off stops the timer; the ledger is kept, so switching on
        /// again does not re-report what the user has already seen and dismissed.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;

                lock (_gate)
                {
                    if (_enabled)
                    {
                        _timer ??= new System.Threading.Timer(_ => Scan(), null,
                            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                        // First scan after one interval, not immediately: an orphan that has
                        // been burning for an hour can wait a minute, and startup is busy.
                        _timer.Change(ScanIntervalMs, ScanIntervalMs);
                    }
                    else
                    {
                        _timer?.Change(System.Threading.Timeout.Infinite,
                                       System.Threading.Timeout.Infinite);
                    }
                }
            }
        }

        /// <summary>
        /// One pass. Never throws: it runs unattended on a timer thread, and an exception here
        /// would take down a monitoring feature the user cannot see failing.
        /// </summary>
        private void Scan()
        {
            if (_disposed || !_enabled) return;

            try
            {
                var snapshot = ProcessSampler.SnapshotOnce();
                if (snapshot.Count == 0) return;

                var byPid = new Dictionary<int, ParentState.SnapshotEntry>(snapshot.Count);
                var alive = new List<ProcessIdentity>(snapshot.Count);
                foreach (var process in snapshot)
                {
                    byPid[process.Pid] = new ParentState.SnapshotEntry(
                        process.Pid, FileTime(process.CreateTime), process.Name);
                    alive.Add(new ProcessIdentity(process.Pid, process.CreateTime));
                }

                var records = new List<ProcessRecord>();
                var candidates = new List<ProcessSampler.RawProcess>();

                foreach (var process in snapshot)
                {
                    // The cheap gate. The expensive calls below are paid only past it, and on a
                    // normal machine nothing gets past it at all.
                    if (!NameCouldMatch(process.Name)) continue;
                    if (!ProcessDetails.TryRead(process.Pid, out string imagePath, out string commandLine))
                        continue;

                    DateTime started = FileTime(process.CreateTime);
                    bool parentExists = ParentState.Resolve(
                        process.ParentPid, started, byPid, out string parentImage);

                    candidates.Add(process);
                    records.Add(new ProcessRecord(
                        process.Pid, process.ParentPid, imagePath, commandLine,
                        process.CpuSeconds, started, parentExists, parentImage));
                }

                _ledger.Prune(alive);
                if (records.Count == 0) return;

                var verdicts = OrphanScan.Decide(records, Options, DateTime.Now);
                var findings = new List<OrphanFinding>();

                for (int i = 0; i < verdicts.Count; i++)
                {
                    OrphanVerdict verdict = verdicts[i];
                    ProcessRecord record = records[i];
                    var identity = new ProcessIdentity(record.Pid, candidates[i].CreateTime);

                    if (_ledger.ShouldLog(identity, verdict.Reason)) Write(record, verdict);
                    if (!verdict.Kill || !_ledger.ShouldAlert(identity)) continue;

                    findings.Add(new OrphanFinding(
                        identity, candidates[i].Name, record.CommandLine,
                        record.CpuSeconds, verdict.Reason));
                    _ledger.MarkAlerted(identity);
                }

                if (findings.Count > 0) Found?.Invoke(findings);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error(Area, "Scan failed", ex);
            }
        }

        /// <summary>
        /// Ends every flagged process, verifying each one and escalating where the first
        /// attempt did not take. Returns a sentence for the caller to show.
        /// </summary>
        public string EndAll(IReadOnlyList<OrphanFinding> findings)
        {
            if (findings == null || findings.Count == 0) return "Nothing to end.";

            int ended = 0, survived = 0;

            foreach (var finding in findings)
            {
                if (_ledger.IsUnkillable(finding.Identity)) { survived++; continue; }

                try
                {
                    if (EndOne(finding)) ended++; else survived++;
                }
                catch (Exception ex)
                {
                    // This runs on the dispatcher thread, from the toast's click handler. An
                    // exception escaping here is unhandled on the UI thread and takes the whole
                    // app down, at the exact moment the user asked it to clean something up.
                    // Count it as survived and move on to the rest of the batch.
                    survived++;
                    DiagnosticsLog.Error(Area, "Ending pid "
                        + finding.Identity.Pid.ToString(CultureInfo.InvariantCulture) + " failed", ex);
                }
            }

            if (survived == 0)
                return ended == 1 ? "Ended it." : "Ended all " + Count(ended) + ".";
            if (ended == 0)
                return survived == 1
                    ? "It survived both attempts. Windows is holding it in a kernel call."
                    : "All " + Count(survived) + " survived both attempts.";

            return "Ended " + Count(ended) + "; " + Count(survived) + " survived.";
        }

        /// <summary>
        /// Terminate, verify, escalate, verify, give up.
        ///
        /// <para>
        /// The escalation is not defensive programming. On the machine that prompted this
        /// feature, a forced kill reported success while the process kept accumulating CPU,
        /// because it was wedged in kernel I/O inside the filesystem walk. A kill that is
        /// assumed rather than verified is how a watchdog reports success and changes nothing.
        /// </para>
        ///
        /// <para>
        /// Two attempts, then the identity is recorded and left alone. Never a loop: a process
        /// Windows will not release is not going to yield to a third try, and retrying it every
        /// minute forever is its own kind of runaway.
        /// </para>
        /// </summary>
        private bool EndOne(OrphanFinding finding)
        {
            var result = ProcessControl.TryEndTask(
                finding.Identity.Pid, finding.Identity.CreateTime, finding.Name, out string message);

            if (result == EndTaskResult.Terminated || result == EndTaskResult.AlreadyExited)
            {
                System.Threading.Thread.Sleep(VerifyDelayMs);
                if (!StillBurning(finding, out double cpuNow))
                {
                    Log(finding, "KILLED", message);
                    return true;
                }

                Log(finding, "SURVIVED",
                    "terminate reported " + result + " but CPU is still climbing ("
                    + cpuNow.ToString("0", CultureInfo.InvariantCulture) + "s); escalating");
            }
            else if (result == EndTaskResult.AccessDenied)
            {
                // A privilege failure, not a wedged process. taskkill would fail identically;
                // the elevated one-shot path is the only thing that can help.
                Log(finding, "ACCESS-DENIED", message);
                return false;
            }
            else
            {
                Log(finding, "FAILED", message);
                return false;
            }

            TreeKill(finding.Identity.Pid);
            System.Threading.Thread.Sleep(VerifyDelayMs);

            if (!StillBurning(finding, out _))
            {
                Log(finding, "KILLED", "taskkill /F /T succeeded where terminate did not");
                return true;
            }

            _ledger.MarkUnkillable(finding.Identity);
            Log(finding, "UNKILLABLE", "survived terminate and taskkill /F /T; giving up on it");
            return false;
        }

        /// <summary>
        /// Whether this exact process is still present and still consuming processor time.
        ///
        /// <para>
        /// Identity, not PID: the number is recycled, and after a successful kill it can belong
        /// to something else within seconds. Reporting that as a survival would escalate a tree
        /// kill against an innocent process.
        /// </para>
        /// </summary>
        private static bool StillBurning(OrphanFinding finding, out double cpuSeconds)
        {
            cpuSeconds = 0;
            foreach (var process in ProcessSampler.SnapshotOnce())
            {
                if (process.Pid != finding.Identity.Pid) continue;
                if (process.CreateTime != finding.Identity.CreateTime) continue;

                cpuSeconds = process.CpuSeconds;
                return cpuSeconds > finding.CpuSeconds;
            }
            return false;
        }

        private static void TreeKill(int pid)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/F /T /PID " + pid.ToString(CultureInfo.InvariantCulture),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var killer = System.Diagnostics.Process.Start(psi);
                killer?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error(Area, "taskkill failed for pid "
                    + pid.ToString(CultureInfo.InvariantCulture), ex);
            }
        }

        /// <summary>
        /// One line per decision.
        ///
        /// <para>
        /// The parent image path is the point of this log rather than decoration: it names the
        /// tool that leaked the child. The watchdog only stops the burning; the leak is fixed
        /// upstream, by whoever owns that image.
        /// </para>
        /// </summary>
        private static void Write(ProcessRecord record, OrphanVerdict verdict)
        {
            string line =
                "pid=" + record.Pid.ToString(CultureInfo.InvariantCulture) +
                " parent=" + record.ParentPid.ToString(CultureInfo.InvariantCulture) +
                " parentImage=" + (record.ParentImagePath.Length == 0 ? "(gone)" : record.ParentImagePath) +
                " cpu=" + record.CpuSeconds.ToString("0", CultureInfo.InvariantCulture) + "s" +
                " age=" + ((long)(DateTime.Now - record.StartTime).TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture) + "s" +
                " verdict=" + (verdict.Kill ? "KILL" : "KEEP") +
                " reason=" + verdict.Reason +
                " cmd=" + record.CommandLine;

            if (verdict.Kill) DiagnosticsLog.Warn(Area, line);
            else DiagnosticsLog.Log(Area, line);
        }

        private static void Log(OrphanFinding finding, string outcome, string detail) =>
            DiagnosticsLog.Warn(Area,
                outcome + " pid=" + finding.Identity.Pid.ToString(CultureInfo.InvariantCulture) +
                " cpu=" + finding.CpuSeconds.ToString("0", CultureInfo.InvariantCulture) + "s" +
                " :: " + detail + " :: cmd=" + finding.CommandLine);

        /// <summary>
        /// Whether a bare image name is worth the two calls that resolve its full path.
        ///
        /// <para>
        /// Deliberately loose: it admits <c>C:\Windows\System32\find.exe</c>, which rule 1 then
        /// rejects on the full path. Being loose here costs one handle on a machine where the
        /// batch tool happens to be running; being tight here would mean deciding on a name,
        /// which is the mistake rule 1 exists to prevent.
        /// </para>
        /// </summary>
        private bool NameCouldMatch(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach (string suffix in Options.BinarySuffixes)
            {
                if (string.IsNullOrEmpty(suffix)) continue;
                string fileName = Path.GetFileName(suffix.Replace('/', '\\'));
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static DateTime FileTime(long value)
        {
            try { return value > 0 ? DateTime.FromFileTime(value) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static string Count(int n) =>
            n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " process" : " processes");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;

            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
