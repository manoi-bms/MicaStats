using System;
using System.Linq;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Watchdog;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The watchdog runs when no window is open, so it cannot ride the sampler's lease-driven
    /// timer. These exercise the one-shot pass it uses instead, against the only process whose
    /// identity the test already knows: itself.
    /// </summary>
    public class ProcessSnapshotTests
    {
        [Fact]
        public void A_one_shot_snapshot_finds_the_current_process()
        {
            var snapshot = ProcessSampler.SnapshotOnce();
            int self = Environment.ProcessId;

            Assert.NotEmpty(snapshot);
            Assert.Contains(snapshot, p => p.Pid == self);
        }

        [Fact]
        public void The_current_process_carries_its_real_parent_pid()
        {
            var snapshot = ProcessSampler.SnapshotOnce();
            var me = snapshot.First(p => p.Pid == Environment.ProcessId);

            // The test host was started by something, and a parent pid of zero would mean the
            // offset is wrong rather than that the process has no parent.
            Assert.True(me.ParentPid > 0);
        }

        [Fact]
        public void The_current_process_reports_cumulative_cpu_and_a_creation_time()
        {
            var snapshot = ProcessSampler.SnapshotOnce();
            var me = snapshot.First(p => p.Pid == Environment.ProcessId);

            // Running this test costs CPU, so the total cannot be zero, and the creation time
            // must not be wilder than the process is old.
            Assert.True(me.CpuSeconds > 0);
            Assert.True(me.CreateTime > 0);

            DateTime started = DateTime.FromFileTime(me.CreateTime);
            Assert.True(started <= DateTime.Now);
            Assert.True(started > DateTime.Now.AddDays(-1));
        }

        [Fact]
        public void A_one_shot_snapshot_works_while_the_sampler_is_idle()
        {
            // The watchdog runs when no window is open, so nothing holds a Retain() lease.
            // Taking one would run full two-second sampling all day to serve a check that
            // happens once a minute — so the one-shot must not need the sampler at all.
            using var sampler = new ProcessSampler();
            Assert.False(sampler.Enabled);

            var snapshot = ProcessSampler.SnapshotOnce();

            Assert.NotEmpty(snapshot);
            Assert.Contains(snapshot, p => p.Pid == Environment.ProcessId);
            Assert.False(sampler.Enabled);
            Assert.Empty(sampler.AllProcesses);
        }

        [Fact]
        public void The_current_process_reports_its_own_image_path_and_command_line()
        {
            bool ok = ProcessDetails.TryRead(Environment.ProcessId, out string image, out string commandLine);

            Assert.True(ok);
            Assert.Equal(Environment.ProcessPath, image, ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(commandLine));
        }

        [Fact]
        public void A_pid_that_does_not_exist_is_reported_as_unreadable_rather_than_throwing()
        {
            // A process can exit between the snapshot and the enrichment; that is ordinary, not
            // an error, and the watchdog runs unattended.
            bool ok = ProcessDetails.TryRead(-1, out string image, out string commandLine);

            Assert.False(ok);
            Assert.Equal("", image);
            Assert.Equal("", commandLine);
        }
    }
}
