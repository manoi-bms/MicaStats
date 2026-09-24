# Process window implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put MicaStats' own process window in the overlay's right-click menu, make its list informative (parent, uptime, threads, handles, filtered totals, a per-row detail pane), and add a guarded *End all filtered*.

**Architecture:** Everything shown in the list comes from the single kernel snapshot `ProcessSampler` already takes — four fields read from the same buffer, parent names resolved once per sample by a pure function. The four facts that need a process handle (path, command line, user, elevation) are read for the selected row only, off the UI thread. Deciding what *End all filtered* ends is a pure, tested function; ending is verified per process with `ProcessControl.HasExited`.

**Tech Stack:** C# 12, .NET 8 (`net8.0-windows`), WPF, xunit 2.9.2, P/Invoke to `kernel32.dll` and `advapi32.dll`.

**Spec:** `docs/superpowers/specs/2026-09-23-process-window-design.md`

**Branch:** `feat/process-window`, created from `main` at `3a820a4`, where the orphan search watchdog was merged. This plan uses `ProcessControl.HasExited(int pid, long createTime)`, `Services/Watchdog/ProcessDetails.TryRead`, and `ProcessSampler.OffParentProcessId`, all introduced by that merge.

## Global Constraints

- **Build and test only with the user-local SDK:** `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. There is no system .NET SDK on this machine; bare `dotnet` fails with "No .NET SDKs were found".
- **No per-row queries in the list.** Every list column comes from the `ProcessSampler` snapshot. Only the detail pane opens a process handle, and only for the selected row, off the UI thread.
- **The list stays virtualized.** The existing test `The_process_list_is_virtualized_and_recycling` must keep passing.
- **Nullable reference types are enabled** project-wide. Do not add `#nullable disable`.
- **Never format a number or date with the ambient culture.** Use `CultureInfo.InvariantCulture`. A Thai locale stamps Buddhist-era years through defaults.
- **XML doc comments on every public type and member**, house style: say *why* where the reason is not obvious. `Services/ProcessControl.cs` is the reference.
- **Every commit message ends with this trailer**, on its own line after a blank line: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` — this exact text, not the implementer's own model name.
- **Avoid apostrophes in commit message bodies.** Commits go through a `git commit -F -` heredoc and an apostrophe breaks it.
- **Do not add package references.**
- **Do not launch MicaStats, touch `%APPDATA%\MicaStats`, or kill or spawn processes** during implementation. The owner runs a production instance. Verification is build plus test suite, except where a task explicitly says otherwise.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Services/ProcessSampler.cs` | *(modify)* read threads and handles; parent PID onto `ProcessUsage`; parent names on `AllProcesses` | 1 |
| `Services/ProcessTree.cs` | Pure: resolve parent names across one snapshot, with the recycled-PID rule | 1 |
| `ViewModels/TaskManagerViewModel.cs` | *(modify)* new sort columns, header mapping, parent search, totals, uptime text, new row fields | 2 |
| `ViewModels/BulkEndPlan.cs` | Pure: what *End all filtered* ends, what it refuses and why; outcome sentence | 3 |
| `Services/ProcessAccountReader.cs` | User account and elevation for one PID | 4 |
| `ViewModels/ProcessDetailViewModel.cs` | Detail pane state; off-thread fetch with stale-result guard | 5 |
| `TaskManagerWindow.xaml` / `.xaml.cs` | *(modify)* four columns, header sorting, detail pane, totals footer | 5 |
| `Services/BulkEnd.cs` | End a planned set, verify each, count outcomes | 6 |
| `BulkEndDialog.xaml` / `.xaml.cs` | Preview + typed-count confirmation | 6 |
| `OverlayWindow.cs`, `ContextMenuWindow.xaml.cs` | *(modify)* *Processes* menu entry | 7 |
| `README.md` | *(modify)* English and Thai process-list sections | 8 |
| `tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs` | Tests for tasks 1-4 | 1 |

---

### Task 1: Snapshot fields and parent names

**Files:**
- Modify: `Services/ProcessSampler.cs`
- Create: `Services/ProcessTree.cs`
- Create: `tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs`

