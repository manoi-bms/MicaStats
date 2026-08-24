# Task Manager Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A dedicated window listing every running process with live CPU, memory and disk figures, sortable and searchable, that can terminate a process and report exactly what happened — and that stays responsive on a machine too busy to open Windows Task Manager.

**Architecture:** `ProcessSampler` already walks every process every 2 s through one `NtQuerySystemInformation` call and discards all but the top few. It gains a full-snapshot property, adding no sampling. A new `ProcessControl` owns termination and classifies every outcome. The window attaches to the existing snapshot, virtualizes its rows, and updates them in place. Elevation happens per kill via a `--kill <pid> <createTime>` relaunch, never permanently.

**Tech Stack:** C# 12, .NET 8 (`net8.0-windows`, WPF), xUnit 2.9.2. No new NuGet packages.

## Global Constraints

- **Build/test SDK:** no system-wide SDK. Use `C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe` (8.0.424). `C:\Program Files\dotnet\dotnet.exe` is runtime-only and fails with "No .NET SDKs were found".
- **Test command:** `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" test tests\Kil0bitSystemMonitor.Tests\Kil0bitSystemMonitor.Tests.csproj`
- **Baseline:** 419 tests pass before this plan starts. Every task must leave the whole suite green.
- **The app runs unelevated and must continue to.** The only elevated code is the `--kill` one-shot path, which must terminate and exit before any window, config or update check is touched.
- **No new NuGet packages.**
- **Never terminate a critical process.** `csrss.exe`, `wininit.exe`, `services.exe`, `smss.exe`, `lsass.exe`, `winlogon.exe` — refuse, do not confirm. Terminating any of these bugchecks Windows.
- **Every kill outcome is reported in words.** A kill that did nothing must never look like a kill that worked; that is one of the four reported symptoms.
- **No per-row queries, ever.** Every column comes from the sampler's single existing pass. No `System.Diagnostics.Process` per row — it throws access-denied for roughly a third of processes on this machine.
- **Culture:** every `ToString` on a number or date uses `CultureInfo.InvariantCulture`. The dev machine runs a Thai locale whose default calendar stamps years as 2569.
- **The project sets `UseWindowsForms` and `UseWPF` together.** In files touching UI types, alias `Color`, `Control`, `Button`, `HorizontalAlignment`, `MessageBox` explicitly — see `Helpers/ToastButton.cs:9-14`.
- **WPF element construction requires STA.** Tests that build WPF objects must run on an STA thread; see the `OnSta` helper in `tests/Kil0bitSystemMonitor.Tests/ToastButtonTests.cs`.
- **Version:** bump to `1.9.0` in Task 9 only.

---

### Task 1: Expose the full snapshot

**Files:**
- Modify: `Services/ProcessSampler.cs` (record at :8, sample loop at :251-283)
- Test: `tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ProcessUsage.CreateTime` (`long`, FILETIME); `ProcessSampler.AllProcesses` (`IReadOnlyList<ProcessUsage>`, sorted by CPU descending); `ProcessSampler.HasCpuData` (`bool`).

**Background:** the sample loop already reads `createTime` at `Services/ProcessSampler.cs:214` and uses it to guard against pid reuse, but never surfaces it. `byCpu` holds every process; `Trim` reduces it to the top few at `:281`. Capture the full list **before** `Trim`, because `Trim` may truncate in place.

