# Orphan search watchdog

**Date:** 2026-09-22
**Status:** approved, ready for implementation planning

## Problem

On a Windows development machine, agentic coding tools run shell commands through Git Bash.
When such a tool launches a whole-filesystem search and the launching shell is then terminated
— a cancelled background task, or a subagent that has finished — the `find` child is not
reaped. It becomes an orphan and keeps scanning at full CPU with nowhere to send its output.

Two real examples, observed together on one machine:

```
PID 50192  "C:\Program Files\Git\usr\bin\find.exe" / -name IdURI.pas
           parent 33960 (already exited), 2258 seconds of CPU consumed

PID 12264  "C:\Program Files\Git\usr\bin\find.exe" / -iname cxEdit.pas -not -path */proc/*
           parent 21452 (already exited), 2115 seconds of CPU consumed
```

Under Git Bash, `/` is the whole drive and the walk includes `/proc`, every mount, and any
mapped or dead network path, so these can run effectively forever.

This is a mitigation, not a cure. The cause is a tool that does not reap its children, and the
fix belongs upstream in that tool. The watchdog exists to stop the machine burning while that
is true, and to record which parent image leaked the child so the leak can be reported.

## Why MicaStats has leverage here

The alternative shape for this feature is a standalone PowerShell script installed as a
Scheduled Task. MicaStats already provides, in production, four of the six things such a script
would have to build for itself:

- `Services/ProcessSampler.cs` reads every process on the system from a single
  `NtQuerySystemInformation` call, walked by struct offset. The parent PID sits at offset 0x58
  in the same buffer that already yields creation time (0x20) and cumulative user and kernel
  time (0x28, 0x30). Parent, CPU seconds and age therefore cost no additional system calls.
- `Services/ProcessControl.cs` terminates a process and classifies every outcome, verifying the
  creation time against the live process first so a recycled PID is never terminated by
  mistake.
- `Services/ConfigService.cs` persists configuration as JSON under `%APPDATA%\MicaStats` with
  debounced atomic writes.
- `Services/DiagnosticsLog.cs` is an append-only rotating log in the same folder.

A separate script would duplicate all four and hold a second copy of the detection rule.

The sampler runs unelevated, which is a correctness requirement rather than a preference:
`Process.TotalProcessorTime` throws access denied for roughly a third of processes on a normal
desktop, so a list built that way omits the answer. The kernel call has no such access check.

## Decisions

1. **Native feature, not a script.** The watchdog ships inside MicaStats, on the application's
   own timer. No Scheduled Task, no separate install, no second implementation of the rule.
2. **Notify, do not kill unattended.** Detection runs always-on and logs. A toast reports what
   was found and offers *End them*. The irreversible step requires a human click. This replaces
   the script design's `-DryRun`-until-`-Enable` rule with a stronger version of the same
   protection: the operator is in the loop on every kill, not only on the first run.
3. **The decision is a pure function.** `OrphanScan.Decide` takes plain records and returns
   verdicts. It calls no process API and reads no clock. This is the unit under test.
4. **Match on full image path, never on process name.** `C:\Windows\System32\find.exe` is a
   completely different Microsoft tool — a string filter used by batch scripts — that merely
   shares the name. It must never match.
5. **Verify every kill.** A kill that reports success is not a kill that happened. On the
   machine above, `Stop-Process -Force` reported success while the process kept accumulating
   CPU, because it was wedged in kernel I/O.
6. **Never loop on one PID.** Two escalating attempts, then the identity is recorded as
   unkillable and left alone permanently.
7. **Thresholds exist to protect deliberate work.** The CPU and age bars are not tuned to catch
   more. A false positive destroys someone's intentional search, which is a worse outcome than
   a missed orphan.

## Architecture

