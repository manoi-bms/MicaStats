using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>One flagged process, as the toast and the kill path need it.</summary>
    /// <param name="CpuSeconds">
    /// The total at the moment this was flagged, not a live figure to render as-is — a
    /// reference point captured before any kill was attempted. It no longer decides whether a
    /// kill took (<see cref="ProcessControl.HasExited(int, long)"/> asks the kernel directly
    /// for that), but the log still wants to know how far along the process already was.
    /// </param>
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

        /// <summary>
        /// Raised on a background thread with processes worth reporting. The subscriber must
        /// call <see cref="MarkShown"/> once the user can actually see them; until it does,
        /// every scan raises them again.
        /// </summary>
        public event Action<IReadOnlyList<OrphanFinding>>? Found;

        /// <summary>
        /// Records that these findings are now in front of the user, so later scans stop
        /// raising them.
        ///
        /// <para>
        /// The subscriber calls this, not the scan, because only the subscriber knows when the
        /// card exists: <see cref="Found"/> is raised on a timer thread and the card is built
        /// later on the dispatcher. Marking any earlier, a card that failed to appear would
        /// leave the ledger believing the user had been told, and the only way to end those
        /// processes would never be offered. Safe from any thread; the ledger locks.
        /// </para>
        /// </summary>
        public void MarkShown(IReadOnlyList<OrphanFinding> findings)
        {
            if (findings == null) return;
            foreach (var finding in findings) _ledger.MarkAlerted(finding.Identity);
        }

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

                    // The snapshot only carries the bare image name. The full path is what
                    // tells one bash.exe from another, so it is read here — for candidates
                    // only, which on a normal machine means never.
                    if (parentExists) parentImage = FullParentPath(process.ParentPid, parentImage);

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

                    // Every scan, not only when a line is written: the parent has to be seen
                    // alive to be remembered, and the line that needs the memory is the later
                    // one written after it has gone.
                    string parentLabel = _ledger.DescribeParent(
                        identity, record.ParentExists, record.ParentImagePath);

                    if (_ledger.ShouldLog(identity, verdict.Reason)) Write(record, verdict, parentLabel);
                    if (!verdict.Kill || !_ledger.ShouldAlert(identity)) continue;

                    findings.Add(new OrphanFinding(
                        identity, candidates[i].Name, record.CommandLine,
                        record.CpuSeconds, verdict.Reason));
                }

                // Not marked alerted here. The subscriber hands the batch to the dispatcher and
                // returns at once, so at this point no card exists yet; marking now would let a
                // card that failed to appear silence these findings for good. The subscriber
                // calls MarkShown once the card is actually on screen.
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
                    // Scoped to one finding, not the whole loop, so a process that blows up
                    // does not abandon the rest of the batch — and logged rather than left to
                    // propagate, since the caller may be running this on any thread.
                    survived++;
                    DiagnosticsLog.Error(Area, "Ending pid "
                        + finding.Identity.Pid.ToString(CultureInfo.InvariantCulture) + " failed", ex);
                }
            }

            if (survived == 0)
                return ended == 1 ? "Ended it." : "Ended all " + Count(ended) + ".";
            if (ended == 0)
                return survived == 1
                    ? "It did not end; the log says why."
                    : "None of the " + Count(survived) + " ended; the log says why.";

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

            // TryEndTask already proved this, by comparing creation times before it touched
            // anything: the PID now belongs to a different process, so the one that was
            // flagged is gone. Taken as-is rather than re-verified — StillRunning would open a
            // fresh handle to that PID and, if the identity check inside it ever failed to line
            // up the same way, a forced tree kill could land on the innocent process now
            // wearing that number, which is the one outcome this whole design exists to avoid.
            if (result == EndTaskResult.Recycled)
            {
                Log(finding, "KILLED", message);
                return true;
            }

            if (result == EndTaskResult.Terminated || result == EndTaskResult.AlreadyExited)
            {
                System.Threading.Thread.Sleep(VerifyDelayMs);
                bool? stillRunning = StillRunning(finding, out double cpuNow);

                if (stillRunning == null)
                {
                    Log(finding, "UNVERIFIED",
                        "terminate reported " + result + ", but the process list could not be "
                        + "read to confirm the kill");
                    return false;
                }

                if (!stillRunning.Value)
                {
                    Log(finding, "KILLED", message);
                    return true;
                }

                Log(finding, "SURVIVED",
                    "terminate reported " + result + " but the process is still running ("
                    + cpuNow.ToString("0", CultureInfo.InvariantCulture) + "s CPU); escalating");
            }
            else if (result == EndTaskResult.AccessDenied)
            {
                // A privilege failure, not a wedged process, and taskkill would fail the same
                // way — so this deliberately logs and stops rather than escalating further. The
                // children this watchdog exists for are leaked by a tool the user runs, so they
                // run as the same user and an unelevated terminate reaches them; a process that
                // refuses is not the case being solved. Raising a UAC prompt from a background
                // thread, seconds after a click on a card that may already be gone, is its own
                // problem — a consent dialog nobody can connect to what they did.
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

            bool? stillRunningAfterTreeKill = StillRunning(finding, out _);

            if (stillRunningAfterTreeKill == null)
            {
                Log(finding, "UNVERIFIED",
                    "taskkill /F /T ran, but the process list could not be read to confirm the "
                    + "kill");
                return false;
            }

            if (!stillRunningAfterTreeKill.Value)
            {
                Log(finding, "KILLED", "taskkill /F /T succeeded where terminate did not");
                return true;
            }

            _ledger.MarkUnkillable(finding.Identity);
            Log(finding, "UNKILLABLE", "survived terminate and taskkill /F /T; giving up on it");
            return false;
        }

        /// <summary>
        /// Whether this exact process is still running, per the kernel rather than inferred
        /// from CPU movement — or <c>null</c> when a fresh snapshot cannot say.
        ///
        /// <para>
        /// A process that is alive but has, for the moment, stopped accumulating CPU is not
        /// the same as a dead one — comparing CPU totals would report it killed, log it as
        /// such, and (because the alert is already marked sent) never surface it again.
        /// Presence in a fresh snapshot is not the answer either: a terminated process lingers
        /// as a zombie for as long as any handle to it stays open, so being listed proves
        /// nothing. <see cref="ProcessControl.HasExited(int, long)"/> asks the process handle
        /// directly, and also treats a recycled PID as the original being gone.
        /// </para>
        ///
        /// <para>
        /// Both must agree before this says "still running": the exact <c>(pid, createTime)</c>
        /// identity is present in a fresh snapshot, and the handle has not exited. The snapshot
        /// is the identity check. <see cref="ProcessControl.HasExited(int, long)"/> answers false
        /// when it cannot tell, and a "yes" that rests on that alone would send
        /// <c>taskkill /F /T</c> at a PID nobody re-confirmed and then close the identity out as
        /// unkillable on an unknown. Absent from a snapshot that is itself non-empty means gone,
        /// whatever the handle said.
        /// </para>
        ///
        /// <para>
        /// An empty snapshot is a third case, not a synonym for "gone": <see
        /// cref="ProcessSampler.SnapshotOnce"/> returns an empty list both when nothing is
        /// running, which never happens on a live system, and when the kernel query itself
        /// failed. Returning <c>false</c> here on an empty snapshot would let a failed
        /// verification read as a confirmed kill, so that case is reported as unknown instead
        /// and left for the caller to treat as "could not tell" rather than "ended".
        /// </para>
        /// </summary>
        /// <param name="cpuSeconds">
        /// The current CPU total, for the SURVIVED log line only. "Still climbing" is useful
        /// colour there even though it no longer decides the verdict; it is 0 when the process
        /// is not in the snapshot.
        /// </param>
        private static bool? StillRunning(OrphanFinding finding, out double cpuSeconds)
        {
            cpuSeconds = 0;

            IReadOnlyList<ProcessSampler.RawProcess> snapshot = ProcessSampler.SnapshotOnce();
            if (snapshot.Count == 0) return null;

            bool present = false;
            foreach (var process in snapshot)
            {
                if (process.Pid != finding.Identity.Pid) continue;
                if (process.CreateTime != finding.Identity.CreateTime) continue;
                cpuSeconds = process.CpuSeconds;
                present = true;
                break;
            }

            if (!present) return false;
            return !ProcessControl.HasExited(finding.Identity.Pid, finding.Identity.CreateTime);
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
        /// The live parent full image path, or <paramref name="bareName"/> when it cannot be
        /// read.
        ///
        /// <para>
        /// The path is only accepted when its file name matches the name the snapshot saw. The
        /// handle is opened by PID after the snapshot was taken, and a parent that exited in
        /// between could have handed its number to something else; a mismatched name means the
        /// path belongs to that newcomer, and naming it would blame the wrong tool — and rule 5
        /// reads this name too.
        /// </para>
        /// </summary>
        private static string FullParentPath(int parentPid, string bareName)
        {
            if (!ProcessDetails.TryRead(parentPid, out string fullPath, out _)) return bareName;

            string fileName;
            try { fileName = Path.GetFileName(fullPath); }
            catch { return bareName; }

            return bareName.Length == 0 ||
                   string.Equals(fileName, bareName, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : bareName;
        }

        /// <summary>
        /// One line per decision.
        ///
        /// <para>
        /// The parent image path is the point of this log rather than decoration: it names the
        /// tool that leaked the child. The watchdog only stops the burning; the leak is fixed
        /// upstream, by whoever owns that image. <paramref name="parentLabel"/> comes from
        /// <see cref="OrphanLedger.DescribeParent"/>, so a parent that has since exited is still
        /// named if any earlier scan saw it alive.
        /// </para>
        /// </summary>
        private static void Write(ProcessRecord record, OrphanVerdict verdict, string parentLabel)
        {
            string line =
                "pid=" + record.Pid.ToString(CultureInfo.InvariantCulture) +
                " parent=" + record.ParentPid.ToString(CultureInfo.InvariantCulture) +
                " parentImage=" + parentLabel +
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

        /// <summary>
        /// Stops future scans and releases the timer.
        ///
        /// <para>
        /// Does not wait for a scan already running on the timer thread — disposing a
        /// <see cref="System.Threading.Timer"/> does not block on a callback in progress, so
        /// that pass simply finishes on its own after this returns.
        /// </para>
        /// </summary>
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
