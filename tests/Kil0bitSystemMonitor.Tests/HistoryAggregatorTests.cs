using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>One-second snapshots become one row per UTC minute; missing readings stay missing.</summary>
    public class HistoryAggregatorTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static DiskMetric Disk(string name, double freeGb, double totalGb) => new()
        {
            Name = name,
            FreeBytes = (ulong)(freeGb * 1024 * 1024 * 1024),
            TotalBytes = (ulong)(totalGb * 1024 * 1024 * 1024),
        };

        private static SystemMetrics Metrics(float cpu = 10, float ram = 40, float cpuTemp = -1, float gpu = 5,
                                             float gpuTemp = -1, float up = 1, float down = 2, float diskActivity = 3,
                                             int battery = -1, bool onAc = false, List<DiskMetric>? disks = null) => new()
        {
            CpuUsage = cpu,
            RamPercent = ram,
            CpuTemperature = cpuTemp,
            GpuUsage = gpu,
            GpuTemperature = gpuTemp,
            NetUpKbps = up,
            NetDownKbps = down,
            DiskUsage = diskActivity,
            BatteryPercent = battery,
            BatteryOnAc = onAc,
            Disks = disks ?? new List<DiskMetric> { Disk("0 C:", 50, 100) },
        };

        [Fact]
        public void A_minute_of_samples_becomes_one_row_when_the_next_minute_starts()
        {
            var agg = new MinuteAggregator();

            Assert.Null(agg.Add(Metrics(cpu: 10, ram: 40, up: 100, down: 300), T0.AddSeconds(5)));
            Assert.Null(agg.Add(Metrics(cpu: 20, ram: 50, up: 200, down: 100), T0.AddSeconds(25)));
            Assert.Null(agg.Add(Metrics(cpu: 60, ram: 60, up: 300, down: 200), T0.AddSeconds(59.999)));
            HistoryRow? row = agg.Add(Metrics(cpu: 99), T0.AddMinutes(1));   // exactly on the boundary: the next minute

            Assert.NotNull(row);
            Assert.Equal(T0, row!.Utc);
            Assert.Equal(DateTimeKind.Utc, row.Utc.Kind);
            Assert.Equal(60, row.Seconds);
            Assert.Equal(30f, row.CpuAvg);
            Assert.Equal(60f, row.CpuMax);
            Assert.Equal(50f, row.RamAvg);
            Assert.Equal(60f, row.RamMax);
            Assert.Equal(200f, row.NetUpAvg);
            Assert.Equal(200f, row.NetDownAvg);
            Assert.Equal(50f, row.DiskFreeMinPercent);
        }

        [Fact]
        public void Readings_that_were_never_available_stay_empty_not_zero()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpuTemp: -1, gpu: -1, gpuTemp: -1, battery: -1, disks: new List<DiskMetric>()), T0.AddSeconds(1));
            agg.Add(Metrics(cpuTemp: -1, gpu: -1, gpuTemp: -1, battery: -1, disks: new List<DiskMetric>()), T0.AddSeconds(2));

            HistoryRow row = agg.Flush()!;

            Assert.Null(row.CpuTempAvg);
            Assert.Null(row.CpuTempMax);
            Assert.Null(row.GpuAvg);
            Assert.Null(row.GpuMax);
            Assert.Null(row.GpuTempMax);
            Assert.Null(row.DiskFreeMinPercent);
            Assert.Null(row.DiskActivityMax);
            Assert.Null(row.BatteryPercent);
            Assert.Null(row.OnAc);
            Assert.Null(row.TopCpuName);
            Assert.Null(row.TopRamMb);
            Assert.Equal(10f, row.CpuAvg);   // what was read is still there
        }

        [Fact]
        public void Temperatures_average_only_the_seconds_that_had_a_reading()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpuTemp: 50, gpuTemp: -1), T0.AddSeconds(1));
            agg.Add(Metrics(cpuTemp: -1, gpuTemp: 65), T0.AddSeconds(2));
            agg.Add(Metrics(cpuTemp: 70, gpuTemp: 60), T0.AddSeconds(3));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(60f, row.CpuTempAvg);
            Assert.Equal(70f, row.CpuTempMax);
            Assert.Equal(65f, row.GpuTempMax);
        }

        [Fact]
        public void Disk_free_is_the_lowest_share_on_any_ready_drive_and_activity_the_peak()
        {
            var agg = new MinuteAggregator();
            var disks = new List<DiskMetric> { Disk("0 C:", 50, 100), Disk("1 D:", 20, 200), new DiskMetric { Name = "2 E:" } };
            agg.Add(Metrics(diskActivity: 3, disks: disks), T0.AddSeconds(1));
            agg.Add(Metrics(diskActivity: 80, disks: disks), T0.AddSeconds(2));
            agg.Add(Metrics(diskActivity: 12, disks: disks), T0.AddSeconds(3));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(10f, row.DiskFreeMinPercent);   // D: 20 of 200 GB; E: was not ready and does not count
            Assert.Equal(80f, row.DiskActivityMax);
        }

        [Fact]
        public void Battery_is_the_last_reading_of_the_minute()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(battery: 80, onAc: true), T0.AddSeconds(5));
            agg.Add(Metrics(battery: 79, onAc: false), T0.AddSeconds(30));
            agg.Add(Metrics(battery: -1), T0.AddSeconds(45));   // a missed reading does not erase the last one

            HistoryRow row = agg.Flush()!;

            Assert.Equal(79f, row.BatteryPercent);
            Assert.False(row.OnAc);
        }

        [Fact]
        public void A_clock_that_jumps_back_finishes_the_minute_too()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpu: 10), T0.AddMinutes(5).AddSeconds(10));

            HistoryRow? finished = agg.Add(Metrics(cpu: 20), T0.AddMinutes(2));
            HistoryRow? rest = agg.Flush();

            Assert.Equal(T0.AddMinutes(5), finished!.Utc);
            Assert.Equal(10f, finished.CpuAvg);
            Assert.Equal(T0.AddMinutes(2), rest!.Utc);
            Assert.Equal(20f, rest.CpuAvg);
        }

        [Fact]
        public void Flush_finishes_the_minute_in_progress_once()
        {
            Assert.Null(new MinuteAggregator().Flush());

            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpu: 42), T0.AddSeconds(10));

            HistoryRow? row = agg.Flush();

            Assert.Equal(T0, row!.Utc);
            Assert.Equal(42f, row.CpuMax);
            Assert.Null(agg.Flush());
        }

        [Fact]
        public void The_top_processes_belong_to_the_minute_they_were_set_in()
        {
            var top = new TopProcessSample("chrome.exe", @"%USERPROFILE%\chrome.exe", 42.5f, "code.exe", 812.3f);
            var agg = new MinuteAggregator();
            agg.Add(Metrics(), T0.AddSeconds(5));
            agg.SetTop(top);
            agg.Add(Metrics(), T0.AddSeconds(30));

            HistoryRow row = agg.Add(Metrics(), T0.AddMinutes(1).AddSeconds(5))!;
            HistoryRow next = agg.Flush()!;

            Assert.Equal("chrome.exe", row.TopCpuName);
            Assert.Equal(@"%USERPROFILE%\chrome.exe", row.TopCpuPath);
            Assert.Equal(42.5f, row.TopCpuPercent);
            Assert.Equal("code.exe", row.TopRamName);
            Assert.Equal(812.3f, row.TopRamMb);
            Assert.Null(next.TopCpuName);
            Assert.Null(next.TopRamName);
        }

        [Fact]
        public void Nan_readings_are_ignored()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpu: float.NaN), T0.AddSeconds(1));
            agg.Add(Metrics(cpu: 40), T0.AddSeconds(2));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(40f, row.CpuAvg);
            Assert.Equal(40f, row.CpuMax);
        }

        [Fact]
        public void An_unspecified_time_is_read_as_utc()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(), DateTime.SpecifyKind(T0.AddSeconds(5), DateTimeKind.Unspecified));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(T0, row.Utc);
            Assert.Equal(DateTimeKind.Utc, row.Utc.Kind);
        }
    }
}