```
Services/Watchdog/
  OrphanScanOptions.cs   thresholds + allowlists, projected from AppConfig
  ProcessRecord.cs       pid, parentPid, imagePath, commandLine, cpuSeconds,
                         startTime, parentExists, parentImagePath
  OrphanVerdict.cs       pid, kill, reason
  OrphanScan.cs          PURE: Decide(records, options, now) -> verdicts
                         PURE: ParentState.Resolve(child, snapshot) -> parentExists
  OrphanWatchdog.cs      impure shell: 60s timer, record collection, logging,
                         toast, kill escalation, unkillable ledger

OrphanToastWindow.cs     "2 orphaned find.exe, 74 min CPU between them" + End them

ProcessSampler           + parent PID (offset 0x58) and cumulative CPU time surfaced
                         + SnapshotOnce(): one-shot pass, no Retain() lease required
AppConfig                + WatchOrphanedSearches and four threshold keys
SettingsWindow           + one checkbox
```

### Where the snapshot comes from

`ProcessSampler` samples only while a client holds a `Retain()` lease — the Task Manager window
or the stats panel being open. The watchdog must work when nothing is open, so it does **not**
take a lease, which would force continuous two-second sampling all day for a check that runs
once a minute.

Instead `ProcessSampler` gains a one-shot `SnapshotOnce()` that performs a single
`NtQuerySystemInformation` pass and returns the records, reusing the existing buffer-walking
code and its offsets. The watchdog calls it every 60 seconds. One system call and one buffer
walk per minute is negligible, and it leaves the sampler's own cadence untouched.

Data flow, once per 60 seconds:

1. Take a one-shot kernel snapshot via `ProcessSampler.SnapshotOnce()`.
2. Prefilter by image **name** — cheap, and normally matches nothing.
3. For each candidate only, resolve the full image path
   (`QueryFullProcessImageName`) and the command line
   (`NtQueryInformationProcess` / `ProcessCommandLineInformation`, available since Windows 8.1
   and permitted with `PROCESS_QUERY_LIMITED_INFORMATION`).
4. Resolve `parentExists` against the same snapshot, via `ParentState.Resolve`. The snapshot
   carries only the parent's bare image name, so for a live parent of a candidate the full image
   path is read as well, and accepted only when its file name matches the snapshot name (a PID
   recycled between the snapshot and the read would otherwise be blamed). The ledger remembers
   that path per candidate identity, so it can still be named once the parent has exited.
5. Call `OrphanScan.Decide`.
6. Log candidate verdicts.
7. If any verdict is a kill, and the set contains an identity not already alerted or recorded
   unkillable, raise one toast. The identities are marked alerted by the subscriber once the card
   is actually on screen, not by the scan, so a card that fails to appear is offered again next
   minute.

Nothing in the application calls `Decide` with live process objects, so the decision stays
testable with synthetic records.

## Detection rule

A process is killed only when all five hold. Rules are evaluated in order and the first failure
produces the keep reason.

