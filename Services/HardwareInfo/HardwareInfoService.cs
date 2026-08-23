using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Helpers;
using Microsoft.Win32;

namespace Kil0bitSystemMonitor.Services.HardwareInfo
{
    /// <summary>
    /// Builds the full hardware snapshot behind the panel's Hardware button, CPU-Z style but
    /// entirely from user mode:
    ///
    /// <list type="bullet">
    /// <item>CPU identity/features — the CPUID instruction (X86Base intrinsic), CPU-Z's own
    /// primary source; ring 3 sees the same registers its driver does for these leaves.</item>
    /// <item>Topology and caches — GetLogicalProcessorInformationEx.</item>
    /// <item>Board / BIOS / DIMMs — the raw SMBIOS (DMI) firmware tables.</item>
    /// <item>GPU — the display-class registry keys, whose QWORD VRAM value dodges the
    /// well-known 4 GB truncation in Win32_VideoController.AdapterRAM (a uint32).</item>
    /// <item>Storage — Win32_DiskDrive plus MSFT_PhysicalDisk for bus type and SSD/HDD.</item>
    /// </list>
    ///
    /// What CPU-Z gets from its kernel driver (MSR core voltage, SPD timing tables over SMBus)
    /// is deliberately out of reach for an unsigned user-mode app and is not faked here.
    /// Every section is independently fault-isolated: a failing source logs to the diagnostics
    /// file and degrades to a status row instead of losing the window.
    /// </summary>
    public static class HardwareInfoService
    {
        public static string AppVersion =>
            typeof(HardwareInfoService).Assembly.GetName().Version?.ToString(3) ?? "?";

        /// <summary>Blocking; call from a background task. Typical cost is WMI-bound (~1s cold).</summary>
        public static HardwareSnapshot Gather()
        {
            var sw = Stopwatch.StartNew();
            var snap = new HardwareSnapshot();

            SmbiosData smbios;
            try
            {
                var raw = SmbiosReader.TryReadRaw();
                smbios = raw != null ? SmbiosData.ParseFirmwareTable(raw) : new SmbiosData();
                if (raw == null) DiagnosticsLog.Warn("hardware", "SMBIOS firmware table unavailable");
            }
            catch (Exception ex)
            {
                smbios = new SmbiosData();
                DiagnosticsLog.Error("hardware", "SMBIOS parse failed", ex);
            }

            CpuIdentity? cpuid = null;
            try
            {
                if (X86CpuIdSource.IsSupported) cpuid = CpuIdDecoder.Decode(new X86CpuIdSource());
            }
            catch (Exception ex) { DiagnosticsLog.Error("hardware", "CPUID read failed", ex); }

            AddTab(snap, "CPU", UiGlyphs.Cpu, () => BuildCpu(smbios, cpuid));
            AddTab(snap, "MAINBOARD", UiGlyphs.Machine, () => BuildMainboard(smbios));
            AddTab(snap, "MEMORY", UiGlyphs.Memory, () => BuildMemory(smbios));
            AddTab(snap, "GRAPHICS", UiGlyphs.Gpu, BuildGraphics);
            AddTab(snap, "STORAGE", UiGlyphs.Disk, BuildStorage);
            AddTab(snap, "SYSTEM", UiGlyphs.System, () => BuildSystem(cpuid));

            sw.Stop();
            snap.GatherDuration = sw.Elapsed;
            snap.Summary = BuildSummary(snap, smbios, cpuid);
            DiagnosticsLog.Log("hardware",
                "Snapshot gathered in " + sw.ElapsedMilliseconds + " ms — " + snap.Summary);
            return snap;
        }

        /// <summary>
        /// Saves the CPU-Z-style text report under the MicaStats data folder and returns its
        /// path. The caller shows the path; the log records it for later investigation.
        /// </summary>
        public static string SaveReport(HardwareSnapshot snap)
        {
            string dir = Path.Combine(DiagnosticsLog.DataDir, "reports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir,
                "hardware-report-" + snap.GeneratedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, HardwareReportWriter.Write(snap, AppVersion));
            DiagnosticsLog.Log("hardware", "Report saved to " + path);
            return path;
        }

