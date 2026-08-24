# Task manager window

**Date:** 2026-08-24
**Status:** approved, ready for implementation planning

## Problem

Windows Task Manager is frequently unusable on the target machine, in four distinct ways
reported by the user:

1. It is slow to open, or never opens.
2. It opens but the list is frozen or empty, so nothing can be identified.
3. *End task* does nothing — the process survives.
4. It makes the machine worse: Task Manager itself costs CPU and the desktop stutters more
   once it is open.

The consequence is that at exactly the moment something is consuming the machine, the tool for
finding and stopping it is unavailable.

MicaStats is already running, and already samples every process on the system every two
seconds. It is therefore in a position to answer all three questions the user needs — what is
running, what is consuming CPU and memory, and how to stop it — with no new sampling work at
the moment it is asked.

## Why MicaStats has leverage here

Task Manager is slow in large part because it enriches each row individually: icon, publisher,
description, per-process detail queries. Each of those blocks when the system is starved, and
there are hundreds of rows.

`Services/ProcessSampler.cs` gets everything from a single `NtQuerySystemInformation`
(`SystemProcessInformation`) call that returns every process in one buffer, walked by struct
offset. It runs on a `System.Threading.Timer` at `SampleIntervalMs = 2000` on a background
thread, computes CPU share from cycle-time deltas against a `_previous` dictionary, and then
**discards** all but the top few entries into `TopByCpu`, `TopByRam` and `TopByDisk`.

The full list is already computed. It is thrown away at the last step.

## Decisions

1. **Focused scope.** One window: every process, sortable, searchable, killable. No services
   tab, no startup tab, no per-process detail panes. The scope is the user's three stated
   needs, and keeping it there is also what keeps the window cheap.
2. **No new sampling.** The window attaches to the sampler's existing snapshot. It takes a
   `Retain()` lease while open and renders the most recent snapshot immediately rather than
   waiting for the next tick, so it is populated the instant it appears.
3. **Terminate directly.** No graceful-close attempt. Task Manager's *End task* first asks a
   window to close politely, which is precisely why it appears to do nothing against a hung
   application. This calls `TerminateProcess` and reports the actual outcome.
4. **Elevate per kill, never permanently.** MicaStats stays unelevated. A refused kill offers
   *Retry as administrator*, which relaunches the executable as `--kill <pid> <startTime>`
   under UAC to terminate one process and exit.
5. **Refuse to terminate critical processes.** Not a warning — a refusal.

## Architecture

```
ProcessSampler (modified)
  + AllProcesses          full snapshot from the pass it already makes
  + ProcessUsage.CreateTime   already read at offset 0x20; now surfaced

ProcessControl (new)      TryEndTask(pid, createTime) -> EndTaskResult
                          IsCriticalProcess(name)

TaskManagerViewModel      sort + filter + in-place row sync
TaskManagerWindow         virtualized list, search box, End task

App startup               --kill <pid> <createTime>: elevated one-shot, no UI
```

### Components

**`ProcessSampler.AllProcesses`** — the complete snapshot, published alongside the existing
top-N rankings from the same pass. No additional syscall. The top-N properties stay as they
are; the panel and the slowdown recorder continue to use them.

**`ProcessUsage.CreateTime`** — the process creation time, a `long` FILETIME. The sampler
already reads this field (offset `0x20`) to detect pid reuse between samples; it simply is not
exposed. It becomes the identity half of a (pid, createTime) pair.

**`ProcessControl`** — the only component that terminates anything. Two members:

```csharp
public enum EndTaskResult { Terminated, AccessDenied, AlreadyExited, Critical, Recycled, Failed }

public static EndTaskResult TryEndTask(int pid, long createTime, out string message);
public static bool IsCriticalProcess(string name);
```

`Recycled` covers a pid that now belongs to a different process than the one selected;
`Failed` covers any other Win32 error, reported with its code rather than as a generic failure.

`TryEndTask` opens the process with `PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION`,
verifies the creation time still matches, and calls `TerminateProcess`. Every failure is
classified rather than swallowed, because "End task does nothing" is one of the four reported
symptoms and a silent failure reproduces it.

**`TaskManagerViewModel`** — holds an `ObservableCollection<ProcessRow>`, applies the current
sort column, direction and search text, and syncs rows in place.

**`TaskManagerWindow`** — a virtualized list, a search box, column headers that sort, and an
End task button. Follows the existing standalone-window pattern
(`HardwareInfoWindow`, `DiagnosticsWindow`).

### Data flow

The sampler raises `Updated` **on a background thread**. The view model marshals to the UI
thread via the dispatcher, applies filter and sort to the snapshot, and syncs the row
collection in place — updating existing rows' values and adding or removing only the
difference. Rebuilding the collection each tick would allocate hundreds of objects every two
seconds and destroy the user's selection and scroll position mid-read, which is the opposite of
what a tool for diagnosing a busy machine should do.

