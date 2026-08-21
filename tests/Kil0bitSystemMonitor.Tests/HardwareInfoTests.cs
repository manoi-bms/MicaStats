using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kil0bitSystemMonitor.Services.HardwareInfo;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// SMBIOS structures are length-prefixed records with a trailing null-terminated string
    /// pool; these tests build synthetic blobs byte-by-byte so the parser's offset arithmetic
    /// (extended sizes, extended DDR5 speeds, KB-granularity bit) is pinned exactly.
    /// </summary>
    public class SmbiosParserTests
    {
        private static void W(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
        private static void D(byte[] b, int off, uint v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }

        private static byte[] Build(byte type, byte len, Action<byte[]> poke, params string[] strings)
        {
            var b = new byte[len];
            b[0] = type;
            b[1] = len;
            poke(b);
            var list = new List<byte>(b);
            if (strings.Length == 0) { list.Add(0); list.Add(0); }
            else
            {
                foreach (var s in strings) { list.AddRange(Encoding.ASCII.GetBytes(s)); list.Add(0); }
                list.Add(0);
            }
            return list.ToArray();
        }

        private static byte[] Blob(params byte[][] structures) =>
            structures.Concat(new[] { Build(127, 4, _ => { }) }).SelectMany(s => s).ToArray();

        [Fact]
        public void Bios_system_and_baseboard_parse()
        {
            var data = SmbiosData.ParseBare(Blob(
                Build(0, 0x12, b => { b[0x04] = 1; b[0x05] = 2; b[0x08] = 3; },
                    "American Megatrends", "F.42", "03/11/2026"),
                Build(1, 0x1B, b => { b[0x04] = 1; b[0x05] = 2; }, "FRAMEWORK", "Laptop 16"),
                Build(2, 0x0F, b => { b[0x04] = 1; b[0x05] = 2; b[0x06] = 3; }, "FRMW", "FRANMZCP07", "A6")));

            Assert.Equal("American Megatrends", data.Bios!.Vendor);
            Assert.Equal("F.42", data.Bios.Version);
            Assert.Equal("03/11/2026", data.Bios.Date);
            Assert.Equal("FRAMEWORK", data.System!.Manufacturer);
            Assert.Equal("Laptop 16", data.System.Product);
            Assert.Equal("FRMW", data.Baseboard!.Manufacturer);
            Assert.Equal("FRANMZCP07", data.Baseboard.Product);
            Assert.Equal("A6", data.Baseboard.Version);
        }

        [Fact]
        public void Processor_socket_clocks_and_counts_parse()
        {
            var data = SmbiosData.ParseBare(Blob(
                Build(4, 0x30, b =>
                {
                    b[0x04] = 1;            // socket designation string
                    b[0x10] = 2;            // version string
                    W(b, 0x12, 100);        // external (bus) clock MHz
                    W(b, 0x14, 5500);       // max speed
                    W(b, 0x16, 4800);       // current speed
                    b[0x23] = 8;            // core count
                    b[0x25] = 16;           // thread count
                }, "AM5", "AMD Ryzen 7")));

            var p = data.Processor!;
            Assert.Equal("AM5", p.SocketDesignation);
            Assert.Equal(100, p.ExternalClockMHz);
            Assert.Equal(5500, p.MaxSpeedMHz);
            Assert.Equal(4800, p.CurrentSpeedMHz);
            Assert.Equal(8, p.CoreCount);
            Assert.Equal(16, p.ThreadCount);
        }

        [Fact]
        public void Processor_saturated_byte_counts_fall_back_to_words()
        {
            var data = SmbiosData.ParseBare(Blob(
                Build(4, 0x30, b =>
                {
                    b[0x23] = 0xFF; W(b, 0x2A, 96);   // 96 cores exceed the byte field
                    b[0x25] = 0xFF; W(b, 0x2E, 192);
                })));

            Assert.Equal(96, data.Processor!.CoreCount);
            Assert.Equal(192, data.Processor.ThreadCount);
        }

        [Fact]
        public void Ddr5_module_uses_extended_size_and_speed_fields()
        {
            var data = SmbiosData.ParseBare(Blob(
                Build(17, 0x5C, b =>
                {
                    W(b, 0x0C, 0x7FFF);      // size: see extended dword
                    D(b, 0x1C, 32768);       // 32768 MB = 32 GB
                    b[0x0E] = 0x0D;          // SODIMM
                    b[0x10] = 1;             // locator string
                    b[0x11] = 2;             // bank locator string
                    b[0x12] = 0x22;          // DDR5
                    W(b, 0x15, 0xFFFF);      // speed: see extended dword
                    b[0x17] = 3;             // manufacturer string
                    b[0x1A] = 4;             // part number string
                    W(b, 0x20, 0xFFFF);      // configured speed: see extended dword
                    W(b, 0x26, 1100);        // 1.1 V
                    D(b, 0x54, 5600);        // rated MT/s
                    D(b, 0x58, 5200);        // configured MT/s
                }, "DIMM_A1", "BANK 0", "Samsung", "M425R2GA3BB0-CWMOD")));

            var d = Assert.Single(data.MemoryDevices);
            Assert.True(d.IsPopulated);
            Assert.Equal(34359738368UL, d.SizeBytes);
            Assert.Equal("DDR5", d.TypeName);
            Assert.Equal("SODIMM", d.FormFactor);
            Assert.Equal("DIMM_A1", d.Locator);
            Assert.Equal(5600, d.SpeedMts);
            Assert.Equal(5200, d.ConfiguredSpeedMts);
            Assert.Equal("Samsung", d.Manufacturer);
            Assert.Equal("M425R2GA3BB0-CWMOD", d.PartNumber);
            Assert.Equal(1100, d.ConfiguredVoltageMv);
        }

        [Fact]
        public void Plain_mb_and_kb_granularity_sizes_decode()
        {
            var data = SmbiosData.ParseBare(Blob(
                Build(17, 0x28, b => { W(b, 0x0C, 16384); b[0x12] = 0x1A; }),          // 16 GB DDR4
                Build(17, 0x28, b => { W(b, 0x0C, 0x8000 | 512); b[0x12] = 0x1A; })));  // 512 KB (bit 15)

            Assert.Equal(16UL * 1024 * 1024 * 1024, data.MemoryDevices[0].SizeBytes);
            Assert.Equal(512UL * 1024, data.MemoryDevices[1].SizeBytes);
        }

        [Fact]
        public void Empty_slot_is_not_populated()
        {
            var data = SmbiosData.ParseBare(Blob(
                Build(17, 0x28, b => { W(b, 0x0C, 0); b[0x10] = 1; }, "DIMM_B2")));

            var d = Assert.Single(data.MemoryDevices);
            Assert.False(d.IsPopulated);
            Assert.Equal("DIMM_B2", d.Locator);
        }

        [Fact]
        public void Firmware_table_header_supplies_version_and_offsets()
        {
            byte[] structures = Blob(Build(2, 0x0F, b => { b[0x04] = 1; }, "GIGABYTE"));
            var rsmb = new byte[8 + structures.Length];
            rsmb[1] = 3; rsmb[2] = 7; // SMBIOS 3.7
            D(rsmb, 4, (uint)structures.Length);
            structures.CopyTo(rsmb, 8);

            var data = SmbiosData.ParseFirmwareTable(rsmb);
            Assert.Equal("3.7", data.SmbiosVersion);
            Assert.Equal("GIGABYTE", data.Baseboard!.Manufacturer);
        }

        [Theory]
        [InlineData(0x1A, "DDR4")]
        [InlineData(0x22, "DDR5")]
        [InlineData(0x23, "LPDDR5")]
        [InlineData(0x63, "RAM")]
        public void Memory_type_codes_map(int code, string expected) =>
            Assert.Equal(expected, SmbiosData.MemoryTypeName(code));
    }

    /// <summary>
    /// CPUID decoding against canned register dumps: the vendor register order (EBX, EDX, ECX),
    /// the extended family/model composition rules, and feature-bit positions.
    /// </summary>
    public class CpuIdDecoderTests
    {
        private sealed class FakeCpuId : ICpuIdSource
        {
            private readonly Dictionary<(uint, uint), CpuIdRegs> _regs = new();
            public FakeCpuId Set(uint leaf, uint eax, uint ebx, uint ecx, uint edx)
            {
                _regs[(leaf, 0u)] = new CpuIdRegs(eax, ebx, ecx, edx);
                return this;
            }
            public bool TryRead(uint leaf, uint subleaf, out CpuIdRegs regs) =>
                _regs.TryGetValue((leaf, subleaf), out regs);
        }

        [Fact]
        public void Vendor_reads_ebx_edx_ecx_order()
        {
            var regs = new CpuIdRegs(0x16, 0x756E6547, 0x6C65746E, 0x49656E69);
            Assert.Equal("GenuineIntel", CpuIdDecoder.DecodeVendor(regs));
        }

        [Fact]
        public void Intel_family6_composes_extended_model()
        {
            // Coffee Lake i7-8700K signature.
            var (family, model, stepping) = CpuIdDecoder.DecodeSignature(0x000906EA);
            Assert.Equal(6u, family);
            Assert.Equal(0x9Eu, model);
            Assert.Equal(0xAu, stepping);
        }

        [Fact]
        public void Amd_family_f_adds_extended_family()
        {
            // Zen 3 signature: base family 0xF + extended 0xA = 0x19.
            var (family, model, stepping) = CpuIdDecoder.DecodeSignature(0x00A20F12);
            Assert.Equal(0x19u, family);
            Assert.Equal(0x21u, model);
            Assert.Equal(2u, stepping);
        }

        [Fact]
        public void Feature_bits_map_to_names()
        {
            var leaf1 = new CpuIdRegs(0,
                0,
                (1u << 0) | (1u << 9) | (1u << 19) | (1u << 20) | (1u << 25) | (1u << 28) | (1u << 12),
                (1u << 23) | (1u << 25) | (1u << 26));
            var leaf7 = new CpuIdRegs(0, (1u << 5) | (1u << 3) | (1u << 8) | (1u << 29) | (1u << 16), 0, 0);
            var ext1 = new CpuIdRegs(0, 0, 1u << 2, 1u << 29);

            var f = CpuIdDecoder.DecodeFeatures(leaf1, leaf7, ext1);

            Assert.Equal("MMX", f[0]);
            Assert.Contains("SSE4.2", f);
            Assert.Contains("AES-NI", f);
            Assert.Contains("AVX2", f);
            Assert.Contains("AVX-512", f);
            Assert.Contains("SHA", f);
            Assert.Contains("x86-64", f);
            Assert.Contains("AMD-V", f);
            Assert.DoesNotContain("VT-x", f);
        }

        [Fact]
        public void Brand_string_spans_three_leaves_and_trims_padding()
        {
            static (uint, uint, uint, uint) Pack(string s16)
            {
                var b = Encoding.ASCII.GetBytes(s16.PadRight(16));
                return (BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4),
                        BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12));
            }

            var fake = new FakeCpuId();
            var (a1, b1, c1, d1) = Pack("      Intel(R) C");
            var (a2, b2, c2, d2) = Pack("ore(TM) i9-14900");
            var (a3, b3, c3, d3) = Pack("K               ");
            fake.Set(0x80000002, a1, b1, c1, d1)
                .Set(0x80000003, a2, b2, c2, d2)
                .Set(0x80000004, a3, b3, c3, d3);

            Assert.Equal("Intel(R) Core(TM) i9-14900K", CpuIdDecoder.DecodeBrand(fake, 0x80000004));
        }

        [Fact]
        public void Brand_absent_below_required_extended_leaf()
        {
            Assert.Equal("", CpuIdDecoder.DecodeBrand(new FakeCpuId(), 0x80000000));
        }

        [Fact]
        public void Decode_returns_null_without_leaf0()
        {
            Assert.Null(CpuIdDecoder.Decode(new FakeCpuId()));
        }

        [Fact]
        public void Hypervisor_bit_is_surfaced()
        {
            var fake = new FakeCpuId()
                .Set(0, 1, 0x756E6547, 0x6C65746E, 0x49656E69)
                .Set(1, 0x000906EA, 0, 1u << 31, 0);
            var id = CpuIdDecoder.Decode(fake)!;
            Assert.True(id.HypervisorPresent);
        }
    }

    /// <summary>
    /// GetLogicalProcessorInformationEx buffers are variable-size records; these tests build
    /// them from raw bytes to pin the offsets (efficiency class at +9, group count at +22,
    /// first affinity mask at +24; cache size at +12, type at +16).
    /// </summary>
    public class ProcessorTopologyTests
    {
        private static void I32(byte[] b, int off, int v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }

        private static byte[] Core(byte flags, byte efficiency, ulong mask)
        {
            var b = new byte[8 + 24 + 16];
            I32(b, 0, 0);                 // RelationProcessorCore
            I32(b, 4, b.Length);
            b[8] = flags;
            b[9] = efficiency;
            b[8 + 22] = 1;                // one group
            BitConverter.GetBytes(mask).CopyTo(b, 8 + 24);
            return b;
        }

        private static byte[] Cache(byte level, uint size, int type)
        {
            var b = new byte[8 + 20];
            I32(b, 0, 2);                 // RelationCache
            I32(b, 4, b.Length);
            b[8] = level;
            I32(b, 12, (int)size);
            I32(b, 16, type);
            return b;
        }

        private static byte[] Package()
        {
            var b = new byte[8 + 24];
            I32(b, 0, 3);                 // RelationProcessorPackage
            I32(b, 4, b.Length);
            return b;
        }

        private static ProcessorTopologyInfo Parse(params byte[][] records)
        {
            var all = records.SelectMany(r => r).ToArray();
            return ProcessorTopology.Parse(all, all.Length);
        }

        [Fact]
        public void Counts_cores_logical_and_smt()
        {
            var t = Parse(Core(1, 0, 0b11), Core(1, 0, 0b1100), Package());
            Assert.Equal(2, t.PhysicalCores);
            Assert.Equal(4, t.LogicalProcessors);
            Assert.True(t.SmtPresent);
            Assert.Equal(1, t.Packages);
            Assert.False(t.IsHybrid);
            Assert.Equal(0, t.EfficiencyCores);
        }

        [Fact]
        public void Hybrid_splits_by_efficiency_class()
        {
            var records = new List<byte[]>();
            for (int i = 0; i < 6; i++) records.Add(Core(1, 1, 0b11ul << (i * 2)));   // P cores, SMT
            for (int i = 0; i < 8; i++) records.Add(Core(0, 0, 1ul << (12 + i)));      // E cores
            var t = Parse(records.ToArray());

            Assert.True(t.IsHybrid);
            Assert.Equal(14, t.PhysicalCores);
            Assert.Equal(6, t.PerformanceCores);
            Assert.Equal(8, t.EfficiencyCores);
            Assert.Equal(20, t.LogicalProcessors);
        }

        [Fact]
        public void Caches_aggregate_by_level_kind_and_size()
        {
            var t = Parse(
                Cache(1, 48 * 1024, 2), Cache(1, 48 * 1024, 2),
                Cache(1, 32 * 1024, 1), Cache(1, 32 * 1024, 1),
                Cache(2, 1310720, 0), Cache(2, 1310720, 0),
                Cache(3, 24 * 1024 * 1024, 0));

            Assert.Equal(4, t.Caches.Count);
            var l1d = t.Caches.Single(c => c.Level == 1 && c.Kind == "Data");
            Assert.Equal(2, l1d.Count);
            Assert.Equal(48 * 1024, l1d.SizeBytes);
            var l3 = t.Caches.Single(c => c.Level == 3);
            Assert.Equal(1, l3.Count);
            Assert.Equal("Unified", l3.Kind);
        }

        [Fact]
        public void Truncated_buffer_stops_cleanly()
        {
            var full = Core(1, 0, 0b11);
            var t = ProcessorTopology.Parse(full, full.Length - 10);
            Assert.Equal(0, t.PhysicalCores);
        }
    }

    public class SpecFormatTests
    {
        [Theory]
        [InlineData(48UL * 1024, "48 KB")]
        [InlineData(1310720UL, "1.25 MB")]
        [InlineData(34359738368UL, "32 GB")]
        public void Binary_bytes_format(ulong bytes, string expected) =>
            Assert.Equal(expected, SpecFormat.Bytes(bytes));

        [Fact]
        public void Disk_bytes_use_decimal_units() =>
            Assert.Equal("500.11 GB", SpecFormat.DiskBytes(500107862016UL));

        [Fact]
        public void Clock_and_transfer_rates_format()
        {
            Assert.Equal("5,500 MHz", SpecFormat.Mhz(5500));
            Assert.Equal("5,600 MT/s", SpecFormat.MtPerSec(5600));
            Assert.Equal("—", SpecFormat.Mhz(0));
        }

        [Fact]
        public void Cache_line_shows_count_and_total()
        {
            var line = SpecFormat.CacheLine(new CacheGroup(2, "Unified", 1310720, 6));
            Assert.Equal("6 × 1.25 MB  (7.5 MB total)", line);
            Assert.Equal("24 MB", SpecFormat.CacheLine(new CacheGroup(3, "Unified", 24 * 1024 * 1024, 1)));
        }

        [Fact]
        public void Group_add_skips_blank_but_addalways_dashes()
        {
            var g = new SpecGroup("T").Add("A", "").AddAlways("B", " ");
            var row = Assert.Single(g.Rows);
            Assert.Equal("B", row.Label);
            Assert.Equal("—", row.Value);
        }
    }

    public class HardwareReportWriterTests
    {
        [Fact]
        public void Report_renders_header_sections_and_dotted_rows()
        {
            var snap = new HardwareSnapshot { GeneratedAt = new DateTime(2026, 8, 21, 10, 0, 0) };
            var tab = new HardwareTab("CPU");
            tab.Groups.Add(new SpecGroup("PROCESSOR").Add("Name", "AMD Ryzen 9"));
            tab.Groups.Add(new SpecGroup("EMPTY"));
            snap.Tabs.Add(tab);
            snap.GatherDuration = TimeSpan.FromMilliseconds(321);

            string text = HardwareReportWriter.Write(snap, "1.3.0");

            Assert.Contains("MicaStats Hardware Report", text);
            Assert.Contains("Generated : 2026-08-21 10:00:00", text);
            Assert.Contains("MicaStats 1.3.0", text);
            Assert.Contains("[CPU — PROCESSOR]", text);
            Assert.Contains("Name ", text);
            Assert.Contains(": AMD Ryzen 9", text);
            Assert.DoesNotContain("EMPTY", text);
            Assert.Contains("Gathered in 321 ms.", text);
        }

        [Fact]
        public void Long_labels_do_not_break_padding()
        {
            var snap = new HardwareSnapshot { GeneratedAt = new DateTime(2026, 1, 1) };
            var tab = new HardwareTab("X");
            tab.Groups.Add(new SpecGroup("G").Add("An Extremely Long Label Name Here", "v"));
            snap.Tabs.Add(tab);

            string text = HardwareReportWriter.Write(snap, "1.3.0");
            Assert.Contains("An Extremely Long Label Name Here : v", text);
        }
    }
}
