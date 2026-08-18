using System;
using System.Linq;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class MetricsHistoryTests
    {
        private static SystemMetrics Sample(
            float cpu = 10, float ram = 20, float gpu = 30, float temp = 40,
            float netUp = 50, float netDown = 60,
            float[]? cores = null, params (string Name, float Activity)[] disks)
        {
            return new SystemMetrics
            {
                CpuUsage = cpu,
                RamPercent = ram,
                GpuUsage = gpu,
                GpuTemperature = temp,
                NetUpKbps = netUp,
                NetDownKbps = netDown,
                CoreUsage = cores ?? Array.Empty<float>(),
                Disks = disks.Select(d => new DiskMetric { Name = d.Name, ActivityPercent = d.Activity }).ToList(),
            };
        }

        [Fact]
        public void Append_records_every_scalar_series()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(cpu: 11, ram: 22, gpu: 33, temp: 44, netUp: 55, netDown: 66));

            Assert.Equal(11f, h.Cpu.Latest);
            Assert.Equal(22f, h.Ram.Latest);
            Assert.Equal(33f, h.Gpu.Latest);
            Assert.Equal(44f, h.Temp.Latest);
            Assert.Equal(55f, h.NetUp.Latest);
            Assert.Equal(66f, h.NetDown.Latest);
        }

        [Fact]
        public void Append_exposes_the_latest_full_sample()
        {
            using var h = new MetricsHistory(capacity: 8);
            var m = Sample(cpu: 77);
            h.Append(m);

            Assert.Same(m, h.Latest);
        }

        [Fact]
        public void Latest_is_never_null_before_any_sample()
        {
            using var h = new MetricsHistory(capacity: 8);
            Assert.NotNull(h.Latest);
        }

        [Fact]
        public void Updated_is_raised_once_per_append()
        {
            using var h = new MetricsHistory(capacity: 8);
            int raised = 0;
            h.Updated += () => raised++;

            h.Append(Sample());
            h.Append(Sample());

            Assert.Equal(2, raised);
        }

        [Fact]
        public void A_null_sample_is_ignored()
        {
            using var h = new MetricsHistory(capacity: 8);
            int raised = 0;
            h.Updated += () => raised++;

            h.Append(null!);

            Assert.Equal(0, raised);
            Assert.Equal(0, h.Cpu.Count);
        }

        [Fact]
        public void An_unreadable_gpu_temperature_is_not_recorded_as_a_value()
        {
            // TelemetryService reports -1 when no source could supply a temperature. Storing it
            // would draw a line below the axis and claim the GPU is at -1 degrees.
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(temp: -1));

            Assert.Equal(0, h.Temp.Count);
            Assert.Equal(Availability.Unavailable, h.Temp.Availability);
        }

        [Fact]
        public void A_zero_gpu_temperature_is_also_treated_as_unavailable()
        {
            // 0 C is not a plausible GPU reading; the overlay already renders it as "N/A".
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(temp: 0));

            Assert.Equal(Availability.Unavailable, h.Temp.Availability);
        }

        [Fact]
        public void A_real_temperature_after_failures_becomes_available()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(temp: -1));
            h.Append(Sample(temp: 55));

            Assert.Equal(Availability.Value, h.Temp.Availability);
            Assert.Equal(55f, h.Temp.Latest);
        }

        [Fact]
        public void Disk_series_are_created_per_instance_name()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(disks: new[] { ("0 C:", 12f), ("1 D:", 34f) }));

            Assert.Equal(12f, h.Disk("0 C:")!.Latest);
            Assert.Equal(34f, h.Disk("1 D:")!.Latest);
        }

        [Fact]
        public void An_unknown_disk_has_no_series()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(disks: new[] { ("0 C:", 12f) }));

            Assert.Null(h.Disk("9 Z:"));
            Assert.Null(h.Disk(""));
        }

        [Fact]
        public void Disk_series_accumulate_across_appends()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(disks: new[] { ("0 C:", 10f) }));
            h.Append(Sample(disks: new[] { ("0 C:", 20f) }));

            var s = h.Disk("0 C:")!;
            Assert.Equal(2, s.Count);
            Assert.Equal(10f, s[0]);
            Assert.Equal(20f, s[1]);
        }

        [Fact]
        public void Series_for_disks_no_longer_reported_are_evicted()
        {
            // Instance names change when drives are added or removed and when the user edits the
            // selection. Without eviction this dictionary grows for the life of the process.
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(disks: new[] { ("0 C:", 10f), ("1 D:", 20f) }));
            Assert.Equal(2, h.DiskNames.Count());

            h.Append(Sample(disks: new[] { ("0 C:", 15f) }));

            Assert.Single(h.DiskNames);
            Assert.NotNull(h.Disk("0 C:"));
            Assert.Null(h.Disk("1 D:"));
        }

        [Fact]
        public void Core_series_are_created_to_match_the_reported_core_count()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(cores: new[] { 1f, 2f, 3f, 4f }));

            Assert.Equal(4, h.Cores.Count);
            Assert.Equal(1f, h.Cores[0].Latest);
            Assert.Equal(4f, h.Cores[3].Latest);
        }

        [Fact]
        public void Core_series_grow_when_more_cores_appear()
        {
            // A VM can hot-add processors, so the count is not fixed for the process lifetime.
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(cores: new[] { 1f, 2f }));
            h.Append(Sample(cores: new[] { 1f, 2f, 3f, 4f }));

            Assert.Equal(4, h.Cores.Count);
        }

        [Fact]
        public void Core_series_shrink_when_fewer_cores_are_reported()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(cores: new[] { 1f, 2f, 3f, 4f }));
            h.Append(Sample(cores: new[] { 1f, 2f }));

            Assert.Equal(2, h.Cores.Count);
        }

        [Fact]
        public void No_core_data_leaves_the_core_list_empty()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample());

            Assert.Empty(h.Cores);
        }

        [Fact]
        public void History_is_bounded_by_capacity()
        {
            using var h = new MetricsHistory(capacity: 4);
            for (int i = 0; i < 100; i++) h.Append(Sample(cpu: i));

            Assert.Equal(4, h.Cpu.Count);
            Assert.Equal(99f, h.Cpu.Latest);
            Assert.Equal(96f, h.Cpu[0]);
        }

        [Fact]
        public void Network_shares_one_scale_so_upload_and_download_stay_comparable()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(netUp: 100f, netDown: 5000f));

            Assert.Equal(Math.Max(h.NetUp.Peak, h.NetDown.Peak), h.SharedNetPeak);
            Assert.True(h.SharedNetPeak >= 5000f);
        }

        [Fact]
        public void Network_autoscale_has_a_floor_so_an_idle_link_is_not_amplified()
        {
            using var h = new MetricsHistory(capacity: 8);
            h.Append(Sample(netUp: 0.5f, netDown: 0.5f));

            // Without a floor, half a KB/s would render as a saturated link.
            Assert.True(h.SharedNetPeak >= 64f, $"expected a floor, got {h.SharedNetPeak}");
        }

        [Fact]
        public void Appending_after_dispose_is_ignored()
        {
            var h = new MetricsHistory(capacity: 8);
            h.Dispose();
            h.Append(Sample(cpu: 50));

            Assert.Equal(0, h.Cpu.Count);
        }

        [Fact]
        public void Dispose_is_idempotent()
        {
            var h = new MetricsHistory(capacity: 8);
            h.Dispose();
            h.Dispose();
        }

        [Fact]
        public void CpuSystem_records_the_kernel_share_capped_at_the_total()
        {
            using var h = new MetricsHistory(capacity: 8);

            var m = Sample(cpu: 40);
            m.CpuSystem = 12f;
            h.Append(m);

            // A racing pair of counter deltas can read system above total; the stacked graph
            // must never draw the tip outside the bar.
            var over = Sample(cpu: 30);
            over.CpuSystem = 35f;
            h.Append(over);

            Assert.Equal(Availability.Value, h.CpuSystem.Availability);
            Assert.Equal(12f, h.CpuSystem[0]);
            Assert.Equal(30f, h.CpuSystem.Latest);
        }

        [Fact]
        public void CpuSystem_sentinel_marks_the_series_unavailable_without_a_sample()
        {
            using var h = new MetricsHistory(capacity: 8);

            h.Append(Sample(cpu: 40)); // Sample() leaves CpuSystem at the -1 sentinel

            Assert.Equal(0, h.CpuSystem.Count);
            Assert.Equal(Availability.Unavailable, h.CpuSystem.Availability);
        }
    }
}