CPU share is a delta between two samples, so it does not exist until the second sample. `HasCpuData` reports that, so the window can show a dash rather than a grid of zeroes.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class TaskManagerTests
    {
        [Fact]
        public void A_usage_carries_its_creation_time_so_a_pid_can_be_identified()
        {
            var usage = new ProcessUsage("chrome.exe", 8420, 24.1f, 1_288_490_188L)
            {
                CreateTime = 133_000_000_000_000_000L,
            };

            Assert.Equal(133_000_000_000_000_000L, usage.CreateTime);
        }

        [Fact]
        public void A_fresh_sampler_reports_no_cpu_data_and_an_empty_snapshot()
        {
            using var sampler = new ProcessSampler();

            Assert.False(sampler.HasCpuData);
            Assert.NotNull(sampler.AllProcesses);
            Assert.Empty(sampler.AllProcesses);
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" test tests\Kil0bitSystemMonitor.Tests\Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~TaskManagerTests"`
Expected: FAIL — `ProcessUsage` has no `CreateTime`.

- [ ] **Step 3: Add the record field**

In `Services/ProcessSampler.cs`, inside the `ProcessUsage` record after `DiskWriteBytesPerSec`:

```csharp
        /// <summary>
        /// Process creation time as a FILETIME. Together with the pid this identifies a
        /// specific process: pids are recycled, and a kill confirmed through a UAC prompt can
        /// land seconds after the pid was selected.
        /// </summary>
        public long CreateTime { get; init; }
```

- [ ] **Step 4: Publish the full snapshot**

In `Services/ProcessSampler.cs`, add beside the existing `TopBy*` properties:

```csharp
        /// <summary>
        /// Every process from the most recent sample, ordered by CPU share descending. Same
        /// objects as the rankings above, from the same pass — reading this costs nothing
        /// beyond the array.
        /// </summary>
        public IReadOnlyList<ProcessUsage> AllProcesses { get; private set; } = Array.Empty<ProcessUsage>();

        /// <summary>
        /// False until a second sample has landed. CPU share is a delta, so it cannot exist
        /// before then; callers must render a dash rather than zero, because a grid of zeroes
        /// is indistinguishable from a frozen list.
        /// </summary>
        public bool HasCpuData => System.Threading.Volatile.Read(ref _sampleCount) >= 2;

        private int _sampleCount;
```

Set `CreateTime` where the usage is built at `:251`:

```csharp
                            var usage = new ProcessUsage(name, (int)pid, percent, workingSet)
                            {
                                DiskReadBytesPerSec = readRate,
                                DiskWriteBytesPerSec = writeRate,
                                CreateTime = createTime,
                            };
```

And capture the snapshot immediately after the three `Sort` calls at `:277-279`, **before** the `Trim` calls:

```csharp
                    // Before Trim: it may truncate in place, and this needs every process.
                    AllProcesses = byCpu.ToArray();
                    System.Threading.Interlocked.Increment(ref _sampleCount);

                    TopByCpu = Trim(byCpu);
```

In the `Enabled = false` branch that calls `_previous.Clear()` (around `:177`), also reset so a
restarted sampler does not claim to have deltas it no longer has:

```csharp
                        _previous.Clear();
                        System.Threading.Volatile.Write(ref _sampleCount, 0);
                        AllProcesses = Array.Empty<ProcessUsage>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Expected: 2 passed in the filter, 421 in the full suite.

- [ ] **Step 6: Commit**

```bash
git add Services/ProcessSampler.cs tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs
git commit -m "feat(processes): expose the full snapshot and creation time"
```

---

### Task 2: The critical-process guard

**Files:**
- Create: `Services/ProcessControl.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs` (add)

**Interfaces:**
- Consumes: nothing.
- Produces: `enum EndTaskResult { Terminated, AccessDenied, AlreadyExited, Critical, Recycled, Failed }`; `ProcessControl.IsCriticalProcess(string name)` returning `bool`.

**Why this is its own task:** it is the one piece of the kill path that is pure and fully testable, and it is the piece whose failure is unrecoverable — a false negative bugchecks the machine. It gates independently of the interop.

- [ ] **Step 1: Write the failing test**

```csharp
        /// <summary>
        /// Terminating any of these stops Windows immediately — a bugcheck, not an error
        /// dialog. The match must be exact: a near-miss that fails to match kills the machine,
        /// and a near-miss that matches wrongly blocks a legitimate kill.
        /// </summary>
        [Theory]
        [InlineData("csrss.exe", true)]
        [InlineData("wininit.exe", true)]
        [InlineData("services.exe", true)]
        [InlineData("smss.exe", true)]
        [InlineData("lsass.exe", true)]
        [InlineData("winlogon.exe", true)]
        [InlineData("CSRSS.EXE", true)]        // the kernel reports whatever case it likes
        [InlineData("Csrss.exe", true)]
        [InlineData("csrss.exe.bak", false)]   // not the real one
        [InlineData("mycsrss.exe", false)]
        [InlineData("csrss", false)]           // no extension is not the image name
        [InlineData("chrome.exe", false)]
        [InlineData("", false)]
        public void Critical_processes_are_identified_exactly(string name, bool critical)
        {
            Assert.Equal(critical, ProcessControl.IsCriticalProcess(name));
        }

        [Fact]
        public void A_null_process_name_is_not_critical_and_does_not_throw()
        {
            Assert.False(ProcessControl.IsCriticalProcess(null!));
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `ProcessControl` not found.

- [ ] **Step 3: Write the implementation**

`Services/ProcessControl.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>What happened when a termination was attempted.</summary>
    public enum EndTaskResult
    {
        /// <summary>The process is gone.</summary>
        Terminated,

        /// <summary>The process runs at a level this unelevated app cannot reach.</summary>
        AccessDenied,

        /// <summary>It had already exited — from the user's point of view, done.</summary>
        AlreadyExited,

        /// <summary>Terminating it would stop Windows. Refused.</summary>
        Critical,

        /// <summary>The pid now belongs to a different process than the one selected.</summary>
        Recycled,

        /// <summary>Any other Win32 failure, reported with its code.</summary>
        Failed,
    }

    /// <summary>
    /// The only component that terminates a process.
    ///
    /// <para>
    /// Every outcome is classified rather than swallowed. "End task does nothing" is one of
    /// the symptoms this window exists to fix, and a silent failure reproduces it exactly.
    /// </para>
    /// </summary>
    public static partial class ProcessControl
    {
        /// <summary>
        /// Processes whose termination bugchecks Windows immediately. Refused rather than
        /// confirmed: a confirmation dialog is one mis-click away from a stop error, and
        /// anyone who genuinely intends this has taskkill.
        /// </summary>
        private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
        {
            "csrss.exe", "wininit.exe", "services.exe", "smss.exe", "lsass.exe", "winlogon.exe",
        };

        /// <summary>
        /// Whether terminating this image would stop the machine.
        ///
        /// <para>
        /// Matches the image name exactly rather than querying whether the process is marked
        /// critical: this has to answer before any handle is opened, and it must not depend on
        /// a call that could itself block on a starved system — which is the condition the
        /// user is in when they reach for this tool.
        /// </para>
        /// </summary>
        public static bool IsCriticalProcess(string name) =>
            !string.IsNullOrEmpty(name) && Critical.Contains(name);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 15 passed in the filter (2 from Task 1 + 13 here), 434 in the full suite.

- [ ] **Step 5: Commit**

```bash
git add Services/ProcessControl.cs tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs
git commit -m "feat(processes): refuse to terminate critical system processes"
```

---

### Task 3: Terminate, and classify the outcome

**Files:**
- Modify: `Services/ProcessControl.cs`
- Test: none — actual termination is not unit testable. The guard (Task 2) and the parser (Task 4) carry the safety weight.

**Interfaces:**
- Consumes: `EndTaskResult`, `IsCriticalProcess` from Task 2.
- Produces: `ProcessControl.TryEndTask(int pid, long createTime, string name, out string message)` returning `EndTaskResult`.

- [ ] **Step 1: Write the implementation**

Add to `Services/ProcessControl.cs`:

```csharp
using System.Globalization;
using System.Runtime.InteropServices;

        private const int PROCESS_TERMINATE = 0x0001;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr handle,
            out long creation, out long exit, out long kernel, out long user);

        /// <summary>
        /// Terminates one process, immediately.
        ///
        /// <para>
        /// No graceful close is attempted. Windows Task Manager's End task first asks a window
        /// to close politely, which is exactly why it appears to do nothing against a hung
        /// application — the request sits in a message queue that is never pumped.
        /// </para>
        ///
        /// <para>
        /// <paramref name="createTime"/> is checked against the live process before anything is
        /// terminated. Pids are recycled, and when this runs behind a UAC prompt the gap
        /// between selection and action is seconds wide, so without the check a slow consent
        /// could terminate an unrelated process with administrator rights.
        /// </para>
        /// </summary>
        public static EndTaskResult TryEndTask(int pid, long createTime, string name, out string message)
        {
            if (IsCriticalProcess(name))
            {
                message = name + " is a core Windows process. Ending it would stop the machine "
                          + "immediately, so MicaStats will not do it.";
                return EndTaskResult.Critical;
            }

            IntPtr handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                switch (error)
                {
                    case ERROR_INVALID_PARAMETER:
                        message = name + " had already exited.";
                        return EndTaskResult.AlreadyExited;

                    case ERROR_ACCESS_DENIED:
                        message = name + " runs at a higher privilege level than MicaStats, "
                                  + "which is unelevated.";
                        return EndTaskResult.AccessDenied;

                    default:
                        message = "Could not open " + name + " (error "
                                  + error.ToString(CultureInfo.InvariantCulture) + ").";
                        return EndTaskResult.Failed;
                }
            }

            try
            {
                // Identity check before the irreversible step.
                if (createTime != 0 &&
                    GetProcessTimes(handle, out long living, out _, out _, out _) &&
                    living != createTime)
                {
                    message = "That process has already exited and PID "
                              + pid.ToString(CultureInfo.InvariantCulture)
                              + " now belongs to a different one. Nothing was ended.";
                    return EndTaskResult.Recycled;
                }

                if (TerminateProcess(handle, 1))
                {
                    message = "Ended " + name + ".";
                    return EndTaskResult.Terminated;
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_ACCESS_DENIED)
                {
                    message = name + " refused to be ended by an unelevated process.";
                    return EndTaskResult.AccessDenied;
                }

                message = "Could not end " + name + " (error "
                          + error.ToString(CultureInfo.InvariantCulture) + ").";
                return EndTaskResult.Failed;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" build Kil0bitSystemMonitor.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the whole suite**

Expected: 434 passed — nothing regressed.

- [ ] **Step 4: Verify by hand against a real process**

Start Notepad, note its pid in the window once Task 7 exists — or for now, from `Get-Process notepad`. Confirm `TryEndTask` returns `Terminated` and Notepad closes. Then call it again with the same pid and confirm `AlreadyExited` rather than a silent failure.

- [ ] **Step 5: Commit**

```bash
git add Services/ProcessControl.cs
git commit -m "feat(processes): terminate directly and classify every outcome"
```

---

### Task 4: The --kill argument parser

**Files:**
- Create: `Services/KillArguments.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs` (add)

**Interfaces:**
- Consumes: nothing.
- Produces: `KillArguments.TryParse(string[] args, out int pid, out long createTime)` returning `bool`.

**Why this is a task of its own:** this parser runs with administrator rights. It is the entire attack surface of the elevated path, and it is pure, so it gets tested hard.

- [ ] **Step 1: Write the failing test**

```csharp
        /// <summary>
        /// This parser is the whole input surface of the elevated path, so it is tested against
        /// malformed and hostile input rather than only the happy case. Anything it does not
        /// fully understand must be rejected.
        /// </summary>
        [Fact]
        public void Kill_arguments_parse_a_well_formed_request()
        {
            Assert.True(KillArguments.TryParse(
                new[] { "--kill", "8420", "133000000000000000" }, out int pid, out long created));

            Assert.Equal(8420, pid);
            Assert.Equal(133_000_000_000_000_000L, created);
        }

        [Theory]
        [InlineData(new string[0])]                                   // nothing
        [InlineData(new[] { "--kill" })]                              // no pid
        [InlineData(new[] { "--kill", "8420" })]                      // no creation time
        [InlineData(new[] { "--kill", "abc", "133" })]                // pid not a number
        [InlineData(new[] { "--kill", "8420", "abc" })]               // time not a number
        [InlineData(new[] { "--kill", "-1", "133" })]                 // negative pid
        [InlineData(new[] { "--kill", "0", "133" })]                  // the idle process
        [InlineData(new[] { "--kill", "8420", "-5" })]                // negative time
        [InlineData(new[] { "--kill", "99999999999", "133" })]        // pid beyond int
        [InlineData(new[] { "--settings", "8420", "133" })]           // a different switch
        [InlineData(new[] { "--kill", "8420", "133", "extra" })]      // trailing junk
        public void Malformed_kill_arguments_are_refused(string[] args)
        {
            Assert.False(KillArguments.TryParse(args, out _, out _));
        }

        /// <summary>A rejected parse must not leave a usable pid behind for a careless caller.</summary>
        [Fact]
        public void A_refused_parse_yields_no_pid()
        {
            KillArguments.TryParse(new[] { "--kill", "abc", "def" }, out int pid, out long created);

            Assert.Equal(0, pid);
            Assert.Equal(0, created);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `KillArguments` not found.

- [ ] **Step 3: Write the implementation**

`Services/KillArguments.cs`:

```csharp
using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Parses the elevated one-shot request <c>--kill &lt;pid&gt; &lt;createTime&gt;</c>.
    ///
    /// <para>
    /// This is the entire input surface of the only code path that runs with administrator
    /// rights, so it refuses anything it does not completely understand: exactly three
    /// arguments, a positive pid inside <see cref="int"/>, and a non-negative FILETIME. Trailing
    /// arguments are a rejection rather than something to ignore, because an argument this code
    /// does not recognise means the caller is not the caller it expects.
    /// </para>
    ///
    /// <para>
    /// Note that this grants no capability: anyone able to run this executable can already run
    /// <c>taskkill /f</c>. What it must guarantee is that the elevated path does one thing.
    /// </para>
    /// </summary>
    public static class KillArguments
    {
        /// <summary>The switch that selects the one-shot termination path.</summary>
        public const string Switch = "--kill";

        public static bool TryParse(string[] args, out int pid, out long createTime)
        {
            pid = 0;
            createTime = 0;

            if (args is not { Length: 3 }) return false;
            if (!string.Equals(args[0], Switch, StringComparison.Ordinal)) return false;

            if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPid))
                return false;
            if (parsedPid <= 0) return false;   // 0 is the idle process; negatives are nonsense

            if (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long parsedTime))
                return false;
            if (parsedTime < 0) return false;

            pid = parsedPid;
            createTime = parsedTime;
            return true;
        }
    }
}
```

`NumberStyles.None` is deliberate: it rejects leading signs, whitespace and thousands separators, so `" +8420 "` does not quietly become 8420.

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 28 passed in the filter, 447 in the full suite.

- [ ] **Step 5: Commit**

```bash
git add Services/KillArguments.cs tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs
git commit -m "feat(processes): parse the elevated one-shot kill request"
```

---

### Task 5: The elevated startup path

**Files:**
- Modify: `App.xaml.cs` (`OnStartup`)
- Test: none — the UAC transition is not unit testable; Task 4 covers the input.

**Interfaces:**
- Consumes: `KillArguments.TryParse`, `ProcessControl.TryEndTask`.
- Produces: an `App` that terminates and exits before any UI when launched with `--kill`.

**Critical requirement:** this must run before the main window, the config load, the update check and the tray icon. An elevated instance that initialises the application is an elevated instance doing far more than one thing.

- [ ] **Step 1: Read the current startup**

Run: `grep -n "OnStartup" -A 20 App.xaml.cs`
Note exactly where the first initialisation happens; the new block goes **above** all of it.

- [ ] **Step 2: Add the one-shot path**

At the very top of `OnStartup`, before `base.OnStartup(e)` and before any other statement:

```csharp
            // The elevated one-shot. This runs with administrator rights, so it does exactly
            // one thing and leaves: parse, verify identity, terminate, exit. No config, no
            // update check, no window, no tray icon. Anything else here would be an elevated
            // instance doing more than the user consented to.
            if (Services.KillArguments.TryParse(e.Args, out int killPid, out long killCreated))
            {
                var result = Services.ProcessControl.TryEndTask(
                    killPid, killCreated, ResolveNameFor(killPid), out _);

                // The exit code is how the unelevated parent learns what happened.
                Shutdown(result == Services.EndTaskResult.Terminated ? 0 : 1);
                return;
            }
```

- [ ] **Step 3: Add the name resolver**

The critical-process guard needs an image name, and the elevated instance is given only a pid. Add to `App.xaml.cs`:

```csharp
        /// <summary>
        /// The image name for a pid, for the critical-process guard in the elevated path.
        /// Falls back to a name that cannot match the guard's list, so an unreadable process is
        /// never mistaken for a safe one — the guard is checked again here because the elevated
        /// instance must not trust an argument to have been checked by its caller.
        /// </summary>
        private static string ResolveNameFor(int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return p.ProcessName + ".exe";
            }
            catch
            {
                return "";
            }
        }
```

Using `System.Diagnostics.Process` here is acceptable and is **not** a violation of the no-per-row-query rule: this is one process, once, in a short-lived elevated instance with no UI to keep responsive.

- [ ] **Step 4: Build and verify the path does not start the UI**

Run: `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" build Kil0bitSystemMonitor.csproj -c Debug`

Then start Notepad, note its pid, and run the built exe directly with the switch:

```powershell
$p = Start-Process notepad -PassThru
$t = (Get-Process -Id $p.Id).StartTime.ToFileTime()
& ".\bin\Debug\net8.0-windows\MicaStats.exe" --kill $p.Id $t
$LASTEXITCODE
Get-Process notepad -ErrorAction SilentlyContinue
```

Expected: exit code 0, no Notepad, and **no MicaStats window or tray icon appears**.

- [ ] **Step 5: Verify the identity check refuses a mismatch**

```powershell
$p = Start-Process notepad -PassThru
& ".\bin\Debug\net8.0-windows\MicaStats.exe" --kill $p.Id 1
$LASTEXITCODE
Get-Process notepad -ErrorAction SilentlyContinue
```

Expected: exit code 1 and Notepad **still running** — the creation time did not match, so nothing was terminated. Then close it by hand.

- [ ] **Step 6: Commit**

```bash
git add App.xaml.cs
git commit -m "feat(processes): elevated one-shot kill path"
```

---

### Task 6: The view model

**Files:**
- Create: `ViewModels/TaskManagerViewModel.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs` (add)

**Interfaces:**
- Consumes: `ProcessUsage`, `ProcessSampler.AllProcesses`, `HasCpuData`.
- Produces: `enum ProcessSortColumn { Name, Pid, Cpu, Memory, Disk }`; `TaskManagerViewModel.Filter(IReadOnlyList<ProcessUsage>, string)`; `TaskManagerViewModel.Sort(List<ProcessUsage>, ProcessSortColumn, bool descending)`; `TaskManagerViewModel.CpuTextFor(ProcessUsage, bool hasCpuData)`.

- [ ] **Step 1: Write the failing test**

```csharp
        private static ProcessUsage P(string name, int pid, float cpu, long ram, long disk = 0) =>
            new(name, pid, cpu, ram) { DiskReadBytesPerSec = disk };

        [Fact]
        public void Filtering_matches_a_name_case_insensitively()
        {
            var all = new[] { P("chrome.exe", 1, 5, 100), P("Code.exe", 2, 3, 200), P("dwm.exe", 3, 1, 50) };

            var hit = TaskManagerViewModel.Filter(all, "CHROME");

            Assert.Single(hit);
            Assert.Equal(1, hit[0].Pid);
        }

        /// <summary>
        /// Typing a number searches the pid, because a user who knows the pid usually knows
        /// only the pid. It must be an exact match, not a substring: searching 42 should not
        /// bury the answer under 420, 1042 and 4231.
        /// </summary>
        [Fact]
        public void Filtering_by_a_number_matches_the_pid_exactly()
        {
            var all = new[] { P("a.exe", 42, 0, 0), P("b.exe", 420, 0, 0), P("c.exe", 1042, 0, 0) };

            var hit = TaskManagerViewModel.Filter(all, "42");

            Assert.Single(hit);
            Assert.Equal(42, hit[0].Pid);
        }

        [Fact]
        public void An_empty_filter_keeps_everything()
        {
            var all = new[] { P("a.exe", 1, 0, 0), P("b.exe", 2, 0, 0) };

            Assert.Equal(2, TaskManagerViewModel.Filter(all, "").Count);
            Assert.Equal(2, TaskManagerViewModel.Filter(all, "   ").Count);
            Assert.Equal(2, TaskManagerViewModel.Filter(all, null!).Count);
        }

        [Fact]
        public void Sorting_orders_by_the_chosen_column_in_both_directions()
        {
            var rows = new List<ProcessUsage>
            {
                P("b.exe", 2, 5f, 300, 10),
                P("a.exe", 1, 9f, 100, 30),
                P("c.exe", 3, 1f, 200, 20),
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Cpu, descending: true);
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Pid));

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Memory, descending: true);
            Assert.Equal(new[] { 2, 3, 1 }, rows.Select(r => r.Pid));

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Disk, descending: true);
            Assert.Equal(new[] { 1, 3, 2 }, rows.Select(r => r.Pid));

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Name, descending: false);
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Pid));

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Pid, descending: true);
            Assert.Equal(new[] { 3, 2, 1 }, rows.Select(r => r.Pid));
        }

        /// <summary>
        /// Rows with equal values must not swap places between ticks. A list that reshuffles
        /// under the cursor is unusable precisely when the machine is busy, which is when this
        /// window is opened.
        /// </summary>
        [Fact]
        public void Ties_are_broken_by_pid_so_the_order_is_stable()
        {
            var rows = new List<ProcessUsage> { P("z.exe", 9, 0f, 0), P("a.exe", 3, 0f, 0), P("m.exe", 7, 0f, 0) };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Cpu, descending: true);
            var first = rows.Select(r => r.Pid).ToArray();

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Cpu, descending: true);
            Assert.Equal(first, rows.Select(r => r.Pid));
            Assert.Equal(new[] { 3, 7, 9 }, first);
        }

        /// <summary>
        /// Before the second sample there is no CPU delta. Showing 0.0% would be a lie that
        /// looks exactly like the frozen list this window replaces.
        /// </summary>
        [Fact]
        public void Cpu_reads_as_a_dash_until_a_delta_exists()
        {
            var row = P("chrome.exe", 1, 0f, 100);

            Assert.Equal("—", TaskManagerViewModel.CpuTextFor(row, hasCpuData: false));
            Assert.Equal("0.0%", TaskManagerViewModel.CpuTextFor(row, hasCpuData: true));
        }
```

Add `using System.Collections.Generic;`, `using System.Linq;` and `using Kil0bitSystemMonitor.ViewModels;` to the test file.

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `TaskManagerViewModel` not found.

- [ ] **Step 3: Write the pure helpers**

`ViewModels/TaskManagerViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.ViewModels
{
    public enum ProcessSortColumn { Name, Pid, Cpu, Memory, Disk }

    /// <summary>
    /// Shapes the sampler's snapshot into a sortable, searchable list.
    ///
    /// <para>
    /// The sort and filter are static and pure so they can be tested without a sampler, a
    /// window or a dispatcher — they are the parts that decide what the user sees, and the
    /// parts most likely to be quietly wrong.
    /// </para>
    /// </summary>
    public sealed partial class TaskManagerViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// Matches a name substring, or a pid exactly when the term is a number. Exact rather
        /// than substring on pids: searching 42 should not bury the answer under 420 and 1042.
        /// </summary>
        public static IReadOnlyList<ProcessUsage> Filter(IReadOnlyList<ProcessUsage> all, string term)
        {
            if (all == null) return Array.Empty<ProcessUsage>();
            if (string.IsNullOrWhiteSpace(term)) return all;

            string t = term.Trim();
            if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                return all.Where(p => p.Pid == pid).ToList();

            return all.Where(p => p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        /// <summary>
        /// Orders in place. Ties break by pid so the order is total: rows with equal values
        /// must not swap places between ticks, because a list that reshuffles under the cursor
        /// is unusable exactly when the machine is busy.
        /// </summary>
        public static void Sort(List<ProcessUsage> rows, ProcessSortColumn column, bool descending)
        {
            Comparison<ProcessUsage> compare = column switch
            {
                ProcessSortColumn.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                ProcessSortColumn.Pid => (a, b) => a.Pid.CompareTo(b.Pid),
                ProcessSortColumn.Cpu => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
                ProcessSortColumn.Memory => (a, b) => a.WorkingSet.CompareTo(b.WorkingSet),
                ProcessSortColumn.Disk => (a, b) => a.DiskBytesPerSec.CompareTo(b.DiskBytesPerSec),
                _ => (a, b) => 0,
            };

            rows.Sort((a, b) =>
            {
                int c = compare(a, b);
                if (descending) c = -c;
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            });
        }

        /// <summary>
        /// CPU share for display. Before a second sample there is no delta, so this reads as a
        /// dash: 0.0% would be a lie indistinguishable from the frozen list being replaced.
        /// </summary>
        public static string CpuTextFor(ProcessUsage row, bool hasCpuData) =>
            hasCpuData ? row.CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 34 passed in the filter, 453 in the full suite.

- [ ] **Step 5: Add the row type**

In the same file, above the view model. It mirrors `SensorRow` in `ViewModels/StatsPanelViewModel.cs` — identity fixed at construction, values mutable behind an inequality guard. Do **not** use `StatsPanelViewModel`'s private `Set<T>`: it is a member of that class and returns void.

```csharp
    /// <summary>
    /// One line in the process list. Identity is (Pid, CreateTime) rather than Pid alone,
    /// because pids are recycled and the End task path must be able to prove it is ending the
    /// process the user selected.
    /// </summary>
    public sealed class ProcessRow : INotifyPropertyChanged
    {
        private string _cpu = "—";
        private string _memory = "";
        private string _disk = "";

        public ProcessRow(string name, int pid, long createTime)
        {
            Name = name;
            Pid = pid;
            CreateTime = createTime;
        }

        public string Name { get; }
        public int Pid { get; }
        public long CreateTime { get; }

        public string Cpu
        {
            get => _cpu;
            set { if (_cpu != value) { _cpu = value; Raise(nameof(Cpu)); } }
        }

        public string Memory
        {
            get => _memory;
            set { if (_memory != value) { _memory = value; Raise(nameof(Memory)); } }
        }

        public string Disk
        {
            get => _disk;
            set { if (_disk != value) { _disk = value; Raise(nameof(Disk)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
```

- [ ] **Step 6: Add the live half**

Append to `TaskManagerViewModel`:

```csharp
        private readonly ProcessSampler _sampler;
        private readonly Action _onUpdated;
        private bool _disposed;

        private string _searchText = "";
        private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;
        private bool _sortDescending = true;
        private int _count;

        public TaskManagerViewModel(ProcessSampler sampler)
        {
            _sampler = sampler;

            // Updated arrives on the sampler's background thread; Rows is bound to the UI.
            _onUpdated = () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
            _sampler.Updated += _onUpdated;
            _sampler.Retain();

            // Render whatever is already in memory rather than waiting up to two seconds for
            // the next tick. When the panel or the slowdown recorder already holds a lease this
            // paints a full list immediately, which is the entire point on a busy machine.
            Refresh();
        }

        public ObservableCollection<ProcessRow> Rows { get; } = new();

        public string SearchText
        {
            get => _searchText;
            set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); Refresh(); } }
        }

        /// <summary>Row count after filtering, for the header.</summary>
        public int Count
        {
            get => _count;
            private set { if (_count != value) { _count = value; OnPropertyChanged(); } }
        }

        /// <summary>Sets the sort column, flipping direction when the same column is chosen twice.</summary>
        public void SortBy(ProcessSortColumn column)
        {
            if (_sortColumn == column) _sortDescending = !_sortDescending;
            else { _sortColumn = column; _sortDescending = column != ProcessSortColumn.Name; }
            Refresh();
        }

        public void Refresh()
        {
            if (_disposed) return;

            var rows = Filter(_sampler.AllProcesses, _searchText).ToList();
            Sort(rows, _sortColumn, _sortDescending);

            // Rebuild only when the set of processes changes; otherwise update in place, so a
            // selection and scroll position survive a tick. Rebuilding every two seconds would
            // move the row out from under the cursor at the exact moment it is being read.
            bool sameSet = Rows.Count == rows.Count;
            if (sameSet)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (Rows[i].Pid == rows[i].Pid && Rows[i].CreateTime == rows[i].CreateTime) continue;
                    sameSet = false;
                    break;
                }
            }

            if (!sameSet)
            {
                Rows.Clear();
                foreach (var p in rows) Rows.Add(new ProcessRow(p.Name, p.Pid, p.CreateTime));
            }

            bool hasCpu = _sampler.HasCpuData;
            for (int i = 0; i < rows.Count; i++)
            {
                Rows[i].Cpu = CpuTextFor(rows[i], hasCpu);
                Rows[i].Memory = rows[i].WorkingSetText;
                Rows[i].Disk = rows[i].DiskBytesPerSec > 0 ? rows[i].DiskText : "—";
            }

            Count = rows.Count;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sampler.Updated -= _onUpdated;
            _sampler.Release();
        }
```

Declare the class as `INotifyPropertyChanged, IDisposable`.

- [ ] **Step 7: Run the whole suite**

Expected: 453 passed.

- [ ] **Step 8: Commit**

```bash
git add ViewModels/TaskManagerViewModel.cs tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs
git commit -m "feat(taskmgr): view model with pure sort and filter"
```

---

### Task 7: The window

**Files:**
- Create: `TaskManagerWindow.xaml`, `TaskManagerWindow.xaml.cs`
- Modify: `StatsPanelWindow.xaml` (the CPU card's "Task Manager" quick button)
- Test: `tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs` (add)

**Interfaces:**
- Consumes: `TaskManagerViewModel`.
- Produces: `TaskManagerWindow.ShowOrActivate(ProcessSampler sampler)`.

**Pattern to follow:** `HardwareInfoWindow.xaml` for the window chrome, dark palette and title bar.

- [ ] **Step 1: Build the window**

A `ListView` with a `GridView` of five columns — Name, PID, CPU, Memory, Disk. Virtualization is not optional:

```xml
<ListView ItemsSource="{Binding Rows}"
          VirtualizingPanel.IsVirtualizing="True"
          VirtualizingPanel.VirtualizationMode="Recycling"
          VirtualizingPanel.ScrollUnit="Pixel"
          ScrollViewer.IsDeferredScrollingEnabled="False">
```

`Recycling` reuses row containers instead of building one visual tree per process; with three hundred processes the difference is the whole point of the feature.

Above the list: a search `TextBox` bound to `SearchText` with `UpdateSourceTrigger=PropertyChanged`, and a count. Below it: an **End task** button and a status line for the kill result.

Column headers are `GridViewColumnHeader` with `Click` handlers calling `SortBy(column)`.

An empty list must never be the way this window reports a problem — an empty list is one of the
four symptoms it replaces. Overlay the list with an explicit message when `Count` is zero, and
distinguish the two reasons:

```xml
<TextBlock x:Name="EmptyMessage" HorizontalAlignment="Center" VerticalAlignment="Center"
           FontSize="11" Foreground="#88EDEDF2" TextWrapping="Wrap" MaxWidth="320"
           TextAlignment="Center" />
```

Set its text in `Refresh`: when the snapshot itself is empty, "Waiting for the first sample…";
when the snapshot has rows but the filter removed them all, "No process matches" plus the search
term. Collapse it whenever `Count > 0`.

- [ ] **Step 2: Verify every StaticResource exists**

Run: `grep -o 'StaticResource [A-Za-z]*' TaskManagerWindow.xaml | sort -u`

Then confirm each key is defined in that file or in `App.xaml`. `StaticResource` is not compile-checked, and a missing key crashes the window at runtime with a green build — this exact mistake shipped once already as `SpecValue`.

- [ ] **Step 3: Wire the opening path**

`StatsPanelWindow.xaml:274` has a quick button `Content="Task Manager" Tag="taskmgr"` that shells out to Windows Task Manager. Change it to open this window, and add a second button `Content="Windows Task Manager" Tag="taskmgr"` so the original is still reachable.

`ShowOrActivate` keeps a single static instance: a second window would take a second sampler lease and double the row count for no benefit.

- [ ] **Step 4: Write the failing test**

```csharp
        /// <summary>
        /// Virtualization is the difference between this window and the one it replaces. With
        /// several hundred processes, a non-virtualized ListView builds a visual tree per row
        /// and costs more than the problem being diagnosed.
        /// </summary>
        [Fact]
        public void The_process_list_is_virtualized_and_recycling()
        {
            string xaml = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "TaskManagerWindow.xaml"));

            Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xaml);
            Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", xaml);
        }
```

`RepoRoot()` walks up from `AppContext.BaseDirectory` until it finds `Kil0bitSystemMonitor.csproj`.

- [ ] **Step 5: Run the whole suite**

Expected: 454 passed.

- [ ] **Step 6: Render it**

Add a capture to the render harness and confirm: three hundred rows scroll smoothly, the search box filters, headers sort, and the CPU column shows dashes before the second sample rather than zeroes.

- [ ] **Step 7: Commit**

```bash
git add TaskManagerWindow.xaml TaskManagerWindow.xaml.cs StatsPanelWindow.xaml tests/Kil0bitSystemMonitor.Tests/TaskManagerTests.cs
git commit -m "feat(taskmgr): the window"
```

---

### Task 8: The kill flow and elevation retry

**Files:**
- Modify: `TaskManagerWindow.xaml.cs`
- Test: none — the UAC transition is not unit testable.

**Interfaces:**
- Consumes: `ProcessControl.TryEndTask`, `EndTaskResult`, `KillArguments.Switch`.

- [ ] **Step 1: Implement End task**

On click, take the selected row's `Pid`, `Name` and `CreateTime`, call `TryEndTask`, and put the returned message in the status line **whatever it says**. There is no silent path.

- [ ] **Step 2: Offer elevation on refusal**

When the result is `AccessDenied`, show a *Retry as administrator* button. On click:

```csharp
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!,
                Arguments = Services.KillArguments.Switch + " "
                            + pid.ToString(CultureInfo.InvariantCulture) + " "
                            + createTime.ToString(CultureInfo.InvariantCulture),
                Verb = "runas",          // triggers the consent prompt
                UseShellExecute = true,  // required for runas
            };

            try
            {
                using var elevated = System.Diagnostics.Process.Start(psi);
                elevated!.WaitForExit(10_000);
                Status = elevated.ExitCode == 0
                    ? "Ended " + name + " as administrator."
                    : "Could not end " + name + ", even elevated. It may be protected by Windows.";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user dismissed the consent prompt. That is an answer, not an error.
                Status = "Cancelled — " + name + " is still running.";
            }
