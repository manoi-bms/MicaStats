# Orphan search watchdog implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect Git Bash `find.exe` processes left orphaned by an agentic tool that did not reap its children, report them in a toast, and end them on the user's click with a verified, escalating kill.

**Architecture:** A pure decision core (`OrphanScan.Decide`) takes plain records and returns kill/keep verdicts with reasons; an impure shell (`OrphanWatchdog`) collects those records from a one-shot kernel snapshot every 60 seconds, logs every candidate verdict, raises a toast, and owns the kill escalation and the unkillable ledger. Nothing in the decision path touches a process API, so the rules are unit tested with synthetic records.

**Tech Stack:** C# 12, .NET 8 (`net8.0-windows`), WPF, xunit 2.9.2, P/Invoke to `ntdll.dll` and `kernel32.dll`.

**Spec:** `docs/superpowers/specs/2026-09-22-orphan-search-watchdog-design.md`

## Global Constraints

- **Build and test only with the user-local SDK.** There is no system .NET SDK on this machine. Every build or test command is `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" ...`, never bare `dotnet`.
- **Namespace:** all new service code lives in `Kil0bitSystemMonitor.Services.Watchdog`. Test code lives in `Kil0bitSystemMonitor.Tests`.
- **Nullable reference types are enabled** project-wide. Do not add `#nullable disable`.
- **Never format a date or number with the ambient culture.** Use `CultureInfo.InvariantCulture`. A Thai locale stamps Buddhist-era years (2569) through `ToString` defaults, and `DiagnosticsLog` already guards this for exactly that reason.
- **The pure functions take `DateTime now` as a parameter.** They never read `DateTime.Now` or `DateTime.UtcNow`.
- **Match search binaries on full image path, never on process name.** `C:\Windows\System32\find.exe` is an unrelated Microsoft tool and must never match.
- **XML doc comments on every public type and member**, in the house style: state *why* a decision was made where the reason is not obvious from the code. Match the density in `Services/ProcessControl.cs`.
- **Every commit message ends with this trailer**, on its own line after a blank line:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Avoid apostrophes in commit message bodies.** Commits are written through a `git commit -F -` heredoc in this environment, and an apostrophe breaks it.
- **Do not add package references.** Everything needed is already referenced.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Services/Watchdog/SearchCommandLine.cs` | Tokenise a command line; decide whether a scan is unbounded | 1 |
| `Services/Watchdog/ProcessRecord.cs` | The plain record the decision consumes; `OrphanVerdict` | 2 |
| `Services/Watchdog/OrphanScanOptions.cs` | Thresholds and allowlists, projected from `AppConfig` | 2 |
| `Services/Watchdog/OrphanScan.cs` | The five rules; pure | 2 |
| `Services/Watchdog/ParentState.cs` | `parentExists` with the recycled-PID guard; pure | 3 |
| `Services/ProcessSampler.cs` | *(modify)* parent PID, cumulative CPU, `SnapshotOnce()` | 4 |
| `Services/Watchdog/ProcessDetails.cs` | Full image path and command line for one candidate PID | 5 |
| `Services/Watchdog/OrphanLedger.cs` | What has been logged, alerted, and given up on; pure | 6 |
| `Services/Watchdog/OrphanWatchdog.cs` | Timer, collection, logging, kill escalation | 7 |
| `Helpers/ToastStack.cs` | One bottom-right corner registry, shared by every toast | 8 |
| `AlertToastWindow.cs` | *(modify)* move onto the shared stack | 8 |
| `OrphanToastWindow.cs` | The corner card with *End them* | 8 |
| `Models/SystemMetrics.cs` | *(modify)* five `AppConfig` properties | 9 |
| `SettingsWindow.xaml` / `.xaml.cs` | *(modify)* one toggle | 9 |
| `App.xaml.cs` | *(modify)* construct, wire, dispose the watchdog | 9 |
| `README.md` | *(modify)* feature entry and the upstream-cause caveat | 10 |
| `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs` | Tasks 1, 2, 3, 6, 8 | 1 |
| `tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs` | Tasks 4, 5 | 4 |

---

### Task 1: Command line tokenising and unbounded-scan detection

This is rule 2 of the detection rule, on its own because it is the only part with real parsing in it.

**Files:**
- Create: `Services/Watchdog/SearchCommandLine.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `static IReadOnlyList<string> SearchCommandLine.Tokenize(string commandLine)`
  - `static string? SearchCommandLine.ScanRoot(string commandLine)`
  - `static bool SearchCommandLine.HasMaxDepth(string commandLine)`
  - `static bool SearchCommandLine.IsFilesystemRoot(string argument)`
  - `static bool SearchCommandLine.IsUnbounded(string commandLine)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Services.Watchdog;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The watchdog kills processes, so the rule that decides which ones is the part that has
    /// to be right. A false positive destroys someone's deliberate whole-disk search; a false
    /// negative leaves a core burning. Both halves are covered here with synthetic records,
    /// which is the entire reason the decision is a pure function.
    /// </summary>
    public class OrphanScanTests
    {
        private const string GitFind = @"C:\Program Files\Git\usr\bin\find.exe";

        // ------------------------------------------------------- command line parsing

        [Fact]
        public void A_quoted_executable_path_with_spaces_is_one_token()
        {
            var tokens = SearchCommandLine.Tokenize("\"" + GitFind + "\" / -name IdURI.pas");

            Assert.Equal(new[] { GitFind, "/", "-name", "IdURI.pas" }, tokens);
        }

        [Fact]
        public void An_unquoted_command_line_splits_on_whitespace()
        {
            var tokens = SearchCommandLine.Tokenize("find.exe  /c/   -name   x.pas");

            Assert.Equal(new[] { "find.exe", "/c/", "-name", "x.pas" }, tokens);
        }

        [Fact]
        public void The_scan_root_is_the_first_non_flag_argument_after_the_executable()
        {
            Assert.Equal("/", SearchCommandLine.ScanRoot("\"" + GitFind + "\" / -name IdURI.pas"));
        }

        [Fact]
        public void Leading_symlink_flags_are_skipped_when_finding_the_scan_root()
        {
            // find [-H] [-L] [-P] [path...] [expression] — the flags precede the path.
            Assert.Equal("/", SearchCommandLine.ScanRoot("find.exe -L / -name x"));
        }

        [Fact]
        public void A_command_line_with_no_path_argument_has_no_scan_root()
        {
            Assert.Null(SearchCommandLine.ScanRoot("find.exe"));
        }

        [Theory]
        [InlineData("/")]
        [InlineData("C:\\")]
        [InlineData("C:/")]
        [InlineData("C:")]
        [InlineData("/c/")]
        [InlineData("d:\\")]
        public void Every_spelling_of_a_filesystem_root_is_recognised(string argument)
        {
            Assert.True(SearchCommandLine.IsFilesystemRoot(argument));
        }

        [Theory]
        [InlineData("C:\\src")]
        [InlineData("/c/src")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("/proc")]
        public void A_path_below_a_root_is_not_a_root(string argument)
        {
            Assert.False(SearchCommandLine.IsFilesystemRoot(argument));
        }

        [Fact]
        public void Maxdepth_is_found_wherever_it_appears_in_the_command_line()
        {
            Assert.True(SearchCommandLine.HasMaxDepth("find.exe / -name x.pas -maxdepth 3"));
            Assert.True(SearchCommandLine.HasMaxDepth("find.exe / -maxdepth 3 -name x.pas"));
            Assert.False(SearchCommandLine.HasMaxDepth("find.exe / -name x.pas"));
        }

        [Fact]
        public void A_root_scan_without_maxdepth_is_unbounded()
        {
            Assert.True(SearchCommandLine.IsUnbounded(
                "\"" + GitFind + "\" / -iname cxEdit.pas -not -path */proc/*"));
        }

        [Fact]
        public void A_root_scan_with_maxdepth_is_bounded()
        {
            Assert.False(SearchCommandLine.IsUnbounded("\"" + GitFind + "\" / -maxdepth 3 -name x.pas"));
        }

        [Fact]
        public void A_scan_that_starts_below_a_root_is_bounded()
        {
            Assert.False(SearchCommandLine.IsUnbounded("\"" + GitFind + "\" C:\\src -name x.pas"));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: FAIL — `error CS0246: The type or namespace name 'SearchCommandLine' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `Services/Watchdog/SearchCommandLine.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// Reads a <c>find</c> command line well enough to answer one question: does this scan have
    /// a bottom?
    ///
    /// <para>
    /// Under Git Bash, <c>/</c> is the whole drive and the walk includes <c>/proc</c>, every
    /// mount, and any mapped or dead network path. A scan rooted there with no
    /// <c>-maxdepth</c> can run effectively forever, which is the condition the watchdog
    /// exists to catch. A scan rooted anywhere else, or bounded by a depth, is somebody doing
    /// ordinary work.
    /// </para>
    /// </summary>
    public static class SearchCommandLine
    {
        /// <summary>
        /// Splits a command line on whitespace, treating a double-quoted run as one token.
        ///
        /// <para>
        /// Quote handling is not decoration: the executable itself is
        /// <c>"C:\Program Files\Git\usr\bin\find.exe"</c>, so a naive split puts
        /// <c>Files\Git\usr\bin\find.exe"</c> where the scan root should be and every command
        /// line on the target machine parses wrongly.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> Tokenize(string commandLine)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return tokens;

            var current = new System.Text.StringBuilder();
            bool quoted = false;

            foreach (char c in commandLine)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        /// <summary>
        /// The directory the scan starts from, or null when the command line names none.
        ///
        /// <para>
        /// <c>find</c> takes its options in the order <c>find [-H] [-L] [-P] [path...]
        /// [expression]</c>, so the root is the first argument after the executable that is not
        /// one of those three link-handling flags.
        /// </para>
        /// </summary>
        public static string? ScanRoot(string commandLine)
        {
            var tokens = Tokenize(commandLine);

            for (int i = 1; i < tokens.Count; i++)   // index 0 is the executable
            {
                string token = tokens[i];
                if (token.Length == 0) continue;

                if (string.Equals(token, "-H", StringComparison.Ordinal) ||
                    string.Equals(token, "-L", StringComparison.Ordinal) ||
                    string.Equals(token, "-P", StringComparison.Ordinal))
                    continue;

                // Anything else starting with '-' is already the expression, which means no
                // path was given and find defaults to the working directory.
                return token[0] == '-' ? null : token;
            }

            return null;
        }

        /// <summary>Whether the command line bounds the walk with <c>-maxdepth</c> anywhere.</summary>
        public static bool HasMaxDepth(string commandLine)
        {
            foreach (string token in Tokenize(commandLine))
            {
                if (string.Equals(token, "-maxdepth", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether an argument names a whole filesystem rather than a subtree.
        ///
        /// <para>
        /// Four spellings reach the same place on this platform: the POSIX root <c>/</c>, a
        /// Windows drive root in either slash (<c>C:\</c>, <c>C:/</c>), a bare drive
        /// (<c>C:</c>), and the MSYS mount form (<c>/c/</c>). All four are unbounded; anything
        /// with a component below them is not.
        /// </para>
        /// </summary>
        public static bool IsFilesystemRoot(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return false;

            string path = argument.Replace('\\', '/');

            if (path == "/") return true;

            // C:  C:/  c:\ -> "c:" or "c:/"
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
                return path.Length == 2 || (path.Length == 3 && path[2] == '/');

            // /c/ and /c
            if (path.Length >= 2 && path[0] == '/' && char.IsLetter(path[1]))
                return path.Length == 2 || (path.Length == 3 && path[2] == '/');

            return false;
        }

        /// <summary>
        /// Whether this command line requests a scan with no bottom: rooted at a whole
        /// filesystem and not bounded by a depth.
        /// </summary>
        public static bool IsUnbounded(string commandLine)
        {
            string? root = ScanRoot(commandLine);
            return root != null && IsFilesystemRoot(root) && !HasMaxDepth(commandLine);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: PASS, 16 tests (the `[Theory]` rows count individually).

- [ ] **Step 5: Commit**

```bash
git add Services/Watchdog/SearchCommandLine.cs tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs
git commit -F - <<'MSG'
feat(watchdog): read whether a find command line has a bottom

