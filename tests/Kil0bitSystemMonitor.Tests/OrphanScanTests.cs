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
    }
}
