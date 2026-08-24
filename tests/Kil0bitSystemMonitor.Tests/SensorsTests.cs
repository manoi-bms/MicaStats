using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Services.Sensors;
using Kil0bitSystemMonitor.Services.Sensors.Publishers;
using Kil0bitSystemMonitor.ViewModels;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The sensor stack exists because CPU die temperature is ring-0 territory and MicaStats
    /// runs unelevated. What it can reach instead — the ACPI thermal zone, the display
    /// kernel's per-adapter block, and whatever a monitoring tool publishes — all report
    /// plausible-looking Celsius values, and only the last of them is actually the die.
    ///
    /// <para>
    /// These tests exist mostly to keep that distinction from eroding. The zone was measured
    /// moving in opposite directions under identical CPU load depending on whether the fans
    /// were already running, so presenting it as a CPU temperature would be worse than
    /// presenting nothing.
    /// </para>
    /// </summary>
    public class SensorsTests
    {
        [Fact]
        public void A_reading_is_not_a_cpu_die_reading_unless_it_says_so()
        {
            var r = new SensorReading("zone.0", "System", SensorCategory.Temperature, 69.0, "°C", "ACPI");
            Assert.False(r.IsCpuDie);
        }

        [Fact]
        public void A_publisher_may_mark_a_reading_as_cpu_die()
        {
            var r = new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature,
                                      71.0, "°C", "Core Temp", IsCpuDie: true);
            Assert.True(r.IsCpuDie);
        }

        /// <summary>
        /// These two assertions are why the shipped D3DKMT path failed silently for so long.
        /// The kernel validates PrivateDriverDataSize, and a wrong size is rejected with the
        /// same STATUS_INVALID_PARAMETER as a wrong query type — so a layout that merely looks
        /// plausible is indistinguishable from a wrong constant. Both were wrong at once.
        /// </summary>
        [Fact]
        public void The_adapter_perf_block_matches_the_measured_layout()
        {
            Assert.Equal(64, Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>());
            Assert.Equal(56, Marshal.OffsetOf<D3DKMT_ADAPTER_PERFDATA>(
                nameof(D3DKMT_ADAPTER_PERFDATA.Temperature)).ToInt32());
            Assert.Equal(48, Marshal.OffsetOf<D3DKMT_ADAPTER_PERFDATA>(
                nameof(D3DKMT_ADAPTER_PERFDATA.FanRPM)).ToInt32());
            Assert.Equal(62, AdapterPerfData.QueryTypePerfData);
        }

        /// <summary>
        /// Runs on whatever hardware is present, including none. The contract is that the
        /// source never throws and never invents a reading — a driver that fills only some
        /// fields must not produce a confident 0°C.
        /// </summary>
        [Fact]
        public void The_adapter_source_never_throws_and_reports_only_plausible_temperatures()
        {
            var readings = new AdapterPerfSource().Read();

            foreach (var r in readings)
            {
                Assert.False(r.IsCpuDie);
                if (r.Category == SensorCategory.Temperature)
                    Assert.InRange(r.Value, 1.0, 150.0);
            }
        }

        /// <summary>
        /// Registry names are written for Device Manager, not for a 372px card. Trimming has
        /// to be conservative: the model number is the part that identifies the adapter, so it
        /// must survive, and a name made entirely of boilerplate must not become empty.
        /// </summary>
        [Fact]
        public void Adapter_names_lose_vendor_boilerplate_but_keep_the_model()
        {
            Assert.Equal("AMD Radeon 890M",
                AdapterPerfSource.ShortAdapterName("AMD Radeon(TM) 890M Graphics"));
            Assert.Equal("NVIDIA RTX PRO 1000 Blackwell",
                AdapterPerfSource.ShortAdapterName("NVIDIA RTX PRO 1000 Blackwell Generation Laptop GPU"));
            Assert.Equal("Intel Iris Xe",
                AdapterPerfSource.ShortAdapterName("Intel(R) Iris Xe Graphics"));

            // Nothing to trim, and nothing to lose.
            Assert.Equal("Basic Display Adapter",
                AdapterPerfSource.ShortAdapterName("Basic Display Adapter"));
            Assert.Equal("Graphics", AdapterPerfSource.ShortAdapterName("Graphics"));
            Assert.Equal("", AdapterPerfSource.ShortAdapterName(""));
        }

        [Fact]
        public void Deci_kelvin_converts_to_celsius()
        {
            // 3432 dK was the live reading when this was measured: 343.2K = 70.05C.
            Assert.Equal(70.05, ThermalZoneSource.DeciKelvinToCelsius(3432), 2);
            Assert.Equal(0.0, ThermalZoneSource.DeciKelvinToCelsius(2731.5), 1);
        }

        /// <summary>
        /// The zone reads like a CPU temperature and is not one. Under two load ramps from
        /// different thermal starting points it rose 64°C to 71°C from cold with the fans
        /// idle, then fell 72°C to 69°C from warm with the fans already running — the same
        /// 24-thread load, opposite directions, because it sits downstream of the fan control
        /// loop. Anything that lets it reach the CPU readout is a bug.
        /// </summary>
        [Fact]
        public void The_zone_never_claims_to_be_the_cpu_die()
        {
            foreach (var r in new ThermalZoneSource().Read()) Assert.False(r.IsCpuDie);
        }

        /// <summary>
        /// The test above passes vacuously if Read() swallows an exception and returns
        /// nothing, which is exactly what a mistyped counter name would do. On a machine that
        /// has a zone, insist a reading actually comes out.
        /// </summary>
        [Fact]
        public void A_machine_with_a_thermal_zone_actually_produces_a_reading()
        {
            if (!System.Diagnostics.PerformanceCounterCategory.Exists("Thermal Zone Information"))
                return;   // no zone on this machine — nothing to prove

            var readings = new ThermalZoneSource().Read();
            Assert.Contains(readings, r => r.Category == SensorCategory.Temperature);
        }

        // ---------------------------------------------------------------- Core Temp
        //
        // Decoding is separated from acquisition so the block can be exercised from a
        // synthetic array. Core Temp is not installed here, and a decoder that can only be
        // tested on a machine that happens to run the tool is a decoder that never gets
        // tested at all.

        [Fact]
        public void Core_temp_block_decodes_the_hottest_core()
        {
            var block = new byte[2686];
            BitConverter.GetBytes(4u).CopyTo(block, 1536);   // uiCoreCnt
            BitConverter.GetBytes(1u).CopyTo(block, 1540);   // uiCpuCnt
            BitConverter.GetBytes(61.0f).CopyTo(block, 1544);
            BitConverter.GetBytes(74.5f).CopyTo(block, 1548);
            BitConverter.GetBytes(58.0f).CopyTo(block, 1552);
            BitConverter.GetBytes(66.0f).CopyTo(block, 1556);

            Assert.Equal(74.5, CoreTempSource.DecodeHottest(block), 1);
        }

        [Fact]
        public void Core_temp_block_handles_delta_to_tjmax()
        {
            var block = new byte[2686];
            BitConverter.GetBytes(2u).CopyTo(block, 1536);
            BitConverter.GetBytes(1u).CopyTo(block, 1540);
            BitConverter.GetBytes(100u).CopyTo(block, 1024);  // TjMax = 100C
            BitConverter.GetBytes(40.0f).CopyTo(block, 1544); // 40 below TjMax = 60C
            BitConverter.GetBytes(25.0f).CopyTo(block, 1548); // 25 below TjMax = 75C
            block[2685] = 1;                                  // ucDeltaToTjMax

            Assert.Equal(75.0, CoreTempSource.DecodeHottest(block), 1);
        }

        [Fact]
        public void Core_temp_block_rejects_an_implausible_core_count()
        {
            var block = new byte[2686];
            BitConverter.GetBytes(9999u).CopyTo(block, 1536);
            Assert.Equal(-1, CoreTempSource.DecodeHottest(block), 1);
        }

        // ------------------------------------------------------------------ HWiNFO

        /// <summary>Builds a synthetic block, so the decoder is testable with HWiNFO absent.</summary>
        private static byte[] HwInfoBlock(params (uint type, string label, double value)[] items)
        {
            const int headerSize = 48, elementSize = 320;
            var block = new byte[headerSize + elementSize * items.Length];

            BitConverter.GetBytes(0x53695748u).CopyTo(block, 0);
            BitConverter.GetBytes((uint)headerSize).CopyTo(block, 36);   // offset of readings
            BitConverter.GetBytes((uint)elementSize).CopyTo(block, 40);  // stride
            BitConverter.GetBytes((uint)items.Length).CopyTo(block, 44);

            for (int i = 0; i < items.Length; i++)
            {
                int at = headerSize + i * elementSize;
                BitConverter.GetBytes(items[i].type).CopyTo(block, at);
                System.Text.Encoding.ASCII.GetBytes(items[i].label).CopyTo(block, at + 12);
                BitConverter.GetBytes(items[i].value).CopyTo(block, at + 288);
            }
            return block;
        }

        [Fact]
        public void Hwinfo_block_picks_the_hottest_cpu_temperature()
        {
            var block = HwInfoBlock(
                (1u, "CPU Package", 71.0),
                (1u, "CPU Core Max", 78.5),
                (1u, "GPU Temperature", 91.0),   // hotter, but not the CPU
                (3u, "CPU Fan", 2400.0));        // a fan, not a temperature

            Assert.Equal(78.5, HwInfoSource.DecodeCpuTemperature(block), 1);
        }

        [Fact]
        public void Hwinfo_block_with_a_wrong_signature_is_refused()
        {
            var block = HwInfoBlock((1u, "CPU Package", 71.0));
            BitConverter.GetBytes(0xDEADBEEFu).CopyTo(block, 0);

            Assert.Equal(-1, HwInfoSource.DecodeCpuTemperature(block), 1);
        }

        [Fact]
        public void Hwinfo_block_with_no_cpu_temperature_returns_unavailable()
        {
            Assert.Equal(-1, HwInfoSource.DecodeCpuTemperature(
                HwInfoBlock((1u, "GPU Temperature", 91.0))), 1);
        }

        /// <summary>
        /// The header declares its own stride, and HWiNFO documents reading it rather than
        /// assuming the element size. A revision that grows the element must not shift every
        /// field read after the first.
        /// </summary>
        [Fact]
        public void Hwinfo_decoder_honours_a_larger_stride_declared_by_the_header()
        {
            const int headerSize = 48, elementSize = 384;   // wider than today's 320
            var block = new byte[headerSize + elementSize * 2];

            BitConverter.GetBytes(0x53695748u).CopyTo(block, 0);
            BitConverter.GetBytes((uint)headerSize).CopyTo(block, 36);
            BitConverter.GetBytes((uint)elementSize).CopyTo(block, 40);
            BitConverter.GetBytes(2u).CopyTo(block, 44);

            foreach (var (i, label, value) in new[] { (0, "CPU Package", 55.0), (1, "CPU Core Max", 81.0) })
            {
                int at = headerSize + i * elementSize;
                BitConverter.GetBytes(1u).CopyTo(block, at);
                System.Text.Encoding.ASCII.GetBytes(label).CopyTo(block, at + 12);
                BitConverter.GetBytes(value).CopyTo(block, at + 288);
            }

            Assert.Equal(81.0, HwInfoSource.DecodeCpuTemperature(block), 1);
        }

        // ------------------------------------------------------------- MSI Afterburner

        private static byte[] AfterburnerBlock(params (string name, float value)[] items)
        {
            const int headerSize = 32, entrySize = 1324;
            var block = new byte[headerSize + entrySize * items.Length];

            BitConverter.GetBytes(0x4D48414Du).CopyTo(block, 0);
            BitConverter.GetBytes((uint)headerSize).CopyTo(block, 8);
            BitConverter.GetBytes((uint)items.Length).CopyTo(block, 12);
            BitConverter.GetBytes((uint)entrySize).CopyTo(block, 16);

            for (int i = 0; i < items.Length; i++)
            {
                int at = headerSize + i * entrySize;
                System.Text.Encoding.ASCII.GetBytes(items[i].name).CopyTo(block, at);
                BitConverter.GetBytes(items[i].value).CopyTo(block, at + 552);
            }
            return block;
        }

        [Fact]
        public void Afterburner_block_picks_the_hottest_cpu_temperature()
        {
            var block = AfterburnerBlock(
                ("CPU temperature", 64f),
                ("CPU1 temperature", 72f),
                ("GPU temperature", 88f),
                ("CPU usage", 41f));

            Assert.Equal(72.0, AfterburnerSource.DecodeCpuTemperature(block), 1);
        }

        [Fact]
        public void Afterburner_block_with_a_wrong_signature_is_refused()
        {
            var block = AfterburnerBlock(("CPU temperature", 64f));
            BitConverter.GetBytes(0u).CopyTo(block, 0);

            Assert.Equal(-1, AfterburnerSource.DecodeCpuTemperature(block), 1);
        }

        // --------------------------------------------------------------------- AIDA64

        [Fact]
        public void Aida_fragment_picks_the_hottest_cpu_temperature()
        {
            const string xml =
                "<temp><id>TCPU</id><label>CPU</label><value>66</value></temp>" +
                "<temp><id>TCPUPKG</id><label>CPU Package</label><value>71</value></temp>" +
                "<temp><id>TGPU1</id><label>GPU Diode</label><value>84</value></temp>";

            Assert.Equal(71.0, AidaSource.DecodeCpuTemperature(xml), 1);
        }

        [Fact]
        public void Aida_fragment_that_is_malformed_returns_unavailable()
        {
            Assert.Equal(-1, AidaSource.DecodeCpuTemperature("<temp><label>CPU"), 1);
        }

        [Fact]
        public void Aida_fragment_with_no_cpu_temperature_returns_unavailable()
        {
            Assert.Equal(-1, AidaSource.DecodeCpuTemperature(
                "<temp><id>TGPU1</id><label>GPU Diode</label><value>84</value></temp>"), 1);
        }

        /// <summary>
        /// AIDA64 writes decimals with whatever separator its locale uses, and this machine
        /// runs a Thai locale. Parsing must be invariant or a comma becomes a parse failure.
        /// </summary>
        [Fact]
        public void Aida_fragment_parses_a_decimal_invariantly()
        {
            Assert.Equal(71.5, AidaSource.DecodeCpuTemperature(
                "<temp><id>TCPU</id><label>CPU</label><value>71.5</value></temp>"), 1);
        }

        // ------------------------------------------------------------------- registry

        private sealed class FakeSource : ISensorSource
        {
            private readonly SensorReading[] _readings;

            public FakeSource(string name, params SensorReading[] readings)
            { Name = name; _readings = readings; }

            public string Name { get; }
            public bool IsAvailable => true;
            public int ReadCount { get; private set; }
            public bool ShouldThrow { get; set; }

            public IReadOnlyList<SensorReading> Read()
            {
                ReadCount++;
                if (ShouldThrow) throw new InvalidOperationException("source is broken");
                return _readings;
            }
        }

        /// <summary>
        /// The load-bearing test of the whole feature. Both of these report Temperature and
        /// neither is the die: the zone follows the fan loop, and the GPU has its own diode.
        /// Selection is on IsCpuDie precisely so that adding a new Temperature source can
        /// never accidentally start populating the CPU readout.
        /// </summary>
        [Fact]
        public void A_zone_reading_is_never_selected_as_the_cpu_die_temperature()
        {
            var zone = new FakeSource("ACPI",
                new SensorReading("zone.TZ01", "System", SensorCategory.Temperature, 69.0, "°C", "ACPI"));
            var gpu = new FakeSource("Display kernel",
                new SensorReading("gpu.temp", "Radeon", SensorCategory.Temperature, 49.0, "°C", "D3DKMT"));

            var registry = new SensorRegistry(new ISensorSource[] { zone, gpu });
            registry.Snapshot();

            Assert.Equal(-1, registry.CpuDieTemperature, 1);
        }

        [Fact]
        public void The_first_publisher_in_order_wins()
        {
            var first = new FakeSource("Core Temp",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 71.0, "°C", "Core Temp", true));
            var second = new FakeSource("HWiNFO",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 68.0, "°C", "HWiNFO", true));

            var registry = new SensorRegistry(new ISensorSource[] { first, second });
            registry.Snapshot();

            Assert.Equal(71.0, registry.CpuDieTemperature, 1);
        }

        [Fact]
        public void A_throwing_source_does_not_stop_the_others()
        {
            var broken = new FakeSource("broken") { ShouldThrow = true };
            var good = new FakeSource("Core Temp",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 71.0, "°C", "Core Temp", true));

            var registry = new SensorRegistry(new ISensorSource[] { broken, good });
            var readings = registry.Snapshot();

            Assert.Single(readings);
            Assert.Equal(71.0, registry.CpuDieTemperature, 1);
        }

        [Fact]
        public void A_throwing_source_is_not_probed_again_until_its_backoff_expires()
        {
            var broken = new FakeSource("broken") { ShouldThrow = true };
            var registry = new SensorRegistry(new ISensorSource[] { broken });

            for (int i = 0; i < 10; i++) registry.Snapshot();

            Assert.Equal(1, broken.ReadCount);
        }

        /// <summary>
        /// A publisher that stops answering must clear the reading rather than leave the last
        /// number on screen: a stale temperature is worse than a dash, because it looks live.
        /// </summary>
        [Fact]
        public void A_publisher_that_stops_answering_clears_the_die_temperature()
        {
            var publisher = new FakeSource("Core Temp",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 71.0, "°C", "Core Temp", true));
            var registry = new SensorRegistry(new ISensorSource[] { publisher });

            registry.Snapshot();
            Assert.Equal(71.0, registry.CpuDieTemperature, 1);

            var empty = new SensorRegistry(new ISensorSource[] { new FakeSource("Core Temp") });
            empty.Snapshot();
            Assert.Equal(-1, empty.CpuDieTemperature, 1);
        }

        [Fact]
        public void Metrics_carry_an_empty_sensor_list_by_default()
        {
            var m = new Kil0bitSystemMonitor.Models.SystemMetrics();
            Assert.NotNull(m.Sensors);
            Assert.Empty(m.Sensors);
        }

        /// <summary>
        /// The metrics default must be an absent CPU temperature, not a cold one. A 0 here
        /// would read as a working sensor on an ice-cold CPU.
        /// </summary>
        [Fact]
        public void Metrics_default_the_cpu_temperature_to_unavailable()
        {
            Assert.Equal(-1f, new Kil0bitSystemMonitor.Models.SystemMetrics().CpuTemperature);
        }

        // ------------------------------------------------------------------ presentation

        [Fact]
        public void An_absent_reading_renders_as_an_em_dash_not_a_zero()
        {
            Assert.Equal("—", StatsPanelViewModel.FormatSensorValue(-1, "°C"));
            Assert.Equal("71°C", StatsPanelViewModel.FormatSensorValue(71.0, "°C"));
            Assert.Equal("2400 RPM", StatsPanelViewModel.FormatSensorValue(2400, "RPM"));
        }

        /// <summary>
        /// The original complaint: with no publisher the temperature suffix simply vanished,
        /// so the header gave no hint the reading existed at all. It now says so.
        /// </summary>
        [Fact]
        public void The_cpu_header_states_the_temperature_is_unavailable_rather_than_omitting_it()
        {
            Assert.Equal("31% · 3.42 GHz · —", StatsPanelViewModel.BuildCpuHeader(31, 3.42f, -1));
            Assert.Equal("31% · 3.42 GHz · 71°", StatsPanelViewModel.BuildCpuHeader(31, 3.42f, 71));
        }

        /// <summary>
        /// The frequency is optional but the temperature slot is not: a machine that cannot
        /// report its clock must still show whether it can report its temperature.
        /// </summary>
        [Fact]
        public void The_cpu_header_keeps_the_temperature_slot_when_the_clock_is_unknown()
        {
            Assert.Equal("31% · —", StatsPanelViewModel.BuildCpuHeader(31, 0f, -1));
        }

        /// <summary>
        /// Readings are routed by id prefix, so a temperature sits beside the load that
        /// produced it rather than in a shared list. Getting this wrong would put a GPU
        /// temperature under the processor, which is the confusion the whole feature avoids.
        /// </summary>
        [Theory]
        [InlineData("gpu.AMD Radeon 890M.temp", true)]
        [InlineData("gpu.NVIDIA RTX PRO 1000.power", true)]
        [InlineData("gpu.AMD Radeon 890M.pthrottle", true)]
        [InlineData("zone.TZ01", false)]
        [InlineData("zone.TZ01.passive", false)]
        [InlineData("cpu.die", false)]
        public void Readings_are_routed_to_the_card_that_owns_them(string id, bool belongsToGpu)
        {
            var reading = new SensorReading(id, "label", SensorCategory.Temperature, 50, "°C", "test");
            Assert.Equal(belongsToGpu, StatsPanelViewModel.IsAdapterReading(reading));
        }

        /// <summary>
        /// Routing keys off the id, never the label. A GPU whose vendor string happens to
        /// contain "CPU" — or an APU reading labelled with the processor's own name — must
        /// still land on the GPU card.
        /// </summary>
        [Fact]
        public void Routing_ignores_the_label_which_is_a_vendor_string()
        {
            var oddlyNamed = new SensorReading(
                "gpu.AMD Ryzen AI 9 HX PRO 370 w/ Radeon 890M.temp",
                "AMD Ryzen AI 9 HX PRO 370 w/ Radeon 890M",
                SensorCategory.Temperature, 46, "°C", "Display kernel");

            Assert.True(StatsPanelViewModel.IsAdapterReading(oddlyNamed));
        }

        /// <summary>
        /// Thai locale renders 3.42 as "3,42" and the year as 2569 under the default calendar.
        /// The header is a fixed format, so it must not follow the ambient culture.
        /// </summary>
        [Fact]
        public void The_cpu_header_formats_invariantly_regardless_of_locale()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("th-TH");
                Assert.Equal("31% · 3.42 GHz · 71°", StatsPanelViewModel.BuildCpuHeader(31, 3.42f, 71));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
        }
    }
}