## Responsiveness requirements

These are requirements, not aspirations; each maps to a reported failure.

| Requirement | Failure it prevents |
| --- | --- |
| No per-row queries — every column comes from the single existing pass | frozen / empty list |
| `VirtualizingStackPanel` with recycling | Task Manager fighting the machine |
| Rows updated in place, not rebuilt | allocation churn; lost selection while reading |
| Window renders the last snapshot on open, before any new sample | slow to open |
| All sampling stays on the sampler's background thread | UI freeze under load |

### The cold-start case

CPU share is computed from a **delta** between two samples, so it cannot exist until a second
sample has been taken. The sampler is lease-based and may not be running when the window opens:
if nothing else holds a lease, the first snapshot arrives with memory and disk populated and
every CPU figure at zero, and the first real CPU column appears one interval later.

A grid of zeroes in the CPU column is indistinguishable from the frozen list this feature
exists to replace, so it must not be shown as if it were data. Until the second sample lands,
the CPU column shows a dash and the window states that it is measuring. This is the same
principle applied to the sensor work: an unavailable reading is never rendered as a zero.

Where a lease is already held — the panel is open, or the slowdown recorder is armed — the
snapshot is complete and the window is fully populated on its first paint.

## Kill semantics

1. If `IsCriticalProcess(name)` — refuse, explain, do not open a handle.
2. Open the process. `ERROR_INVALID_PARAMETER` means it already exited — report that; it is a
   success from the user's point of view.
3. Compare the creation time. A mismatch means the pid was recycled onto a different process —
   return `Recycled` and refuse.
4. `TerminateProcess`. On `ERROR_ACCESS_DENIED`, return `AccessDenied` so the window can offer
   elevation.

The window reports the outcome in words, always. A kill that does nothing must never look the
same as a kill that worked.

### Elevation and pid recycling

*Retry as administrator* starts the current executable with `Verb = "runas"` and
`--kill <pid> <createTime>`. That instance parses two integers, re-reads the target's creation
time, terminates only on a match, and exits with a status code the parent reports.

The creation-time check is load-bearing rather than defensive. Between the user's click and
their consent, Windows can recycle the pid onto an unrelated process — and the UAC dialog makes
that window seconds wide rather than microseconds. Without the check, a slow consent could
terminate something the user never selected, with administrator rights.

The elevated path must complete before any window, configuration or telemetry is initialised.
It parses, verifies, terminates, exits.

**Security note.** A `--kill` argument on the executable grants no capability that
`taskkill /f /pid` does not already provide to anyone able to run the program, so it adds no
privilege. What matters is that the elevated code path does exactly one thing: two integers in,
one verified termination, exit. No configuration load, no update check, no UI.

### Critical processes

`csrss.exe`, `wininit.exe`, `services.exe`, `smss.exe`, `lsass.exe`, `winlogon.exe`.

Terminating any of these bugchecks Windows immediately — an instant stop error, not a
recoverable failure. Task Manager prompts for confirmation; this refuses. A user who genuinely
intends it has `taskkill` available, and will not reach it by mis-clicking a row in a list that
is re-sorting itself while the machine is busy.

Matching is by file name, case-insensitive. This is deliberately a name check rather than a
protected-process query: it must work before any handle is opened, and it must not depend on a
query that could itself block on a starved system.

## Error handling

Every failure is classified and shown. The window has no silent path: a refused kill, a
recycled pid, an already-exited process and an access denial each produce distinct text.

If the sampler is unavailable, the window says so rather than showing an empty list — an empty
list is one of the symptoms being fixed and must never be this tool's way of reporting a
problem.

## Testing

Pure logic, unit tested:

- Sort comparators for each column and direction, including ties.
- Filter matching: case-insensitive substring on name, and exact match on a numeric pid.
- `IsCriticalProcess` — every name in the list, case variations, and near-misses such as
  `csrss.exe.bak` or `mycsrss.exe`, which must **not** match.
- `--kill` argument parsing: valid input, missing arguments, non-numeric, negative, and
  absurdly large values.
- Row sync: an added process appears, a departed one is removed, an existing one keeps its
  identity while its values change.

Not unit testable: actual termination and the UAC transition. The guard and the parser
therefore carry the safety weight, and both are pure functions.

## Out of scope

- Services and startup tabs. (`StartupEntries` from the diagnostics release could support a
  startup tab cheaply, but it is not part of the stated problem.)
- Per-process command line, which requires reading the PEB of another process and can block or
  be denied — exactly the kind of per-row query this design exists to avoid.
- Process icons, for the same reason.
- Priority and affinity adjustment.
- Restarting a killed process.
- Any permanently elevated mode.
