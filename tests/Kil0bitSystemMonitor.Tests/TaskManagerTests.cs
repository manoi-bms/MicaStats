using System;
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The task manager exists because Windows Task Manager fails four ways on the target
    /// machine: slow or failing to open, opening with a frozen or empty list, End task doing
    /// nothing, and costing enough CPU to worsen the stutter it was opened to diagnose.
    ///
    /// <para>
    /// Most of that is a UI and threading problem and is not unit testable. What is testable is
    /// the part that must never be wrong: the guard that refuses to terminate a process whose
    /// death stops Windows, and the parser that is the entire input surface of the only code
    /// path running with administrator rights.
    /// </para>
    /// </summary>
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

        // ------------------------------------------------------- critical process guard

        /// <summary>
        /// Terminating any of these stops Windows immediately — a bugcheck, not an error
        /// dialog. The match has to be exact in both directions: a near-miss that fails to
        /// match kills the machine, and a near-miss that matches wrongly blocks a legitimate
        /// kill of an unrelated program that happens to be named similarly.
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
    }
}