Tokenises respecting quotes, because the Git Bash executable path
contains a space and a naive split puts a path fragment where the
scan root belongs.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 2: The five rules

**Files:**
- Create: `Services/Watchdog/ProcessRecord.cs`
- Create: `Services/Watchdog/OrphanScanOptions.cs`
- Create: `Services/Watchdog/OrphanScan.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`

**Interfaces:**
- Consumes: `SearchCommandLine.IsUnbounded`, `SearchCommandLine.ScanRoot`, `SearchCommandLine.HasMaxDepth` from Task 1.
- Produces:
  - `sealed record ProcessRecord(int Pid, int ParentPid, string ImagePath, string CommandLine, double CpuSeconds, DateTime StartTime, bool ParentExists, string ParentImagePath)`
  - `sealed record OrphanVerdict(int Pid, bool Kill, string Reason)`
  - `sealed class OrphanScanOptions` with `CpuSecondsThreshold`, `Grace`, `BinarySuffixes`, `ExpectedParents`, and `static OrphanScanOptions Defaults`
  - `static IReadOnlyList<OrphanVerdict> OrphanScan.Decide(IReadOnlyList<ProcessRecord>, OrphanScanOptions, DateTime now)`
  - `static OrphanVerdict OrphanScan.DecideOne(ProcessRecord, OrphanScanOptions, DateTime now)`
  - `static bool OrphanScan.IsAllowlisted(string imagePath, IReadOnlyList<string> suffixes)`

- [ ] **Step 1: Write the failing tests**

Append inside the `OrphanScanTests` class in `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`, before the closing brace:

