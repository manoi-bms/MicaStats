using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kil0bitSystemMonitor.Services.HardwareInfo
{
    public sealed record SmbiosBios(string Vendor, string Version, string Date);
    public sealed record SmbiosSystem(string Manufacturer, string Product);
    public sealed record SmbiosBaseboard(string Manufacturer, string Product, string Version);

    public sealed record SmbiosProcessor(
        string SocketDesignation,
        string Version,
        int ExternalClockMHz,
        int MaxSpeedMHz,
        int CurrentSpeedMHz,
        int CoreCount,
        int ThreadCount);

    public sealed record SmbiosMemoryDevice(
        string Locator,
        string BankLocator,
        ulong SizeBytes,
        string TypeName,
        string FormFactor,
        int SpeedMts,
        int ConfiguredSpeedMts,
        string Manufacturer,
        string PartNumber,
        int ConfiguredVoltageMv)
    {
        public bool IsPopulated => SizeBytes > 0;
    }

    /// <summary>
    /// Reads the raw SMBIOS (DMI) firmware table via <c>GetSystemFirmwareTable("RSMB")</c>.
    /// These are the same tables CPU-Z parses for its Mainboard and memory info; WMI classes
    /// like Win32_PhysicalMemory are merely slow views over this exact data, and they drop the
    /// SMBIOS 3.3 extended-speed fields that DDR5 needs.
    /// </summary>
    public static class SmbiosReader
    {
        private const uint RSMB = 0x52534D42; // 'RSMB'

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

        public static byte[]? TryReadRaw()
        {
            try
            {
                uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
                if (size == 0 || size > 4 * 1024 * 1024) return null;
                var buf = new byte[size];
                uint got = GetSystemFirmwareTable(RSMB, 0, buf, size);
                if (got == 0) return null;
                if (got < size) Array.Resize(ref buf, (int)got);
                return buf;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Pure parser for the raw SMBIOS blob. Every structure is length-prefixed and every string
    /// is an index into a trailing null-terminated pool, so all field reads are guarded by the
    /// structure's own declared length — firmware regularly ships short (older-spec) records.
    /// </summary>
    public sealed class SmbiosData
    {
        public string SmbiosVersion { get; private set; } = "";
        public SmbiosBios? Bios { get; private set; }
        public SmbiosSystem? System { get; private set; }
        public SmbiosBaseboard? Baseboard { get; private set; }
        public SmbiosProcessor? Processor { get; private set; }
        public List<SmbiosMemoryDevice> MemoryDevices { get; } = new();

        /// <summary>
        /// Parses the buffer returned by GetSystemFirmwareTable, which prepends an 8-byte
        /// RawSMBIOSData header (calling method, major, minor, DMI revision, DWORD length).
        /// </summary>
        public static SmbiosData ParseFirmwareTable(byte[] rsmb)
        {
            var data = new SmbiosData();
            if (rsmb == null || rsmb.Length < 8) return data;
            int declared = BitConverter.ToInt32(rsmb, 4);
            int avail = Math.Min(declared, rsmb.Length - 8);
            data.SmbiosVersion = rsmb[1] + "." + rsmb[2];
            data.ParseStructures(rsmb, 8, avail);
            return data;
        }

        /// <summary>Parses bare structures with no RawSMBIOSData header (used by tests).</summary>
        public static SmbiosData ParseBare(byte[] table)
        {
            var data = new SmbiosData();
            data.ParseStructures(table, 0, table.Length);
            return data;
        }

        private void ParseStructures(byte[] b, int start, int length)
        {
            int off = start;
            int end = start + length;

            while (off + 4 <= end)
            {
                byte type = b[off];
                byte slen = b[off + 1];
                if (slen < 4 || off + slen > end) break;

                // The string pool follows the formatted area and ends at a double null. A
                // structure with no strings is followed immediately by two zero bytes.
                int poolStart = off + slen;
                int p = poolStart;
                while (p + 1 < end && !(b[p] == 0 && b[p + 1] == 0)) p++;
                if (p + 1 >= end) { ParseOne(b, off, slen, ExtractStrings(b, poolStart, end)); break; }

                ParseOne(b, off, slen, ExtractStrings(b, poolStart, p + 1));

                if (type == 127) break; // end-of-table structure
                off = p + 2;
            }
        }

        private void ParseOne(byte[] b, int off, byte slen, List<string> strings)
        {
            string Str(int rel)
            {
                if (rel >= slen) return "";
                int idx = b[off + rel];
                return idx >= 1 && idx <= strings.Count ? strings[idx - 1].Trim() : "";
            }
            int Word(int rel) => rel + 2 <= slen ? BitConverter.ToUInt16(b, off + rel) : 0;
            uint DWord(int rel) => rel + 4 <= slen ? BitConverter.ToUInt32(b, off + rel) : 0;
            int ByteAt(int rel) => rel + 1 <= slen ? b[off + rel] : 0;

            switch (b[off])
            {
                case 0:
                    Bios ??= new SmbiosBios(Str(0x04), Str(0x05), Str(0x08));
                    break;

                case 1:
                    System ??= new SmbiosSystem(Str(0x04), Str(0x05));
                    break;

                case 2:
                    Baseboard ??= new SmbiosBaseboard(Str(0x04), Str(0x05), Str(0x06));
                    break;

                case 4:
                {
                    // Core/thread byte fields saturate at 0xFF; SMBIOS 3.0 added word fields.
                    int cores = ByteAt(0x23);
                    if (cores == 0xFF) cores = Word(0x2A);
                    int threads = ByteAt(0x25);
                    if (threads == 0xFF) threads = Word(0x2E);
                    Processor ??= new SmbiosProcessor(
                        Str(0x04), Str(0x10), Word(0x12), Word(0x14), Word(0x16), cores, threads);
                    break;
                }

                case 17:
                {
                    ulong sizeBytes = 0;
                    int sizeW = Word(0x0C);
                    if (sizeW == 0x7FFF)
                    {
                        // Extended size (SMBIOS 2.7+), value in MB with bit 31 reserved.
                        sizeBytes = (DWord(0x1C) & 0x7FFFFFFFu) * 1024UL * 1024UL;
                    }
                    else if (sizeW != 0xFFFF && sizeW != 0)
                    {
                        // Bit 15 selects KB granularity; otherwise the value is in MB.
                        sizeBytes = (sizeW & 0x8000) != 0
                            ? (ulong)(sizeW & 0x7FFF) * 1024UL
                            : (ulong)sizeW * 1024UL * 1024UL;
                    }

                    // 0xFFFF in the word speed fields defers to the SMBIOS 3.3 extended
                    // dwords, which modern firmware already uses for ordinary DDR5 speeds.
                    int speed = Word(0x15);
                    if (speed == 0xFFFF) speed = (int)DWord(0x54);
                    int configured = Word(0x20);
                    if (configured == 0xFFFF) configured = (int)DWord(0x58);

                    MemoryDevices.Add(new SmbiosMemoryDevice(
                        Str(0x10), Str(0x11), sizeBytes,
                        MemoryTypeName(ByteAt(0x12)), FormFactorName(ByteAt(0x0E)),
                        speed, configured, Str(0x17), Str(0x1A), Word(0x26)));
                    break;
                }
            }
        }

        private static List<string> ExtractStrings(byte[] b, int start, int endExclusive)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            for (int i = start; i < endExclusive; i++)
            {
                if (b[i] == 0)
                {
                    if (sb.Length == 0) break; // double null / empty pool
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append((char)b[i]);
                }
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        public static string MemoryTypeName(int code) => code switch
        {
            0x12 => "DDR",
            0x13 => "DDR2",
            0x18 => "DDR3",
            0x1A => "DDR4",
            0x1B => "LPDDR",
            0x1C => "LPDDR2",
            0x1D => "LPDDR3",
            0x1E => "LPDDR4",
            0x22 => "DDR5",
            0x23 => "LPDDR5",
            _ => "RAM",
        };

        public static string FormFactorName(int code) => code switch
        {
            0x09 => "DIMM",
            0x0D => "SODIMM",
            0x0F => "Die",
            _ => "",
        };
    }
}