```

Dismissing UAC throws `Win32Exception` with `ERROR_CANCELLED`; treating that as a failure would tell the user something went wrong when they simply said no.

- [ ] **Step 3: Refuse critical processes visibly**

`TryEndTask` already returns `Critical` without opening a handle. Show its message, and do **not** offer elevation — elevation does not make terminating `csrss.exe` a good idea.

- [ ] **Step 4: Verify by hand**

Kill an ordinary process (Notepad) — expect it to disappear and the status to say so. Kill an elevated process — expect access denied and the retry button, then confirm the elevated retry works. Select a critical process — expect the refusal and no retry button. Dismiss a UAC prompt — expect "Cancelled", not an error.

- [ ] **Step 5: Run the whole suite**

Expected: 454 passed.

- [ ] **Step 6: Commit**

```bash
git add TaskManagerWindow.xaml.cs
git commit -m "feat(taskmgr): kill flow with per-kill elevation"
```

---

### Task 9: Docs and release

**Files:**
- Modify: `README.md` (English at :50-57, Thai at :573-580), `GUIDE.md`, `Kil0bitSystemMonitor.csproj:15`

**Note:** there is no `README.th.md`. Both languages live in `README.md`.

- [ ] **Step 1: Document the window**

In both language sections of `README.md`, describe the process list and state plainly that MicaStats runs unelevated, so ending an elevated process asks for consent once, for that kill only.

In `GUIDE.md`, add a troubleshooting entry: **"End task says access denied"** — explaining the elevation offer — and **"Why will it not end csrss.exe?"** — explaining that doing so stops Windows.

Write `%APPDATA%` paths using string concatenation rather than an embedded backslash escape; `GUIDE.md` previously acquired a literal carriage return that way.

- [ ] **Step 2: Bump the version**

`Kil0bitSystemMonitor.csproj:15` → `<Version>1.9.0</Version>`.

- [ ] **Step 3: Run the whole suite**

Expected: 454 passed.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(taskmgr): docs; v1.9.0"
```

