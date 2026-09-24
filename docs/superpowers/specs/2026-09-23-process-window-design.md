# Process window: menu entry, richer columns, end all filtered

**Date:** 2026-09-23
**Status:** approved, ready for implementation planning
**Builds on:** the orphan search watchdog, merged to `main` at `3a820a4` — reuses `ProcessControl.HasExited(pid, createTime)`
and the verified-kill sequence introduced there.

## Problem

Three things, reported by the user:

1. The overlay's right-click menu offers **Task Manager**, which launches Windows `taskmgr`.
   MicaStats' own process window exists and works, but the only way to reach it is the
   *Processes* quick action inside the stats panel. From the overlay it is invisible.
2. The process window shows five columns — Name, PID, CPU, Memory, Disk. That is enough to find
   what is consuming the machine, and not enough to understand it: six identical `node.exe`
   rows cannot be told apart, and nothing says which process is an orphan or whose it is.
3. Ending processes is one row at a time. Clearing out a family — every leaked `find.exe`,
   every stale `node.exe` from a dead dev server — means selecting and ending each in turn.

## The constraint this design keeps

`TaskManagerWindow.xaml` states its own rule in its header comment: **no per-row queries
anywhere in this window.** Every column comes from the single `NtQuerySystemInformation` snapshot
`ProcessSampler` already takes every two seconds. That is the whole reason the window stays usable
on a machine too busy to open Task Manager — Task Manager enriches each row individually, and
those queries block precisely when the system is starved.

Every addition below either comes from that same buffer, or is fetched for **one** row, on
demand, off the UI thread.

The existing test `The_process_list_is_virtualized_and_recycling` asserts the list stays
virtualized; it must keep passing.

## Decisions

1. **Menu: add, do not replace.** *Processes* is added to the overlay menu and opens MicaStats'
   window. *Task Manager* stays: it is the fallback when MicaStats itself is the problem, and it
   has tabs this window deliberately does not (startup, services, users).
2. **Snapshot-sourced data becomes columns; handle-sourced data goes in a detail pane.**
3. **End all filtered is gated three ways**: disabled with an empty filter, previewed, and
   confirmed by typing the count.
4. **The bulk kill is verified per process** with the same check the watchdog uses, and it never
   tree-kills — children the filter did not match are left alone.

## 1. Menu entry

`OverlayWindow.cs` builds the right-click menu with Win32 `AppendMenu`. Add a *Processes* item
immediately above *Task Manager* (id 1002) with a new id, and on selection call
`TaskManagerWindow.ShowOrActivate(App.SharedProcessSampler)` on the dispatcher — the same call
`StatsPanelWindow.OnOpenProcesses` already makes.

`ContextMenuWindow` (the WPF menu) builds a parallel list with its own *Task Manager* item; give
it the same *Processes* entry so the two menus do not drift.

## 2. Columns from the snapshot

`SYSTEM_PROCESS_INFORMATION` (x64) already contains, in the buffer `ProcessSampler` walks:

| Field | Offset | Becomes |
|---|---|---|
| `NumberOfThreads` | `0x04` | **Threads** column |
| `CreateTime` | `0x20` | **Uptime** column (already read) |
| `InheritedFromUniqueProcessId` | `0x58` | **Parent** column (already added on this branch as `OffParentProcessId`) |
| `HandleCount` | `0x60` | **Handles** column |

`ProcessUsage` gains `ParentPid`, `ParentName`, `Threads`, `Handles`. `ParentName` is the
resolved display name, or `(gone)`. Uptime is derived from `CreateTime` at render time, not
stored.

**Parent** shows the parent's name and PID, resolved by looking the parent PID up in the same
snapshot. It uses the recycled-PID rule already proven in `ParentState.Resolve`: a parent entry
that started *after* the child is not the parent. An absent or recycled parent renders as
`(gone)` — which is exactly how an orphan announces itself in this list.

Parent-name resolution happens once per sample, in the sampler's pass, from a `pid → name`
dictionary built from the same buffer. No handle is opened.

Sorting extends to the new columns: Parent (by name), Uptime, Threads, Handles. The existing pure
`TaskManagerViewModel.Sort` gains those cases and stays pure and unit tested.

Search keeps matching name and PID, and additionally matches the parent name, so typing `bash`
finds every process whose parent is a bash.

## 3. Detail pane for the selected row

Four facts need a handle, so they are never columns:

| Fact | Call |
|---|---|
| Full image path | `QueryFullProcessImageNameW` |
| Command line | `NtQueryInformationProcess(ProcessCommandLineInformation)` |
| User account | `OpenProcessToken` + `GetTokenInformation(TokenUser)` + `LookupAccountSid` |
| Elevated | `GetTokenInformation(TokenElevation)` |

The first two are exactly what `Services/Watchdog/ProcessDetails.TryRead` already does. The
token queries are added beside it as a new, separate reader rather than widening `TryRead`, whose
contract the watchdog relies on.

The pane sits below the list, fixed height, shown only when a row is selected. Fetch runs on a
background task when the selection changes; the pane shows the row's identity immediately and
fills the four fields when the read returns. A read that loses the race with a newer selection is
discarded. A process that cannot be opened — protected, or exited — shows a plain reason
(`Access denied`, `Process has exited`) rather than blanks.

