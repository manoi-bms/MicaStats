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
    }
}