**Interfaces:**
- Consumes: `ProcessSampler.OffParentProcessId` (`0x58`, already on this branch).
- Produces:
  - `ProcessUsage` init properties: `int ParentPid`, `int Threads`, `int Handles`, `string ParentName` (default `""`)
  - `static string ProcessUsage.FormatBytes(long bytes)`
  - `static class ProcessTree` with `const string Gone = "(gone)"` and `static IReadOnlyList<ProcessUsage> WithParentNames(IReadOnlyList<ProcessUsage> snapshot)`
  - `ProcessSampler.AllProcesses` rows carry `ParentName`

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The process window's new columns come from the same kernel snapshot as the old ones, and
    /// its bulk kill decides what to end in a pure function. Both are tested here; the WPF layout
    /// and the handle reads for the detail pane are verified by hand.
    /// </summary>
    public class ProcessWindowTests
    {
        private static ProcessUsage P(string name, int pid, int parent, long createTime) =>
            new ProcessUsage(name, pid, 0f, 0) { ParentPid = parent, CreateTime = createTime };

        // ------------------------------------------------------- parent names

        [Fact]
        public void A_live_parent_is_named()
        {
            var rows = ProcessTree.WithParentNames(new[]
            {
                P("bash.exe", 100, 4, 10),
                P("find.exe", 200, 100, 20),
            });

            Assert.Equal("bash.exe", rows.Single(r => r.Pid == 200).ParentName);
        }

        [Fact]
        public void An_absent_parent_reads_as_gone()
        {
            var rows = ProcessTree.WithParentNames(new[] { P("find.exe", 200, 100, 20) });

            Assert.Equal(ProcessTree.Gone, rows.Single().ParentName);
        }

        [Fact]
        public void A_parent_younger_than_its_child_is_a_recycled_pid_and_reads_as_gone()
        {
            // The real parent exited and Windows gave 100 to something newer. Naming it would
            // tell the user an unrelated notepad launched their find.
            var rows = ProcessTree.WithParentNames(new[]
            {
                P("notepad.exe", 100, 4, 50),
                P("find.exe", 200, 100, 20),
            });

            Assert.Equal(ProcessTree.Gone, rows.Single(r => r.Pid == 200).ParentName);
        }

        [Fact]
        public void A_process_with_no_parent_pid_has_an_empty_parent_rather_than_gone()
        {
            // The System process has parent 0. It is not an orphan; it never had a parent.
            var rows = ProcessTree.WithParentNames(new[] { P("System", 4, 0, 1) });

            Assert.Equal("", rows.Single().ParentName);
        }

        [Fact]
        public void Resolving_parent_names_keeps_every_row_in_its_order_and_its_other_fields()
        {
            var input = new[]
            {
                P("c.exe", 3, 0, 1) with { Threads = 7, Handles = 70 },
                P("a.exe", 1, 3, 2),
                P("b.exe", 2, 3, 3),
            };

            var rows = ProcessTree.WithParentNames(input);

            Assert.Equal(new[] { 3, 1, 2 }, rows.Select(r => r.Pid));
            Assert.Equal(7, rows[0].Threads);
            Assert.Equal(70, rows[0].Handles);
            Assert.Equal("c.exe", rows[1].ParentName);
        }

        [Fact]
        public void An_empty_snapshot_resolves_to_an_empty_list()
        {
            Assert.Empty(ProcessTree.WithParentNames(Array.Empty<ProcessUsage>()));
        }

        // ------------------------------------------------------- snapshot fields

        [Fact]
        public void The_sampler_reports_threads_handles_and_parent_for_the_current_process()
        {
            using var sampler = new ProcessSampler();
            using var ready = new System.Threading.ManualResetEventSlim();
            sampler.Updated += () => ready.Set();
            sampler.Retain();

            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "no sample within 10 seconds");

            var me = sampler.AllProcesses.Single(p => p.Pid == Environment.ProcessId);
            using var self = Process.GetCurrentProcess();
            self.Refresh();

            // The offsets are hand-computed. A wrong one reads a neighbouring field, which is
            // usually a small plausible number, so compare against the managed API rather than
            // only checking for non-zero.
            Assert.InRange(me.Threads, self.Threads.Count / 2, self.Threads.Count * 2 + 8);
            Assert.InRange(me.Handles, self.HandleCount / 2, self.HandleCount * 2 + 64);
            Assert.True(me.ParentPid > 0);
        }

        [Theory]
        [InlineData(0L, "0 MB")]
        [InlineData(512L * 1024 * 1024, "512 MB")]
        [InlineData(1536L * 1024 * 1024, "1.5 GB")]
        public void Bytes_format_in_the_largest_readable_unit(long bytes, string expected)
        {
            Assert.Equal(expected, ProcessUsage.FormatBytes(bytes));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: FAIL — `error CS0117: 'ProcessUsage' does not contain a definition for 'ParentPid'` and `CS0103: The name 'ProcessTree' does not exist`.

- [ ] **Step 3: Extend `ProcessUsage`**

In `Services/ProcessSampler.cs`, inside `public sealed record ProcessUsage(...)`, find:

```csharp
        /// <summary>Combined disk traffic per second — how a process is ranked as a disk hog.</summary>
        public long DiskBytesPerSec => DiskReadBytesPerSec + DiskWriteBytesPerSec;
```

Add immediately before it:

```csharp
        /// <summary>
        /// The PID of the process that created this one, as the kernel recorded it. Not proof
        /// the parent is alive: the number may since have been reused. Resolve it through
        /// <see cref="ProcessTree.WithParentNames"/>, which applies the recycled-PID rule.
        /// </summary>
        public int ParentPid { get; init; }

        /// <summary>
        /// The parent's image name, <see cref="ProcessTree.Gone"/> when the parent has exited or
        /// its PID was reused, or empty when the process never had one. Filled only on
        /// <see cref="ProcessSampler.AllProcesses"/>.
        /// </summary>
        public string ParentName { get; init; } = "";

        /// <summary>Thread count, from the same kernel buffer as everything else here.</summary>
        public int Threads { get; init; }

        /// <summary>Open handle count, from the same kernel buffer as everything else here.</summary>
        public int Handles { get; init; }

```

Then replace:

```csharp
        public string WorkingSetText => WorkingSet >= 1024L * 1024 * 1024
            ? $"{WorkingSet / 1024d / 1024d / 1024d:F1} GB"
            : $"{WorkingSet / 1024d / 1024d:F0} MB";
```

with:

```csharp
        public string WorkingSetText => FormatBytes(WorkingSet);

        /// <summary>
        /// A byte count in the largest unit that keeps the number readable, e.g. "412 MB" or
        /// "1.8 GB". Shared by a row's memory and the footer's total, so the two always agree.
        /// </summary>
        public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
            ? (bytes / 1024d / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : (bytes / 1024d / 1024d).ToString("F0", CultureInfo.InvariantCulture) + " MB";
```

Add `using System.Globalization;` to the top of `Services/ProcessSampler.cs` if it is not already there.

- [ ] **Step 4: Read the two new fields in the sampler**

In `Services/ProcessSampler.cs`, find:

```csharp
        private const int OffNextEntry = 0x00;
```

Add immediately after it:

```csharp

        // NumberOfThreads and HandleCount, in the same buffer. Two more reads per process and no
        // additional syscall — which is how the process window can show them without querying
        // each process, the thing that makes Task Manager slow on a busy machine.
        private const int OffNumberOfThreads = 0x04;
        private const int OffHandleCount = 0x60;
```

In `Sample()`, find:

```csharp
                            var usage = new ProcessUsage(name, (int)pid, percent, workingSet)
                            {
                                DiskReadBytesPerSec = readRate,
                                DiskWriteBytesPerSec = writeRate,
                                CreateTime = createTime,
                            };
```

Replace with:

```csharp
                            var usage = new ProcessUsage(name, (int)pid, percent, workingSet)
                            {
                                DiskReadBytesPerSec = readRate,
                                DiskWriteBytesPerSec = writeRate,
                                CreateTime = createTime,
                                ParentPid = (int)Marshal.ReadIntPtr(entry, OffParentProcessId).ToInt64(),
                                Threads = Marshal.ReadInt32(entry, OffNumberOfThreads),
                                Handles = Marshal.ReadInt32(entry, OffHandleCount),
                            };
```

Then find:

```csharp
                    AllProcesses = byCpu.ToArray();
```

Replace with:

```csharp
                    // Parent names need the whole snapshot, so they are resolved after the walk.
                    // Only the full list carries them: the top-five rankings never show a parent.
                    AllProcesses = ProcessTree.WithParentNames(byCpu);
```

- [ ] **Step 5: Write `ProcessTree`**

Create `Services/ProcessTree.cs`:

```csharp
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Names each process's parent, from one snapshot and nothing else.
    ///
    /// <para>
    /// Pure, and applied once per sample rather than per row, so the process window can show a
    /// parent for every process without opening any of them.
    /// </para>
    ///
    /// <para>
    /// A PID is not an identity. Windows reuses process IDs, so a parent entry that started
    /// <em>after</em> its supposed child is an unrelated newcomer wearing the dead parent's
    /// number, and is reported as <see cref="Gone"/>. Naming it would tell the user an
    /// unrelated program launched the process in front of them.
    /// </para>
    /// </summary>
    public static class ProcessTree
    {
        /// <summary>Shown when a process had a parent that has since exited.</summary>
        public const string Gone = "(gone)";

        /// <summary>
        /// The same rows, in the same order, each carrying its parent's name. Every other field
        /// is preserved.
        /// </summary>
        public static IReadOnlyList<ProcessUsage> WithParentNames(IReadOnlyList<ProcessUsage> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0) return System.Array.Empty<ProcessUsage>();

            var byPid = new Dictionary<int, ProcessUsage>(snapshot.Count);
            foreach (var p in snapshot) byPid[p.Pid] = p;

            var result = new ProcessUsage[snapshot.Count];
            for (int i = 0; i < snapshot.Count; i++)
            {
                var p = snapshot[i];
                result[i] = p with { ParentName = NameOfParent(p, byPid) };
            }
            return result;
        }

        private static string NameOfParent(ProcessUsage child, Dictionary<int, ProcessUsage> byPid)
        {
            // PID 0 is the idle process: a process whose parent is 0 never had one.
            if (child.ParentPid <= 0) return "";
            if (!byPid.TryGetValue(child.ParentPid, out var parent)) return Gone;

            // A parent cannot be younger than its child; that entry is a reused PID.
            if (parent.CreateTime > child.CreateTime) return Gone;

            return parent.Name;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: PASS, all tests in the class.

- [ ] **Step 7: Run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`

Expected: PASS, every test, including the pre-existing `ProcessSamplerTests` and `TaskManagerTests`.

- [ ] **Step 8: Commit**

```bash
git add Services/ProcessSampler.cs Services/ProcessTree.cs tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs
git commit -F - <<'MSG'
feat(processes): threads, handles and parent from the snapshot

Two more reads from the kernel buffer the sampler already walks, and
parent names resolved once per sample by a pure function that rejects
a reused parent PID. No process is opened to show any of it.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 2: View model — columns, sorting, search, totals

**Files:**
- Modify: `ViewModels/TaskManagerViewModel.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs`

**Interfaces:**
- Consumes: `ProcessUsage.ParentPid`, `ParentName`, `Threads`, `Handles`, `FormatBytes` (Task 1); existing `ProcessUsage.FormatRate`.
- Produces:
  - `enum ProcessSortColumn { Name, Pid, Parent, Cpu, Memory, Disk, Uptime, Threads, Handles }`
  - `ProcessRow` properties `Parent`, `Uptime`, `Threads`, `Handles` (all `string`, change-notifying)
  - `static ProcessSortColumn? TaskManagerViewModel.ColumnFor(string? header)`
  - `readonly record struct ProcessTotals(int Count, float Cpu, long Memory, long Disk)`
  - `static ProcessTotals TaskManagerViewModel.Totals(IReadOnlyList<ProcessUsage> rows)`
  - `static string TaskManagerViewModel.FormatTotals(ProcessTotals totals)`
  - `static string TaskManagerViewModel.FormatUptime(TimeSpan uptime)`
  - `static string TaskManagerViewModel.ParentText(ProcessUsage row)`
  - `IReadOnlyList<ProcessUsage> TaskManagerViewModel.Filtered` and `IReadOnlyList<ProcessUsage> TaskManagerViewModel.Snapshot`
  - `bool TaskManagerViewModel.CanEndAllFiltered`

- [ ] **Step 1: Write the failing tests**

Append inside the `ProcessWindowTests` class, before its closing brace:

```csharp
        // ------------------------------------------------------- sorting, search, totals

        private static ProcessUsage R(string name, int pid, float cpu = 0, long mem = 0) =>
            new ProcessUsage(name, pid, cpu, mem);

        [Fact]
        public void Every_column_header_maps_to_its_sort_column_and_nothing_else_does()
        {
            Assert.Equal(ProcessSortColumn.Parent, TaskManagerViewModel.ColumnFor("Parent"));
            Assert.Equal(ProcessSortColumn.Uptime, TaskManagerViewModel.ColumnFor("Uptime"));
            Assert.Equal(ProcessSortColumn.Threads, TaskManagerViewModel.ColumnFor("Threads"));
            Assert.Equal(ProcessSortColumn.Handles, TaskManagerViewModel.ColumnFor("Handles"));
            Assert.Equal(ProcessSortColumn.Cpu, TaskManagerViewModel.ColumnFor("CPU"));
            Assert.Null(TaskManagerViewModel.ColumnFor("Nonsense"));
            Assert.Null(TaskManagerViewModel.ColumnFor(null));
        }

        [Fact]
        public void Sorting_by_threads_descending_puts_the_busiest_first()
        {
            var rows = new List<ProcessUsage>
            {
                R("a", 1) with { Threads = 5 },
                R("b", 2) with { Threads = 50 },
                R("c", 3) with { Threads = 20 },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Threads, descending: true);

            Assert.Equal(new[] { 2, 3, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Sorting_by_handles_ascending_puts_the_fewest_first()
        {
            var rows = new List<ProcessUsage>
            {
                R("a", 1) with { Handles = 900 },
                R("b", 2) with { Handles = 30 },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Handles, descending: false);

            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Sorting_by_uptime_descending_puts_the_longest_running_first()
        {
            // Longest running means the OLDEST creation time.
            var rows = new List<ProcessUsage>
            {
                R("young", 1) with { CreateTime = 900 },
                R("old", 2) with { CreateTime = 100 },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Uptime, descending: true);

            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Sorting_by_parent_orders_by_parent_name()
        {
            var rows = new List<ProcessUsage>
            {
                R("x", 1) with { ParentName = "node.exe" },
                R("y", 2) with { ParentName = "bash.exe" },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Parent, descending: false);

            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Searching_a_parent_name_finds_its_children()
        {
            var all = new[]
            {
                R("find.exe", 1) with { ParentName = "bash.exe" },
                R("notepad.exe", 2) with { ParentName = "explorer.exe" },
            };

            var hits = TaskManagerViewModel.Filter(all, "bash");

            Assert.Equal(new[] { 1 }, hits.Select(r => r.Pid));
        }

        [Fact]
        public void Totals_sum_every_filtered_row()
        {
            var rows = new[]
            {
                R("a", 1, cpu: 1.5f, mem: 100L * 1024 * 1024) with { DiskReadBytesPerSec = 1000 },
                R("b", 2, cpu: 2.5f, mem: 300L * 1024 * 1024) with { DiskWriteBytesPerSec = 2000 },
            };

            var t = TaskManagerViewModel.Totals(rows);

            Assert.Equal(2, t.Count);
            Assert.Equal(4.0, t.Cpu, 3);
            Assert.Equal(400L * 1024 * 1024, t.Memory);
            Assert.Equal(3000L, t.Disk);
        }

        [Fact]
        public void Totals_of_nothing_are_zero()
        {
            Assert.Equal(new ProcessTotals(0, 0f, 0L, 0L),
                TaskManagerViewModel.Totals(Array.Empty<ProcessUsage>()));
        }

        [Fact]
        public void Totals_read_as_one_line()
        {
            string text = TaskManagerViewModel.FormatTotals(
                new ProcessTotals(37, 4.26f, 1932735283L, 12L * 1024 * 1024));

            Assert.Equal("37 processes · 4.3% CPU · 1.8 GB · 12 MB/s", text);
        }

        [Fact]
        public void A_single_process_is_not_pluralised_and_idle_disk_reads_as_zero()
        {
            string text = TaskManagerViewModel.FormatTotals(new ProcessTotals(1, 0f, 0L, 0L));

            Assert.Equal("1 process · 0.0% CPU · 0 MB · 0 B/s", text);
        }

        [Theory]
        [InlineData(45, "45s")]
        [InlineData(12 * 60 + 5, "12m")]
        [InlineData(3 * 3600 + 4 * 60, "3h 04m")]
        [InlineData(2 * 86400 + 5 * 3600, "2d 5h")]
        public void Uptime_reads_at_the_scale_that_matters(int seconds, string expected)
        {
            Assert.Equal(expected, TaskManagerViewModel.FormatUptime(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void A_negative_uptime_from_clock_skew_reads_as_zero()
        {
            Assert.Equal("0s", TaskManagerViewModel.FormatUptime(TimeSpan.FromSeconds(-5)));
        }

        [Fact]
        public void The_parent_column_names_the_parent_and_its_pid_or_says_it_is_gone()
        {
            Assert.Equal("bash.exe (1234)",
                TaskManagerViewModel.ParentText(R("x", 1) with { ParentName = "bash.exe", ParentPid = 1234 }));
            Assert.Equal(ProcessTree.Gone,
                TaskManagerViewModel.ParentText(R("x", 1) with { ParentName = ProcessTree.Gone, ParentPid = 1234 }));
            Assert.Equal("",
                TaskManagerViewModel.ParentText(R("x", 1) with { ParentName = "", ParentPid = 0 }));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: FAIL — `error CS0117: 'ProcessSortColumn' does not contain a definition for 'Parent'` and missing `ColumnFor`, `Totals`, `ProcessTotals`.

- [ ] **Step 3: Extend the enum and the row**

In `ViewModels/TaskManagerViewModel.cs`, replace:

```csharp
    public enum ProcessSortColumn { Name, Pid, Cpu, Memory, Disk }
```

with:

```csharp
    public enum ProcessSortColumn { Name, Pid, Parent, Cpu, Memory, Disk, Uptime, Threads, Handles }

    /// <summary>What the filtered rows cost between them, for the footer.</summary>
    public readonly record struct ProcessTotals(int Count, float Cpu, long Memory, long Disk);
```

In `ProcessRow`, find:

```csharp
        private string _disk = "";
```

Add immediately after it:

```csharp
        private string _parent = "";
        private string _uptime = "";
        private string _threads = "";
        private string _handles = "";
```

Then find:

```csharp
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
```

Replace with:

```csharp
        /// <summary>Parent name and PID, <c>(gone)</c> for an orphan. Mutable: a parent can exit.</summary>
        public string Parent
        {
            get => _parent;
            set { if (_parent != value) { _parent = value; Raise(nameof(Parent)); } }
        }

        public string Uptime
        {
            get => _uptime;
            set { if (_uptime != value) { _uptime = value; Raise(nameof(Uptime)); } }
        }

        public string Threads
        {
            get => _threads;
            set { if (_threads != value) { _threads = value; Raise(nameof(Threads)); } }
        }

        public string Handles
        {
            get => _handles;
            set { if (_handles != value) { _handles = value; Raise(nameof(Handles)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
```

- [ ] **Step 4: Search parent names**

In `Filter`, replace:

```csharp
            return all.Where(p => p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
```

with:

```csharp
            // The parent name is searched too, so typing "bash" shows everything a bash started
            // — which is how a family of leaked children is found and then ended together.
            return all.Where(p =>
                    p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.ParentName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
```

- [ ] **Step 5: Sort the new columns, and map headers**

In `Sort`, replace the `Comparison<ProcessUsage> compare = column switch { ... };` block with:

```csharp
            Comparison<ProcessUsage> compare = column switch
            {
                ProcessSortColumn.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                ProcessSortColumn.Pid => (a, b) => a.Pid.CompareTo(b.Pid),
                ProcessSortColumn.Parent => (a, b) => string.Compare(a.ParentName, b.ParentName, StringComparison.OrdinalIgnoreCase),
                ProcessSortColumn.Cpu => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
                ProcessSortColumn.Memory => (a, b) => a.WorkingSet.CompareTo(b.WorkingSet),
                ProcessSortColumn.Disk => (a, b) => a.DiskBytesPerSec.CompareTo(b.DiskBytesPerSec),
                // Longer uptime is an OLDER creation time, so the comparison is reversed.
                ProcessSortColumn.Uptime => (a, b) => b.CreateTime.CompareTo(a.CreateTime),
                ProcessSortColumn.Threads => (a, b) => a.Threads.CompareTo(b.Threads),
                ProcessSortColumn.Handles => (a, b) => a.Handles.CompareTo(b.Handles),
                _ => (a, b) => 0,
            };
```

Immediately after the closing brace of `Sort`, add:

```csharp
        /// <summary>
        /// The sort column a header text stands for, or null. Kept beside <see cref="Sort"/> so
        /// renaming a header in the XAML cannot silently disconnect it from its column without
        /// failing a test.
        /// </summary>
        public static ProcessSortColumn? ColumnFor(string? header) => header switch
        {
            "Name" => ProcessSortColumn.Name,
            "PID" => ProcessSortColumn.Pid,
            "Parent" => ProcessSortColumn.Parent,
            "CPU" => ProcessSortColumn.Cpu,
            "Memory" => ProcessSortColumn.Memory,
            "Disk" => ProcessSortColumn.Disk,
            "Uptime" => ProcessSortColumn.Uptime,
            "Threads" => ProcessSortColumn.Threads,
            "Handles" => ProcessSortColumn.Handles,
            _ => null,
        };

        /// <summary>What a set of rows costs between them.</summary>
        public static ProcessTotals Totals(IReadOnlyList<ProcessUsage> rows)
        {
            if (rows == null || rows.Count == 0) return new ProcessTotals(0, 0f, 0L, 0L);

            float cpu = 0f;
            long memory = 0, disk = 0;
            foreach (var r in rows)
            {
                cpu += r.CpuPercent;
                memory += r.WorkingSet;
                disk += r.DiskBytesPerSec;
            }
            return new ProcessTotals(rows.Count, cpu, memory, disk);
        }

        /// <summary>
        /// The footer's summary, e.g. "37 processes · 4.2% CPU · 1.8 GB · 12 MB/s". Tells you what
        /// a filtered group is costing before you decide to end it.
        /// </summary>
        public static string FormatTotals(ProcessTotals t)
        {
            string count = t.Count.ToString(CultureInfo.InvariantCulture)
                           + (t.Count == 1 ? " process" : " processes");
            string cpu = t.Cpu.ToString("F1", CultureInfo.InvariantCulture) + "% CPU";
            string disk = t.Disk > 0 ? ProcessUsage.FormatRate(t.Disk) : "0 B/s";
            return count + " · " + cpu + " · " + ProcessUsage.FormatBytes(t.Memory) + " · " + disk;
        }

        /// <summary>
        /// How long a process has run, at the one scale that matters: seconds, then minutes,
        /// then hours and minutes, then days and hours. A clock that moved backwards reads as 0s
        /// rather than a negative age.
        /// </summary>
        public static string FormatUptime(TimeSpan uptime)
        {
            if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
            var inv = CultureInfo.InvariantCulture;

            if (uptime.TotalMinutes < 1) return ((int)uptime.TotalSeconds).ToString(inv) + "s";
            if (uptime.TotalHours < 1) return ((int)uptime.TotalMinutes).ToString(inv) + "m";
            if (uptime.TotalDays < 1)
                return ((int)uptime.TotalHours).ToString(inv) + "h " + uptime.Minutes.ToString("00", inv) + "m";
            return ((int)uptime.TotalDays).ToString(inv) + "d " + uptime.Hours.ToString(inv) + "h";
        }

        /// <summary>The Parent column: "bash.exe (1234)", <c>(gone)</c>, or empty.</summary>
        public static string ParentText(ProcessUsage row)
        {
            if (string.IsNullOrEmpty(row.ParentName)) return "";
            if (row.ParentName == ProcessTree.Gone) return ProcessTree.Gone;
            return row.ParentName + " (" + row.ParentPid.ToString(CultureInfo.InvariantCulture) + ")";
        }
```

- [ ] **Step 6: Carry totals and the filtered set through `Refresh`**

In the fields block of `TaskManagerViewModel`, find:

```csharp
        private string _message = "";
```

Add immediately after it:

```csharp
        private string _totals = "0 processes";
        private IReadOnlyList<ProcessUsage> _filtered = Array.Empty<ProcessUsage>();
        private IReadOnlyList<ProcessUsage> _snapshot = Array.Empty<ProcessUsage>();
```

Replace the `SearchText` property with:

```csharp
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;
                _searchText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEndAllFiltered));
                Refresh();
            }
        }

        /// <summary>
        /// The rows the filter currently matches, as data rather than as display rows. What
        /// End all filtered plans against.
        /// </summary>
        public IReadOnlyList<ProcessUsage> Filtered => _filtered;

        /// <summary>The full snapshot the list was built from, for walking parent chains.</summary>
        public IReadOnlyList<ProcessUsage> Snapshot => _snapshot;

        /// <summary>
        /// End all filtered is offered only while a filter is typed and matches something. With
        /// no filter, "all filtered" is every process on the machine, and the button is simply
        /// not available rather than asking.
        /// </summary>
        public bool CanEndAllFiltered => !string.IsNullOrWhiteSpace(_searchText) && _count > 0;
```

Replace the `Count` property with:

```csharp
        /// <summary>Row count after filtering.</summary>
        public int Count
        {
            get => _count;
            private set
            {
                if (_count == value) return;
                _count = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Footer));
                OnPropertyChanged(nameof(CanEndAllFiltered));
            }
        }
```

Replace the `Footer` property with:

```csharp
        /// <summary>One line: the last result if there is one, then what the filtered rows cost.</summary>
        public string Footer => string.IsNullOrEmpty(_message) ? _totals : _message + "   ·   " + _totals;
```

In `Refresh()`, replace:

```csharp
            var snapshot = _sampler.AllProcesses;
            var rows = Filter(snapshot, _searchText).ToList();
            Sort(rows, _sortColumn, _sortDescending);
```

with:

```csharp
            var snapshot = _sampler.AllProcesses;
            var rows = Filter(snapshot, _searchText).ToList();
            Sort(rows, _sortColumn, _sortDescending);
            _snapshot = snapshot;
            _filtered = rows;
```

Replace:

```csharp
            bool hasCpu = _sampler.HasCpuData;
            for (int i = 0; i < rows.Count; i++)
            {
                Rows[i].Cpu = CpuTextFor(rows[i], hasCpu);
                Rows[i].Memory = rows[i].WorkingSetText;
                Rows[i].Disk = rows[i].DiskBytesPerSec > 0 ? rows[i].DiskText : "—";
            }

            Count = rows.Count;
```

with:

```csharp
            bool hasCpu = _sampler.HasCpuData;
            var now = DateTime.Now;
            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < rows.Count; i++)
            {
                var p = rows[i];
                Rows[i].Cpu = CpuTextFor(p, hasCpu);
                Rows[i].Memory = p.WorkingSetText;
                Rows[i].Disk = p.DiskBytesPerSec > 0 ? p.DiskText : "—";
                Rows[i].Parent = ParentText(p);
                Rows[i].Uptime = p.CreateTime > 0 ? FormatUptime(now - DateTime.FromFileTime(p.CreateTime)) : "";
                Rows[i].Threads = p.Threads.ToString(inv);
                Rows[i].Handles = p.Handles.ToString(inv);
            }

            _totals = FormatTotals(Totals(rows));
            OnPropertyChanged(nameof(Footer));
            Count = rows.Count;
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests|FullyQualifiedName~TaskManagerTests"`

Expected: PASS. The existing `TaskManagerTests` must stay green — if one asserted the old footer format, update its expectation to the totals format and say so in your report.

- [ ] **Step 8: Run the whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests` — expected PASS.

```bash
git add ViewModels/TaskManagerViewModel.cs tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs
git commit -F - <<'MSG'
feat(processes): sort, search and total the new columns

Parent, uptime, threads and handles become sortable, a parent name is
searchable so a whole family can be filtered at once, and the footer
reports what the filtered rows cost between them.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 3: What End all filtered ends

**Files:**
- Create: `ViewModels/BulkEndPlan.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs`

**Interfaces:**
- Consumes: `ProcessUsage` (Task 1); `ProcessControl.IsCriticalProcess(string)` (existing).
- Produces:
  - `sealed record BulkEndExcluded(ProcessUsage Process, string Reason)`
  - `sealed record BulkEndPlan(IReadOnlyList<ProcessUsage> ToEnd, IReadOnlyList<BulkEndExcluded> Excluded)` with `static BulkEndPlan Build(IReadOnlyList<ProcessUsage> filtered, int selfPid, IReadOnlyCollection<int> selfAncestors)` and `static IReadOnlyCollection<int> AncestorsOf(int pid, IReadOnlyList<ProcessUsage> snapshot)`
  - `sealed record BulkEndResult(int Ended, int AccessDenied, int Survived, int Failed)` with `string Describe()`

- [ ] **Step 1: Write the failing tests**

Append inside `ProcessWindowTests`, before its closing brace:

```csharp
        // ------------------------------------------------------- end all filtered

        private static ProcessUsage Q(string name, int pid, int parent = 0, long created = 10) =>
            new ProcessUsage(name, pid, 1f, 1024) { ParentPid = parent, CreateTime = created };

        [Fact]
        public void An_empty_filter_result_plans_nothing()
        {
            var plan = BulkEndPlan.Build(Array.Empty<ProcessUsage>(), 999, Array.Empty<int>());

            Assert.Empty(plan.ToEnd);
            Assert.Empty(plan.Excluded);
        }

        [Fact]
        public void Ordinary_matches_are_all_planned()
        {
            var plan = BulkEndPlan.Build(
                new[] { Q("find.exe", 10), Q("find.exe", 11) }, 999, Array.Empty<int>());

            Assert.Equal(new[] { 10, 11 }, plan.ToEnd.Select(p => p.Pid));
            Assert.Empty(plan.Excluded);
        }

        [Fact]
        public void A_critical_windows_process_is_excluded_with_a_reason()
        {
            var plan = BulkEndPlan.Build(
                new[] { Q("csrss.exe", 600), Q("cmd.exe", 700) }, 999, Array.Empty<int>());

            Assert.Equal(new[] { 700 }, plan.ToEnd.Select(p => p.Pid));
            var excluded = Assert.Single(plan.Excluded);
            Assert.Equal(600, excluded.Process.Pid);
            Assert.Contains("Windows", excluded.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void MicaStats_itself_is_never_planned()
        {
            var plan = BulkEndPlan.Build(
                new[] { Q("MicaStats.exe", 999), Q("Micro.exe", 5) }, 999, Array.Empty<int>());

            Assert.Equal(new[] { 5 }, plan.ToEnd.Select(p => p.Pid));
            Assert.Equal(999, Assert.Single(plan.Excluded).Process.Pid);
        }

        [Fact]
        public void A_process_MicaStats_runs_inside_is_never_planned()
        {
            var plan = BulkEndPlan.Build(
                new[] { Q("WindowsTerminal.exe", 50), Q("pwsh.exe", 60) }, 999, new[] { 50 });

            Assert.Equal(new[] { 60 }, plan.ToEnd.Select(p => p.Pid));
            Assert.Equal(50, Assert.Single(plan.Excluded).Process.Pid);
        }

        [Fact]
        public void Nothing_outside_the_filtered_input_is_ever_planned()
        {
            var input = new[] { Q("a.exe", 1), Q("b.exe", 2) };

            var plan = BulkEndPlan.Build(input, 999, new[] { 3, 4 });

            Assert.All(plan.ToEnd, p => Assert.Contains(p, input));
            Assert.All(plan.Excluded, e => Assert.Contains(e.Process, input));
            Assert.Equal(input.Length, plan.ToEnd.Count + plan.Excluded.Count);
        }

        [Fact]
        public void Ancestors_are_walked_up_the_parent_chain()
        {
            var snapshot = new[]
            {
                Q("explorer.exe", 10, parent: 5, created: 1),
                Q("WindowsTerminal.exe", 20, parent: 10, created: 2),
                Q("MicaStats.exe", 30, parent: 20, created: 3),
            };

            var ancestors = BulkEndPlan.AncestorsOf(30, snapshot);

            Assert.Equal(new[] { 10, 20 }, ancestors.OrderBy(p => p));
        }

        [Fact]
        public void An_ancestor_walk_stops_at_a_recycled_parent_pid()
        {
            // 10 was reused by a process newer than its supposed child; it is not an ancestor.
            var snapshot = new[]
            {
                Q("unrelated.exe", 10, parent: 0, created: 99),
                Q("MicaStats.exe", 30, parent: 10, created: 3),
            };

            Assert.Empty(BulkEndPlan.AncestorsOf(30, snapshot));
        }

        [Fact]
        public void An_ancestor_walk_terminates_on_a_cycle()
        {
            // Not possible from a correct kernel snapshot, but a walk that can loop forever on
            // bad input must not exist in code that runs before ending processes.
            var snapshot = new[]
            {
                Q("a.exe", 1, parent: 2, created: 1),
                Q("b.exe", 2, parent: 1, created: 1),
                Q("MicaStats.exe", 3, parent: 1, created: 2),
            };

            var ancestors = BulkEndPlan.AncestorsOf(3, snapshot);

            Assert.Equal(new[] { 1, 2 }, ancestors.OrderBy(p => p));
        }

        [Fact]
        public void The_outcome_reads_as_one_sentence()
        {
            Assert.Equal("Ended 35 · 2 need administrator · 0 survived",
                new BulkEndResult(35, 2, 0, 0).Describe());
            Assert.Equal("Ended 3 · 0 need administrator · 1 survived · 1 failed",
                new BulkEndResult(3, 0, 1, 1).Describe());
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: FAIL — `error CS0246: The type or namespace name 'BulkEndPlan' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `ViewModels/BulkEndPlan.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>A matching process that End all filtered will not end, and why.</summary>
    public sealed record BulkEndExcluded(ProcessUsage Process, string Reason);

    /// <summary>
    /// What End all filtered will end, and what it refuses to.
    ///
    /// <para>
    /// Pure, so the decision behind the most destructive button in the app is tested rather
    /// than trusted. It only ever partitions its input: nothing outside the filtered rows can be
    /// planned, and every filtered row is accounted for on one side or the other. Refusals are
    /// listed with their reason rather than silently dropped, so the preview never shows a count
    /// that differs from what the user can see.
    /// </para>
    /// </summary>
    public sealed record BulkEndPlan(IReadOnlyList<ProcessUsage> ToEnd, IReadOnlyList<BulkEndExcluded> Excluded)
    {
        /// <summary>
        /// Partitions the filtered rows. Excluded: core Windows processes, MicaStats itself, and
        /// any process MicaStats is running inside — ending a terminal whose job object owns this
        /// app would end the app from under the user.
        /// </summary>
        public static BulkEndPlan Build(
            IReadOnlyList<ProcessUsage> filtered, int selfPid, IReadOnlyCollection<int> selfAncestors)
        {
            var toEnd = new List<ProcessUsage>();
            var excluded = new List<BulkEndExcluded>();
            if (filtered == null) return new BulkEndPlan(toEnd, excluded);

            var ancestors = selfAncestors as ISet<int> ?? new HashSet<int>(selfAncestors ?? Array.Empty<int>());

            foreach (var p in filtered)
            {
                if (ProcessControl.IsCriticalProcess(p.Name))
                    excluded.Add(new BulkEndExcluded(p, "core Windows process"));
                else if (p.Pid == selfPid)
                    excluded.Add(new BulkEndExcluded(p, "this is MicaStats"));
                else if (ancestors.Contains(p.Pid))
                    excluded.Add(new BulkEndExcluded(p, "MicaStats is running inside it"));
                else
                    toEnd.Add(p);
            }

            return new BulkEndPlan(toEnd, excluded);
        }

        /// <summary>
        /// Every process <paramref name="pid"/> descends from, walking parent links in one
        /// snapshot. Stops at a missing parent, at a reused parent PID (a parent younger than its
        /// child), and on a cycle — a walk that could loop forever must not run before a kill.
        /// </summary>
        public static IReadOnlyCollection<int> AncestorsOf(int pid, IReadOnlyList<ProcessUsage> snapshot)
        {
            var result = new HashSet<int>();
            if (snapshot == null || snapshot.Count == 0) return result;

            var byPid = new Dictionary<int, ProcessUsage>(snapshot.Count);
            foreach (var p in snapshot) byPid[p.Pid] = p;

            if (!byPid.TryGetValue(pid, out var current)) return result;

            while (current.ParentPid > 0 &&
                   byPid.TryGetValue(current.ParentPid, out var parent) &&
                   parent.CreateTime <= current.CreateTime &&
                   parent.Pid != pid &&
                   result.Add(parent.Pid))
            {
                current = parent;
            }

            return result;
        }
    }

    /// <summary>What happened when a planned set was ended.</summary>
    /// <param name="Ended">Gone, verified by asking the kernel whether each one exited.</param>
    /// <param name="AccessDenied">Refused for lack of privilege; retried one at a time from the list.</param>
    /// <param name="Survived">Reported ended, but the kernel says still running.</param>
    /// <param name="Failed">Any other failure, including an exception while ending it.</param>
    public sealed record BulkEndResult(int Ended, int AccessDenied, int Survived, int Failed)
    {
        /// <summary>
        /// One sentence for the footer, e.g. "Ended 35 · 2 need administrator · 0 survived".
        /// Failures are named only when there are some, so the common case stays short.
        /// </summary>
        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            string text = "Ended " + Ended.ToString(inv)
                          + " · " + AccessDenied.ToString(inv) + " need administrator"
                          + " · " + Survived.ToString(inv) + " survived";
            return Failed > 0 ? text + " · " + Failed.ToString(inv) + " failed" : text;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ViewModels/BulkEndPlan.cs tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs
git commit -F - <<'MSG'
feat(processes): decide what end all filtered may end

A pure partition of the filtered rows: core Windows processes,
MicaStats itself and anything MicaStats runs inside are listed with a
reason rather than ended, and nothing outside the filter can be
planned. The ancestor walk stops on a reused parent pid or a cycle.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 4: User account and elevation for one process

**Files:**
- Create: `Services/ProcessAccountReader.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `sealed record ProcessAccount(string User, bool? Elevated)` and `static bool ProcessAccountReader.TryRead(int pid, out ProcessAccount account, out string reason)`

- [ ] **Step 1: Write the failing tests**

Append inside `ProcessWindowTests`, before its closing brace:

```csharp
        // ------------------------------------------------------- account and elevation

        [Fact]
        public void The_current_process_reports_the_current_user()
        {
            bool ok = ProcessAccountReader.TryRead(Environment.ProcessId, out var account, out _);

            Assert.True(ok);
            Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().Name, account.User,
                ignoreCase: true);
        }

        [Fact]
        public void The_current_process_reports_its_real_elevation()
        {
            // Under UAC a member of Administrators is in the role only when elevated, so the
            // principal check agrees with the token exactly when the reader is right.
            bool ok = ProcessAccountReader.TryRead(Environment.ProcessId, out var account, out _);
            var principal = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent());

            Assert.True(ok);
            Assert.Equal(principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
                account.Elevated);
        }

        [Fact]
        public void A_process_that_is_not_running_is_reported_with_a_reason_not_an_exception()
        {
            // Windows PIDs are multiples of 4 and stay far below int.MaxValue for the life of a
            // boot, so this reaches OpenProcess and fails there.
            bool ok = ProcessAccountReader.TryRead(int.MaxValue - 3, out var account, out string reason);

            Assert.False(ok);
            Assert.Equal("", account.User);
            Assert.Null(account.Elevated);
            Assert.Equal("Process has exited", reason);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: FAIL — `error CS0103: The name 'ProcessAccountReader' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `Services/ProcessAccountReader.cs`:

```csharp
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>Who a process runs as, and whether it holds administrator rights.</summary>
    /// <param name="User">DOMAIN\name, or the SID string when the account cannot be named.</param>
    /// <param name="Elevated">Null when the token could be opened for its user but not its elevation.</param>
    public sealed record ProcessAccount(string User, bool? Elevated)
    {
        /// <summary>The empty answer, for a process that could not be read.</summary>
        public static ProcessAccount None { get; } = new("", null);
    }

    /// <summary>
    /// Reads a process's account and elevation from its token, for one process at a time.
    ///
    /// <para>
    /// Deliberately separate from <see cref="Watchdog.ProcessDetails"/>: that reader's contract is
    /// relied on by the watchdog, and a token read is a different, more often refused operation.
    /// This is called only for the row the user has selected, off the UI thread — never for the
    /// list, which must not open a handle per row.
    /// </para>
    /// </summary>
    public static class ProcessAccountReader
    {
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int TOKEN_QUERY = 0x0008;
        private const int TokenUser = 1;
        private const int TokenElevation = 20;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        /// <summary>
        /// Reads both, or explains why not. Never throws: the caller is a background task whose
        /// only job is to fill in a pane.
        /// </summary>
        /// <param name="reason">
        /// "Process has exited", "Access denied", or "Unavailable (error N)" when false.
        /// </param>
        public static bool TryRead(int pid, out ProcessAccount account, out string reason)
        {
            account = ProcessAccount.None;
            reason = "";

            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                reason = Describe(Marshal.GetLastWin32Error());
                return false;
            }

            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY, out token))
                {
                    reason = Describe(Marshal.GetLastWin32Error());
                    return false;
                }

                string? user = ReadUser(token);
                if (user == null)
                {
                    reason = "Unavailable";
                    return false;
                }

                account = new ProcessAccount(user, ReadElevation(token));
                return true;
            }
            catch (Exception)
            {
                account = ProcessAccount.None;
                reason = "Unavailable";
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(process);
            }
        }

        /// <summary>
        /// The account as DOMAIN\name. Asked twice: once for the size, once for real. Falls back
        /// to the SID string for an account that cannot be translated, such as one deleted since
        /// the process started.
        /// </summary>
        private static string? ReadUser(IntPtr token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);
            if (needed <= 0 || needed > 64 * 1024) return null;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;

                // TOKEN_USER begins with SID_AND_ATTRIBUTES, whose first field is the PSID.
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                try { return ((NTAccount)sid.Translate(typeof(NTAccount))).Value; }
                catch (IdentityNotMappedException) { return sid.Value; }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>TOKEN_ELEVATION is a single DWORD: non-zero when elevated.</summary>
        private static bool? ReadElevation(IntPtr token)
        {
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                return GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out _)
                    ? Marshal.ReadInt32(buffer) != 0
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string Describe(int error) => error switch
        {
            ERROR_INVALID_PARAMETER => "Process has exited",
            ERROR_ACCESS_DENIED => "Access denied",
            _ => "Unavailable (error " + error.ToString(CultureInfo.InvariantCulture) + ")",
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessWindowTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/ProcessAccountReader.cs tests/Kil0bitSystemMonitor.Tests/ProcessWindowTests.cs
git commit -F - <<'MSG'
feat(processes): read the account and elevation of one process

From its token, for the selected row only and never for the list.
Falls back to the SID for an account that cannot be named, and reports
a plain reason rather than throwing when the process is protected or
already gone.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 5: The window — columns, header sorting, detail pane, totals

**Files:**
- Create: `ViewModels/ProcessDetailViewModel.cs`
- Modify: `TaskManagerWindow.xaml`
- Modify: `TaskManagerWindow.xaml.cs`

**Interfaces:**
- Consumes: `ProcessRow.Parent`, `Uptime`, `Threads`, `Handles`, `TaskManagerViewModel.ColumnFor`, `Footer` (Task 2); `ProcessAccountReader.TryRead` (Task 4); `Services.Watchdog.ProcessDetails.TryRead`, `ProcessControl.HasExited(int, long)` (watchdog branch).
- Produces: `sealed class ProcessDetailViewModel : INotifyPropertyChanged` with `Title`, `ImagePath`, `CommandLine`, `User`, `Elevated` (strings), `void Load(ProcessRow row)`, `void Clear()`; `TaskManagerWindow.Detail`.

This task is WPF and has no unit tests. It is verified by building, by the whole suite (the virtualization test inspects this XAML), and by the manual check in Task 8.

- [ ] **Step 1: Write the detail pane's view model**

Create `ViewModels/ProcessDetailViewModel.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Watchdog;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>
    /// The four facts about the selected process that need a handle: path, command line, user,
    /// elevation.
    ///
    /// <para>
    /// Read for one row, when it is selected, on a background task — never for the list, which
    /// must not open a handle per row. A read that finishes after the user has already selected
    /// something else is discarded, so the pane can never show one process's command line under
    /// another's name.
    /// </para>
    /// </summary>
    public sealed class ProcessDetailViewModel : INotifyPropertyChanged
    {
        private const string Prompt = "Select a process to see its path, command line and account.";

        private int _version;
        private string _title = Prompt;
        private string _imagePath = "";
        private string _commandLine = "";
        private string _user = "";
        private string _elevated = "";

        public string Title { get => _title; private set => Set(ref _title, value); }
        public string ImagePath { get => _imagePath; private set => Set(ref _imagePath, value); }
        public string CommandLine { get => _commandLine; private set => Set(ref _commandLine, value); }
        public string User { get => _user; private set => Set(ref _user, value); }
        public string Elevated { get => _elevated; private set => Set(ref _elevated, value); }

        /// <summary>Back to the prompt, and any read in flight is discarded.</summary>
        public void Clear()
        {
            Interlocked.Increment(ref _version);
            Title = Prompt;
            ImagePath = CommandLine = User = Elevated = "";
        }

        /// <summary>
        /// Shows the row's identity at once and fills the four facts when the read returns.
        /// </summary>
        public void Load(ProcessRow row)
        {
            int version = Interlocked.Increment(ref _version);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            int pid = row.Pid;
            long created = row.CreateTime;

            Title = row.Name + "   ·   PID " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ImagePath = CommandLine = User = Elevated = "Reading…";

            Task.Run(() =>
            {
                string path, cmd, user, elevated;
                try
                {
                    // Identity first: the PID may already belong to a different process.
                    if (ProcessControl.HasExited(pid, created))
                    {
                        path = cmd = user = elevated = "Process has exited";
                    }
                    else
                    {
                        if (!ProcessDetails.TryRead(pid, out path, out cmd))
                            path = cmd = "Unavailable — protected, or already exited";
                        else if (cmd.Length == 0)
                            cmd = "Unavailable";

                        if (ProcessAccountReader.TryRead(pid, out var account, out string reason))
                        {
                            user = account.User;
                            elevated = account.Elevated switch { true => "Yes", false => "No", _ => "Unknown" };
                        }
                        else
                        {
                            user = elevated = reason;
                        }
                    }
                }
                catch (Exception ex)
                {
                    path = cmd = user = elevated = "Unavailable";
                    DiagnosticsLog.Error("processes", "Reading process details failed", ex);
                }

                dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (version != Volatile.Read(ref _version)) return;   // superseded
                    ImagePath = path;
                    CommandLine = cmd;
                    User = user;
                    Elevated = elevated;
                }));
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
```

- [ ] **Step 2: Widen the window and add the detail row**

In `TaskManagerWindow.xaml`, replace:

```xml
    Width="640" Height="700" MinWidth="520" MinHeight="420"
```

with:

```xml
    Width="900" Height="760" MinWidth="840" MinHeight="520"
```

Replace the outer grid's row definitions:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <!-- Fixed rather than Auto. With Auto the star-sized list above took the space
                 first and the footer was laid out half outside the window; a definite height
                 is allocated before the star row gets its remainder. -->
            <RowDefinition Height="42" />
        </Grid.RowDefinitions>
```

with:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <!-- The detail pane and the footer are fixed rather than Auto. With Auto the
                 star-sized list above took the space first and they were laid out half outside
                 the window; a definite height is allocated before the star row gets its
                 remainder. The pane is always present, showing a prompt when nothing is
                 selected, so selecting a row never makes the list jump. -->
            <RowDefinition Height="112" />
            <RowDefinition Height="42" />
        </Grid.RowDefinitions>
```

Then change the footer grid from `<Grid Grid.Row="2" Margin="0,10,0,0">` to `<Grid Grid.Row="3" Margin="0,10,0,0">`.

- [ ] **Step 3: Replace the columns and wire header clicks**

In `TaskManagerWindow.xaml`, on the `<ListView x:Name="ProcessList"` element, add this attribute after `SelectionChanged="OnSelectionChanged"`:

```xml
                      GridViewColumnHeader.Click="OnHeaderClick"
```

Replace the whole `<GridView AllowsColumnReorder="False"> ... </GridView>` element with:

```xml
                    <GridView AllowsColumnReorder="False">
                        <!-- Widths total 776, inside the client area at the 840 minimum width
                             once the margins, the list border and a vertical scrollbar are taken
                             out. Overflowing produces a horizontal scrollbar, and a process list
                             that scrolls sideways is a process list nobody can read.
                             Header text is also the sort key: TaskManagerViewModel.ColumnFor maps
                             it, and a test pins every mapping. Rename both together. -->
                        <GridViewColumn Header="Name" Width="170">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource ColumnText}" Text="{Binding Name}" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="PID" Width="58">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Pid}"
                                               Foreground="{StaticResource RowInk}" FontWeight="Normal" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="Parent" Width="150">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource ColumnText}" Text="{Binding Parent}"
                                               Foreground="{StaticResource Muted}" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="CPU" Width="62">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Cpu}" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="Memory" Width="76">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Memory}" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="Disk" Width="72">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Disk}" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="Uptime" Width="66">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Uptime}"
                                               Foreground="{StaticResource RowInk}" FontWeight="Normal" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="Threads" Width="58">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Threads}"
                                               Foreground="{StaticResource RowInk}" FontWeight="Normal" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="Handles" Width="64">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate DataType="{x:Type vm:ProcessRow}">
                                    <TextBlock Style="{StaticResource NumberText}" Text="{Binding Handles}"
                                               Foreground="{StaticResource RowInk}" FontWeight="Normal" />
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                    </GridView>
```

- [ ] **Step 4: Add the detail pane**

In `TaskManagerWindow.xaml`, immediately before the footer's `<!--` comment block that begins `Actions and result on one row.`, insert:

```xml
        <!--
          The selected process: the four facts that need a handle, read for this one row only.
          Selectable text, so a command line can be copied out.
        -->
        <Border Grid.Row="2" Margin="0,10,0,0" Padding="12,9" CornerRadius="6"
                Background="#141419" BorderBrush="#22FFFFFF" BorderThickness="1"
                DataContext="{Binding Detail, RelativeSource={RelativeSource AncestorType=Window}}">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="86" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <TextBlock Grid.Row="0" Grid.ColumnSpan="2" Text="{Binding Title}" Margin="0,0,0,6"
                           FontSize="12" FontWeight="SemiBold" Foreground="#EDEDF2"
                           TextTrimming="CharacterEllipsis" />

                <TextBlock Grid.Row="1" Grid.Column="0" Text="Path" FontSize="11" Foreground="{StaticResource Muted}" />
                <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding ImagePath, Mode=OneWay}" IsReadOnly="True"
                         Background="Transparent" BorderThickness="0" Padding="0" FontSize="11"
                         Foreground="{StaticResource RowInk}" />

                <TextBlock Grid.Row="2" Grid.Column="0" Text="Command line" FontSize="11" Foreground="{StaticResource Muted}" />
                <TextBox Grid.Row="2" Grid.Column="1" Text="{Binding CommandLine, Mode=OneWay}" IsReadOnly="True"
                         Background="Transparent" BorderThickness="0" Padding="0" FontSize="11"
                         Foreground="{StaticResource RowInk}" />

                <TextBlock Grid.Row="3" Grid.Column="0" Text="Account" FontSize="11" Foreground="{StaticResource Muted}" />
                <TextBlock Grid.Row="3" Grid.Column="1" FontSize="11" Foreground="{StaticResource RowInk}"
                           TextTrimming="CharacterEllipsis">
                    <Run Text="{Binding User, Mode=OneWay}" />
                    <Run Text="   ·   Elevated: " Foreground="{StaticResource Muted}" />
                    <Run Text="{Binding Elevated, Mode=OneWay}" />
                </TextBlock>
            </Grid>
        </Border>

```

- [ ] **Step 5: Wire the code-behind**

In `TaskManagerWindow.xaml.cs`, find:

```csharp
        private readonly TaskManagerViewModel _model;
```

Add immediately after it:

```csharp

        /// <summary>The detail pane's state, bound from the XAML.</summary>
        public ProcessDetailViewModel Detail { get; } = new();
```

Replace `OnSelectionChanged` with:

```csharp
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EndTaskButton.IsEnabled = ProcessList.SelectedItem is ProcessRow;

            // A new selection invalidates the previous refusal.
            _pendingElevation = null;
            RetryElevated.Visibility = Visibility.Collapsed;

            if (ProcessList.SelectedItem is ProcessRow row) Detail.Load(row);
            else Detail.Clear();
        }

        /// <summary>
        /// Sorts by the clicked column, flipping direction on a second click. The header text is
        /// the key, mapped by <see cref="TaskManagerViewModel.ColumnFor"/>.
        /// </summary>
        private void OnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
            if (TaskManagerViewModel.ColumnFor(header.Column.Header as string) is { } column)
                _model.SortBy(column);
        }
```

- [ ] **Step 6: Build and run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`
Expected: PASS, including `The_process_list_is_virtualized_and_recycling`.

- [ ] **Step 7: Commit**

```bash
git add ViewModels/ProcessDetailViewModel.cs TaskManagerWindow.xaml TaskManagerWindow.xaml.cs
git commit -F - <<'MSG'
feat(processes): parent, uptime, threads, handles and a detail pane

Four new columns from the snapshot, every column sortable by clicking
its header, the filtered totals in the footer, and a pane that reads
the path, command line and account of the selected process only, off
the UI thread, discarding a read that a newer selection superseded.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 6: End all filtered

**Files:**
- Create: `Services/BulkEnd.cs`
- Create: `BulkEndDialog.xaml`
- Create: `BulkEndDialog.xaml.cs`
- Modify: `TaskManagerWindow.xaml`
- Modify: `TaskManagerWindow.xaml.cs`

**Interfaces:**
- Consumes: `BulkEndPlan`, `BulkEndResult` (Task 3); `TaskManagerViewModel.Filtered`, `Snapshot`, `CanEndAllFiltered`, `Message`, `Totals`, `FormatTotals` (Task 2); `ProcessControl.TryEndTask`, `ProcessControl.HasExited(int, long)`, `EndTaskResult`, `DiagnosticsLog`.
- Produces: `static BulkEndResult BulkEnd.Run(IReadOnlyList<ProcessUsage> targets)`; `BulkEndDialog(BulkEndPlan plan)` whose `ShowDialog()` returns true only on typed confirmation.

This task has no unit tests: it is termination and WPF. Its decisions are already tested in Task 3.

- [ ] **Step 1: Write the runner**

Create `Services/BulkEnd.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.ViewModels;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Ends a planned set of processes and reports what actually happened.
    ///
    /// <para>
    /// End first, wait once, then verify. Terminating all of them before any wait keeps a batch
    /// of dozens to a couple of seconds; waiting per process would take minutes. Each reported
    /// termination is then checked by asking the kernel whether that exact process exited,
    /// because a process wedged in kernel I/O can report a successful kill and keep running.
    /// </para>
    ///
    /// <para>
    /// No tree kill. A filter matches what the user can see; ending children it did not match
    /// would end processes that were never in the preview. Blocking, so it must never run on the
    /// UI thread.
    /// </para>
    /// </summary>
    public static class BulkEnd
    {
        /// <summary>How long to let a batch of terminations land before verifying them.</summary>
        private const int SettleMs = 2000;

        private const string Area = "processes";

        /// <summary>Ends every target and counts the outcomes. Never throws.</summary>
        public static BulkEndResult Run(IReadOnlyList<ProcessUsage> targets)
        {
            if (targets == null || targets.Count == 0) return new BulkEndResult(0, 0, 0, 0);

            int ended = 0, denied = 0, failed = 0;
            var toVerify = new List<ProcessUsage>();

            foreach (var p in targets)
            {
                try
                {
                    var result = ProcessControl.TryEndTask(p.Pid, p.CreateTime, p.Name, out string message);
                    switch (result)
                    {
                        case EndTaskResult.Terminated:
                        case EndTaskResult.AlreadyExited:
                            toVerify.Add(p);
                            break;
                        case EndTaskResult.Recycled:
                            // The PID belongs to another process now; the one planned is gone.
                            ended++;
                            break;
                        case EndTaskResult.AccessDenied:
                            denied++;
                            Log(p, "ACCESS-DENIED", message);
                            break;
                        default:
                            failed++;
                            Log(p, "FAILED", message);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // One process that throws must not abandon the rest of the batch.
                    failed++;
                    DiagnosticsLog.Error(Area, "Ending pid " + p.Pid.ToString(CultureInfo.InvariantCulture) + " failed", ex);
                }
            }

            int survived = 0;
            if (toVerify.Count > 0)
            {
                System.Threading.Thread.Sleep(SettleMs);
                foreach (var p in toVerify)
                {
                    bool gone;
                    try { gone = ProcessControl.HasExited(p.Pid, p.CreateTime); }
                    catch (Exception) { gone = false; }

                    if (gone) { ended++; Log(p, "ENDED", ""); }
                    else { survived++; Log(p, "SURVIVED", "reported ended but the kernel says it is still running"); }
                }
            }

            var summary = new BulkEndResult(ended, denied, survived, failed);
            DiagnosticsLog.Log(Area, "End all filtered: " + summary.Describe());
            return summary;
        }

        private static void Log(ProcessUsage p, string outcome, string detail) =>
            DiagnosticsLog.Log(Area,
                outcome + " " + p.Name + " pid=" + p.Pid.ToString(CultureInfo.InvariantCulture)
                + (detail.Length == 0 ? "" : " :: " + detail));
    }
}
```

- [ ] **Step 2: Write the dialog markup**

Create `BulkEndDialog.xaml`:

```xml
<Window
    x:Class="Kil0bitSystemMonitor.BulkEndDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.modernwpf.com/2019"
    Title="End processes"
    Width="560" Height="560" MinWidth="460" MinHeight="400"
    WindowStartupLocation="CenterOwner"
    ShowInTaskbar="False"
    ResizeMode="CanResizeWithGrip"
    Background="#0E0E13"
    Foreground="#E9EDEDF2"
    FontFamily="Segoe UI Variable Text, Segoe UI"
    ui:ThemeManager.RequestedTheme="Dark"
    UseLayoutRounding="True">
    <!--
      The preview for the most destructive button in the app. It shows exactly what will end
      and what will not, and asks for the count to be typed: a count rather than a word, so a
      filter that changed since the user last looked forces them to read the number again.
    -->
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Margin="0,0,0,10">
            <TextBlock x:Name="Heading" FontSize="16" FontWeight="SemiBold" />
            <TextBlock x:Name="Cost" FontSize="11.5" Foreground="#88EDEDF2" Margin="0,3,0,0" />
        </StackPanel>

        <ListBox Grid.Row="1" x:Name="Targets" Background="#141419" BorderBrush="#22FFFFFF"
                 BorderThickness="1" FontSize="11" Foreground="#CFEDEDF2"
                 VirtualizingPanel.IsVirtualizing="True"
                 VirtualizingPanel.VirtualizationMode="Recycling" />

        <StackPanel Grid.Row="2" x:Name="ExcludedPanel" Margin="0,10,0,0" Visibility="Collapsed">
            <TextBlock Text="Will not be ended" FontSize="11.5" FontWeight="SemiBold" />
            <ItemsControl x:Name="ExcludedList" Margin="0,4,0,0" FontSize="11" Foreground="#88EDEDF2" />
        </StackPanel>

        <StackPanel Grid.Row="3" Margin="0,12,0,0">
            <TextBlock x:Name="Instruction" FontSize="11.5" Margin="0,0,0,5" />
            <TextBox x:Name="Confirmation" FontSize="12" Padding="8,5" Background="#1A1A22"
                     Foreground="#EDEDF2" BorderBrush="#33FFFFFF" BorderThickness="1"
                     TextChanged="OnConfirmationChanged" />
        </StackPanel>

        <StackPanel Grid.Row="4" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,14,0,0">
            <Button x:Name="CancelButton" Content="Cancel" Padding="16,6" Margin="0,0,8,0"
                    IsCancel="True" Click="OnCancel" />
            <Button x:Name="EndButton" Content="End processes" Padding="16,6" IsEnabled="False"
                    Click="OnEnd" />
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: Write the dialog code-behind**

Create `BulkEndDialog.xaml.cs`:

```csharp
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// Confirms End all filtered. Returns true from <see cref="Window.ShowDialog"/> only when the
    /// user typed the exact number of processes that will end.
    ///
    /// <para>
    /// The plan is fixed when this opens: processes that appear afterwards are not in it, and a
    /// PID reused afterwards is refused by the identity check in <see cref="ProcessControl.TryEndTask"/>.
    /// </para>
    /// </summary>
    public partial class BulkEndDialog : Window
    {
        private readonly string _expected;

        public BulkEndDialog(BulkEndPlan plan)
        {
            InitializeComponent();

            var inv = CultureInfo.InvariantCulture;
            int count = plan.ToEnd.Count;
            _expected = count.ToString(inv);

            Heading.Text = count == 0
                ? "Nothing here can be ended"
                : "End " + _expected + (count == 1 ? " process?" : " processes?");
            Cost.Text = count == 0 ? "" : "They are using " + TaskManagerViewModel.FormatTotals(
                TaskManagerViewModel.Totals(plan.ToEnd)) + " between them.";

            Targets.ItemsSource = plan.ToEnd
                .Select(p => p.Name + "   ·   PID " + p.Pid.ToString(inv)
                             + "   ·   " + p.CpuText + "   ·   " + p.WorkingSetText)
                .ToList();

            if (plan.Excluded.Count > 0)
            {
                ExcludedPanel.Visibility = Visibility.Visible;
                ExcludedList.ItemsSource = plan.Excluded
                    .Select(e => e.Process.Name + " (" + e.Process.Pid.ToString(inv) + ") — " + e.Reason)
                    .ToList();
            }

            if (count == 0)
            {
                Instruction.Text = "Every match is protected from being ended.";
                Confirmation.IsEnabled = false;
            }
            else
            {
                Instruction.Text = "Type " + _expected + " to end these processes. This cannot be undone.";
            }

            Loaded += (s, e) => Confirmation.Focus();
        }

        private void OnConfirmationChanged(object sender, TextChangedEventArgs e) =>
            EndButton.IsEnabled = _expected != "0" && Confirmation.Text.Trim() == _expected;

        private void OnEnd(object sender, RoutedEventArgs e) => DialogResult = true;

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
```

- [ ] **Step 4: Add the button to the window**

In `TaskManagerWindow.xaml`, find:

```xml
                <Button x:Name="EndTaskButton" Style="{StaticResource ActionButton}"
                        Content="End task" IsEnabled="False" Click="OnEndTask" />
```

Add immediately after it:

```xml
                <!-- Enabled only while a filter is typed and matches something: with no filter,
                     "all filtered" would be every process on the machine. -->
                <Button x:Name="EndAllButton" Style="{StaticResource ActionButton}"
                        Content="End all filtered" IsEnabled="{Binding CanEndAllFiltered}"
                        Click="OnEndAllFiltered" />
```

- [ ] **Step 5: Wire the click**

In `TaskManagerWindow.xaml.cs`, add this method immediately after `OnHeaderClick`:

```csharp
        /// <summary>
        /// Plans against what the filter shows right now, asks for typed confirmation, then ends
        /// the set off the UI thread and reports the outcome in the footer.
        /// </summary>
        private void OnEndAllFiltered(object sender, RoutedEventArgs e)
        {
            if (!_model.CanEndAllFiltered) return;

            var ancestors = BulkEndPlan.AncestorsOf(Environment.ProcessId, _model.Snapshot);
            var plan = BulkEndPlan.Build(_model.Filtered, Environment.ProcessId, ancestors);

            var dialog = new BulkEndDialog(plan) { Owner = this };
            if (dialog.ShowDialog() != true || plan.ToEnd.Count == 0) return;

            EndAllButton.IsEnabled = false;
            _model.Message = "Ending " + plan.ToEnd.Count.ToString(CultureInfo.InvariantCulture) + "…";

            System.Threading.Tasks.Task.Run(() =>
            {
                string outcome;
                try { outcome = BulkEnd.Run(plan.ToEnd).Describe(); }
                catch (Exception ex)
                {
                    outcome = "Ending the filtered processes failed; the log has the detail.";
                    DiagnosticsLog.Error("processes", "End all filtered failed", ex);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _model.Message = outcome;
                    EndAllButton.IsEnabled = _model.CanEndAllFiltered;
                    _model.Refresh();
                }));
            });
        }
```

Setting `EndAllButton.IsEnabled` directly replaces the XAML binding with a local value for the rest of the window's life. That is acceptable only because the completion handler re-assigns it from `CanEndAllFiltered`; if you prefer to keep the binding live, add a `bool _ending` flag to the view model folded into `CanEndAllFiltered` instead, and say which you chose in your report.

- [ ] **Step 6: Build and run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj` — expected 0 warnings, 0 errors.

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests` — expected PASS.

- [ ] **Step 7: Commit**

```bash
git add Services/BulkEnd.cs BulkEndDialog.xaml BulkEndDialog.xaml.cs TaskManagerWindow.xaml TaskManagerWindow.xaml.cs
git commit -F - <<'MSG'
feat(processes): end all filtered, previewed and verified

A preview of exactly what will end and what will not, confirmed by
typing the count. Ends every target, waits once, then asks the kernel
whether each one really exited, and reports ended, denied and survived
separately. Never a tree kill, never on the UI thread.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 7: The menu entry

**Files:**
- Modify: `OverlayWindow.cs`
- Modify: `ContextMenuWindow.xaml.cs`

**Interfaces:**
- Consumes: `TaskManagerWindow.ShowOrActivate(ProcessSampler)`, `App.SharedProcessSampler` (existing).
- Produces: nothing new.

- [ ] **Step 1: Add the item to the overlay menu**

In `OverlayWindow.cs`, find:

```csharp
                    AppendMenu(hMenu, 0, 1002, "Task Manager");
```

Replace with:

```csharp
                    AppendMenu(hMenu, 0, 1011, "Processes");
                    AppendMenu(hMenu, 0, 1002, "Task Manager");
```

Then find:

```csharp
                    else if (ch == 1002) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
```

Add immediately before it:

```csharp
                    // MicaStats' own list, from the snapshot it already holds. Task Manager stays
                    // beside it for its other tabs, and for when MicaStats itself is the problem.
                    else if (ch == 1011) _dispatcher.BeginInvoke(() => TaskManagerWindow.ShowOrActivate(App.SharedProcessSampler));
```

Confirm `1011` is not already used: search `OverlayWindow.cs` for `1011`. It must appear only in the two lines you added.

- [ ] **Step 2: Add the item to the WPF context menu**

In `ContextMenuWindow.xaml.cs`, find:

```csharp
            var taskMgrItem = new MenuItem { Header = "Task Manager" };
```

Add immediately before it:

```csharp
            var processesItem = new MenuItem { Header = "Processes" };
            processesItem.Click += (s, e) =>
            {
                TaskManagerWindow.ShowOrActivate(App.SharedProcessSampler);
                this.Close();
            };

```

Then find:

```csharp
            menu.Items.Add(taskMgrItem);
```

Replace with:

```csharp
            menu.Items.Add(processesItem);
            menu.Items.Add(taskMgrItem);
```

- [ ] **Step 3: Build and run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj` — expected 0 warnings, 0 errors.

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests` — expected PASS.

- [ ] **Step 4: Commit**

```bash
git add OverlayWindow.cs ContextMenuWindow.xaml.cs
git commit -F - <<'MSG'
feat(processes): open the process window from the overlay menu

Both context menus gain Processes above Task Manager. Until now the
window was reachable only from the stats panel; Task Manager stays for
its other tabs and for when MicaStats itself is misbehaving.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 8: Documentation and manual verification

**Files:**
- Modify: `README.md`

**Interfaces:** none.

- [ ] **Step 1: Update the English process-list section**

In `README.md`, find the English paragraph under `#### The process list` that begins:

```markdown
Open it from **Processes** on the CPU card.
```

Replace that opening sentence with:

```markdown
Open it from **Processes** in the overlay's right-click menu, or from **Processes** on the CPU card.
```

Find the paragraph that begins `MicaStats has an advantage there:` and replace its sentence

```markdown
The window performs no sampling of its own and makes no per-process queries, so it opens from data already in memory and stays responsive on a machine that cannot open Task Manager at all.
```

with:

```markdown
The list performs no sampling of its own and makes no per-process queries, so it opens from data already in memory and stays responsive on a machine that cannot open Task Manager at all. The same snapshot supplies each process's parent, uptime, thread count and handle count, so those columns cost nothing either. A process whose parent has exited shows its parent as `(gone)` — which is how an orphan announces itself. Click any column header to sort by it.

Selecting a row fills a pane beneath the list with that one process's full path, command line, account and whether it is elevated. Those need the process opened, so they are read for the selected row only, in the background, and never for the list. The footer shows what everything the search matches is costing between them — count, CPU, memory and disk.
```

Immediately after the paragraph that begins `**End task** terminates immediately`, insert:

```markdown
**End all filtered** ends everything the search currently matches, and is available only while something is typed in the search box — with no filter it would mean every process on the machine. It first shows exactly what will end and what it will not: core Windows processes, MicaStats itself, and anything MicaStats is running inside are listed as refused, with the reason. You confirm by typing the number of processes that will end. Each one is then checked with Windows to confirm it actually exited, and the footer reports how many ended, how many need administrator rights, and how many survived. It never ends a process's children unless the search matched them too.
```

- [ ] **Step 2: Update the Thai process-list section to match**

In `README.md`, under `#### รายการโปรเซส`, replace the opening sentence:

```markdown
เปิดได้จากปุ่ม **Processes** บนการ์ด CPU
```

with:

```markdown
เปิดได้จากเมนู **Processes** เมื่อคลิกขวาที่โอเวอร์เลย์ หรือจากปุ่ม **Processes** บนการ์ด CPU
```

Replace the sentence

```markdown
หน้าต่างนี้จึงไม่เก็บข้อมูลเพิ่มเองและไม่สอบถามข้อมูลรายโปรเซส เปิดจากข้อมูลที่มีอยู่ในหน่วยความจำและยังตอบสนองได้บนเครื่องที่เปิด Task Manager ไม่ขึ้นเลย
```

with:

```markdown
รายการนี้จึงไม่เก็บข้อมูลเพิ่มเองและไม่สอบถามข้อมูลรายโปรเซส เปิดจากข้อมูลที่มีอยู่ในหน่วยความจำและยังตอบสนองได้บนเครื่องที่เปิด Task Manager ไม่ขึ้นเลย ข้อมูลชุดเดียวกันนี้ยังให้โปรเซสแม่ ระยะเวลาที่ทำงาน จำนวนเธรด และจำนวนแฮนเดิลของแต่ละโปรเซส คอลัมน์เหล่านี้จึงไม่มีต้นทุนเพิ่ม โปรเซสที่โปรเซสแม่ปิดไปแล้วจะแสดงโปรเซสแม่เป็น `(gone)` ซึ่งเป็นสัญญาณของโปรเซสกำพร้า คลิกหัวคอลัมน์ใดก็ได้เพื่อเรียงตามคอลัมน์นั้น

เมื่อเลือกแถว จะมีแผงด้านล่างรายการแสดงพาธเต็ม คำสั่งที่ใช้เรียก บัญชีผู้ใช้ และสถานะการยกระดับสิทธิ์ของโปรเซสนั้น ข้อมูลเหล่านี้ต้องเปิดโปรเซสเพื่ออ่าน จึงอ่านเฉพาะแถวที่เลือกเท่านั้น ในเบื้องหลัง และไม่อ่านสำหรับทั้งรายการ ส่วนแถบด้านล่างแสดงต้นทุนรวมของทุกโปรเซสที่ตรงกับคำค้น ทั้งจำนวน CPU หน่วยความจำ และดิสก์
```

Immediately after the Thai paragraph that begins `**End task** สั่งปิดทันที`, insert:

```markdown
**End all filtered** ปิดทุกโปรเซสที่ตรงกับคำค้นในขณะนั้น และใช้ได้เฉพาะเมื่อพิมพ์คำค้นไว้แล้วเท่านั้น เพราะถ้าไม่มีคำค้นจะหมายถึงทุกโปรเซสในเครื่อง ก่อนปิดจะแสดงรายการที่จะถูกปิดและรายการที่จะไม่ถูกปิดให้เห็นชัดเจน ได้แก่ โปรเซสหลักของ Windows ตัว MicaStats เอง และโปรเซสที่ MicaStats ทำงานอยู่ภายใน โดยระบุเหตุผลกำกับ ผู้ใช้ยืนยันด้วยการพิมพ์จำนวนโปรเซสที่จะถูกปิด จากนั้นจะตรวจสอบกับ Windows ทีละตัวว่าปิดไปจริงหรือไม่ แล้วรายงานที่แถบด้านล่างว่าปิดได้กี่ตัว ต้องใช้สิทธิ์ผู้ดูแลระบบกี่ตัว และรอดกี่ตัว จะไม่ปิดโปรเซสลูกของโปรเซสใด เว้นแต่คำค้นจะตรงกับโปรเซสลูกนั้นด้วย
```

Keep every inline code span, key name and number verbatim. Report the Thai text as needing the owner's check.

- [ ] **Step 3: Run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests` — expected PASS.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -F - <<'MSG'
docs(processes): the menu entry, new columns and end all filtered

English and Thai sections updated together. The list still makes no
per-process queries; the detail pane reads the selected row only, and
the wording now says so rather than claiming the whole window never
opens a process.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

- [ ] **Step 5: Manual verification (controller, with the owner present)**

Not for an implementing subagent: this needs the running application replaced and a human at the screen.

1. Build this branch to an isolated output directory and run it in place of the owner's instance, recording the original's path and arguments first so it can be restored exactly.
2. Right-click the overlay: *Processes* appears above *Task Manager* and opens the window.
3. Check Parent, Uptime, Threads and Handles for three processes against Windows Task Manager's Details tab.
4. Click each header; the list sorts, and a second click reverses it.
5. Select rows: the pane fills with path, command line and account, and quickly changing selection never shows one row's data under another's name.
6. Spawn five `notepad.exe`, type `notepad` in the search, and click *End all filtered*. The preview lists five; the End button stays disabled until `5` is typed; after confirming, the footer reads `Ended 5 · 0 need administrator · 0 survived`.
7. Clear the search: *End all filtered* is disabled.
8. Restore the owner's original instance.

---

## Self-review

**Spec coverage.** Menu entry → Task 7. Snapshot columns (threads, handles, parent, uptime) → Tasks 1, 2, 5. Parent-name search and sorting → Task 2, with header wiring in Task 5 — the spec assumed sorting existed in the UI; it did not, so Task 5 adds it. Detail pane → Tasks 4, 5. Totals → Tasks 2, 5. End all filtered: disabled with an empty filter, preview, typed count, identities fixed at preview, exclusions with reasons, end-all-then-settle-once-then-verify, no tree kill, per-process guard, logging → Tasks 3, 6. README English and Thai → Task 8. Manual verification → Task 8 step 5.

**Naming.** The spec's `ProcessIdentityReader` became `ProcessAccountReader`: `ProcessIdentity` already names the watchdog's `(pid, createTime)` record struct in `Services.Watchdog`, and two types differing only in suffix across namespaces would invite a wrong `using`.

**Type consistency.** `ProcessTotals(int Count, float Cpu, long Memory, long Disk)` is produced in Task 2 and consumed by `BulkEndDialog` in Task 6. `BulkEndPlan.Build(filtered, selfPid, selfAncestors)` and `AncestorsOf(pid, snapshot)` are produced in Task 3 and consumed in Task 6 with the same argument order. `BulkEndResult.Describe()` is produced in Task 3 and consumed by `BulkEnd.Run` and the window in Task 6. `ProcessUsage.ParentName` is always non-null (default `""`), which `Filter`'s `IndexOf` relies on.

**Known limits.** The detail pane's read is keyed by PID and guarded by `HasExited(pid, createTime)` first, so a recycled PID reads as exited rather than showing another process. A process that exits between that check and the reads shows "Unavailable". The bulk kill's identity capture happens in `BulkEndPlan.Build` from the snapshot current when the button is clicked, which is also when the preview opens.
