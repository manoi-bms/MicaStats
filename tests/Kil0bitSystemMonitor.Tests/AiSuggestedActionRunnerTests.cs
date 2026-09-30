using System.Collections.Generic;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A suggestion button re-checks its target on click. These drive the runner through a fake
    /// host, so no real process is looked up or ended.
    /// </summary>
    public class AiSuggestedActionRunnerTests
    {
        private sealed class FakeHost : ISuggestedActionHost
        {
            public readonly List<ProcessSampler.RawProcess> Running = new();
            public readonly List<(int Pid, long CreateTime, string Name)> Ended = new();
            public EndTaskResult Result = EndTaskResult.Terminated;
            public string Message = "Ended chrome.exe.";
            public int Recorded;
            public int DiagnosticsOpened;
            public int ProcessWindowsOpened;

            public int OwnProcessId => 4242;

            public IReadOnlyList<ProcessSampler.RawProcess> Processes() => Running;

            public EndTaskResult EndProcess(int pid, long createTime, string name, out string message)
            {
                Ended.Add((pid, createTime, name));
                message = Message;
                return Result;
            }

            public string RecordSlowdown()
            {
                Recorded++;
                return "Saved slowdown-20260930-101500.txt. It is listed in Diagnostics > Slowdowns.";
            }

            public void OpenDiagnostics() => DiagnosticsOpened++;

            public void OpenProcessWindow() => ProcessWindowsOpened++;
        }

        private static FakeHost HostWith(params (int Pid, string Name, long CreateTime)[] processes)
        {
            var host = new FakeHost();
            foreach (var p in processes)
                host.Running.Add(new ProcessSampler.RawProcess(p.Pid, 1, p.Name, p.CreateTime, 0));
            return host;
        }

        private static SuggestedAction End(string name, int? pid, long? createTime) =>
            new(SuggestedActionKind.EndProcess, "End " + name, "It is using most of the CPU", pid, createTime, name);

        [Fact]
        public void Ends_the_process_when_pid_start_time_and_name_still_match()
        {
            var host = HostWith((1234, "chrome.exe", 555));

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("Ended chrome.exe.", message);
            Assert.Equal(new[] { (1234, 555L, "chrome.exe") }, host.Ended);
        }

        [Fact]
        public void Refuses_when_the_pid_now_has_a_different_start_time()
        {
            var host = HostWith((1234, "chrome.exe", 999));

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("chrome.exe (PID 1234) is no longer running, or its PID now belongs to another process. Nothing was ended.", message);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_when_the_pid_now_belongs_to_a_different_program()
        {
            var host = HostWith((1234, "notepad.exe", 555));

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("PID 1234 is now notepad.exe, not chrome.exe. Nothing was ended.", message);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_a_core_windows_process_without_trying()
        {
            var host = HostWith((700, "lsass.exe", 555));

            string message = SuggestedActionRunner.Run(End("lsass.exe", 700, 555), host);

            Assert.StartsWith("lsass.exe is a core Windows process.", message, System.StringComparison.Ordinal);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_without_an_exact_target()
        {
            var host = HostWith((1234, "chrome.exe", 555));

            string noPid = SuggestedActionRunner.Run(End("chrome.exe", null, null), host);
            string noStart = SuggestedActionRunner.Run(End("chrome.exe", 1234, null), host);

            Assert.StartsWith("This suggestion does not say exactly which process to end", noPid, System.StringComparison.Ordinal);
            Assert.StartsWith("This suggestion does not say exactly which process to end", noStart, System.StringComparison.Ordinal);

            string zeroStart = SuggestedActionRunner.Run(End("chrome.exe", 1234, 0), host);
            Assert.StartsWith("This suggestion does not say exactly which process to end", zeroStart, System.StringComparison.Ordinal);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_to_end_micastats_itself()
        {
            var host = HostWith((4242, "MicaStats.exe", 555));

            string message = SuggestedActionRunner.Run(End("MicaStats.exe", 4242, 555), host);

            Assert.StartsWith("That is MicaStats itself", message, System.StringComparison.Ordinal);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Access_denied_points_to_the_process_window()
        {
            var host = HostWith((1234, "chrome.exe", 555));
            host.Result = EndTaskResult.AccessDenied;
            host.Message = "chrome.exe runs at a higher privilege level than MicaStats, which is unelevated.";

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("chrome.exe runs at a higher privilege level than MicaStats, which is unelevated."
                         + " To end it anyway, open Processes, select it, press End task, then Retry as administrator.", message);
        }

        [Fact]
        public void The_other_kinds_go_to_the_host()
        {
            var host = new FakeHost();

            string recorded = SuggestedActionRunner.Run(new SuggestedAction(SuggestedActionKind.RecordSlowdown, "Record a slowdown now", "It just stalled"), host);
            string diagnostics = SuggestedActionRunner.Run(new SuggestedAction(SuggestedActionKind.OpenDiagnostics, "Open Diagnostics", "See the reports"), host);
            string processes = SuggestedActionRunner.Run(new SuggestedAction(SuggestedActionKind.OpenProcessWindow, "Open Processes", "See every process"), host);

            Assert.Equal("Saved slowdown-20260930-101500.txt. It is listed in Diagnostics > Slowdowns.", recorded);
            Assert.Equal("Opened Diagnostics.", diagnostics);
            Assert.Equal("Opened Processes.", processes);
            Assert.Equal(1, host.Recorded);
            Assert.Equal(1, host.DiagnosticsOpened);
            Assert.Equal(1, host.ProcessWindowsOpened);
            Assert.Empty(host.Ended);
        }
    }
}