1. **Image allowlist.** `imagePath`, with `/` normalised to `\`, ends with an allowlist suffix,
   compared ordinal and case-insensitive. Default suffix: `\Git\usr\bin\find.exe`.
   `C:\Windows\System32\find.exe` does not end with that suffix and so cannot match. The process
   name alone is never consulted.
2. **Unbounded scan.** The command line is tokenised respecting quotes; the executable token is
   dropped; leading `-H`, `-L` and `-P` are skipped; the first remaining non-flag argument is
   the scan root. The scan is unbounded when that root is `/`, or a bare drive root written as
   `C:/`, `C:\`, `C:` or `/c/` for any drive letter — **and** the command line has no
   `-maxdepth`, or its value is greater than `OrphanTrustedMaxDepth` (default 3), or its value
   cannot be read as a plain non-negative integer. Under Git Bash `/` mounts every drive, so
   `find / -maxdepth 6` still walks every drive five levels deep: a real orphan with exactly
   `"C:\Program Files\Git\usr\bin\find.exe" / -name madExcept.pas -maxdepth 6` burned more than
   5400 seconds of CPU with a dead parent, and was kept while any `-maxdepth` counted as a
   bound. A depth that is not trusted only falls through to rules 3 to 5, which still protect
   a deliberate search. A candidate whose command line could not be read is kept with the
   reason `command line unreadable` rather than judged on text nobody saw.
3. **CPU.** `cpuSeconds` exceeds `OrphanCpuSecondsThreshold`, default 120.
4. **Age.** `now - startTime` exceeds `OrphanGraceMinutes`, default 5.
5. **Parent.** Either `parentExists` is false, or the parent's image file name is outside
   `OrphanExpectedParents`, default `bash.exe`, `sh.exe`, `pwsh.exe`, `cmd.exe`.

Rules 3 and 4 exist so a deliberate search is never killed mid-flight. Rule 5 is the real
signal: an orphan has no consumer for its output, so it can only waste CPU.

### Reasons are branch-specific

A kill reason states which branch of rule 5 fired, and never claims the parent is unrecognised
when the parent has simply exited:

- `parent 33960 has exited`
- `parent 21452 (explorer.exe) is not a recognised shell`

Keep reasons name the rule that saved the process, for example `bounded by -maxdepth 3`,
`42s CPU is under the 120s threshold`, `30s old, under the 5 minute grace period`, or
`parent 21452 (bash.exe) is a live shell`.

### Recycled parent PIDs

`ParentState.Resolve` returns false when the parent PID is present in the snapshot but its
creation time is **newer than the child's**. A parent younger than its own child is a recycled
PID, which means the real parent is gone and the process is an orphan. Testing only whether the
PID exists would find the unrelated newcomer and wrongly conclude a live parent.

### Deviation from the source specification

The originating specification's fifth test case asks that a `find.exe` with a **live
`bash.exe`** parent be killed through rule 5's second branch. `bash.exe` is in that rule's
expected-shell list, so the rule as written keeps it, and killing it would contradict rule 5's
own justification — a live shell parent is exactly the case where a consumer for the output
still exists.

Resolved in favour of the rule. The case is covered by two tests instead of one: a live
`bash.exe` parent is kept, and a live `explorer.exe` parent is killed with the
not-a-recognised-shell reason. Two tests prove both branches and prove the reason text is
branch-specific; one test can prove neither.

## Configuration

Flat properties on `AppConfig`, so they serialise into the existing
`%APPDATA%\MicaStats\config.json` and need no new file or format:

| Key | Default | Meaning |
| --- | --- | --- |
| `WatchOrphanedSearches` | `true` | Master switch; the one Settings checkbox |
| `OrphanCpuSecondsThreshold` | `120` | Rule 3 |
| `OrphanGraceMinutes` | `5` | Rule 4 |
| `OrphanTrustedMaxDepth` | `3` | Rule 2; clamped to 0..32 |
| `OrphanBinaryAllowlist` | `["\\Git\\usr\\bin\\find.exe"]` | Rule 1 |
| `OrphanExpectedParents` | `["bash.exe","sh.exe","pwsh.exe","cmd.exe"]` | Rule 5 |

Thresholds and both lists are editable without touching code. Only the master switch appears in
`SettingsWindow`; the rest are values tuned once, if ever, and a settings page full of them
would cost more than it returns.

The 60 second scan interval is a constant in `OrphanWatchdog`, not configuration. It is the
cadence the behaviour was designed around, not a tuning knob.

## Kill and escalation

Per flagged `(pid, createTime)` identity, when the user clicks *End them*:

1. `ProcessControl.TryEndTask(pid, createTime, name)`. It verifies the identity against the live
   process before terminating.
2. Wait two seconds. Re-read the snapshot for that exact identity.
3. Still running — the exact `(pid, createTime)` identity is present in the fresh snapshot
   **and** `ProcessControl.HasExited(pid, createTime)` says it has not exited: the process is
   wedged in kernel I/O rather than protected. Escalate to `taskkill /F /T /PID <pid>` with no
   visible window. Presence alone is not enough, because a terminated process stays listed
   while any handle to it is open; absence is decisive, because an identity missing from the
   snapshot is gone whatever an inconclusive handle query said.
4. Wait two seconds and check once more the same way. Still running: log the identity as
   `UNKILLABLE`, record it, and never retry or alert on it again.

`TryEndTask` returning `AccessDenied` takes a different branch: it is logged as `ACCESS-DENIED`
and the attempt ends there. It is a privilege failure, not a wedged process, and `taskkill`
would fail the same way. It does **not** route to the elevated one-shot `--kill` path, although
an earlier draft of this document said it would. The children this watchdog exists for are
leaked by a tool the user runs, so they run as the same user and an unelevated terminate
reaches them; a process that refuses is not the case being solved. And a UAC prompt raised from
a background thread, some seconds after a click on a card that has already closed, is its own
problem — a consent dialog nobody can connect to what they did.

Two attempts, then the ledger closes the identity out. There is no third try and no loop.

## Logging

Appended to `%APPDATA%\MicaStats\logs\micastats.log` through `DiagnosticsLog`, area `watchdog`.

Only allowlist-hit candidates are logged; the hundreds of processes that fail rule 1 produce no
output. A keep is written **once per identity per distinct reason**, so a legitimately
long-running search logs one line rather than sixty an hour, while a verdict that changes is
always recorded.

A kill line carries timestamp, pid, parentPid, the parent's image, cpuSeconds, age, the full
command line, the verdict and the reason.

The parent is named by its full path whenever any scan saw it alive, and that path is remembered
per candidate identity, so a later line written after the parent has exited reads
`parentImage=(gone, was C:\...\bash.exe)`. A parent that exited before the first scan ever saw
it cannot be identified, and the line says `parentImage=(gone, never seen)` rather than guessing.

The parent image path is the point of the log, not decoration: it names the tool that leaked the
child, which is the only route to fixing the cause rather than the symptom.

## Toast

`OrphanToastWindow`, built on the pattern already shared by `AlertToastWindow` and
`UpdateToastWindow`, reusing `ToastButton` and `UiGlyphs`. It never takes focus, stacks rather
than replaces, and fades itself away.

Text names the images and the totals, for example *2 orphaned find.exe, 74 min CPU between
them*. Buttons: *End them* and *Dismiss*.

Identities already alerted on, and identities recorded unkillable, never produce another toast.
The command lines themselves live in the log rather than on the card.

## Testing

`tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`, xunit, matching the repository's existing
convention rather than the source specification's Pester.

`OrphanScan.Decide` against synthetic records:

1. `C:\Windows\System32\find.exe` scanning `/` with 5000s CPU — kept, reason names rule 1.
2. Git `find.exe` with `-maxdepth 3` and 5000s CPU — kept, reason names the bound.
3. Git `find.exe` scanning `/`, 10s CPU, 30s old — kept, reason names the CPU threshold.
4. Git `find.exe` scanning `/`, orphaned, 2258s CPU, hours old — killed, reason says the parent
   has exited.
5. Git `find.exe` scanning `/`, live `bash.exe` parent, 2258s CPU — kept, reason says the parent
   is a live shell.
6. Git `find.exe` scanning `/`, live `explorer.exe` parent, 2258s CPU — killed, reason says the
   parent is not a recognised shell.
7. Empty input — no verdicts, no exception.

Additional cases the source specification does not list but the implementation requires:

8. Quoted executable path with spaces is tokenised correctly.
9. `-maxdepth` appearing after other predicates still bounds the scan, but only at or below
   the trusted depth; the observed `-maxdepth 6` orphan is killed, and a `-maxdepth` with no
   readable number is not trusted.
10. A drive root spelled `C:\`, `C:/`, `C:` and `/c/` is each recognised as unbounded.
11. A non-root first argument, such as `C:\src`, is bounded.

`ParentState.Resolve`:

12. Parent absent from the snapshot — not existing.
13. Parent present and older than the child — existing.
14. Parent present but **newer** than the child — not existing, PID was recycled.

## Deliberately dropped from the source specification

- **Exit codes 0, 1 and 2.** There is no process to exit; the watchdog lives inside a running
  GUI application.
- **`-DryRun` and `-Enable`.** Superseded by notify-then-confirm, which keeps the operator in
  the loop on every kill rather than only the first run.
- **Scheduled Task install and uninstall commands.** The application's own timer replaces them.

## Documentation

`README.md` gains a feature entry alongside the existing Processes entry, and states plainly
that the watchdog treats a symptom whose cause is a tool not reaping its children, and that the
logged parent image path is how that cause gets reported upstream.
