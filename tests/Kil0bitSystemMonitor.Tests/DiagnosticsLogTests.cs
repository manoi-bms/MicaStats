using System;
using System.IO;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// One diagnostics log file in a temporary folder, never %APPDATA%. The app and the
    /// <c>--mcp</c> bridge are separate processes, so another handle on the file is normal:
    /// it must never switch the log off for the rest of the session.
    /// </summary>
    public sealed class DiagnosticsLogFileTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "micastats-log-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        internal static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [Fact]
        public void A_line_is_written_while_another_handle_holds_the_file_open_for_writing()
        {
            var log = new DiagnosticsLogFile(_folder, DiagnosticsLog.AppFileName);
            log.Append("first\n");

            using (var other = new FileStream(log.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                log.Append("second\n");
            }

            Assert.False(log.IsDead);
            Assert.Equal("first\nsecond\n", ReadShared(log.FilePath));
        }

        [Fact]
        public void A_line_refused_by_a_sharing_violation_is_skipped_and_later_lines_still_arrive()
        {
            var log = new DiagnosticsLogFile(_folder, DiagnosticsLog.AppFileName);
            log.Append("first\n");

            // This holder admits readers only, so the next write is refused with a sharing violation.
            using (var exclusive = new FileStream(log.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                log.Append("lost\n");
            }
            log.Append("third\n");

            Assert.False(log.IsDead);
            Assert.Equal("first\nthird\n", ReadShared(log.FilePath));
        }

        [Fact]
        public void A_folder_that_cannot_be_created_switches_the_file_off_without_throwing()
        {
            Directory.CreateDirectory(_folder);
            string blocker = Path.Combine(_folder, "logs");
            File.WriteAllText(blocker, "a file where the folder should be");
            var log = new DiagnosticsLogFile(blocker, DiagnosticsLog.AppFileName);

            log.Append("one\n");
            log.Append("two\n");

            Assert.True(log.IsDead);
        }

        [Fact]
        public void A_full_file_rotates_to_its_own_dash_one_name()
        {
            Directory.CreateDirectory(_folder);
            var log = new DiagnosticsLogFile(_folder, DiagnosticsLog.BridgeFileName);
            File.WriteAllText(log.FilePath, new string('x', 512 * 1024));

            log.Append("fresh\n");

            Assert.Equal("fresh\n", ReadShared(log.FilePath));
            Assert.Equal(512 * 1024, new FileInfo(Path.Combine(_folder, "mcp-bridge-1.log")).Length);
        }
    }

    /// <summary>Tests that change where the whole process logs, so nothing else runs beside them.</summary>
    [CollectionDefinition("DiagnosticsLogGlobal", DisableParallelization = true)]
    public class DiagnosticsLogGlobalCollection
    {
    }

    /// <summary>The bridge's own log file, so micastats.log has a single writer (the app).</summary>
    [Collection("DiagnosticsLogGlobal")]
    public sealed class DiagnosticsLogFileNameTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "micastats-log-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        [Fact]
        public void The_bridge_file_name_sends_later_lines_to_their_own_file()
        {
            string savedFolder = DiagnosticsLog.LogDir;
            string savedName = Path.GetFileName(DiagnosticsLog.LogPath);
            try
            {
                DiagnosticsLog.UseFile(_folder, DiagnosticsLog.AppFileName);
                DiagnosticsLog.UseFileName(DiagnosticsLog.BridgeFileName);

                DiagnosticsLog.Log("mcp", "MCP bridge started");

                string bridgeLog = Path.Combine(_folder, "mcp-bridge.log");
                Assert.Equal(bridgeLog, DiagnosticsLog.LogPath);
                Assert.Contains("[mcp] MCP bridge started", DiagnosticsLogFileTests.ReadShared(bridgeLog), StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(_folder, "micastats.log")));
            }
            finally
            {
                DiagnosticsLog.UseFile(savedFolder, savedName);
            }
        }

        [Fact]
        public void The_bridge_switches_to_its_own_file_before_its_first_log_line()
        {
            string source = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "Services", "Ai", "Mcp", "McpBridge.cs"));

            int runStdio = source.IndexOf("public static int RunStdio()", StringComparison.Ordinal);
            int switchAt = source.IndexOf("DiagnosticsLog.UseFileName(DiagnosticsLog.BridgeFileName);", StringComparison.Ordinal);
            int firstLine = source.IndexOf("DiagnosticsLog.Log(", StringComparison.Ordinal);
            int firstWarn = source.IndexOf("DiagnosticsLog.Warn(", StringComparison.Ordinal);
            int firstError = source.IndexOf("DiagnosticsLog.Error(", StringComparison.Ordinal);

            Assert.True(runStdio >= 0);
            Assert.True(switchAt > runStdio, "RunStdio must switch the log file");
            Assert.True(switchAt < firstLine && switchAt < firstWarn && switchAt < firstError,
                "the switch must come before every log call in the bridge");
        }
    }
}