The command line and path are selectable text, so they can be copied.

## 4. Totals for the filtered set

The footer currently shows a count. It becomes:

`37 processes · 4.2% CPU · 1.8 GB · 12 MB/s`

computed from the filtered rows the view model already holds, in the same pass that builds the
list. A pure `TaskManagerViewModel.Totals(IReadOnlyList<ProcessUsage>)` function, unit tested.
With no filter the totals are for everything shown.

## 5. End all filtered

A new **End all filtered** button beside *End task*.

**Disabled while the search box is empty.** With no filter, "all filtered" means every process on
the machine; the button does not exist in that state rather than asking.

Clicking it opens a modal preview:

- Every matching process, with name, PID, CPU and memory, and the combined cost.
- Processes that will **not** be ended, shown in their own group with the reason, never silently
  dropped:
  - critical Windows processes, per the existing `ProcessControl.IsCriticalProcess`;
  - MicaStats itself;
  - MicaStats' own parent chain up to the shell, so a filter that happens to match the host does
    not end the app from under the user.
- A text box and the instruction *Type 37 to end these processes*. The confirm button enables only
  when the typed text equals the count of processes that will actually be ended. A count rather
  than a fixed word means a filter that changed between opening and confirming forces a re-read.

The target set is captured as `(pid, createTime)` identities **when the preview opens**, not when
confirm is clicked. A process that appears afterwards is not ended; one whose PID is recycled is
refused by `TryEndTask`'s identity check.

On confirm, ending runs off the UI thread:

1. `ProcessControl.TryEndTask(pid, createTime, name)` for **every** identity first.
2. Then **one** settle of 2 seconds for the whole batch — not one per process, which would make
   37 processes take over a minute.
3. Then verify every identity that reported `Terminated` or `AlreadyExited` with
   `ProcessControl.HasExited(pid, createTime)`, so a process wedged in kernel I/O is reported as
   surviving rather than as ended. `Recycled` counts as ended without verification — the
   original is already known gone.
4. **No** `taskkill /T`. A tree kill would end children the filter did not match.

The window's status line then reports the outcome in one sentence:
`Ended 35 · 2 need administrator · 0 survived`. Rows that failed with access denied stay in the
list, where the existing per-row *Retry as administrator* already handles them one at a time.

The kill loop is guarded per process, so one failure does not abandon the rest — the lesson the
watchdog's `EndAll` already learned. Every outcome is written to the diagnostics log, area
`processes`.

The pure part — deciding which rows are ended and which are excluded, and why — is a function
`BulkEndPlan.Build(IReadOnlyList<ProcessUsage> filtered, int selfPid, IReadOnlyCollection<int> selfAncestors)`
returning the two groups. Unit tested, including: empty filter input, critical processes
excluded, self and ancestors excluded, and that nothing outside the input is ever included.

## Architecture

```
ProcessSampler           + Threads, Handles, ParentPid on ProcessUsage (offsets 0x04, 0x60, 0x58)
                         + ParentName resolved once per sample from the same buffer

ViewModels/
  TaskManagerViewModel   + sort cases, parent-name search, Totals() — pure, tested
  BulkEndPlan            PURE: filtered rows -> (toEnd, excluded with reasons)

Services/
  ProcessAccountReader   token-based user + elevation for ONE pid (new; beside ProcessDetails).
                         Not "ProcessIdentityReader": ProcessIdentity already names the
                         watchdog's (pid, createTime) record struct.

TaskManagerWindow        + 4 columns, detail pane, totals footer, End all filtered button
BulkEndDialog            preview + typed-count confirmation
OverlayWindow            + Processes menu item
ContextMenuWindow        + Processes menu item
```

## Error handling

- The detail-pane read and the bulk kill both run off the dispatcher and both are wrapped: an
  exception is logged and shown as a status message, never unhandled on a background task.
- The bulk kill never throws out of its loop; per-process failures are counted and logged.
- The sampler's added reads cost two `Marshal.ReadInt32` calls per process and no syscall.

## Testing

Unit tests (xunit, existing `TaskManagerTests` / new `ProcessWindowTests`):

- `ProcessUsage` carries threads, handles and parent PID from a real snapshot of the test host.
- Parent resolution: live parent named; absent parent `(gone)`; recycled parent `(gone)`.
- Sort by each new column, ascending and descending.
- Search matches parent name.
- `Totals` over a known set; over an empty set.
- `BulkEndPlan`: every case listed in section 5.
- The existing virtualization test still passes.

No unit tests for the WPF layout, the detail-pane fetch or the kill loop — they are UI, handles
and termination, consistent with how the watchdog's shell was treated. Verified manually: open
from the overlay menu, check the new columns against Windows Task Manager for a few processes,
select rows and read the detail pane, filter to a harmless disposable set (e.g. several spawned
`notepad.exe`) and end all.

## Out of scope

- Services, startup items, users tabs — that is what *Task Manager* stays in the menu for.
- Per-process GPU or network columns.
- Grouping or a parent/child tree view. The Parent column and parent-name search cover the need
  without a second list mode.
- Bulk elevation: access-denied rows are retried one at a time through the existing path.