        private static void AddTab(HardwareSnapshot snap, string name, string icon, Func<List<SpecGroup>> build)
        {
            var tab = new HardwareTab(name, icon);
            try
            {
                tab.Groups.AddRange(build());
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("hardware", name + " tab failed", ex);
                tab.Groups.Add(new SpecGroup("STATUS", UiGlyphs.Warning)
                    .Add("Error", "This section could not be read (" + ex.GetType().Name + ")"));
            }
            snap.Tabs.Add(tab);
        }

        // ----- CPU ---------------------------------------------------------------------------

        private static List<SpecGroup> BuildCpu(SmbiosData smbios, CpuIdentity? cpuid)
        {
            var groups = new List<SpecGroup>();
            var topo = ProcessorTopology.TryReadNative();
            (string regName, int baseMhz) = ReadCpuRegistry();

            string name = cpuid?.BrandString is { Length: > 0 } b ? b : regName;

            var id = new SpecGroup("PROCESSOR", UiGlyphs.Cpu);
            id.AddAlways("Name", name);
            id.Add("Vendor", cpuid?.Vendor ?? "");
            id.Add("Socket", smbios.Processor?.SocketDesignation ?? "");
            if (cpuid != null)
                id.Add("Family / Model / Stepping",
                    cpuid.Family.ToString("X", CultureInfo.InvariantCulture) + " / " +
                    cpuid.Model.ToString("X", CultureInfo.InvariantCulture) + " / " +
                    cpuid.Stepping.ToString("X", CultureInfo.InvariantCulture));
            groups.Add(id);

            if (topo != null)
            {
                var t = new SpecGroup("TOPOLOGY", UiGlyphs.Layers);
                string cores = topo.PhysicalCores.ToString(CultureInfo.InvariantCulture);
                if (topo.IsHybrid)
                    cores += " (" + topo.PerformanceCores + "P + " + topo.EfficiencyCores + "E)";
                t.Add("Cores", cores);
                t.Add("Threads", topo.LogicalProcessors.ToString(CultureInfo.InvariantCulture));
                if (topo.Packages > 1) t.Add("Packages", topo.Packages.ToString(CultureInfo.InvariantCulture));
                t.Add("SMT / Hyper-Threading", topo.SmtPresent ? "Yes" : "No");
                groups.Add(t);

                if (topo.Caches.Count > 0)
                {
                    var c = new SpecGroup("CACHES", UiGlyphs.Memory);
                    foreach (var cache in topo.Caches)
                    {
                        string label = cache.Level == 1 ? "L1 " + cache.Kind : "L" + cache.Level;
                        c.Add(label, SpecFormat.CacheLine(cache));
                    }
                    groups.Add(c);
                }
            }

            var clocks = new SpecGroup("CLOCKS", UiGlyphs.Clock);
            clocks.Add("Base Clock", SpecFormat.Mhz(baseMhz));
            if (smbios.Processor is { } p)
            {
                clocks.Add("Max Speed (firmware)", p.MaxSpeedMHz > 0 ? SpecFormat.Mhz(p.MaxSpeedMHz) : "");
                clocks.Add("Bus Clock", p.ExternalClockMHz > 0 ? SpecFormat.Mhz(p.ExternalClockMHz) : "");
            }
            groups.Add(clocks);

            var isa = new SpecGroup("INSTRUCTION SETS", UiGlyphs.Code);
            if (cpuid != null && cpuid.Features.Count > 0)
                isa.Add("Extensions", string.Join(", ", cpuid.Features));
            else
                isa.AddAlways("Extensions", X86CpuIdSource.IsSupported ? "" : "CPUID not available on this architecture");
            groups.Add(isa);

            return groups;
        }

