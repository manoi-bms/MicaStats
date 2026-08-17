using System;
using System.Linq;
using System.Threading;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Exercises ProcessSampler against the live system. These are integration tests by necessity:
    /// the whole point of the class is that it walks a raw kernel structure by byte offset, and a
    /// wrong offset can only be caught by comparing against reality.
    /// </summary>
    public class ProcessSamplerTests
    {
        /// <summary>Waits for one completed sample, or returns false on timeout.</summary>
        private static bool WaitForSample(ProcessSampler s, int timeoutMs = 8000)
        {
            using var signalled = new ManualResetEventSlim(false);
            void Handler() => signalled.Set();
            s.Updated += Handler;
            try
            {
                s.Enabled = true;
                return signalled.Wait(timeoutMs);
            }
            finally
            {
                s.Updated -= Handler;
            }
        }

        [Fact]
        public void A_disabled_sampler_reports_nothing()
        {
            using var s = new ProcessSampler();
            Assert.False(s.Enabled);
            Assert.Empty(s.TopByCpu);
            Assert.Empty(s.TopByRam);
        }

        [Fact]
        public void Enabling_produces_a_sample()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s), "expected a sample within the timeout");
            Assert.NotEmpty(s.TopByRam);
        }

        [Fact]
        public void Reported_process_names_are_plausible()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));

            foreach (var p in s.TopByRam)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
                // A misread ImageName offset yields control characters or mojibake rather than a
                // filename, so require printable text.
                Assert.DoesNotContain('\0', p.Name);
                Assert.All(p.Name, ch => Assert.True(!char.IsControl(ch), $"control char in '{p.Name}'"));
            }
        }

        [Fact]
        public void Working_sets_are_positive_and_within_physical_limits()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));

            foreach (var p in s.TopByRam)
            {
                Assert.True(p.WorkingSet > 0, $"{p.Name} reported a non-positive working set");
                // 1 TB is far beyond any real working set; exceeding it means the offset is wrong.
                Assert.True(p.WorkingSet < 1024L * 1024 * 1024 * 1024, $"{p.Name} reported {p.WorkingSet} bytes");
            }
        }

        [Fact]
        public void Pids_are_positive_and_the_idle_process_is_excluded()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));

            foreach (var p in s.TopByRam)
            {
                Assert.True(p.Pid > 0, "PID 0 is the idle process and must not be listed");
            }
        }

        [Fact]
        public void Cpu_shares_stay_within_a_single_machine_worth_of_capacity()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));
            // Give the second sample a chance so deltas are real rather than a cold baseline.
            Thread.Sleep(2500);

            foreach (var p in s.TopByCpu)
            {
                Assert.InRange(p.CpuPercent, 0f, 100f);
            }
        }

        [Fact]
        public void Rankings_are_ordered()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));
            Thread.Sleep(2500);

            var ram = s.TopByRam.ToList();
            for (int i = 1; i < ram.Count; i++)
                Assert.True(ram[i - 1].WorkingSet >= ram[i].WorkingSet, "memory ranking is not descending");

            var cpu = s.TopByCpu.ToList();
            for (int i = 1; i < cpu.Count; i++)
                Assert.True(cpu[i - 1].CpuPercent >= cpu[i].CpuPercent, "CPU ranking is not descending");
        }

        [Fact]
        public void Rankings_are_capped_to_the_display_count()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));

            Assert.True(s.TopByCpu.Count <= ProcessSampler.TopCount);
            Assert.True(s.TopByRam.Count <= ProcessSampler.TopCount);
        }

        [Fact]
        public void Disabling_drops_retained_results_so_a_closed_panel_holds_no_state()
        {
            using var s = new ProcessSampler();
            Assert.True(WaitForSample(s));
            Assert.NotEmpty(s.TopByRam);

            s.Enabled = false;

            Assert.Empty(s.TopByCpu);
            Assert.Empty(s.TopByRam);
        }

        [Fact]
        public void Working_set_text_switches_unit_at_a_gigabyte()
        {
            var mb = new ProcessUsage("a", 1, 0f, 512L * 1024 * 1024);
            var gb = new ProcessUsage("b", 2, 0f, 3L * 1024 * 1024 * 1024);

            Assert.EndsWith("MB", mb.WorkingSetText);
            Assert.EndsWith("GB", gb.WorkingSetText);
        }

        [Fact]
        public void Dispose_is_idempotent_and_stops_sampling()
        {
            var s = new ProcessSampler();
            Assert.True(WaitForSample(s));
            s.Dispose();
            s.Dispose();
            Assert.False(s.Enabled);
        }
    }
}
