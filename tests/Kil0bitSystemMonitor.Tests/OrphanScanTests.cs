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