```csharp
        // ------------------------------------------------------- the five rules

        private static readonly DateTime Now = new DateTime(2026, 9, 22, 14, 0, 0, DateTimeKind.Local);

        /// <summary>A record that would be killed, so each test can spoil exactly one rule.</summary>
        private static ProcessRecord Killable(
            string? imagePath = null,
            string? commandLine = null,
            double cpuSeconds = 2258,
            TimeSpan? age = null,
            bool parentExists = false,
            string parentImagePath = "") =>
            new ProcessRecord(
                Pid: 50192,
                ParentPid: 33960,
                ImagePath: imagePath ?? GitFind,
                CommandLine: commandLine ?? "\"" + GitFind + "\" / -name IdURI.pas",
                CpuSeconds: cpuSeconds,
                StartTime: Now - (age ?? TimeSpan.FromHours(2)),
                ParentExists: parentExists,
                ParentImagePath: parentImagePath);

        [Fact]
        public void The_windows_string_filter_that_shares_the_name_is_never_touched()
        {
            // C:\Windows\System32\find.exe is a Microsoft batch-script tool, unrelated to the
            // GNU find that leaks. Matching on process name alone would kill it.
            var verdict = OrphanScan.DecideOne(
                Killable(imagePath: @"C:\Windows\System32\find.exe", cpuSeconds: 5000),
                OrphanScanOptions.Defaults, Now);

            Assert.False(verdict.Kill);
            Assert.Contains("not an allowlisted search binary", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_bounded_scan_is_kept_however_much_cpu_it_has_burned()
        {
            var verdict = OrphanScan.DecideOne(
                Killable(commandLine: "\"" + GitFind + "\" / -maxdepth 3 -name x.pas", cpuSeconds: 5000),
                OrphanScanOptions.Defaults, Now);

            Assert.False(verdict.Kill);
            Assert.Contains("bounded", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_scan_under_the_cpu_threshold_is_kept()
        {
            var verdict = OrphanScan.DecideOne(
                Killable(cpuSeconds: 10, age: TimeSpan.FromSeconds(30)),
                OrphanScanOptions.Defaults, Now);

            Assert.False(verdict.Kill);
            Assert.Contains("120s threshold", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_scan_inside_the_grace_period_is_kept_even_past_the_cpu_threshold()
        {
            // A deliberate search on a fast machine can burn two minutes of CPU in the first
            // minute of wall clock. The grace period is what stops that being killed.
            var verdict = OrphanScan.DecideOne(
                Killable(cpuSeconds: 300, age: TimeSpan.FromMinutes(1)),
                OrphanScanOptions.Defaults, Now);

            Assert.False(verdict.Kill);
            Assert.Contains("grace period", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void An_orphan_burning_cpu_is_killed_and_the_reason_says_the_parent_exited()
        {
            var verdict = OrphanScan.DecideOne(Killable(), OrphanScanOptions.Defaults, Now);

            Assert.True(verdict.Kill);
            Assert.Contains("parent 33960 has exited", verdict.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("recognised shell", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_live_shell_parent_keeps_the_scan_however_much_cpu_it_has_burned()
        {
            // Rule 5 exists because an orphan has no consumer for its output. A live bash
            // parent is exactly the case where a consumer still exists, so this is a
            // deliberate search and killing it would contradict the rule it is filed under.
            var verdict = OrphanScan.DecideOne(
                Killable(parentExists: true, parentImagePath: @"C:\Program Files\Git\usr\bin\bash.exe"),
                OrphanScanOptions.Defaults, Now);

            Assert.False(verdict.Kill);
            Assert.Contains("live shell", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_live_non_shell_parent_is_killed_and_the_reason_names_the_parent()
        {
            var verdict = OrphanScan.DecideOne(
                Killable(parentExists: true, parentImagePath: @"C:\Windows\explorer.exe"),
                OrphanScanOptions.Defaults, Now);

            Assert.True(verdict.Kill);
            Assert.Contains("explorer.exe", verdict.Reason, StringComparison.Ordinal);
            Assert.Contains("not a recognised shell", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_input_produces_no_verdicts_and_no_exception()
        {
            var verdicts = OrphanScan.Decide(Array.Empty<ProcessRecord>(), OrphanScanOptions.Defaults, Now);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void Both_observed_orphans_from_the_reported_machine_are_killed()
        {
            var records = new[]
            {
                new ProcessRecord(50192, 33960, GitFind,
                    "\"" + GitFind + "\" / -name IdURI.pas",
                    2258, Now.AddHours(-2), false, ""),
                new ProcessRecord(12264, 21452, GitFind,
                    "\"" + GitFind + "\" / -iname cxEdit.pas -not -path */proc/*",
                    2115, Now.AddHours(-2), false, ""),
            };

            var verdicts = OrphanScan.Decide(records, OrphanScanOptions.Defaults, Now);

            Assert.Equal(2, verdicts.Count);
            Assert.All(verdicts, v => Assert.True(v.Kill));
        }

        [Fact]
        public void Thresholds_come_from_the_options_rather_than_being_baked_in()
        {
            var strict = new OrphanScanOptions
            {
                CpuSecondsThreshold = 5,
                Grace = TimeSpan.FromSeconds(10),
            };

            var verdict = OrphanScan.DecideOne(
                Killable(cpuSeconds: 10, age: TimeSpan.FromSeconds(30)), strict, Now);

            Assert.True(verdict.Kill);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: FAIL — `error CS0246: The type or namespace name 'ProcessRecord' could not be found`.

- [ ] **Step 3: Write the records and options**

Create `Services/Watchdog/ProcessRecord.cs`:

```csharp
using System;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// One process, flattened to exactly what the decision needs.
    ///
    /// <para>
    /// A plain record rather than a handle or a <see cref="System.Diagnostics.Process"/> is the
    /// point: it is what lets the rules be exercised against the two orphans observed on the
    /// reported machine without those orphans existing. Everything expensive or privileged
    /// happens before one of these is built.
    /// </para>
    /// </summary>
    /// <param name="ParentExists">
    /// Whether the parent is still running. Resolved by <see cref="ParentState"/>, which also
    /// rejects a recycled parent PID — an unrelated newcomer wearing the dead parent number.
    /// </param>
    /// <param name="ParentImagePath">
    /// The parent's image path, or an empty string when the parent is gone or unreadable. Also
    /// written to the log on every kill: it names the tool that leaked the child, which is the
    /// only route to fixing the cause rather than the symptom.
    /// </param>
    public sealed record ProcessRecord(
        int Pid,
        int ParentPid,
        string ImagePath,
        string CommandLine,
        double CpuSeconds,
        DateTime StartTime,
        bool ParentExists,
        string ParentImagePath);

    /// <summary>What the scan decided about one process, and why.</summary>
    /// <param name="Reason">
    /// Human-readable and branch-specific. A keep names the rule that saved the process; a kill
    /// names which half of rule 5 fired. The log is read after the fact by someone asking why a
    /// process was or was not ended, and a generic reason answers neither question.
    /// </param>
    public sealed record OrphanVerdict(int Pid, bool Kill, string Reason);
}
```

Create `Services/Watchdog/OrphanScanOptions.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// The thresholds and allowlists the rules are measured against.
    ///
    /// <para>
    /// The defaults are deliberately conservative and must not be relaxed to catch more. A
    /// missed orphan costs a core until the next scan; a false positive destroys work somebody
    /// intended, silently, and they find out when the results never arrive.
    /// </para>
    /// </summary>
    public sealed class OrphanScanOptions
    {
        /// <summary>Rule 3. Below this, a scan is still plausibly doing its job.</summary>
        public double CpuSecondsThreshold { get; init; } = 120;

        /// <summary>Rule 4. Nothing younger than this is ever killed.</summary>
        public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Rule 1. Image-path suffixes, matched case-insensitively after slashes are normalised.
        /// A suffix rather than a name so <c>C:\Windows\System32\find.exe</c> cannot match.
        /// </summary>
        public IReadOnlyList<string> BinarySuffixes { get; init; } = new[] { @"\Git\usr\bin\find.exe" };

        /// <summary>
        /// Rule 5. Parent images that mean a human still has somewhere to receive the output.
        /// Compared on file name only: the same shell ships from several install locations.
        /// </summary>
        public IReadOnlyList<string> ExpectedParents { get; init; } =
            new[] { "bash.exe", "sh.exe", "pwsh.exe", "cmd.exe" };

        /// <summary>The shipped defaults, as described in the README.</summary>
        public static OrphanScanOptions Defaults { get; } = new();
    }
}
```

- [ ] **Step 4: Write the rules**

Create `Services/Watchdog/OrphanScan.cs`:

```csharp
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

            // 2 — unbounded scan.
            if (!SearchCommandLine.IsUnbounded(record.CommandLine))
            {
                string reason = SearchCommandLine.HasMaxDepth(record.CommandLine)
                    ? "bounded by -maxdepth"
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: PASS, 26 tests.

- [ ] **Step 6: Commit**

```bash
git add Services/Watchdog/ProcessRecord.cs Services/Watchdog/OrphanScanOptions.cs Services/Watchdog/OrphanScan.cs tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs
git commit -F - <<'MSG'
feat(watchdog): the five rules, as a pure function

Records and options in, verdicts out. No process API and no clock, so
the rule that ends processes is exercised against the two orphans
observed on the reported machine without those orphans existing.

Rule 5 keeps a scan whose parent is a live shell: the rule exists
because an orphan has no consumer for its output, and a live shell is
exactly the case where a consumer still exists.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 3: Parent resolution with the recycled-PID guard

**Files:**
- Create: `Services/Watchdog/ParentState.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `readonly record struct ParentState.SnapshotEntry(int Pid, DateTime StartTime, string ImagePath)`
  - `static bool ParentState.Resolve(int parentPid, DateTime childStartTime, IReadOnlyDictionary<int, ParentState.SnapshotEntry> snapshot, out string parentImagePath)`

- [ ] **Step 1: Write the failing tests**

Append inside the `OrphanScanTests` class, before the closing brace:

```csharp
        // ------------------------------------------------------- parent resolution

        private static Dictionary<int, ParentState.SnapshotEntry> Snapshot(
            params ParentState.SnapshotEntry[] entries)
        {
            var map = new Dictionary<int, ParentState.SnapshotEntry>();
            foreach (var entry in entries) map[entry.Pid] = entry;
            return map;
        }

        [Fact]
        public void A_parent_absent_from_the_snapshot_does_not_exist()
        {
            bool exists = ParentState.Resolve(
                33960, Now.AddHours(-2), Snapshot(), out string parentImage);

            Assert.False(exists);
            Assert.Equal("", parentImage);
        }

        [Fact]
        public void A_parent_older_than_its_child_exists()
        {
            var bash = new ParentState.SnapshotEntry(
                33960, Now.AddHours(-3), @"C:\Program Files\Git\usr\bin\bash.exe");

            bool exists = ParentState.Resolve(
                33960, Now.AddHours(-2), Snapshot(bash), out string parentImage);

            Assert.True(exists);
            Assert.Equal(@"C:\Program Files\Git\usr\bin\bash.exe", parentImage);
        }

        [Fact]
        public void A_parent_younger_than_its_child_is_a_recycled_pid_and_does_not_exist()
        {
            // The real parent exited and Windows handed 33960 to something else. Asking only
            // whether the PID is present finds the newcomer and wrongly reports a live parent,
            // which keeps an orphan alive forever.
            var newcomer = new ParentState.SnapshotEntry(
                33960, Now.AddMinutes(-5), @"C:\Windows\notepad.exe");

            bool exists = ParentState.Resolve(
                33960, Now.AddHours(-2), Snapshot(newcomer), out string parentImage);

            Assert.False(exists);
            Assert.Equal("", parentImage);
        }

        [Fact]
        public void A_parent_pid_of_zero_does_not_exist()
        {
            bool exists = ParentState.Resolve(
                0, Now.AddHours(-2), Snapshot(), out string parentImage);

            Assert.False(exists);
            Assert.Equal("", parentImage);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: FAIL — `error CS0103: The name 'ParentState' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `Services/Watchdog/ParentState.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// Answers whether a process still has its parent, from a snapshot rather than from a
    /// handle.
    ///
    /// <para>
    /// Pure, and separate from <see cref="OrphanScan"/>, because it carries the one piece of
    /// reasoning that the spec's <c>parentExists</c> flag quietly assumes somebody got right:
    /// a PID is not an identity. Windows reuses process IDs freely, so a dead parent number can
    /// belong to an unrelated process minutes later.
    /// </para>
    /// </summary>
    public static class ParentState
    {
        /// <summary>One process in the snapshot, as much of it as parent resolution needs.</summary>
        public readonly record struct SnapshotEntry(int Pid, DateTime StartTime, string ImagePath);

        /// <summary>
        /// Whether <paramref name="parentPid"/> is a live parent of a process started at
        /// <paramref name="childStartTime"/>.
        ///
        /// <para>
        /// A candidate that started <em>after</em> its supposed child is rejected. Nothing can
        /// be its own child's junior: that entry is a recycled PID, the real parent is gone, and
        /// the child is an orphan. Testing only for presence would find the newcomer, report a
        /// live parent, and leave the orphan running forever — exactly the case the watchdog
        /// exists to end.
        /// </para>
        /// </summary>
        public static bool Resolve(
            int parentPid,
            DateTime childStartTime,
            IReadOnlyDictionary<int, SnapshotEntry> snapshot,
            out string parentImagePath)
        {
            parentImagePath = "";

            // PID 0 is the idle process and is never a real parent.
            if (parentPid <= 0 || snapshot == null) return false;
            if (!snapshot.TryGetValue(parentPid, out SnapshotEntry parent)) return false;
            if (parent.StartTime > childStartTime) return false;

            parentImagePath = parent.ImagePath ?? "";
            return true;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: PASS, 30 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Watchdog/ParentState.cs tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs
git commit -F - <<'MSG'
feat(watchdog): reject a recycled parent pid

A PID is not an identity. A parent entry that started after its own
child is an unrelated newcomer wearing the dead parent number, and
treating it as a live parent leaves the orphan running forever.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 4: One-shot kernel snapshot with parent and cumulative CPU

`ProcessSampler` currently samples only while a caller holds a `Retain()` lease, and it does not surface the parent PID or cumulative CPU time. The watchdog needs all three, without forcing two-second sampling all day for a check that runs once a minute.

**Files:**
- Modify: `Services/ProcessSampler.cs`
- Create: `tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `readonly record struct ProcessSampler.RawProcess(int Pid, int ParentPid, string Name, long CreateTime, double CpuSeconds)`
  - `static IReadOnlyList<ProcessSampler.RawProcess> ProcessSampler.SnapshotOnce()`

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs`:

```csharp
using System;
using System.Linq;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The watchdog runs when no window is open, so it cannot ride the sampler's lease-driven
    /// timer. These exercise the one-shot pass it uses instead, against the only process whose
    /// identity the test already knows: itself.
    /// </summary>
    public class ProcessSnapshotTests
    {
        [Fact]
        public void A_one_shot_snapshot_finds_the_current_process()
        {
            var snapshot = ProcessSampler.SnapshotOnce();
            int self = Environment.ProcessId;

            Assert.NotEmpty(snapshot);
            Assert.Contains(snapshot, p => p.Pid == self);
        }

        [Fact]
        public void The_current_process_carries_its_real_parent_pid()
        {
            var snapshot = ProcessSampler.SnapshotOnce();
            var me = snapshot.First(p => p.Pid == Environment.ProcessId);

            // The test host was started by something, and a parent pid of zero would mean the
            // offset is wrong rather than that the process has no parent.
            Assert.True(me.ParentPid > 0);
        }

        [Fact]
        public void The_current_process_reports_cumulative_cpu_and_a_creation_time()
        {
            var snapshot = ProcessSampler.SnapshotOnce();
            var me = snapshot.First(p => p.Pid == Environment.ProcessId);

            // Running this test costs CPU, so the total cannot be zero, and the creation time
            // must not be wilder than the process is old.
            Assert.True(me.CpuSeconds > 0);
            Assert.True(me.CreateTime > 0);

            DateTime started = DateTime.FromFileTime(me.CreateTime);
            Assert.True(started <= DateTime.Now);
            Assert.True(started > DateTime.Now.AddDays(-1));
        }

        [Fact]
        public void A_one_shot_snapshot_does_not_start_the_sampler()
        {
            // Taking a Retain() lease would run full two-second sampling all day to serve a
            // check that runs once a minute.
            using var sampler = new ProcessSampler();

            ProcessSampler.SnapshotOnce();

            Assert.False(sampler.Enabled);
            Assert.Empty(sampler.AllProcesses);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessSnapshotTests"`

Expected: FAIL — `error CS0117: 'ProcessSampler' does not contain a definition for 'SnapshotOnce'`.

- [ ] **Step 3: Add the parent-PID offset**

In `Services/ProcessSampler.cs`, find this line:

```csharp
        private const int OffUniqueProcessId = 0x50;
```

Add immediately after it:

```csharp

        // InheritedFromUniqueProcessId, in the same buffer as everything above. The whole
        // reason the watchdog costs no extra syscall: orphan detection needs the parent, and
        // the parent is already here.
        private const int OffParentProcessId = 0x58;
```

- [ ] **Step 4: Make the buffer query reusable**

In `Services/ProcessSampler.cs`, replace the whole `TryQuery` method:

```csharp
        private bool TryQuery()
        {
            if (_bufferSize == 0)
            {
                _bufferSize = 1 << 21; // 2 MB covers a typical desktop's ~12k threads
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }

            for (int attempt = 0; attempt < 6; attempt++)
            {
                uint status = NtQuerySystemInformation(SystemProcessInformation, _buffer, (uint)_bufferSize, out uint needed);
                if (status == 0) return true;
                if (status != STATUS_INFO_LENGTH_MISMATCH) return false;

                // Grow past what the kernel asked for: more processes may appear before the retry.
                int target = Math.Max(_bufferSize * 2, (int)needed + (64 * 1024));
                Marshal.FreeHGlobal(_buffer);
                _bufferSize = target;
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }
            return false;
        }
```

with:

```csharp
        private bool TryQuery() => TryQuery(ref _buffer, ref _bufferSize);

        /// <summary>
        /// Fills a caller-owned buffer with a fresh snapshot, growing it on
        /// STATUS_INFO_LENGTH_MISMATCH. The required size scales with thread count rather than
        /// process count, so it moves around.
        ///
        /// <para>
        /// Static and buffer-agnostic so the one-shot snapshot can use it without touching the
        /// instance buffer, which belongs to the sampling timer and is held under its lock.
        /// </para>
        /// </summary>
        private static bool TryQuery(ref IntPtr buffer, ref int bufferSize)
        {
            if (bufferSize == 0)
            {
                bufferSize = 1 << 21; // 2 MB covers a typical desktop's ~12k threads
                buffer = Marshal.AllocHGlobal(bufferSize);
            }

            for (int attempt = 0; attempt < 6; attempt++)
            {
                uint status = NtQuerySystemInformation(SystemProcessInformation, buffer, (uint)bufferSize, out uint needed);
                if (status == 0) return true;
                if (status != STATUS_INFO_LENGTH_MISMATCH) return false;

                // Grow past what the kernel asked for: more processes may appear before the retry.
                int target = Math.Max(bufferSize * 2, (int)needed + (64 * 1024));
                Marshal.FreeHGlobal(buffer);
                bufferSize = target;
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
            return false;
        }
```

- [ ] **Step 5: Add the one-shot snapshot**

In `Services/ProcessSampler.cs`, insert immediately before the `Dispose` method:

```csharp
        /// <summary>
        /// One process as the watchdog needs it: identity, parent, and how much processor time
        /// it has consumed since it started.
        /// </summary>
        /// <param name="CreateTime">Creation time as a FILETIME, as the kernel reports it.</param>
        /// <param name="CpuSeconds">Cumulative user plus kernel time, in seconds.</param>
        public readonly record struct RawProcess(
            int Pid, int ParentPid, string Name, long CreateTime, double CpuSeconds);

        /// <summary>
        /// Every process, from a single pass, without starting or disturbing the sampler.
        ///
        /// <para>
        /// The sampler proper runs only while a caller holds a <see cref="Retain"/> lease — a
        /// window being open. The watchdog has to work when nothing is open, and taking a lease
        /// would run full two-second sampling all day to serve a check that happens once a
        /// minute. This allocates its own buffer, makes one call, walks it, and frees it.
        /// </para>
        ///
        /// <para>
        /// Returns an empty list rather than throwing on any failure. It runs unattended.
        /// </para>
        /// </summary>
        public static IReadOnlyList<RawProcess> SnapshotOnce()
        {
            IntPtr buffer = IntPtr.Zero;
            int size = 0;

            try
            {
                if (!TryQuery(ref buffer, ref size)) return Array.Empty<RawProcess>();

                var result = new List<RawProcess>(512);
                IntPtr entry = buffer;

                while (true)
                {
                    int next = Marshal.ReadInt32(entry, OffNextEntry);

                    long pid = Marshal.ReadIntPtr(entry, OffUniqueProcessId).ToInt64();
                    if (pid != 0)
                    {
                        long parent = Marshal.ReadIntPtr(entry, OffParentProcessId).ToInt64();
                        long createTime = Marshal.ReadInt64(entry, OffCreateTime);
                        long cpuTime = Marshal.ReadInt64(entry, OffUserTime)
                                       + Marshal.ReadInt64(entry, OffKernelTime);

                        result.Add(new RawProcess(
                            (int)pid,
                            (int)parent,
                            ReadImageName(entry, pid),
                            createTime,
                            cpuTime / 10_000_000d));   // kernel times are 100ns units
                    }

                    if (next == 0) break;
                    entry = IntPtr.Add(entry, next);
                }

                return result;
            }
            catch
            {
                return Array.Empty<RawProcess>();
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessSnapshotTests"`

Expected: PASS, 4 tests.

- [ ] **Step 7: Run the whole suite to confirm the refactor broke nothing**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`

Expected: PASS, all tests, including the pre-existing `ProcessSamplerTests` and `TaskManagerTests`.

- [ ] **Step 8: Commit**

```bash
git add Services/ProcessSampler.cs tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs
git commit -F - <<'MSG'
feat(watchdog): one-shot process snapshot with parent and cpu total

The sampler runs only while a window holds a lease, and the watchdog
must work when nothing is open. Taking a lease would run full
two-second sampling all day to serve a once-a-minute check, so this
makes its own single pass with its own buffer.

Parent PID comes from offset 0x58 of the buffer already being read,
so orphan detection costs no additional syscall.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 5: Full image path and command line for a candidate

The snapshot gives a bare image name. Rules 1 and 2 need the full path and the command line, which cost a handle and two calls each — paid only for processes whose name already matched the allowlist, normally none.

**Files:**
- Create: `Services/Watchdog/ProcessDetails.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `static bool ProcessDetails.TryRead(int pid, out string imagePath, out string commandLine)`

- [ ] **Step 1: Write the failing tests**

Add this using directive to the top of `tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs`, after the existing ones:

```csharp
using Kil0bitSystemMonitor.Services.Watchdog;
```

Append inside the `ProcessSnapshotTests` class, before the closing brace:

```csharp
        [Fact]
        public void The_current_process_reports_its_own_image_path_and_command_line()
        {
            bool ok = ProcessDetails.TryRead(Environment.ProcessId, out string image, out string commandLine);

            Assert.True(ok);
            Assert.Equal(Environment.ProcessPath, image, ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(commandLine));
        }

        [Fact]
        public void A_pid_that_does_not_exist_is_reported_as_unreadable_rather_than_throwing()
        {
            // A process can exit between the snapshot and the enrichment; that is ordinary, not
            // an error, and the watchdog runs unattended.
            bool ok = ProcessDetails.TryRead(-1, out string image, out string commandLine);

            Assert.False(ok);
            Assert.Equal("", image);
            Assert.Equal("", commandLine);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessSnapshotTests"`

Expected: FAIL — `error CS0103: The name 'ProcessDetails' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `Services/Watchdog/ProcessDetails.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// The two facts the snapshot does not carry: the full image path and the command line.
    ///
    /// <para>
    /// Both cost a handle and a call, so they are read only for processes whose bare image name
    /// already matched the allowlist — on a normal machine, none. Reading them for every
    /// process would reproduce the per-process enrichment that makes Windows Task Manager slow
    /// to open on a struggling machine.
    /// </para>
    ///
    /// <para>
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is enough for both, so this works unelevated
    /// against same-user processes, which is what the leaked children are.
    /// </para>
    /// </summary>
    public static class ProcessDetails
    {
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// <c>ProcessCommandLineInformation</c>. Available since Windows 8.1 and readable with
        /// limited-information rights, unlike walking the PEB, which needs
        /// <c>PROCESS_VM_READ</c> and a matching bitness.
        /// </summary>
        private const int ProcessCommandLineInformation = 60;

        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr handle, int flags, StringBuilder buffer, ref int size);

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryInformationProcess(
            IntPtr handle, int infoClass, IntPtr buffer, uint bufferSize, out uint returnLength);

        /// <summary>
        /// Reads both, or reports that it could not.
        ///
        /// <para>
        /// Returns false rather than throwing on every failure, including the ordinary one: a
        /// process that exits between the snapshot and this call. The watchdog runs unattended
        /// and a candidate that vanished needs no verdict.
        /// </para>
        /// </summary>
        public static bool TryRead(int pid, out string imagePath, out string commandLine)
        {
            imagePath = "";
            commandLine = "";

            if (pid <= 0) return false;

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;

            try
            {
                imagePath = ReadImagePath(handle);
                commandLine = ReadCommandLine(handle);
                return imagePath.Length > 0;
            }
            catch
            {
                imagePath = "";
                commandLine = "";
                return false;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string ReadImagePath(IntPtr handle)
        {
            // 32767 is the documented maximum path length with the extended-length prefix.
            int size = 32768;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : "";
        }

        /// <summary>
        /// The command line, as a UNICODE_STRING whose buffer follows the struct in the same
        /// allocation. Asked for twice: once with no room, to learn the size the kernel wants,
        /// then once for real.
        /// </summary>
        private static string ReadCommandLine(IntPtr handle)
        {
            uint status = NtQueryInformationProcess(
                handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out uint needed);

            if (status != STATUS_INFO_LENGTH_MISMATCH || needed == 0) return "";
            if (needed > 64 * 1024) return "";   // nothing legitimate is this long

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (NtQueryInformationProcess(
                        handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                    return "";

                // UNICODE_STRING on x64: USHORT Length, USHORT MaximumLength, 4 bytes padding,
                // PWSTR Buffer.
                ushort byteLength = unchecked((ushort)Marshal.ReadInt16(buffer, 0));
                IntPtr text = Marshal.ReadIntPtr(buffer, 8);

                if (text == IntPtr.Zero || byteLength == 0) return "";
                return Marshal.PtrToStringUni(text, byteLength / 2) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ProcessSnapshotTests"`

Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Watchdog/ProcessDetails.cs tests/Kil0bitSystemMonitor.Tests/ProcessSnapshotTests.cs
git commit -F - <<'MSG'
feat(watchdog): read image path and command line for a candidate

Paid only for processes whose bare image name already matched the
allowlist, which on a normal machine is none. Reading these for every
process is the per-process enrichment that makes Task Manager slow to
open on a struggling machine.

ProcessCommandLineInformation rather than a PEB walk: it needs only
limited-information rights and does not care about bitness.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 6: The ledger

What has already been logged, alerted on, and given up on. Pure, so the "never loop forever on one PID" and "never toast twice" guarantees are tested rather than hoped for.

**Files:**
- Create: `Services/Watchdog/OrphanLedger.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `readonly record struct ProcessIdentity(int Pid, long CreateTime)`
  - `sealed class OrphanLedger` with `bool ShouldLog(ProcessIdentity, string reason)`, `bool ShouldAlert(ProcessIdentity)`, `void MarkAlerted(ProcessIdentity)`, `void MarkUnkillable(ProcessIdentity)`, `bool IsUnkillable(ProcessIdentity)`, `void Prune(IReadOnlyCollection<ProcessIdentity> alive)`

- [ ] **Step 1: Write the failing tests**

Append inside the `OrphanScanTests` class, before the closing brace:

```csharp
        // ------------------------------------------------------- the ledger

        private static readonly ProcessIdentity Orphan = new ProcessIdentity(50192, 133_000_000_000_000_000L);

        [Fact]
        public void The_same_verdict_for_the_same_process_is_logged_once()
        {
            // A legitimately long search would otherwise write sixty identical lines an hour.
            var ledger = new OrphanLedger();

            Assert.True(ledger.ShouldLog(Orphan, "bounded by -maxdepth"));
            Assert.False(ledger.ShouldLog(Orphan, "bounded by -maxdepth"));
        }

        [Fact]
        public void A_changed_verdict_for_the_same_process_is_logged_again()
        {
            var ledger = new OrphanLedger();
            ledger.ShouldLog(Orphan, "42s CPU is under the 120s threshold");

            Assert.True(ledger.ShouldLog(Orphan, "parent 33960 has exited"));
        }

        [Fact]
        public void A_recycled_pid_is_a_different_identity_and_is_logged_again()
        {
            var ledger = new OrphanLedger();
            ledger.ShouldLog(Orphan, "parent 33960 has exited");

            var newcomer = new ProcessIdentity(Orphan.Pid, Orphan.CreateTime + 1);
            Assert.True(ledger.ShouldLog(newcomer, "parent 33960 has exited"));
        }

        [Fact]
        public void A_process_is_alerted_on_once()
        {
            var ledger = new OrphanLedger();

            Assert.True(ledger.ShouldAlert(Orphan));
            ledger.MarkAlerted(Orphan);
            Assert.False(ledger.ShouldAlert(Orphan));
        }

        [Fact]
        public void A_process_that_survived_both_kill_attempts_is_never_alerted_on_again()
        {
            // Nagging about a process that cannot be ended is noise the user can do nothing
            // about.
            var ledger = new OrphanLedger();
            ledger.MarkUnkillable(Orphan);

            Assert.True(ledger.IsUnkillable(Orphan));
            Assert.False(ledger.ShouldAlert(Orphan));
        }

        [Fact]
        public void Pruning_forgets_processes_that_are_gone()
        {
            var ledger = new OrphanLedger();
            ledger.MarkAlerted(Orphan);
            ledger.ShouldLog(Orphan, "parent 33960 has exited");

            ledger.Prune(Array.Empty<ProcessIdentity>());

            // Forgotten entirely, so the bookkeeping cannot grow without bound across days of
            // uptime.
            Assert.True(ledger.ShouldAlert(Orphan));
            Assert.True(ledger.ShouldLog(Orphan, "parent 33960 has exited"));
        }

        [Fact]
        public void Pruning_keeps_processes_that_are_still_alive()
        {
            var ledger = new OrphanLedger();
            ledger.MarkAlerted(Orphan);

            ledger.Prune(new[] { Orphan });

            Assert.False(ledger.ShouldAlert(Orphan));
        }

        [Fact]
        public void An_unkillable_process_is_forgotten_only_once_it_is_actually_gone()
        {
            var ledger = new OrphanLedger();
            ledger.MarkUnkillable(Orphan);

            ledger.Prune(new[] { Orphan });
            Assert.True(ledger.IsUnkillable(Orphan));

            ledger.Prune(Array.Empty<ProcessIdentity>());
            Assert.False(ledger.IsUnkillable(Orphan));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: FAIL — `error CS0246: The type or namespace name 'ProcessIdentity' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `Services/Watchdog/OrphanLedger.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// A specific process, not a slot.
    ///
    /// <para>
    /// PID plus creation time, because a PID alone is reused. Every piece of the watchdog's
    /// bookkeeping is keyed on this: alerting, logging, and the record of what could not be
    /// killed. Keyed on PID alone, a recycled number would inherit the previous tenant's
    /// history and be silently exempted from a kill it deserves.
    /// </para>
    /// </summary>
    public readonly record struct ProcessIdentity(int Pid, long CreateTime);

    /// <summary>
    /// What the watchdog has already said and already tried.
    ///
    /// <para>
    /// Pure state with no process API, so the two guarantees that matter — never nag, and never
    /// loop forever on one PID — are tested rather than hoped for.
    /// </para>
    /// </summary>
    public sealed class OrphanLedger
    {
        private readonly Dictionary<ProcessIdentity, string> _lastLogged = new();
        private readonly HashSet<ProcessIdentity> _alerted = new();
        private readonly HashSet<ProcessIdentity> _unkillable = new();

        /// <summary>
        /// Whether this verdict is worth a log line, recording it as said when it is.
        ///
        /// <para>
        /// A verdict that has not changed is not news. Without this a legitimately long search
        /// writes the same line sixty times an hour and buries the one line somebody needs.
        /// </para>
        /// </summary>
        public bool ShouldLog(ProcessIdentity identity, string reason)
        {
            if (_lastLogged.TryGetValue(identity, out string? previous) &&
                string.Equals(previous, reason, StringComparison.Ordinal))
                return false;

            _lastLogged[identity] = reason;
            return true;
        }

        /// <summary>
        /// Whether to raise a notice. False once this process has been reported, and false
        /// forever for one that survived both kill attempts — nagging about a process the user
        /// cannot end is noise.
        /// </summary>
        public bool ShouldAlert(ProcessIdentity identity) =>
            !_alerted.Contains(identity) && !_unkillable.Contains(identity);

        /// <summary>Records that the user has been told about this process.</summary>
        public void MarkAlerted(ProcessIdentity identity) => _alerted.Add(identity);

        /// <summary>
        /// Records that this process survived a terminate and a tree kill. It is never tried
        /// again and never reported again.
        /// </summary>
        public void MarkUnkillable(ProcessIdentity identity) => _unkillable.Add(identity);

        /// <summary>Whether this process has already been given up on.</summary>
        public bool IsUnkillable(ProcessIdentity identity) => _unkillable.Contains(identity);

        /// <summary>
        /// Drops bookkeeping for processes no longer present.
        ///
        /// <para>
        /// MicaStats runs for weeks. Without this the three collections accumulate an entry per
        /// search anyone has ever run.
        /// </para>
        /// </summary>
        public void Prune(IReadOnlyCollection<ProcessIdentity> alive)
        {
            if (alive == null) return;
            var live = alive as HashSet<ProcessIdentity> ?? new HashSet<ProcessIdentity>(alive);

            Remove(_lastLogged.Keys, live, key => _lastLogged.Remove(key));
            Remove(_alerted, live, key => _alerted.Remove(key));
            Remove(_unkillable, live, key => _unkillable.Remove(key));
        }

        /// <summary>
        /// Drops every key not in <paramref name="live"/>. The doomed keys are collected first
        /// rather than removed during iteration, which would invalidate the enumerator.
        /// </summary>
        private static void Remove(
            IEnumerable<ProcessIdentity> keys,
            HashSet<ProcessIdentity> live,
            Func<ProcessIdentity, bool> remove)
        {
            List<ProcessIdentity>? gone = null;
            foreach (var key in keys)
            {
                if (!live.Contains(key)) (gone ??= new List<ProcessIdentity>()).Add(key);
            }
            if (gone == null) return;
            foreach (var key in gone) remove(key);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: PASS, 38 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Watchdog/OrphanLedger.cs tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs
git commit -F - <<'MSG'
feat(watchdog): remember what has been said and tried

Keyed on pid plus creation time, so a recycled pid cannot inherit the
exemption of the previous tenant. Logs a verdict only when it changes,
alerts once per process, and never reports one that survived both kill
attempts.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 7: The watchdog shell

The impure part: timer, record collection, logging, kill escalation. Not unit tested — it is all clock, syscalls and I/O. Verified by compiling and by the end-to-end check in Task 10.

**Files:**
- Create: `Services/Watchdog/OrphanWatchdog.cs`

**Interfaces:**
- Consumes: `ProcessSampler.SnapshotOnce`, `ProcessSampler.RawProcess` (Task 4); `ProcessDetails.TryRead` (Task 5); `OrphanScan.Decide`, `OrphanScanOptions`, `ProcessRecord`, `OrphanVerdict` (Task 2); `ParentState.Resolve`, `ParentState.SnapshotEntry` (Task 3); `OrphanLedger`, `ProcessIdentity` (Task 6); `ProcessControl.TryEndTask`, `EndTaskResult`, `DiagnosticsLog`.
- Produces:
  - `sealed record OrphanFinding(ProcessIdentity Identity, string Name, string CommandLine, double CpuSeconds, string Reason)`
  - `sealed class OrphanWatchdog : IDisposable`
  - `OrphanScanOptions OrphanWatchdog.Options { get; set; }`
  - `bool OrphanWatchdog.Enabled { get; set; }`
  - `event Action<IReadOnlyList<OrphanFinding>>? OrphanWatchdog.Found`
  - `string OrphanWatchdog.EndAll(IReadOnlyList<OrphanFinding> findings)`

- [ ] **Step 1: Write the implementation**

Create `Services/Watchdog/OrphanWatchdog.cs`:

```csharp
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
                if (EndOne(finding)) ended++; else survived++;
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
```

- [ ] **Step 2: Build to verify it compiles**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`

Expected: PASS, all tests.

- [ ] **Step 4: Commit**

```bash
git add Services/Watchdog/OrphanWatchdog.cs
git commit -F - <<'MSG'
feat(watchdog): scan, log, and end with a verified escalating kill

Terminate, wait, re-read, escalate to taskkill /F /T, re-read, give
up. On the machine that prompted this, a forced kill reported success
while the process kept accumulating CPU because it was wedged in
kernel I/O, so a kill that is assumed rather than verified changes
nothing.

Two attempts and then the identity is recorded and left alone. A
process Windows will not release is not going to yield to a third try,
and retrying every minute forever is its own runaway.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 8: The toast, on a shared corner stack

`AlertToastWindow` owns a private `Open` list and its own `RestackAll`, both of which lay cards
up from the bottom-right corner. A second toast class with its own copy of that code would place
its cards in the same corner from a list the first one cannot see, so an alert card and an orphan
card open together would draw on top of each other — and on a struggling machine both fire at
once. The registry is extracted first, then both classes use it.

**Files:**
- Create: `Helpers/ToastStack.cs`
- Modify: `AlertToastWindow.cs`
- Create: `OrphanToastWindow.cs`
- Modify: `Kil0bitSystemMonitor.csproj`
- Modify: `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`

**Interfaces:**
- Consumes: `OrphanFinding` (Task 7); `Helpers/ToastButton.Create`, `Helpers/UiGlyphs`.
- Produces:
  - `static void ToastStack.Add(Window toast, int maxOfSameType)`
  - `static void ToastStack.Remove(Window toast)`
  - `static void ToastStack.Restack()`
  - `static void ToastStack.PlayEntrance(Window toast)`
  - `static void ToastStack.CloseAll<T>() where T : Window`
  - `static OrphanToastWindow OrphanToastWindow.ShowFor(IReadOnlyList<OrphanFinding> findings, Action<IReadOnlyList<OrphanFinding>> onEndAll)`
  - `static void OrphanToastWindow.CloseAll()`
  - `internal static string OrphanToastWindow.Headline(IReadOnlyList<OrphanFinding> findings)`

- [ ] **Step 1: Write the shared stack**

Create `Helpers/ToastStack.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// The bottom-right corner, and who is currently in it.
    ///
    /// <para>
    /// One registry for every kind of notice, because the corner is one place. Each toast class
    /// keeping its own list would have each class laying cards out from a list that cannot see
    /// the others, and two cards would occupy the same rectangle — which is exactly what
    /// happens on a struggling machine, where an alert and a runaway-search notice fire
    /// together.
    /// </para>
    ///
    /// <para>
    /// Trimming is per type: a burst of alerts must not evict a notice of a different kind that
    /// the user has not answered yet.
    /// </para>
    /// </summary>
    public static class ToastStack
    {
        private const double CardMargin = 18;
        private const double CardGap = 8;

        /// <summary>Every notice currently on screen, newest last.</summary>
        private static readonly List<Window> Open = new();

        /// <summary>
        /// Registers a notice and drops the oldest of its own type once there are more than
        /// <paramref name="maxOfSameType"/> of them.
        ///
        /// <para>
        /// The oldest goes rather than the newest: the most recent problem is the one the user
        /// has not seen yet.
        /// </para>
        /// </summary>
        public static void Add(Window toast, int maxOfSameType)
        {
            if (toast == null) return;

            Type kind = toast.GetType();
            while (CountOf(kind) >= maxOfSameType)
            {
                Window? oldest = OldestOf(kind);
                if (oldest == null) break;

                Open.Remove(oldest);
                try { oldest.Close(); } catch { }
            }

            Open.Add(toast);
        }

        /// <summary>Drops a notice that has closed, and closes the gap it left.</summary>
        public static void Remove(Window toast)
        {
            if (toast != null && Open.Remove(toast)) Restack();
        }

        /// <summary>
        /// Lays the open notices up from the bottom-right corner. Re-run whenever one appears or
        /// closes, so a gap never opens in the middle of the stack.
        /// </summary>
        public static void Restack()
        {
            var work = SystemParameters.WorkArea;
            double bottom = work.Bottom - CardMargin;

            for (int i = Open.Count - 1; i >= 0; i--)
            {
                Window toast = Open[i];
                if (!toast.IsLoaded) continue;

                toast.Left = work.Right - toast.ActualWidth - CardMargin;
                toast.Top = bottom - toast.ActualHeight;
                bottom -= toast.ActualHeight + CardGap;
            }
        }

        /// <summary>Fades and lifts a card into place.</summary>
        public static void PlayEntrance(Window toast)
        {
            if (toast == null) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            toast.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            toast.BeginAnimation(Window.TopProperty,
                new DoubleAnimation(toast.Top + 16, toast.Top, TimeSpan.FromMilliseconds(260))
                { EasingFunction = ease });
        }

        /// <summary>
        /// Closes every notice of one kind, leaving the others alone — switching off alerts must
        /// not silently take away an unanswered notice about something else.
        /// </summary>
        public static void CloseAll<T>() where T : Window
        {
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                if (Open[i] is not T toast) continue;

                Open.RemoveAt(i);
                try { toast.Close(); } catch { }
            }
            Restack();
        }

        private static int CountOf(Type kind)
        {
            int n = 0;
            foreach (Window toast in Open) if (toast.GetType() == kind) n++;
            return n;
        }

        private static Window? OldestOf(Type kind)
        {
            foreach (Window toast in Open) if (toast.GetType() == kind) return toast;
            return null;
        }
    }
}
```

- [ ] **Step 2: Move `AlertToastWindow` onto it**

In `AlertToastWindow.cs`, delete these members entirely:

- the `Open` field and its doc comment
- the `CardMargin` and `CardGap` constants
- the whole `RestackAll` method and its doc comment
- the whole `PlayEntrance` method

Then find:

```csharp
            Loaded += (s, e) => { RestackAll(); PlayEntrance(); };
```

Replace with:

```csharp
            Loaded += (s, e) => { ToastStack.Restack(); ToastStack.PlayEntrance(this); };
```

Find:

```csharp
            Closed += (s, e) => { _dismiss.Stop(); Open.Remove(this); RestackAll(); };
```

Replace with:

```csharp
            Closed += (s, e) => { _dismiss.Stop(); ToastStack.Remove(this); };
```

Find the whole body of `ShowFor`:

```csharp
            // Drop the oldest rather than the newest: the most recent problem is the one the
            // user has not seen yet.
            while (Open.Count >= MaxOnScreen)
            {
                var oldest = Open[0];
                try { oldest.Close(); } catch { }
                Open.Remove(oldest);
            }

            var toast = new AlertToastWindow(alert);
            toast.OpenRequested += onOpen;
            Open.Add(toast);
            toast.Show();
            return toast;
```

Replace with:

```csharp
            var toast = new AlertToastWindow(alert);
            toast.OpenRequested += onOpen;
            ToastStack.Add(toast, MaxOnScreen);
            toast.Show();
            return toast;
```

Find the whole body of `CloseAll`:

```csharp
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                try { Open[i].Close(); } catch { }
            }
            Open.Clear();
```

Replace with:

```csharp
            ToastStack.CloseAll<AlertToastWindow>();
```

`AlertToastWindow.cs` already has `using Kil0bitSystemMonitor.Helpers;`. Keep `MaxOnScreen` where
it is — it is this card's own policy, not the corner's.

- [ ] **Step 3: Build to verify the migration compiles**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`

Expected: Build succeeded, 0 errors. If `System.Collections.Generic` or
`System.Windows.Media.Animation` is now unused in `AlertToastWindow.cs`, leave the directives —
removing them is churn outside this task.

- [ ] **Step 4: Write the orphan card**

Create `OrphanToastWindow.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Watchdog;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The notice that something is burning a core for nobody.
    ///
    /// <para>
    /// Shares the corner with <see cref="AlertToastWindow"/> through
    /// <see cref="ToastStack"/>, so the two kinds of card stack together rather than on top of
    /// each other. What is its own is the one thing a card is for: what it says, and what its
    /// button does.
    /// </para>
    ///
    /// <para>
    /// The button ends processes, so it waits for a click and the card never takes focus. An
    /// irreversible action on a card that stole focus mid-keystroke would be pressed by
    /// accident.
    /// </para>
    /// </summary>
    public sealed class OrphanToastWindow : Window
    {
        /// <summary>One card at a time: every finding from a scan is reported on it.</summary>
        private const int MaxOnScreen = 1;

        /// <summary>Amber, matching the alert card: this is a warning, not information.</summary>
        private static readonly Color Amber = Color.FromRgb(0xE8, 0xA5, 0x3C);

        private readonly DispatcherTimer _dismiss;
        private readonly IReadOnlyList<OrphanFinding> _findings;

        private OrphanToastWindow(IReadOnlyList<OrphanFinding> findings,
                                  Action<IReadOnlyList<OrphanFinding>> onEndAll)
        {
            _findings = findings;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;           // must never steal focus: its button is destructive
            Title = "MicaStats runaway search";

            Content = BuildCard(onEndAll);

            Loaded += (s, e) => { ToastStack.Restack(); ToastStack.PlayEntrance(this); };

            // Longer than the alert card. This one asks for a decision, and the machine it
            // appears on is by definition busy.
            _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _dismiss.Tick += (s, e) => Close();
            _dismiss.Start();

            MouseEnter += (s, e) => _dismiss.Stop();
            MouseLeave += (s, e) => _dismiss.Start();
            Closed += (s, e) => { _dismiss.Stop(); ToastStack.Remove(this); };
        }

        /// <summary>Shows one card for a set of findings.</summary>
        public static OrphanToastWindow ShowFor(
            IReadOnlyList<OrphanFinding> findings, Action<IReadOnlyList<OrphanFinding>> onEndAll)
        {
            var toast = new OrphanToastWindow(findings, onEndAll);
            ToastStack.Add(toast, MaxOnScreen);
            toast.Show();
            return toast;
        }

        /// <summary>Closes every runaway-search notice, e.g. when the watchdog is switched off.</summary>
        public static void CloseAll() => ToastStack.CloseAll<OrphanToastWindow>();

        /// <summary>
        /// The headline: what it is, how many, and how much it has cost. The command lines are
        /// in the log — a card is read at a glance, and a full find expression is not.
        /// </summary>
        internal static string Headline(IReadOnlyList<OrphanFinding> findings)
        {
            double totalCpu = 0;
            foreach (var finding in findings) totalCpu += finding.CpuSeconds;

            string what = findings.Count == 1
                ? "An orphaned " + findings[0].Name
                : findings.Count.ToString(CultureInfo.InvariantCulture) + " orphaned searches";

            string cost = totalCpu >= 120
                ? (totalCpu / 60d).ToString("0", CultureInfo.InvariantCulture) + " minutes"
                : totalCpu.ToString("0", CultureInfo.InvariantCulture) + " seconds";

            return what + ", " + cost + " of CPU burned";
        }

        private UIElement BuildCard(Action<IReadOnlyList<OrphanFinding>> onEndAll)
        {
            var stack = new StackPanel { Margin = new Thickness(16, 13, 16, 13) };

            var heading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4),
            };
            heading.Children.Add(new TextBlock
            {
                Text = UiGlyphs.Alert,
                FontFamily = new FontFamily(UiGlyphs.FontStack),
                FontSize = 11,
                Foreground = new SolidColorBrush(Amber),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Runaway search",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
                Foreground = new SolidColorBrush(Amber),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(heading);

            stack.Children.Add(new TextBlock
            {
                Text = Headline(_findings),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xFF, 0xFF)),
            });

            stack.Children.Add(new TextBlock
            {
                Text = _findings[0].Reason
                       + ". Scanning the whole drive with nothing reading the output. "
                       + "The command lines are in the MicaStats log.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xE9, 0xED, 0xF2)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                Margin = new Thickness(0, 3, 0, 10),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ToastButton.Create("End them", Amber, primary: true,
                Color.FromRgb(0x2A, 0x18, 0x06), () =>
                {
                    onEndAll(_findings);
                    Close();
                }));
            buttons.Children.Add(ToastButton.Create("Dismiss", Amber, primary: false,
                Color.FromRgb(0x2A, 0x18, 0x06), Close));
            stack.Children.Add(buttons);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x1C, 0x16, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x77, Amber.R, Amber.G, Amber.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = stack,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.5,
                    Color = Colors.Black,
                },
            };
        }
    }
}
```

- [ ] **Step 5: Let the test project see `Headline`**

`Headline` is `internal` because it is a rendering detail, not API — but the wording is worth
testing. In `Kil0bitSystemMonitor.csproj`, find:

```xml
    <ItemGroup>
        <None Include="icon.ico">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>
```

Add a new item group immediately after it:

```xml

    <ItemGroup>
        <InternalsVisibleTo Include="Kil0bitSystemMonitor.Tests" />
    </ItemGroup>
```

- [ ] **Step 6: Write the headline tests**

Add this using directive to the top of `tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs`,
after the existing ones:

```csharp
using Kil0bitSystemMonitor;
```

Append inside the `OrphanScanTests` class, before the closing brace:

```csharp
        // ------------------------------------------------------- the notice

        [Fact]
        public void One_finding_reads_as_a_single_process_with_its_cost_in_minutes()
        {
            var findings = new[]
            {
                new OrphanFinding(Orphan, "find.exe", "find / -name x", 2258, "parent 33960 has exited"),
            };

            Assert.Equal("An orphaned find.exe, 38 minutes of CPU burned",
                OrphanToastWindow.Headline(findings));
        }

        [Fact]
        public void Two_findings_read_as_a_count_and_a_combined_cost()
        {
            var findings = new[]
            {
                new OrphanFinding(Orphan, "find.exe", "find / -name x", 2258, "parent 33960 has exited"),
                new OrphanFinding(new ProcessIdentity(12264, 1), "find.exe", "find / -iname y", 2115,
                    "parent 21452 has exited"),
            };

            Assert.Equal("2 orphaned searches, 73 minutes of CPU burned",
                OrphanToastWindow.Headline(findings));
        }

        [Fact]
        public void A_short_burn_is_reported_in_seconds_rather_than_zero_minutes()
        {
            var findings = new[]
            {
                new OrphanFinding(Orphan, "find.exe", "find / -name x", 95, "parent 33960 has exited"),
            };

            Assert.Equal("An orphaned find.exe, 95 seconds of CPU burned",
                OrphanToastWindow.Headline(findings));
        }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~OrphanScanTests"`

Expected: PASS, 41 tests.

- [ ] **Step 8: Run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`

Expected: PASS, all tests — `AlertToastWindow` was modified, so the pre-existing suite has to
stay green.

- [ ] **Step 9: Commit**

```bash
git add Helpers/ToastStack.cs AlertToastWindow.cs OrphanToastWindow.cs Kil0bitSystemMonitor.csproj tests/Kil0bitSystemMonitor.Tests/OrphanScanTests.cs
git commit -F - <<'MSG'
feat(watchdog): the corner card with End them, on a shared stack

The corner is one place, so it gets one registry. A second toast class
with its own list would lay cards out over the alert cards it cannot
see, and on a struggling machine both kinds fire together.

The new card never takes focus: its button ends processes, and a card
that stole focus mid-keystroke would be pressed by accident.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 9: Configuration, the Settings toggle, and startup wiring

**Files:**
- Modify: `Models/SystemMetrics.cs`
- Modify: `Services/Watchdog/OrphanScanOptions.cs`
- Modify: `SettingsWindow.xaml`
- Modify: `SettingsWindow.xaml.cs`
- Modify: `App.xaml.cs`

**Interfaces:**
- Consumes: `OrphanWatchdog` (Task 7), `OrphanToastWindow.ShowFor` and `.CloseAll` (Task 8), `OrphanScanOptions` (Task 2).
- Produces:
  - `AppConfig.WatchOrphanedSearches`, `.OrphanCpuSecondsThreshold`, `.OrphanGraceMinutes`, `.OrphanBinaryAllowlist`, `.OrphanExpectedParents`
  - `static OrphanScanOptions OrphanScanOptions.FromConfig(AppConfig config)`
  - `static OrphanWatchdog? App.Watchdog { get; }`

- [ ] **Step 1: Add the config properties**

In `Models/SystemMetrics.cs`, find the diagnostics backing fields:

```csharp
        private bool _alertsEnabled = true;
        private string _alertRules = "";
```

Add immediately after `_alertRules`:

```csharp

        // Orphan search watchdog. Thresholds are deliberately conservative: a missed orphan
        // costs a core until the next scan, a false positive destroys work somebody intended.
        private bool _watchOrphanedSearches = true;
        private int _orphanCpuSecondsThreshold = 120;
        private int _orphanGraceMinutes = 5;
        private string[] _orphanBinaryAllowlist = new[] { @"\Git\usr\bin\find.exe" };
        private string[] _orphanExpectedParents = new[] { "bash.exe", "sh.exe", "pwsh.exe", "cmd.exe" };
```

Then find this property:

```csharp
        public string AlertRules { get => _alertRules; set { Set(ref _alertRules, value); } }
```

Add immediately after it:

```csharp

        /// <summary>Whether to watch for whole-drive searches left running with no parent.</summary>
        public bool WatchOrphanedSearches
        {
            get => _watchOrphanedSearches;
            set { Set(ref _watchOrphanedSearches, value); }
        }

        /// <summary>Rule 3. Clamped so a hand-edited config cannot disable the guard entirely.</summary>
        public int OrphanCpuSecondsThreshold
        {
            get => _orphanCpuSecondsThreshold;
            set { Set(ref _orphanCpuSecondsThreshold, Math.Clamp(value, 10, 86_400)); }
        }

        /// <summary>Rule 4, in minutes. Clamped for the same reason.</summary>
        public int OrphanGraceMinutes
        {
            get => _orphanGraceMinutes;
            set { Set(ref _orphanGraceMinutes, Math.Clamp(value, 1, 1440)); }
        }

        /// <summary>
        /// Rule 1. Image-path suffixes, not names: <c>C:\Windows\System32\find.exe</c> is an
        /// unrelated Microsoft tool that shares the file name and must never match.
        /// </summary>
        public string[] OrphanBinaryAllowlist
        {
            get => _orphanBinaryAllowlist;
            set { Set(ref _orphanBinaryAllowlist, value ?? Array.Empty<string>()); }
        }

        /// <summary>Rule 5. Parent images that mean a human is still receiving the output.</summary>
        public string[] OrphanExpectedParents
        {
            get => _orphanExpectedParents;
            set { Set(ref _orphanExpectedParents, value ?? Array.Empty<string>()); }
        }
```

- [ ] **Step 2: Project the config onto the scan options**

In `Services/Watchdog/OrphanScanOptions.cs`, add inside the class, immediately after the `Defaults` property:

```csharp

        /// <summary>
        /// The user's settings, with an empty list falling back to the shipped default rather
        /// than to nothing.
        ///
        /// <para>
        /// An empty binary allowlist would silently switch the feature off; an empty parent list
        /// would silently make every live parent unrecognised and put deliberate searches in
        /// range. Neither is a thing anyone means by clearing a list in a config file.
        /// </para>
        /// </summary>
        public static OrphanScanOptions FromConfig(Kil0bitSystemMonitor.Models.AppConfig config)
        {
            if (config == null) return Defaults;

            return new OrphanScanOptions
            {
                CpuSecondsThreshold = config.OrphanCpuSecondsThreshold,
                Grace = TimeSpan.FromMinutes(config.OrphanGraceMinutes),
                BinarySuffixes = config.OrphanBinaryAllowlist is { Length: > 0 } binaries
                    ? binaries
                    : Defaults.BinarySuffixes,
                ExpectedParents = config.OrphanExpectedParents is { Length: > 0 } parents
                    ? parents
                    : Defaults.ExpectedParents,
            };
        }
```

- [ ] **Step 3: Add the Settings toggle**

In `SettingsWindow.xaml`, find the alerts card — the `Border` containing `<ui:ToggleSwitch x:Name="AlertsToggle" .../>` and `<TextBlock x:Name="AlertRulesText" .../>`. Insert this new `Border` immediately after that card's closing `</Border>`, before the battery card that contains `ShowBatteryToggle`:

```xml
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE71C;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Watch for runaway searches" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="A coding tool that exits without cleaning up can leave a whole-drive file search running at full speed with nothing reading its output. MicaStats notices, and offers to end it. It never ends anything on its own." Opacity="0.65" FontSize="12" TextWrapping="Wrap" Margin="0,2,0,0"/>
                                </StackPanel>
                                <ui:ToggleSwitch x:Name="WatchOrphansToggle" Grid.Column="2" Toggled="OnDiagnosticsToggle" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
```

- [ ] **Step 4: Load and save the toggle**

In `SettingsWindow.xaml.cs`, in `LoadDiagnosticsSettings`, find:

```csharp
                ShowBatteryToggle.IsOn = config.ShowBattery;
```

Add immediately after it:

```csharp
                WatchOrphansToggle.IsOn = config.WatchOrphanedSearches;
```

In the same file, in `OnDiagnosticsToggle`, find:

```csharp
            config.ShowBattery = ShowBatteryToggle.IsOn;
```

Add immediately after it:

```csharp
            config.WatchOrphanedSearches = WatchOrphansToggle.IsOn;
```

- [ ] **Step 5: Declare the property**

In `App.xaml.cs`, find:

```csharp
        public static Kil0bitSystemMonitor.Services.ProcessSampler SharedProcessSampler { get; } = new();
```

Add immediately after it:

```csharp

        /// <summary>The runaway-search watchdog, or null before diagnostics have started.</summary>
        public static Kil0bitSystemMonitor.Services.Watchdog.OrphanWatchdog? Watchdog { get; private set; }
```

- [ ] **Step 6: Construct and wire it**

In `App.xaml.cs`, inside `StartDiagnostics`, find:

```csharp
                s_alerts = new Kil0bitSystemMonitor.Services.Diagnostics.AlertMonitor(m_history!, Battery);
                s_alerts.Raised += alert =>
                    AlertToastWindow.ShowFor(alert, () => DiagnosticsWindow.ShowDiagnostics(3));
```

Add immediately after those lines:

```csharp

                // The watchdog reports; it never ends anything by itself. The click that does
                // is on the card. Found is raised on a timer thread, so the card is built on
                // the dispatcher.
                Watchdog = new Kil0bitSystemMonitor.Services.Watchdog.OrphanWatchdog();
                Watchdog.Found += findings =>
                    Dispatcher.BeginInvoke(new Action(() =>
                        OrphanToastWindow.ShowFor(findings, toEnd =>
                        {
                            string outcome = Watchdog?.EndAll(toEnd) ?? "";
                            Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("watchdog", outcome);
                        })));
```

In the same method, find the settings subscription:

```csharp
                    if (e.PropertyName.StartsWith("Slowdown", StringComparison.Ordinal) ||
                        e.PropertyName.StartsWith("Alert", StringComparison.Ordinal))
```

Replace those two lines with:

```csharp
                    if (e.PropertyName.StartsWith("Slowdown", StringComparison.Ordinal) ||
                        e.PropertyName.StartsWith("Alert", StringComparison.Ordinal) ||
                        e.PropertyName.StartsWith("Orphan", StringComparison.Ordinal) ||
                        e.PropertyName == nameof(Kil0bitSystemMonitor.Models.AppConfig.WatchOrphanedSearches))
```

- [ ] **Step 7: Apply the settings**

In `App.xaml.cs`, in `ApplyDiagnosticsSettings`, find the end of the alerts block:

```csharp
                    if (config.AlertsEnabled) s_alerts.Start();
                    else { s_alerts.Stop(); AlertToastWindow.CloseAll(); }
                }
```

Add immediately after that closing brace:

```csharp

                if (Watchdog != null)
                {
                    Watchdog.Options =
                        Kil0bitSystemMonitor.Services.Watchdog.OrphanScanOptions.FromConfig(config);

                    Watchdog.Enabled = config.WatchOrphanedSearches;
                    if (!config.WatchOrphanedSearches) OrphanToastWindow.CloseAll();
                }
```

- [ ] **Step 8: Dispose it**

In `App.xaml.cs`, in `OnExit`, find:

```csharp
                s_alerts?.Dispose();
```

Add immediately before it:

```csharp
                Watchdog?.Dispose();
```

- [ ] **Step 9: Build and run the whole suite**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`

Expected: Build succeeded, 0 errors.

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`

Expected: PASS, all tests.

- [ ] **Step 10: Verify the config round-trips**

Start the app, leave it for twenty seconds, then look at the config:

```bash
"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" run --project Kil0bitSystemMonitor.csproj &
sleep 20
grep -i orphan "$APPDATA/MicaStats/config.json"
grep -i WatchOrphanedSearches "$APPDATA/MicaStats/config.json"
```

Expected: five keys present — `"WatchOrphanedSearches": true`, `"OrphanCpuSecondsThreshold": 120`, `"OrphanGraceMinutes": 5`, `"OrphanBinaryAllowlist": ["\\Git\\usr\\bin\\find.exe"]`, `"OrphanExpectedParents": ["bash.exe","sh.exe","pwsh.exe","cmd.exe"]`.

Close the app from its tray icon before continuing.

- [ ] **Step 11: Commit**

```bash
git add Models/SystemMetrics.cs Services/Watchdog/OrphanScanOptions.cs SettingsWindow.xaml SettingsWindow.xaml.cs App.xaml.cs
git commit -F - <<'MSG'
feat(watchdog): settings, thresholds and startup wiring

One toggle in Settings; thresholds and both allowlists live in
config.json so they can be tuned without touching code. An emptied
list falls back to the shipped default rather than to nothing:
clearing the binaries would silently switch the feature off, and
clearing the parents would put deliberate searches in range.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

### Task 10: Documentation and end-to-end verification

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: no code.

- [ ] **Step 1: Add the feature entry**

In `README.md`, find the Processes bullet in the "Real-time monitoring" list:

```markdown
* **Processes** — A searchable, sortable list of every running process with live CPU, memory and disk figures, and an End task that says what actually happened
```

Add immediately after it:

```markdown
* **Runaway searches** — Notices when a whole-drive file search has been left running with no parent to receive its output, and offers to end it. Never ends anything on its own
```

- [ ] **Step 2: Add the section**

In `README.md`, find the section that documents the process list (search for a heading containing `Processes`) and add this section immediately after it:

```markdown
### Runaway search watchdog

Agentic coding tools run shell commands through Git Bash. When one of them starts a
whole-filesystem search and the launching shell is then killed — a cancelled background task, a
subagent that finished — the `find` child is not reaped. It keeps scanning at full speed with
nowhere to send its output. Under Git Bash `/` is the whole drive and the walk includes `/proc`,
every mount and any dead network path, so it can run effectively forever. Two such processes were
found on one machine having consumed 2258 and 2115 seconds of CPU between them.

Once a minute MicaStats looks for processes where **all** of these hold, and flags nothing that
misses any one of them:

1. The full image path is on the allowlist — by default anything ending `\Git\usr\bin\find.exe`.
   `C:\Windows\System32\find.exe` is a different Microsoft tool that shares the name, and the
   match is on path rather than name so it can never be touched.
2. The scan is rooted at a whole filesystem (`/`, `C:\`, `/c/`) with no `-maxdepth`.
3. It has burned more than 120 seconds of CPU.
4. It is more than 5 minutes old.
5. Its parent has exited, or the parent is not a shell it recognises.

Rules 3 and 4 exist so a search you started on purpose is never killed mid-flight. Rule 5 is the
real signal: an orphan has nothing receiving its output, so it can only waste the processor.

When one is found you get a quiet corner card naming it and what it has cost. **Nothing is ended
until you click End them.** The kill is then verified rather than assumed — a process wedged in
kernel I/O reports a successful termination and keeps running — and escalated once to
`taskkill /F /T` if the first attempt did not take. A process that survives both is logged and
left alone, never retried in a loop.

Every decision, including the ones that keep a process, is written to
`%APPDATA%\MicaStats\logs\micastats.log` with the full command line and the parent's image path.

Thresholds and both lists live in `%APPDATA%\MicaStats\config.json` as `OrphanCpuSecondsThreshold`,
`OrphanGraceMinutes`, `OrphanBinaryAllowlist` and `OrphanExpectedParents`. The feature itself is
switched on and off from Settings → Diagnostics.

> [!NOTE]
> This treats a symptom. The cause is a tool that does not reap its children, and the fix belongs
> in that tool. The parent image path in the log is there so you can identify which tool is
> leaking and report it upstream; the watchdog only stops the burning in the meantime.
```

- [ ] **Step 3: Run the whole suite one final time**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests`

Expected: PASS, all tests, no skips.

- [ ] **Step 4: Verify end to end against a real orphan**

This proves the whole path: a genuine unbounded search, orphaned, detected, reported and ended.
Only run it if Git Bash is installed at the default location.

First lower the thresholds so the check does not take ten minutes. With MicaStats **closed**,
edit `%APPDATA%\MicaStats\config.json`:

```json
  "OrphanCpuSecondsThreshold": 5,
  "OrphanGraceMinutes": 1,
```

Start MicaStats, then create the orphan — `setsid` detaches it so the launching bash exits and
leaves it parentless, which is the exact shape of the reported bug:

```bash
"C:/Program Files/Git/bin/bash.exe" -c 'setsid "C:/Program Files/Git/usr/bin/find.exe" / -name nothing-matches-this.pas > /dev/null 2>&1 &'
```

Wait about two minutes. Expected: a corner card reading *An orphaned find.exe, N seconds of CPU
burned*. Click **End them**.

Then confirm it is gone and the log recorded it:

```bash
tasklist | grep -i find
grep watchdog "$APPDATA/MicaStats/logs/micastats.log" | tail -5
```

Expected: no Git `find.exe` in the task list, and log lines showing the `KILL` verdict with the
full command line and the parent image path, followed by a `KILLED` outcome.

If the orphan is still running, end it by hand before continuing:

```bash
taskkill //F //IM find.exe
```

Restore the thresholds to `120` and `5` in `config.json` afterwards, with MicaStats closed.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -F - <<'MSG'
docs(watchdog): document the runaway search watchdog

States the five rules, that nothing is ended without a click, where
the thresholds live, and that this treats a symptom whose cause is a
tool not reaping its children.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Self-review

**Spec coverage.** Every section of the design document maps to a task: the five rules to Task 2, branch-specific reasons to Task 2, the recycled-PID guard to Task 3, the snapshot source to Task 4, candidate enrichment to Task 5, log dedupe and the unkillable ledger to Task 6, the 60-second timer and the verified escalating kill to Task 7, the toast to Task 8, configuration and the single Settings toggle to Task 9, the README and the upstream-cause caveat to Task 10. The 14 test cases the spec lists are distributed across Tasks 1, 2 and 3; Task 6 adds eight for the ledger and Task 8 adds three for the headline wording.

**Type consistency.** `ProcessIdentity` is `(int Pid, long CreateTime)` in Tasks 6, 7 and 8. `ProcessSampler.RawProcess.CreateTime` is a FILETIME `long` throughout and becomes a `DateTime` only where the rules need an age, via `OrphanWatchdog.FileTime`. `OrphanScanOptions.Grace` is a `TimeSpan` in the options and `int` minutes in `AppConfig`, converted once in `FromConfig`. `OrphanScan.Decide` takes `(records, options, now)` in that order at every call site. `OrphanFinding` carries `Identity`, `Name`, `CommandLine`, `CpuSeconds`, `Reason` in that order in Tasks 7 and 8.

**Known rough edge.** `AppConfig.Set<T>` compares with `EqualityComparer<T>.Default`, which is reference equality for `string[]`. Assigning an equal-but-new array therefore notifies and triggers one extra debounced config write. Both allowlists are assigned once at load and never from the UI, so this costs at most one write at startup.
