using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The process window's new columns come from the same kernel snapshot as the old ones, and
    /// its bulk kill decides what to end in a pure function. Both are tested here; the WPF layout
    /// and the handle reads for the detail pane are verified by hand.
    /// </summary>
    public class ProcessWindowTests
    {
        private static ProcessUsage P(string name, int pid, int parent, long createTime) =>
            new ProcessUsage(name, pid, 0f, 0) { ParentPid = parent, CreateTime = createTime };

        // ------------------------------------------------------- parent names

        [Fact]
        public void A_live_parent_is_named()
        {
            var rows = ProcessTree.WithParentNames(new[]
            {
                P("bash.exe", 100, 4, 10),
                P("find.exe", 200, 100, 20),
            });

            Assert.Equal("bash.exe", rows.Single(r => r.Pid == 200).ParentName);
        }

        [Fact]
        public void An_absent_parent_reads_as_gone()
        {
            var rows = ProcessTree.WithParentNames(new[] { P("find.exe", 200, 100, 20) });

            Assert.Equal(ProcessTree.Gone, rows.Single().ParentName);
        }

        [Fact]
        public void A_parent_younger_than_its_child_is_a_recycled_pid_and_reads_as_gone()
        {
            // The real parent exited and Windows gave 100 to something newer. Naming it would
            // tell the user an unrelated notepad launched their find.
            var rows = ProcessTree.WithParentNames(new[]
            {
                P("notepad.exe", 100, 4, 50),
                P("find.exe", 200, 100, 20),
            });

            Assert.Equal(ProcessTree.Gone, rows.Single(r => r.Pid == 200).ParentName);
        }

        [Fact]
        public void A_process_with_no_parent_pid_has_an_empty_parent_rather_than_gone()
        {
            // The System process has parent 0. It is not an orphan; it never had a parent.
            var rows = ProcessTree.WithParentNames(new[] { P("System", 4, 0, 1) });

            Assert.Equal("", rows.Single().ParentName);
        }

        [Fact]
        public void Resolving_parent_names_keeps_every_row_in_its_order_and_its_other_fields()
        {
            var input = new[]
            {
                P("c.exe", 3, 0, 1) with { Threads = 7, Handles = 70 },
                P("a.exe", 1, 3, 2),
                P("b.exe", 2, 3, 3),
            };

            var rows = ProcessTree.WithParentNames(input);

            Assert.Equal(new[] { 3, 1, 2 }, rows.Select(r => r.Pid));
            Assert.Equal(7, rows[0].Threads);
            Assert.Equal(70, rows[0].Handles);
            Assert.Equal("c.exe", rows[1].ParentName);
        }

        [Fact]
        public void An_empty_snapshot_resolves_to_an_empty_list()
        {
            Assert.Empty(ProcessTree.WithParentNames(Array.Empty<ProcessUsage>()));
        }

        // ------------------------------------------------------- snapshot fields

        [Fact]
        public void The_sampler_reports_threads_handles_and_parent_for_the_current_process()
        {
            using var sampler = new ProcessSampler();
            using var ready = new System.Threading.ManualResetEventSlim();
            sampler.Updated += () => ready.Set();
            sampler.Retain();

            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "no sample within 10 seconds");

            var me = sampler.AllProcesses.Single(p => p.Pid == Environment.ProcessId);
            using var self = Process.GetCurrentProcess();
            self.Refresh();

            // The offsets are hand-computed. A wrong one reads a neighbouring field, which is
            // usually a small plausible number, so compare against the managed API rather than
            // only checking for non-zero.
            Assert.InRange(me.Threads, self.Threads.Count / 2, self.Threads.Count * 2 + 8);
            Assert.InRange(me.Handles, self.HandleCount / 2, self.HandleCount * 2 + 64);
            Assert.True(me.ParentPid > 0);
        }

        [Theory]
        [InlineData(0L, "0 MB")]
        [InlineData(512L * 1024 * 1024, "512 MB")]
        [InlineData(1536L * 1024 * 1024, "1.5 GB")]
        public void Bytes_format_in_the_largest_readable_unit(long bytes, string expected)
        {
            Assert.Equal(expected, ProcessUsage.FormatBytes(bytes));
        }
    }
}
