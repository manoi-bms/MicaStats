using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Battery wear figures. The whole feature rests on parsing one report correctly, and the
    /// failure is silent — a missed field shows "unknown" rather than throwing.
    /// </summary>
    public class BatteryReportParserTests
    {
        /// <summary>
        /// Shaped after the real output of <c>powercfg /batteryreport /XML</c>, including the
        /// namespace and the serial number the parser must leave alone. Values are synthetic.
        /// </summary>
        private const string SampleXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <Id>ACME PACK-1</Id>
                  <Manufacturer>ACME</Manufacturer>
                  <SerialNumber>SECRET-4815162342</SerialNumber>
                  <Chemistry>LiP</Chemistry>
                  <DesignCapacity>90000</DesignCapacity>
                  <FullChargeCapacity>81000</FullChargeCapacity>
                  <CycleCount>120</CycleCount>
                </Battery>
              </Batteries>
            </BatteryReport>
            """;

        [Fact]
        public void Reads_capacity_and_cycles()
        {
            var health = BatteryReportParser.Parse(SampleXml);

            Assert.True(health.Any);
            Assert.Equal(90000, health.DesignCapacityMwh);
            Assert.Equal(81000, health.FullChargeCapacityMwh);
            Assert.Equal(120, health.CycleCount);
            Assert.Equal(90d, health.HealthPercent, 3);
        }

        /// <summary>
        /// The report names the pack's serial number. It identifies the machine and this data
        /// reaches a log file and a shareable report, so nothing must carry it through.
        /// </summary>
        [Fact]
        public void Serial_number_is_not_carried_into_the_model()
        {
            var health = BatteryReportParser.Parse(SampleXml);
            var pack = Assert.Single(health.Packs);

            Assert.DoesNotContain("SECRET", pack.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("4815162342", pack.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", pack.Chemistry, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The namespace URI has changed between Windows releases, so matching on the fully
        /// qualified element name would silently return "no battery" after an OS update.
        /// </summary>
        [Fact]
        public void Unexpected_namespace_still_parses()
        {
            string moved = SampleXml.Replace(
                "http://schemas.microsoft.com/battery/2012",
                "http://schemas.microsoft.com/battery/2031", StringComparison.Ordinal);

            Assert.True(BatteryReportParser.Parse(moved).Any);
        }

        [Fact]
        public void Two_packs_are_summed()
        {
            string two = SampleXml.Replace("</Batteries>",
                "<Battery><Id>B</Id><DesignCapacity>10000</DesignCapacity>" +
                "<FullChargeCapacity>9000</FullChargeCapacity><CycleCount>7</CycleCount></Battery></Batteries>",
                StringComparison.Ordinal);

            var health = BatteryReportParser.Parse(two);
            Assert.Equal(2, health.Packs.Count);
            Assert.Equal(100000, health.DesignCapacityMwh);
            Assert.Equal(90000, health.FullChargeCapacityMwh);
            Assert.Equal(120, health.CycleCount);      // the highest, not the sum
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not xml at all")]
        [InlineData("<BatteryReport><Batteries /></BatteryReport>")]
        public void Unusable_input_reports_no_battery(string? xml)
        {
            var health = BatteryReportParser.Parse(xml);
            Assert.False(health.Any);
            Assert.Equal(-1d, health.HealthPercent);
        }

        [Fact]
        public void Pack_with_no_capacity_is_skipped()
        {
            const string placeholder = """
                <BatteryReport><Batteries>
                  <Battery><Id>Empty</Id><DesignCapacity>0</DesignCapacity>
                  <FullChargeCapacity>0</FullChargeCapacity></Battery>
                </Batteries></BatteryReport>
                """;
            Assert.False(BatteryReportParser.Parse(placeholder).Any);
        }
    }

    public class BatteryEstimateTests
    {
        /// <summary>
        /// The value this machine actually returns from Win32_Battery.EstimatedRunTime. It is
        /// the documented "unknown" sentinel — about 136 years — and forwarding it as an
        /// estimate is precisely the bug this feature exists to avoid.
        /// </summary>
        [Fact]
        public void Windows_unknown_sentinel_is_rejected()
        {
            Assert.False(BatteryEstimate.IsPlausibleOsEstimate(71582788));
            Assert.False(BatteryEstimate.IsPlausibleOsEstimate(0));
            Assert.False(BatteryEstimate.IsPlausibleOsEstimate(-5));
            Assert.True(BatteryEstimate.IsPlausibleOsEstimate(180));
        }

        [Fact]
        public void Time_to_empty_divides_capacity_by_draw()
        {
            // 90,000 mWh at 30,000 mW is exactly three hours.
            var span = BatteryEstimate.TimeToEmpty(90000, 30000);
            Assert.NotNull(span);
            Assert.Equal(3d, span!.Value.TotalHours, 3);
        }

        [Theory]
        [InlineData(0, 30000)]        // nothing left to count down
        [InlineData(90000, 0)]        // no measurable draw
        [InlineData(90000, 100)]      // 900 hours — a rate sampled at the wrong moment
        public void Unusable_inputs_give_no_estimate(int remaining, int rate)
        {
            Assert.Null(BatteryEstimate.TimeToEmpty(remaining, rate));
        }

        [Fact]
        public void Time_to_full_uses_the_deficit()
        {
            var span = BatteryEstimate.TimeToFull(remainingMwh: 40000, fullChargeMwh: 80000, chargeRateMw: 20000);
            Assert.NotNull(span);
            Assert.Equal(2d, span!.Value.TotalHours, 3);
        }

        [Fact]
        public void Already_full_is_zero_not_null()
        {
            Assert.Equal(TimeSpan.Zero,
                BatteryEstimate.TimeToFull(80000, 80000, 20000));
        }

        [Theory]
        [InlineData(100, "Normal")]
        [InlineData(80, "Normal")]
        [InlineData(79.9, "Service recommended")]
        [InlineData(65, "Service recommended")]
        [InlineData(64.9, "Replace soon")]
        [InlineData(-1, "Unknown")]
        public void Health_verdict_follows_the_eighty_percent_line(double percent, string expected)
        {
            Assert.Equal(expected, BatteryEstimate.HealthVerdict(percent));
        }

        [Fact]
        public void Format_is_readable_and_culture_independent()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // The development machine runs th-TH, where a careless format would also swap
                // the calendar and the digits.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");

                Assert.Equal("3 h 12 min", BatteryEstimate.Format(TimeSpan.FromMinutes(192)));
                Assert.Equal("48 min", BatteryEstimate.Format(TimeSpan.FromMinutes(48)));
                Assert.Equal("—", BatteryEstimate.Format(null));
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }
    }

    /// <summary>
    /// Boot performance payload parsing. Every field here is read by name because the payload
    /// interleaves each value with its own length field, and the ordering has moved between
    /// Windows releases.
    /// </summary>
    public class BootEventParserTests
    {
        /// <summary>Field names and shape copied from a real event 101 on this machine.</summary>
        private const string DelayXml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><EventID>101</EventID></System>
              <EventData>
                <Data Name="StartTime">2026-08-23T01:09:50.0758459Z</Data>
                <Data Name="NameLength">19</Data>
                <Data Name="Name">Example Overlay.exe</Data>
                <Data Name="FriendlyNameLength">11</Data>
                <Data Name="FriendlyName">Example App</Data>
                <Data Name="TotalTime">6701</Data>
                <Data Name="DegradationTime">1701</Data>
                <Data Name="ProductNameLength">11</Data>
                <Data Name="ProductName">Example App</Data>
                <Data Name="CompanyNameLength">7</Data>
                <Data Name="CompanyName">Example</Data>
              </EventData>
            </Event>
            """;

        [Fact]
        public void Reads_fields_by_name()
        {
            var fields = BootEventParser.ReadFields(DelayXml);

            Assert.Equal("6701", fields["TotalTime"]);
            Assert.Equal("Example Overlay.exe", fields["Name"]);
            Assert.Equal("19", fields["NameLength"]);
        }

        /// <summary>
        /// The regression this guards: a positional reader would take NameLength (19) as the
        /// duration and report a 19 ms delay for something that cost 6.7 seconds.
        /// </summary>
        [Fact]
        public void Duration_is_the_duration_not_an_adjacent_length_field()
        {
            var delay = BootEventParser.ParseDelay(101, BootEventParser.ReadFields(DelayXml), DateTime.Now);

            Assert.NotNull(delay);
            Assert.Equal(6701, delay!.TotalMs);
            Assert.Equal(1701, delay.DegradationMs);
            Assert.Equal("Example App", delay.DisplayName);
            Assert.Equal(StartupDelayKind.Application, delay.Kind);
        }

        [Fact]
        public void Boot_event_reads_the_headline_numbers()
        {
            const string bootXml = """
                <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
                  <EventData>
                    <Data Name="BootTime">117822</Data>
                    <Data Name="MainPathBootTime">42000</Data>
                    <Data Name="BootPostBootTime">75822</Data>
                    <Data Name="BootNumStartupApps">25</Data>
                    <Data Name="BootIsDegradation">1</Data>
                  </EventData>
                </Event>
                """;

            var boot = BootEventParser.ParseBoot(BootEventParser.ReadFields(bootXml),
                                                 new DateTime(2026, 8, 23, 8, 12, 23));

            Assert.NotNull(boot);
            Assert.Equal(117822, boot!.BootTimeMs);
            Assert.Equal(25, boot.StartupAppCount);
            Assert.True(boot.IsDegradation);
            Assert.Equal(117.822, boot.Seconds, 3);
            Assert.Equal("117.8 s", boot.SecondsText);
        }

        [Fact]
        public void Payload_without_a_duration_is_not_a_record()
        {
            Assert.Null(BootEventParser.ParseBoot(new Dictionary<string, string>(), DateTime.Now));
            Assert.Null(BootEventParser.ParseDelay(101,
                new Dictionary<string, string> { ["Name"] = "x" }, DateTime.Now));
        }

        [Theory]
        [InlineData("")]
        [InlineData("<not-xml")]
        public void Unparseable_xml_yields_no_fields(string xml)
        {
            Assert.Empty(BootEventParser.ReadFields(xml));
        }

        [Theory]
        [InlineData(101, StartupDelayKind.Application)]
        [InlineData(102, StartupDelayKind.Driver)]
        [InlineData(103, StartupDelayKind.Service)]
        [InlineData(106, StartupDelayKind.Other)]
        public void Event_ids_map_to_kinds(int id, StartupDelayKind expected)
        {
            Assert.Equal(expected, BootEventParser.KindOf(id));
        }

        [Fact]
        public void Trend_compares_the_latest_boot_against_the_rest()
        {
            var analysis = new BootAnalysis
            {
                Boots = new[]
                {
                    Boot(120_000, 3),   // newest
                    Boot(80_000, 2),
                    Boot(80_000, 1),
                },
            };

            Assert.Equal(93.333, analysis.AverageSeconds, 2);
            Assert.Equal(40d, analysis.TrendSeconds, 2);   // 120 s against an 80 s average
        }

        [Fact]
        public void A_single_boot_has_no_trend()
        {
            var analysis = new BootAnalysis { Boots = new[] { Boot(90_000, 1) } };
            Assert.Equal(0d, analysis.TrendSeconds);
        }

        /// <summary>
        /// Windows logs these records twice. Confirmed on the development machine, where
        /// event 100 appears with an identical timestamp and boot time, and each delay record
        /// likewise — the boot list showed "NVIDIA App 6.70 s" twice before this was handled,
        /// reading as two separate problems.
        /// </summary>
        [Fact]
        public void Duplicate_records_collapse_to_one_finding()
        {
            var first = new StartupDelay(StartupDelayKind.Application, "app.exe", "App",
                                         "Vendor", 6701, 1701, new DateTime(2026, 8, 23, 8, 12, 0));

            // The second copy arrives a moment later with a different event timestamp; it is
            // still the same finding.
            var duplicate = first with { At = first.At.AddMilliseconds(40) };
            var different = first with { TotalMs = 4250 };

            Assert.Equal(BootAnalyzer.DelayKey(first), BootAnalyzer.DelayKey(duplicate));
            Assert.NotEqual(BootAnalyzer.DelayKey(first), BootAnalyzer.DelayKey(different));
        }

        [Fact]
        public void Different_components_are_not_collapsed()
        {
            var app = new StartupDelay(StartupDelayKind.Application, "a.exe", "A", "V", 1000, 0, DateTime.Now);
            var driver = app with { Kind = StartupDelayKind.Driver };
            var other = app with { Name = "b.exe", FriendlyName = "B" };

            Assert.NotEqual(BootAnalyzer.DelayKey(app), BootAnalyzer.DelayKey(driver));
            Assert.NotEqual(BootAnalyzer.DelayKey(app), BootAnalyzer.DelayKey(other));
        }

        private static BootRecord Boot(int ms, int day) =>
            new(new DateTime(2026, 8, day), ms, 0, 0, 0, false);
    }

    /// <summary>
    /// The startup on/off switch. This writes to the registry, so the encoding is pinned
    /// tightly — a wrong byte here changes what launches on someone's machine.
    /// </summary>
    public class StartupApprovalTests
    {
        [Theory]
        [InlineData(0x02, true)]    // per-user, enabled
        [InlineData(0x03, false)]   // per-user, disabled
        [InlineData(0x06, true)]    // machine-wide, enabled
        [InlineData(0x07, false)]   // machine-wide, disabled
        public void Flag_is_the_low_bit(byte first, bool expected)
        {
            var value = new byte[12];
            value[0] = first;
            Assert.Equal(expected, StartupApproval.IsEnabled(value));
        }

        [Fact]
        public void Absent_value_means_it_runs()
        {
            Assert.True(StartupApproval.IsEnabled(null));
            Assert.True(StartupApproval.IsEnabled(Array.Empty<byte>()));
        }

        /// <summary>
        /// The bug this exists to prevent. Both byte families are live on this machine:
        /// HKCU entries read 0x02/0x03 and HKLM entries read 0x06/0x07. Hardcoding 0x03 as
        /// "disabled" would rewrite a machine entry's flag into an unrelated value.
        /// </summary>
        [Theory]
        [InlineData(0x02, false, 0x03)]
        [InlineData(0x03, true, 0x02)]
        [InlineData(0x06, false, 0x07)]
        [InlineData(0x07, true, 0x06)]
        public void Encoding_preserves_the_base_byte(byte existing, bool enable, byte expected)
        {
            var prior = new byte[12];
            prior[0] = existing;

            var written = StartupApproval.Encode(enable, prior, DateTime.UtcNow);
            Assert.Equal(expected, written[0]);
            Assert.Equal(12, written.Length);
        }

        [Fact]
        public void A_never_touched_entry_gets_the_per_user_default()
        {
            var written = StartupApproval.Encode(enabled: false, existing: null, DateTime.UtcNow);
            Assert.Equal(0x03, written[0]);
        }

        [Fact]
        public void Disable_stamps_the_time_and_enable_clears_it()
        {
            var when = new DateTime(2026, 8, 23, 9, 30, 0, DateTimeKind.Utc);

            var off = StartupApproval.Encode(false, null, when);
            var recovered = StartupApproval.DisabledAtUtc(off);
            Assert.NotNull(recovered);
            Assert.Equal(when, recovered!.Value);

            var on = StartupApproval.Encode(true, off, when);
            Assert.Null(StartupApproval.DisabledAtUtc(on));
            for (int i = 4; i < 12; i++) Assert.Equal(0, on[i]);
        }

        [Fact]
        public void Summary_counts_only_what_will_run()
        {
            var entries = new[]
            {
                new StartupEntry("A", "a.exe", "Registry (this user)", StartupScope.CurrentUser, true),
                new StartupEntry("B", "b.exe", "Registry (this user)", StartupScope.CurrentUser, false),
                new StartupEntry("C", "c.exe", "Registry (all users)", StartupScope.Machine, true),
            };

            Assert.Equal(2, StartupEntries.CountEnabled(entries));
            Assert.Equal("2 of 3 enabled", StartupEntries.Summarise(entries));
        }

        [Fact]
        public void Machine_entries_are_refused_rather_than_half_applied()
        {
            var entry = new StartupEntry("C", "c.exe", "Registry (all users)", StartupScope.Machine, true);
            string? problem = StartupEntries.SetEnabled(entry, false);

            Assert.NotNull(problem);
            Assert.Contains("administrator", problem!, StringComparison.OrdinalIgnoreCase);
            Assert.False(entry.CanToggle);
        }
    }

    /// <summary>
    /// The slowdown trigger. Driven entirely by the timestamps on the frames, so a whole
    /// afternoon of readings runs in a millisecond and the firing tick is exact.
    /// </summary>
    public class SlowdownDetectorTests
    {
        private static readonly DateTime Start = new(2026, 8, 23, 10, 0, 0);

        private static SlowdownFrame Frame(int second, float cpu = 0f, long disk = 0, float ram = 0f) =>
            new(Start.AddSeconds(second), cpu, ram, disk,
                Array.Empty<ProcessUsage>(), Array.Empty<ProcessUsage>(), Array.Empty<ProcessUsage>());

        private static SlowdownThresholds Thresholds(int sustain = 8) =>
            new(CpuPercent: 90f, DiskBytesPerSec: 150L * 1024 * 1024, MemoryPercent: 92f, SustainSeconds: sustain);

        [Fact]
        public void A_brief_spike_does_not_fire()
        {
            var detector = new SlowdownDetector();
            var thresholds = Thresholds();

            for (int s = 0; s < 7; s++)
                Assert.Equal(SlowdownCause.None, detector.Feed(Frame(s, cpu: 99f), thresholds));
        }

        [Fact]
        public void Fires_once_the_run_reaches_the_sustain_window()
        {
            var detector = new SlowdownDetector();
            var thresholds = Thresholds(sustain: 8);

            for (int s = 0; s < 8; s++) detector.Feed(Frame(s, cpu: 99f), thresholds);

            // The run started at second 0, so second 8 is the first frame eight seconds in.
            Assert.Equal(SlowdownCause.Cpu, detector.Feed(Frame(8, cpu: 99f), thresholds));
        }

        [Fact]
        public void A_recovery_resets_the_run()
        {
            var detector = new SlowdownDetector();
            var thresholds = Thresholds(sustain: 8);

            for (int s = 0; s < 7; s++) detector.Feed(Frame(s, cpu: 99f), thresholds);
            detector.Feed(Frame(7, cpu: 10f), thresholds);            // calmed down
            for (int s = 8; s < 15; s++)
                Assert.Equal(SlowdownCause.None, detector.Feed(Frame(s, cpu: 99f), thresholds));
        }

        /// <summary>
        /// When the CPU and the disk are both saturated, the stall a person feels is the one
        /// waiting on I/O, so that is what the report is titled.
        /// </summary>
        [Fact]
        public void Disk_contention_outranks_cpu()
        {
            var detector = new SlowdownDetector();
            var thresholds = Thresholds(sustain: 2);

            detector.Feed(Frame(0, cpu: 99f, disk: 600L * 1024 * 1024), thresholds);
            detector.Feed(Frame(1, cpu: 99f, disk: 600L * 1024 * 1024), thresholds);
            Assert.Equal(SlowdownCause.Disk,
                detector.Feed(Frame(2, cpu: 99f, disk: 600L * 1024 * 1024), thresholds));
        }

        [Fact]
        public void The_cooldown_prevents_recording_one_episode_repeatedly()
        {
            var detector = new SlowdownDetector { Cooldown = TimeSpan.FromMinutes(10) };
            var thresholds = Thresholds(sustain: 2);

            for (int s = 0; s <= 2; s++) detector.Feed(Frame(s, cpu: 99f), thresholds);

            // Still pinned five minutes later — inside the cooldown, so it stays quiet.
            for (int s = 300; s < 320; s++)
                Assert.Equal(SlowdownCause.None, detector.Feed(Frame(s, cpu: 99f), thresholds));
        }

        [Fact]
        public void After_the_cooldown_a_new_episode_can_be_recorded()
        {
            var detector = new SlowdownDetector { Cooldown = TimeSpan.FromMinutes(10) };
            var thresholds = Thresholds(sustain: 2);

            for (int s = 0; s <= 2; s++) detector.Feed(Frame(s, cpu: 99f), thresholds);

            bool firedAgain = false;
            for (int s = 700; s < 720 && !firedAgain; s++)
                firedAgain = detector.Feed(Frame(s, cpu: 99f), thresholds) != SlowdownCause.None;

            Assert.True(firedAgain);
        }

        [Fact]
        public void An_idle_machine_never_fires()
        {
            var detector = new SlowdownDetector();
            var thresholds = Thresholds();

            for (int s = 0; s < 600; s++)
                Assert.Equal(SlowdownCause.None,
                    detector.Feed(Frame(s, cpu: 4f, disk: 2048, ram: 40f), thresholds));
        }
    }

    public class SlowdownReportTests
    {
        private static SlowdownFrame Frame(int second, params ProcessUsage[] disk) =>
            new(new DateTime(2026, 8, 23, 10, 0, 0).AddSeconds(second), 96f, 71f,
                SumDisk(disk), Array.Empty<ProcessUsage>(), disk, Array.Empty<ProcessUsage>());

        private static long SumDisk(ProcessUsage[] all)
        {
            long t = 0;
            foreach (var p in all) t += p.DiskBytesPerSec;
            return t;
        }

        private static ProcessUsage Hog(string name, long bytesPerSecond) =>
            new(name, 1234, 3f, 100L * 1024 * 1024) { DiskReadBytesPerSec = bytesPerSecond };

        [Fact]
        public void Report_names_the_culprit_and_shows_the_timeline()
        {
            var frames = new List<SlowdownFrame>();
            for (int s = 0; s < 5; s++) frames.Add(Frame(s, Hog("backup-agent.exe", 549L * 1024 * 1024)));

            string text = SlowdownReportWriter.Write(frames, SlowdownCause.Disk,
                SlowdownThresholds.Default, "1.6.0");

            Assert.Contains("MicaStats Slowdown Report", text, StringComparison.Ordinal);
            Assert.Contains("backup-agent.exe", text, StringComparison.Ordinal);
            Assert.Contains("Worst offenders", text, StringComparison.Ordinal);
            Assert.Contains("2026-08-23 10:00:00", text, StringComparison.Ordinal);
            Assert.Contains("549 MB/s", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// This machine runs th-TH with a Buddhist calendar, where a locale-default format
        /// stamps the year 2569. The reports must be readable by anyone, in any locale.
        /// </summary>
        [Fact]
        public void Timestamps_are_gregorian_regardless_of_locale()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");

                string text = SlowdownReportWriter.Write(
                    new[] { Frame(0, Hog("x.exe", 1024)) }, SlowdownCause.Manual,
                    SlowdownThresholds.Default, "1.6.0");

                Assert.Contains("2026-08-23", text, StringComparison.Ordinal);
                Assert.DoesNotContain("2569", text, StringComparison.Ordinal);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        [Fact]
        public void An_empty_window_says_so_instead_of_throwing()
        {
            string text = SlowdownReportWriter.Write(Array.Empty<SlowdownFrame>(),
                SlowdownCause.Manual, SlowdownThresholds.Default, "1.6.0");

            Assert.Contains("No samples", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Headline_names_the_process_that_was_moving_data()
        {
            var frames = new[] { Frame(0, Hog("backup-agent.exe", 549L * 1024 * 1024)) };
            string headline = SlowdownReportWriter.Headline(frames, SlowdownCause.Disk);

            Assert.Contains("backup-agent.exe", headline, StringComparison.Ordinal);
            Assert.Contains("549 MB/s", headline, StringComparison.Ordinal);
        }
    }

    public class AlertEvaluatorTests
    {
        private static readonly DateTime Start = new(2026, 8, 23, 10, 0, 0);

        private static AlertRule Hot(int sustain = 30) =>
            new("cpu-temp", "CPU temperature", AlertMetric.CpuTemperature, 95d,
                Above: true, SustainSeconds: sustain, Enabled: true) { ClearMargin = 5d };

        private static AlertRule LowSpace(int sustain = 60) =>
            new("disk-free", "Free space", AlertMetric.DiskSpaceFree, 10d,
                Above: false, SustainSeconds: sustain, Enabled: true) { ClearMargin = 2d };

        [Fact]
        public void Raises_only_after_the_reading_has_held()
        {
            var evaluator = new AlertEvaluator();
            var rule = Hot(sustain: 30);

            Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 97d, Start));
            Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 97d, Start.AddSeconds(29)));
            Assert.Equal(AlertTransition.Raised, evaluator.Feed(rule, 97d, Start.AddSeconds(30)));
        }

        [Fact]
        public void Raises_once_not_on_every_tick()
        {
            var evaluator = new AlertEvaluator();
            var rule = Hot(sustain: 1);

            evaluator.Feed(rule, 97d, Start);
            Assert.Equal(AlertTransition.Raised, evaluator.Feed(rule, 97d, Start.AddSeconds(1)));

            for (int s = 2; s < 60; s++)
                Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 97d, Start.AddSeconds(s)));
        }

        /// <summary>
        /// Without hysteresis a reading sitting on the threshold alternates raise and clear
        /// every second, which is how an alert feature gets switched off and stops helping.
        /// </summary>
        [Fact]
        public void Clears_only_after_recovering_past_the_margin()
        {
            var evaluator = new AlertEvaluator();
            var rule = Hot(sustain: 1);

            evaluator.Feed(rule, 97d, Start);
            evaluator.Feed(rule, 97d, Start.AddSeconds(1));
            Assert.True(evaluator.IsFiring("cpu-temp"));

            // Back under the threshold but inside the 5-degree margin: still considered hot.
            Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 94d, Start.AddSeconds(2)));
            Assert.True(evaluator.IsFiring("cpu-temp"));

            Assert.Equal(AlertTransition.Cleared, evaluator.Feed(rule, 89d, Start.AddSeconds(3)));
            Assert.False(evaluator.IsFiring("cpu-temp"));
        }

        /// <summary>
        /// An unreadable sensor reports -1 upstream and NaN here. Treating that as zero would
        /// make a missing probe look like a cold CPU, and a missing free-space figure look
        /// like a full disk.
        /// </summary>
        [Fact]
        public void An_unreadable_sensor_never_fires()
        {
            var evaluator = new AlertEvaluator();
            var rule = LowSpace(sustain: 0);

            for (int s = 0; s < 100; s++)
                Assert.Equal(AlertTransition.None, evaluator.Feed(rule, double.NaN, Start.AddSeconds(s)));
        }

        [Fact]
        public void Below_rules_fire_when_the_reading_falls()
        {
            var evaluator = new AlertEvaluator();
            var rule = LowSpace(sustain: 60);

            Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 40d, Start));
            Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 8d, Start.AddSeconds(1)));
            Assert.Equal(AlertTransition.Raised, evaluator.Feed(rule, 8d, Start.AddSeconds(61)));
        }

        [Fact]
        public void A_disabled_rule_is_inert()
        {
            var evaluator = new AlertEvaluator();
            var rule = Hot(sustain: 0) with { Enabled = false };

            for (int s = 0; s < 50; s++)
                Assert.Equal(AlertTransition.None, evaluator.Feed(rule, 120d, Start.AddSeconds(s)));
        }

        [Fact]
        public void Message_reads_as_a_sentence_in_any_locale()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                var alert = new AlertEvent(Hot(), 97.5d, "", Start);

                Assert.Contains("97.5°C", alert.Message, StringComparison.Ordinal);
                Assert.Contains("95°C", alert.Message, StringComparison.Ordinal);
                Assert.Equal("CPU temperature", alert.Title);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        [Fact]
        public void Free_space_alerts_name_the_drive()
        {
            var alert = new AlertEvent(LowSpace(), 4.2d, "0 C:", Start);
            Assert.Contains("0 C:", alert.Message, StringComparison.Ordinal);
            Assert.Contains("4.2 GB left", alert.Message, StringComparison.Ordinal);
        }
    }

    public class AlertRuleSettingsTests
    {
        [Fact]
        public void Round_trips_through_the_config_line()
        {
            var rules = AlertRuleSettings.Parse(null);
            rules[0] = rules[0] with { Enabled = false, Threshold = 88.5d, SustainSeconds = 45 };

            var restored = AlertRuleSettings.Parse(AlertRuleSettings.Serialize(rules));

            Assert.False(restored[0].Enabled);
            Assert.Equal(88.5d, restored[0].Threshold);
            Assert.Equal(45, restored[0].SustainSeconds);
        }

        /// <summary>
        /// A config written under a comma-decimal locale must not become unreadable to the
        /// invariant parse that reads it back.
        /// </summary>
        [Fact]
        public void Serialisation_is_culture_independent()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");   // comma decimals

                var rules = AlertRuleSettings.Parse(null);
                rules[0] = rules[0] with { Threshold = 88.5d };
                string line = AlertRuleSettings.Serialize(rules);

                Assert.Contains("88.5", line, StringComparison.Ordinal);
                Assert.Equal(88.5d, AlertRuleSettings.Parse(line)[0].Threshold);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        [Fact]
        public void Unknown_ids_are_ignored_and_missing_ones_keep_defaults()
        {
            var rules = AlertRuleSettings.Parse("not-a-rule:1:5:5;cpu-temp:0:70:10");

            Assert.Equal(AlertRule.Defaults.Count, rules.Count);

            var cpu = rules.Find(r => r.Id == "cpu-temp");
            Assert.NotNull(cpu);
            Assert.False(cpu!.Enabled);
            Assert.Equal(70d, cpu.Threshold);

            // Untouched rules keep whatever the build shipped.
            var disk = rules.Find(r => r.Id == "disk-free");
            var shipped = AlertRule.Defaults[1];
            Assert.NotNull(disk);
            Assert.Equal(shipped.Threshold, disk!.Threshold);
            Assert.Equal(shipped.Enabled, disk.Enabled);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("garbage")]
        [InlineData("::::")]
        public void Unusable_lines_fall_back_to_defaults(string? line)
        {
            Assert.Equal(AlertRule.Defaults.Count, AlertRuleSettings.Parse(line).Count);
        }
    }

    public class ProcessDiskRateTests
    {
        [Fact]
        public void Rate_is_bytes_per_second()
        {
            Assert.Equal(500, ProcessSampler.Rate(current: 1500, previous: 1000, elapsedSeconds: 1d));
            Assert.Equal(250, ProcessSampler.Rate(current: 1500, previous: 1000, elapsedSeconds: 2d));
        }

        /// <summary>
        /// Kernel totals only ever climb. A decrease means the counter wrapped or the process
        /// was replaced, and a negative rate would sort straight to the top of the disk table.
        /// </summary>
        [Fact]
        public void A_decrease_reports_nothing_rather_than_a_negative_rate()
        {
            Assert.Equal(0, ProcessSampler.Rate(current: 500, previous: 1000, elapsedSeconds: 1d));
            Assert.Equal(0, ProcessSampler.Rate(current: 1500, previous: 1000, elapsedSeconds: 0d));
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(512, "512 B/s")]
        [InlineData(2048, "2 KB/s")]
        [InlineData(548919988, "523 MB/s")]
        public void Rates_render_in_a_readable_unit(long bytesPerSecond, string expected)
        {
            Assert.Equal(expected, ProcessUsage.FormatRate(bytesPerSecond));
        }

        [Fact]
        public void Disk_total_is_read_plus_write()
        {
            var usage = new ProcessUsage("x.exe", 1, 0f, 0)
            {
                DiskReadBytesPerSec = 1000,
                DiskWriteBytesPerSec = 24,
            };
            Assert.Equal(1024, usage.DiskBytesPerSec);
            Assert.Equal("1 KB/s", usage.DiskText);
        }
    }

    public class DiagnosticsReportTests
    {
        [Fact]
        public void Report_covers_every_section_even_when_nothing_is_available()
        {
            string text = DiagnosticsService.BuildReport(null, null, null, null, null, "1.6.0");

            Assert.Contains("[Boot]", text, StringComparison.Ordinal);
            Assert.Contains("[Battery]", text, StringComparison.Ordinal);
            Assert.Contains("[Alerts]", text, StringComparison.Ordinal);
            Assert.Contains("No battery on this machine", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Report_states_the_boot_time_and_the_worst_offender()
        {
            var boot = new BootAnalysis
            {
                Boots = new[] { new BootRecord(new DateTime(2026, 8, 23, 8, 12, 0), 117822, 0, 0, 25, true) },
                Delays = new[]
                {
                    new StartupDelay(StartupDelayKind.Application, "Example Overlay.exe",
                                     "Example App", "Example", 6701, 1701, DateTime.Now),
                },
                Entries = new[]
                {
                    new StartupEntry("A", "a.exe", "Registry (this user)", StartupScope.CurrentUser, true),
                },
            };

            string text = DiagnosticsService.BuildReport(boot, null, null,
                AlertRule.Defaults, Array.Empty<SavedReport>(), "1.6.0");

            Assert.Contains("117.8 s", text, StringComparison.Ordinal);
            Assert.Contains("Example App", text, StringComparison.Ordinal);
            Assert.Contains("6.70 s", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Battery_section_gives_the_verdict_windows_withholds()
        {
            var health = new BatteryHealth(new[]
            {
                new BatteryPack("ACME PACK-1", "LiP", 90000, 63000, 480),
            });

            string text = DiagnosticsService.BuildReport(null, health, null, null, null, "1.6.0");

            Assert.Contains("70.0%", text, StringComparison.Ordinal);
            Assert.Contains("Service recommended", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Battery_rows_report_our_estimate_and_whether_windows_had_one()
        {
            var health = new BatteryHealth(new[] { new BatteryPack("P", "LiP", 90000, 90000, 2) });
            var reading = new BatteryReading(true, false, false, true, 80, 72000, 24000, 13272);

            var groups = DiagnosticsService.BuildBattery(health, reading, osEstimate: null);
            string flat = string.Join("\n", groups.ConvertAll(g =>
                g.Title + ": " + string.Join(", ", g.Rows.ConvertAll(r => r.Label + "=" + r.Value))));

            Assert.Contains("3 h 0 min", flat, StringComparison.Ordinal);   // 72000 mWh at 24 W
            Assert.Contains("Not available", flat, StringComparison.Ordinal);
            Assert.Contains("24.0 W", flat, StringComparison.Ordinal);
        }
    }
}
