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

        // ----------------------------------------------------------- elevated arguments

        /// <summary>
        /// This parser is the whole input surface of the only code path that runs with
        /// administrator rights, so it is tested against malformed and hostile input rather
        /// than only the happy case. Anything it does not fully understand must be refused.
        /// </summary>
        [Fact]
        public void Kill_arguments_parse_a_well_formed_request()
        {
            Assert.True(KillArguments.TryParse(
                new[] { "--kill", "8420", "133000000000000000" }, out int pid, out long created));

            Assert.Equal(8420, pid);
            Assert.Equal(133_000_000_000_000_000L, created);
        }

        // Each case is cast to object so xUnit passes the array as a single argument rather
        // than splatting its elements across the parameter list.
        [Theory]
        [InlineData((object)new string[0])]                                // nothing
        [InlineData((object)new[] { "--kill" })]                           // no pid
        [InlineData((object)new[] { "--kill", "8420" })]                   // no creation time
        [InlineData((object)new[] { "--kill", "abc", "133" })]             // pid not a number
        [InlineData((object)new[] { "--kill", "8420", "abc" })]            // time not a number
        [InlineData((object)new[] { "--kill", "-1", "133" })]              // negative pid
        [InlineData((object)new[] { "--kill", "0", "133" })]               // the idle process
        [InlineData((object)new[] { "--kill", "8420", "-5" })]             // negative time
        [InlineData((object)new[] { "--kill", "99999999999", "133" })]     // pid beyond int
        [InlineData((object)new[] { "--settings", "8420", "133" })]        // a different switch
        [InlineData((object)new[] { "--kill", "8420", "133", "extra" })]   // trailing junk
        [InlineData((object)new[] { "--kill", " 8420", "133" })]           // padded
        [InlineData((object)new[] { "--kill", "+8420", "133" })]           // signed
        [InlineData((object)new[] { "--kill", "8,420", "133" })]           // grouped
        [InlineData((object)new[] { "--KILL", "8420", "133" })]            // case-sensitive
        public void Malformed_kill_arguments_are_refused(string[] args)
        {
            Assert.False(KillArguments.TryParse(args, out _, out _));
        }

        /// <summary>A refused parse must not leave a usable pid behind for a careless caller.</summary>
        [Fact]
        public void A_refused_parse_yields_no_pid()
        {
            KillArguments.TryParse(new[] { "--kill", "abc", "def" }, out int pid, out long created);

            Assert.Equal(0, pid);
            Assert.Equal(0, created);
        }

        /// <summary>
        /// A null argument array is what a host that passes nothing looks like. It must be a
        /// refusal, not a crash inside an elevated process.
        /// </summary>
        [Fact]
        public void Null_arguments_are_refused_without_throwing()
        {
            Assert.False(KillArguments.TryParse(null!, out _, out _));
        }
    }
}
