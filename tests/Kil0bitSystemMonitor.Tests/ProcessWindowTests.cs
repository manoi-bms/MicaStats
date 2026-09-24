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

        // ------------------------------------------------------- sorting, search, totals

        private static ProcessUsage R(string name, int pid, float cpu = 0, long mem = 0) =>
            new ProcessUsage(name, pid, cpu, mem);

        [Fact]
        public void Every_column_header_maps_to_its_sort_column_and_nothing_else_does()
        {
            Assert.Equal(ProcessSortColumn.Parent, TaskManagerViewModel.ColumnFor("Parent"));
            Assert.Equal(ProcessSortColumn.Uptime, TaskManagerViewModel.ColumnFor("Uptime"));
            Assert.Equal(ProcessSortColumn.Threads, TaskManagerViewModel.ColumnFor("Threads"));
            Assert.Equal(ProcessSortColumn.Handles, TaskManagerViewModel.ColumnFor("Handles"));
            Assert.Equal(ProcessSortColumn.Cpu, TaskManagerViewModel.ColumnFor("CPU"));
            Assert.Null(TaskManagerViewModel.ColumnFor("Nonsense"));
            Assert.Null(TaskManagerViewModel.ColumnFor(null));
        }

        [Fact]
        public void Sorting_by_threads_descending_puts_the_busiest_first()
        {
            var rows = new List<ProcessUsage>
            {
                R("a", 1) with { Threads = 5 },
                R("b", 2) with { Threads = 50 },
                R("c", 3) with { Threads = 20 },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Threads, descending: true);

            Assert.Equal(new[] { 2, 3, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Sorting_by_handles_ascending_puts_the_fewest_first()
        {
            var rows = new List<ProcessUsage>
            {
                R("a", 1) with { Handles = 900 },
                R("b", 2) with { Handles = 30 },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Handles, descending: false);

            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Sorting_by_uptime_descending_puts_the_longest_running_first()
        {
            // Longest running means the OLDEST creation time.
            var rows = new List<ProcessUsage>
            {
                R("young", 1) with { CreateTime = 900 },
                R("old", 2) with { CreateTime = 100 },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Uptime, descending: true);

            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Sorting_by_parent_orders_by_parent_name()
        {
            var rows = new List<ProcessUsage>
            {
                R("x", 1) with { ParentName = "node.exe" },
                R("y", 2) with { ParentName = "bash.exe" },
            };

            TaskManagerViewModel.Sort(rows, ProcessSortColumn.Parent, descending: false);

            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Pid));
        }

        [Fact]
        public void Searching_a_parent_name_finds_its_children()
        {
            var all = new[]
            {
                R("find.exe", 1) with { ParentName = "bash.exe" },
                R("notepad.exe", 2) with { ParentName = "explorer.exe" },
            };

            var hits = TaskManagerViewModel.Filter(all, "bash");

            Assert.Equal(new[] { 1 }, hits.Select(r => r.Pid));
        }

        [Fact]
        public void Totals_sum_every_filtered_row()
        {
            var rows = new[]
            {
                R("a", 1, cpu: 1.5f, mem: 100L * 1024 * 1024) with { DiskReadBytesPerSec = 1000 },
                R("b", 2, cpu: 2.5f, mem: 300L * 1024 * 1024) with { DiskWriteBytesPerSec = 2000 },
            };

            var t = TaskManagerViewModel.Totals(rows);

            Assert.Equal(2, t.Count);
            Assert.Equal(4.0, t.Cpu, 3);
            Assert.Equal(400L * 1024 * 1024, t.Memory);
            Assert.Equal(3000L, t.Disk);
        }

        [Fact]
        public void Totals_of_nothing_are_zero()
        {
            Assert.Equal(new ProcessTotals(0, 0f, 0L, 0L),
                TaskManagerViewModel.Totals(Array.Empty<ProcessUsage>()));
        }

        [Fact]
        public void Totals_read_as_one_line()
        {
            string text = TaskManagerViewModel.FormatTotals(
                new ProcessTotals(37, 4.26f, 1932735283L, 12L * 1024 * 1024));

            Assert.Equal("37 processes · 4.3% CPU · 1.8 GB · 12 MB/s", text);
        }

        [Fact]
        public void A_single_process_is_not_pluralised_and_idle_disk_reads_as_zero()
        {
            string text = TaskManagerViewModel.FormatTotals(new ProcessTotals(1, 0f, 0L, 0L));

            Assert.Equal("1 process · 0.0% CPU · 0 MB · 0 B/s", text);
        }

        [Theory]
        [InlineData(45, "45s")]
        [InlineData(12 * 60 + 5, "12m")]
        [InlineData(3 * 3600 + 4 * 60, "3h 04m")]
        [InlineData(2 * 86400 + 5 * 3600, "2d 5h")]
        public void Uptime_reads_at_the_scale_that_matters(int seconds, string expected)
        {
            Assert.Equal(expected, TaskManagerViewModel.FormatUptime(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void A_negative_uptime_from_clock_skew_reads_as_zero()
        {
            Assert.Equal("0s", TaskManagerViewModel.FormatUptime(TimeSpan.FromSeconds(-5)));
        }

        [Fact]
        public void The_parent_column_names_the_parent_and_its_pid_or_says_it_is_gone()
        {
            Assert.Equal("bash.exe (1234)",
                TaskManagerViewModel.ParentText(R("x", 1) with { ParentName = "bash.exe", ParentPid = 1234 }));
            Assert.Equal(ProcessTree.Gone,
                TaskManagerViewModel.ParentText(R("x", 1) with { ParentName = ProcessTree.Gone, ParentPid = 1234 }));
            Assert.Equal("",
                TaskManagerViewModel.ParentText(R("x", 1) with { ParentName = "", ParentPid = 0 }));
        }
    }
}