- [ ] **Step 5: Release**

Full suite green → publish to `release-output` → build the installer with
`ISCC.exe /DAppVersion=1.9.0 installer.iss` → compute SHA-256 with `sha256sum`, cross-check
with `certutil`, and assert the sidecar is non-empty → `gh release create v1.9.0 --latest` →
verify `git rev-parse "v1.9.0^{commit}"` equals HEAD → refresh `deploy/` → rebuild
`bin\Release` and restart the app.

`Get-FileHash` is not available in the `powershell` on PATH and silently produces an empty
sidecar. Rebuild `bin\Release` explicitly before restarting: publishing to `release-output`
does not update it, and the running app will otherwise report a stale commit.

---

## Risks

**The elevated path is the whole security surface.** It is reachable by anyone who can run the
executable, but grants nothing `taskkill /f` does not. The guarantee that matters is that it
does exactly one thing, which is why Task 4 tests the parser against hostile input and Task 5
places the block above every other line of `OnStartup`.

**The creation-time check is load-bearing.** Without it, a UAC prompt left sitting for a minute
could terminate a process that happened to inherit the pid. Task 3 checks it before terminating
and Task 5 verifies by hand that a mismatched time refuses.

**Killing is irreversible.** There is no undo and no confirmation dialog for ordinary
processes, which is deliberate — a confirmation on every kill trains people to click through
it. The protection is that critical processes are refused outright rather than confirmed.