        private static (string Name, int BaseMhz) ReadCpuRegistry()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                string name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
                int mhz = key?.GetValue("~MHz") is int m ? m : 0;
                return (name, mhz);
            }
            catch { return ("", 0); }
        }

        // ----- Mainboard ---------------------------------------------------------------------

        private static List<SpecGroup> BuildMainboard(SmbiosData smbios)
        {
            var groups = new List<SpecGroup>();

            if (smbios.System is { } sys)
                groups.Add(new SpecGroup("SYSTEM", UiGlyphs.Info)
                    .Add("Manufacturer", sys.Manufacturer)
                    .Add("Product", sys.Product));

            var board = new SpecGroup("MAINBOARD", UiGlyphs.Machine);
            if (smbios.Baseboard is { } bb)
            {
                board.AddAlways("Manufacturer", bb.Manufacturer);
                board.AddAlways("Model", bb.Product);
                board.Add("Version", bb.Version);
            }
            else
            {
                board.AddAlways("Manufacturer", "");
            }
            groups.Add(board);

            var bios = new SpecGroup("BIOS", UiGlyphs.Firmware);
            if (smbios.Bios is { } bi)
            {
                bios.AddAlways("Brand", bi.Vendor);
                bios.AddAlways("Version", bi.Version);
                bios.Add("Date", bi.Date);
            }
            bios.Add("SMBIOS Version", smbios.SmbiosVersion);
            groups.Add(bios);

            return groups;
        }

        // ----- Memory ------------------------------------------------------------------------

        private static List<SpecGroup> BuildMemory(SmbiosData smbios)
        {
            var groups = new List<SpecGroup>();
            var populated = smbios.MemoryDevices.Where(d => d.IsPopulated).ToList();

            var general = new SpecGroup("GENERAL", UiGlyphs.Memory);
            ulong installed = populated.Aggregate(0UL, (a, d) => a + d.SizeBytes);
            ulong usable = ReadUsableRam();

            general.Add("Type", populated.Count > 0
                ? populated[0].TypeName + (populated[0].FormFactor.Length > 0 ? " (" + populated[0].FormFactor + ")" : "")
                : "");
            if (installed > 0) general.Add("Installed", SpecFormat.Bytes(installed));
            if (usable > 0) general.Add("Usable (Windows)", SpecFormat.Bytes(usable));
            if (smbios.MemoryDevices.Count > 0)
                general.Add("Slots Used", populated.Count + " of " + smbios.MemoryDevices.Count);
            int conf = populated.Count > 0 ? populated.Max(d => d.ConfiguredSpeedMts) : 0;
            general.Add("Configured Speed", conf > 0 ? SpecFormat.MtPerSec(conf) : "");
            groups.Add(general);

            int slot = 0;
            foreach (var d in populated)
            {
                slot++;
                string title = d.Locator.Length > 0 ? d.Locator.ToUpperInvariant() : "MODULE " + slot;
                var g = new SpecGroup(title, UiGlyphs.Memory);
                g.Add("Size", SpecFormat.Bytes(d.SizeBytes));
                g.Add("Type", (d.TypeName + " " + d.FormFactor).Trim());
                g.Add("Manufacturer", d.Manufacturer);
                g.Add("Part Number", d.PartNumber);
                if (d.SpeedMts > 0 && d.ConfiguredSpeedMts > 0 && d.SpeedMts != d.ConfiguredSpeedMts)
                    g.Add("Speed", SpecFormat.MtPerSec(d.ConfiguredSpeedMts) + " (rated " + SpecFormat.MtPerSec(d.SpeedMts) + ")");
                else
                    g.Add("Speed", SpecFormat.MtPerSec(Math.Max(d.SpeedMts, d.ConfiguredSpeedMts)));
                if (d.ConfiguredVoltageMv > 0)
                    g.Add("Voltage", (d.ConfiguredVoltageMv / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " V");
                groups.Add(g);
            }

            if (populated.Count == 0)
                general.AddAlways("Modules", "No SMBIOS module data (virtualized or locked firmware)");

            return groups;
        }

        private static ulong ReadUsableRam()
        {
            var status = new MEMORYSTATUSEX();
            try { if (GlobalMemoryStatusEx(status)) return status.ullTotalPhys; } catch { }
            return 0;
        }

        /// <summary>Current physical-memory load 0..100, for the live strip. Cheap kernel call.</summary>
        public static int? TryReadMemoryLoad()
        {
            var status = new MEMORYSTATUSEX();
            try { if (GlobalMemoryStatusEx(status)) return (int)status.dwMemoryLoad; } catch { }
            return null;
        }

        // ----- Graphics ----------------------------------------------------------------------

        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static List<SpecGroup> BuildGraphics()
        {
            var groups = new List<SpecGroup>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey))
            {
                foreach (string sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                    using var k = cls!.OpenSubKey(sub);
                    string desc = (k?.GetValue("DriverDesc") as string)?.Trim() ?? "";
                    if (desc.Length == 0 || !seen.Add(desc)) continue;

                    var g = new SpecGroup(desc.ToUpperInvariant(), UiGlyphs.Gpu);
                    long vram = ReadQword(k!, "HardwareInformation.qwMemorySize");
                    if (vram > 0) g.Add("Video Memory", SpecFormat.Bytes((ulong)vram));
                    g.Add("Driver Version", k!.GetValue("DriverVersion") as string ?? "");
                    g.Add("Driver Date", k.GetValue("DriverDate") as string ?? "");
                    g.Add("Provider", k.GetValue("ProviderName") as string ?? "");
                    groups.Add(g);
                }
            }

            var display = new SpecGroup("DISPLAY", UiGlyphs.Display);
            try
            {
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                if (primary != null)
                {
                    var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                    if (EnumDisplaySettingsW(primary.DeviceName, -1, ref dm) && dm.dmPelsWidth > 0)
                        display.Add("Primary Mode",
                            dm.dmPelsWidth + " × " + dm.dmPelsHeight + " @ " + dm.dmDisplayFrequency + " Hz");
                }
                int screens = System.Windows.Forms.Screen.AllScreens.Length;
                if (screens > 1) display.Add("Monitors", screens.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
            if (display.Rows.Count > 0) groups.Add(display);

            if (groups.Count == 0)
                groups.Add(new SpecGroup("GRAPHICS", UiGlyphs.Gpu).AddAlways("Adapter", ""));
            return groups;
        }

        /// <summary>
        /// The VRAM value is REG_QWORD on most drivers but shows up as an 8-byte REG_BINARY on
        /// some, so both encodings are accepted.
        /// </summary>
        private static long ReadQword(RegistryKey key, string name)
        {
            try
            {
                return key.GetValue(name) switch
                {
                    long l => l,
                    int i => i,
                    byte[] { Length: >= 8 } a => BitConverter.ToInt64(a, 0),
                    _ => 0,
                };
            }
            catch { return 0; }
        }

        // ----- Storage -----------------------------------------------------------------------

        private static List<SpecGroup> BuildStorage()
        {
            var groups = new List<SpecGroup>();

            // Optional enrichment: bus type and media kind live in the Storage Management
            // provider, which may be unavailable — the basic Win32_DiskDrive rows must survive.
            var extras = new Dictionary<string, (string Bus, string Media, string Health)>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage");
                using var s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT DeviceId, BusType, MediaType, HealthStatus FROM MSFT_PhysicalDisk"));
                foreach (ManagementBaseObject o in s.Get())
                {
                    string id = o["DeviceId"]?.ToString() ?? "";
                    extras[id] = (BusTypeName(ToInt(o["BusType"])),
                                  MediaTypeName(ToInt(o["MediaType"])),
                                  HealthName(ToInt(o["HealthStatus"])));
                }
            }
            catch (Exception ex) { DiagnosticsLog.Warn("hardware", "MSFT_PhysicalDisk unavailable: " + ex.Message); }

            using var disks = new ManagementObjectSearcher(
                "SELECT Model, Size, InterfaceType, FirmwareRevision, Index FROM Win32_DiskDrive");
            foreach (ManagementBaseObject d in disks.Get())
            {
                string model = d["Model"]?.ToString()?.Trim() ?? "Disk";
                var g = new SpecGroup(model.ToUpperInvariant(), UiGlyphs.Disk);
                ulong size = d["Size"] is ulong u ? u : 0;
                if (size > 0) g.Add("Capacity", SpecFormat.DiskBytes(size));

                string index = d["Index"]?.ToString() ?? "";
                if (extras.TryGetValue(index, out var x))
                {
                    g.Add("Bus", x.Bus);
                    g.Add("Kind", x.Media);
                    g.Add("Health", x.Health);
                }
                else
                {
                    g.Add("Interface", d["InterfaceType"]?.ToString() ?? "");
                }
                g.Add("Firmware", d["FirmwareRevision"]?.ToString()?.Trim() ?? "");
                groups.Add(g);
            }

            if (groups.Count == 0)
                groups.Add(new SpecGroup("STORAGE", UiGlyphs.Disk).AddAlways("Disks", ""));
            return groups;
        }

        private static int ToInt(object? o) => o switch
        {
            null => -1,
            ushort u => u,
            short s => s,
            int i => i,
            uint u => (int)u,
            _ => int.TryParse(o.ToString(), out int v) ? v : -1,
        };

        private static string BusTypeName(int code) => code switch
        {
            3 => "ATA", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
            11 => "SATA", 16 => "SD", 17 => "NVMe", _ => "",
        };

        private static string MediaTypeName(int code) => code switch
        {
            3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "",
        };

        private static string HealthName(int code) => code switch
        {
            0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "",
        };

        // ----- System ------------------------------------------------------------------------

        private static List<SpecGroup> BuildSystem(CpuIdentity? cpuid)
        {
            var groups = new List<SpecGroup>();
            var win = new SpecGroup("WINDOWS", UiGlyphs.System);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var v = Environment.OSVersion.Version;
                string edition = key?.GetValue("EditionID") as string ?? "";
                string displayVersion = key?.GetValue("DisplayVersion") as string ?? "";
                int ubr = key?.GetValue("UBR") is int u ? u : 0;

                // ProductName still says "Windows 10" on Windows 11, so the name comes from the
                // same build threshold the panel header uses.
                string name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
                win.Add("Edition", (name + " " + edition).Trim());
                win.Add("Version", displayVersion);
                win.Add("Build", v.Build + (ubr > 0 ? "." + ubr : ""));
            }
            catch { win.AddAlways("Edition", ""); }
            win.Add("Architecture", RuntimeInformation.OSArchitecture.ToString());
            win.Add("Hypervisor", cpuid?.HypervisorPresent == true ? "Present" : "");
            groups.Add(win);

            var env = new SpecGroup("ENVIRONMENT", UiGlyphs.Activity);
            env.Add("Machine", Environment.MachineName);
            env.Add("Uptime", SystemInfoProvider.FormatUptime(SystemInfoProvider.Uptime));
            env.Add(".NET Runtime", Environment.Version.ToString());
            env.Add("MicaStats", AppVersion);
            env.Add("Data Folder", DiagnosticsLog.DataDir);
            groups.Add(env);

            return groups;
        }

        private static string BuildSummary(HardwareSnapshot snap, SmbiosData smbios, CpuIdentity? cpuid)
        {
            string cpu = cpuid?.BrandString is { Length: > 0 } b ? b : "unknown CPU";
            string board = smbios.Baseboard != null
                ? (smbios.Baseboard.Manufacturer + " " + smbios.Baseboard.Product).Trim()
                : "unknown board";
            int dimms = smbios.MemoryDevices.Count(d => d.IsPopulated);
            return cpu + " | " + board + " | " + dimms + " DIMM(s) | " +
                   snap.Tabs.Count + " sections";
        }

        // ----- Native ------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
            public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);
    }

    /// <summary>
    /// Live effective core clock for the window's header strip: the base clock scaled by the
    /// "% Processor Performance" counter, which tracks turbo above 100%. This is the honest
    /// user-mode substitute for CPU-Z's per-core MSR clock reads.
    /// </summary>
    public sealed class CpuClockMonitor : IDisposable
    {
        private readonly PerformanceCounter? _perf;
        private readonly double _baseMhz;

        public CpuClockMonitor()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                _baseMhz = key?.GetValue("~MHz") is int m ? m : 0;
            }
            catch { _baseMhz = 0; }

            if (_baseMhz <= 0) return;
            try
            {
                _perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                _perf.NextValue(); // prime; the first sample of a rate counter is always 0
            }
            catch (Exception ex)
            {
                _perf = null;
                DiagnosticsLog.Warn("hardware", "Processor performance counter unavailable: " + ex.Message);
            }
        }

        public double BaseMhz => _baseMhz;

        public double? ReadMhz()
        {
            if (_perf == null || _baseMhz <= 0) return null;
            try
            {
                float pct = _perf.NextValue();
                return pct > 1 ? _baseMhz * pct / 100.0 : null;
            }
            catch { return null; }
        }

        public void Dispose() => _perf?.Dispose();
    }
}
