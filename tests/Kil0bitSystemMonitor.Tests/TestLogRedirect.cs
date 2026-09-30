using System;
using System.IO;
using System.Runtime.CompilerServices;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Every diagnostics line the test run writes goes to a temp folder, never to the real
    /// <c>%APPDATA%\MicaStats\logs</c>: code under test logs on paths no test can redirect one by
    /// one (a file that vanished, a dismissed update), and tests must never touch the real profile.
    /// </summary>
    internal static class TestLogRedirect
    {
        /// <summary>The folder the run logs into.</summary>
        internal static readonly string Folder =
            Path.Combine(Path.GetTempPath(), "MicaStatsTests", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "logs");

#pragma warning disable CA2255 // A test assembly, not a library: the redirect must run before any test logs.
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Redirect() => DiagnosticsLog.UseFile(Folder, DiagnosticsLog.AppFileName);
    }

    public class TestLogRedirectTests
    {
        [Fact]
        public void The_test_run_never_logs_into_the_real_profile()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Assert.False(DiagnosticsLog.LogDir.StartsWith(appData, StringComparison.OrdinalIgnoreCase),
                "Tests log into " + DiagnosticsLog.LogDir);
        }
    }
}
