using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Services.Sensors;
using Kil0bitSystemMonitor.Services.Sensors.Publishers;
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
    }
}
