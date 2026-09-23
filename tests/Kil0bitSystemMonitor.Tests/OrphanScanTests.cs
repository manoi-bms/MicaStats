using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor;
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

        [Theory]
        [InlineData("find.exe / -maxdepth 3 -name x", 3)]
        [InlineData("find.exe / -name x -maxdepth 0", 0)]
        [InlineData("find.exe / -MAXDEPTH 12", 12)]
        [InlineData("find.exe / -maxdepth 2 -maxdepth 9", 9)]   // find obeys the last one
        public void Maxdepth_reads_the_number_that_follows_it(string commandLine, int expected)
        {
            Assert.Equal(expected, SearchCommandLine.MaxDepth(commandLine));
        }

        [Theory]
        [InlineData("find.exe / -name x")]            // absent
        [InlineData("find.exe / -name x -maxdepth")]  // no argument at all
        [InlineData("find.exe / -maxdepth -name x")]  // the next token is the expression
        [InlineData("find.exe / -maxdepth -1")]       // a sign is not a depth
        [InlineData("find.exe / -maxdepth 1,000")]    // nor is a thousands separator
        [InlineData("find.exe / -maxdepth three")]
        [InlineData("find.exe / -maxdepth 9 -maxdepth x")]   // the last one is unreadable
        public void Maxdepth_is_null_when_absent_or_unreadable(string commandLine)
        {
            Assert.Null(SearchCommandLine.MaxDepth(commandLine));
        }

        [Fact]
        public void A_root_scan_without_maxdepth_is_unbounded()
        {
            Assert.True(SearchCommandLine.IsUnbounded(
                "\"" + GitFind + "\" / -iname cxEdit.pas -not -path */proc/*", 3));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void A_root_scan_with_a_shallow_maxdepth_is_bounded(int depth)
        {
            Assert.False(SearchCommandLine.IsUnbounded(
                "\"" + GitFind + "\" / -maxdepth " + depth + " -name x.pas", 3));
        }

        [Fact]
        public void A_root_scan_with_a_maxdepth_past_the_trusted_depth_is_unbounded()
        {
            // Under Git Bash / is every drive, so depth 4 from there is not a small walk.
            Assert.True(SearchCommandLine.IsUnbounded("\"" + GitFind + "\" / -maxdepth 4 -name x.pas", 3));
        }

        [Fact]
        public void A_maxdepth_with_no_number_is_not_trusted()
        {
            Assert.True(SearchCommandLine.IsUnbounded("\"" + GitFind + "\" / -name x.pas -maxdepth", 3));
        }

        [Fact]
        public void A_scan_that_starts_below_a_root_is_bounded()
        {
            Assert.False(SearchCommandLine.IsUnbounded("\"" + GitFind + "\" C:\\src -name x.pas", 3));
        }

        [Fact]
        public void A_scan_below_a_root_is_bounded_however_deep_its_maxdepth()
        {
            Assert.False(SearchCommandLine.IsUnbounded("\"" + GitFind + "\" C:\\src -maxdepth 20 -name x", 3));
        }

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

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void A_shallow_bounded_scan_is_kept_however_much_cpu_it_has_burned(int depth)
        {
            var verdict = OrphanScan.DecideOne(
                Killable(commandLine: "\"" + GitFind + "\" / -maxdepth " + depth + " -name x.pas",
                         cpuSeconds: 5000),
                OrphanScanOptions.Defaults, Now);

            Assert.False(verdict.Kill);
            Assert.Equal("bounded by -maxdepth " + depth, verdict.Reason);
        }

        [Fact]
        public void The_observed_depth_6_orphan_is_killed_rather_than_trusted_as_bounded()
        {
            // The real command from the reported machine: 5400+ s of CPU, parent gone, and kept
            // under the old rule because it said -maxdepth at all.
            var verdict = OrphanScan.DecideOne(
                Killable(commandLine: "\"" + GitFind + "\" / -name madExcept.pas -maxdepth 6",
                         cpuSeconds: 5400),
                OrphanScanOptions.Defaults, Now);

            Assert.True(verdict.Kill);
            Assert.Contains("parent 33960 has exited", verdict.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_maxdepth_with_no_number_falls_through_to_the_later_rules()
        {
            var verdict = OrphanScan.DecideOne(
                Killable(commandLine: "\"" + GitFind + "\" / -name x.pas -maxdepth"),
                OrphanScanOptions.Defaults, Now);

            Assert.True(verdict.Kill);
        }

        [Fact]
        public void The_trusted_depth_comes_from_the_options()
        {
            var verdict = OrphanScan.DecideOne(
                Killable(commandLine: "\"" + GitFind + "\" / -name madExcept.pas -maxdepth 6"),
                new OrphanScanOptions { TrustedMaxDepth = 6 }, Now);

            Assert.False(verdict.Kill);
            Assert.Equal("bounded by -maxdepth 6", verdict.Reason);
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

        [Fact]
        public void The_trusted_depth_defaults_to_3_and_is_carried_from_the_config()
        {
            Assert.Equal(3, OrphanScanOptions.Defaults.TrustedMaxDepth);
            Assert.Equal(3, new Kil0bitSystemMonitor.Models.AppConfig().OrphanTrustedMaxDepth);

            var config = new Kil0bitSystemMonitor.Models.AppConfig { OrphanTrustedMaxDepth = 5 };
            Assert.Equal(5, OrphanScanOptions.FromConfig(config).TrustedMaxDepth);
        }

        [Theory]
        [InlineData(-4, 0)]
        [InlineData(0, 0)]
        [InlineData(32, 32)]
        [InlineData(500, 32)]
        public void The_configured_trusted_depth_is_clamped(int written, int expected)
        {
            var config = new Kil0bitSystemMonitor.Models.AppConfig { OrphanTrustedMaxDepth = written };

            Assert.Equal(expected, config.OrphanTrustedMaxDepth);
        }

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
    }
}
