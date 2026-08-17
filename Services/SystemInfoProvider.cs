using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>Static hardware and OS identity, resolved once per process.</summary>
    public sealed record SystemInfo(string CpuName, string GpuName, ulong TotalRamBytes, string OsVersion)
    {
        public static readonly SystemInfo Unknown = new("Unknown processor", "Unknown display adapter", 0, "Windows");

        /// <summary>Total physical memory rendered for display, e.g. "16.0 GB".</summary>
        public string TotalRamText => TotalRamBytes == 0
            ? "Unknown"
            : $"{TotalRamBytes / 1024d / 1024d / 1024d:F1} GB";
    }

    /// <summary>
    /// Resolves the values shown in the detail panel's header.
    ///
    /// <para>
    /// The GPU name comes from WMI, which is slow enough that <c>SettingsWindow</c> already pushes
    /// the equivalent query onto a background thread. Resolution therefore happens once at startup
    /// off the UI thread and the result is cached, so opening the panel never blocks on WMI.
    /// </para>
    /// </summary>
    public static class SystemInfoProvider
    {
        private static volatile SystemInfo? _cached;

        /// <summary>The resolved info, or a placeholder until resolution completes.</summary>
        public static SystemInfo Current => _cached ?? SystemInfo.Unknown;

        /// <summary>True once resolution has finished.</summary>
        public static bool IsResolved => _cached != null;

        /// <summary>Raised on a background thread once resolution completes.</summary>
        public static event Action? Resolved;

        /// <summary>Starts resolution on a background thread. Safe to call more than once.</summary>
        public static void BeginResolve()
        {
            if (_cached != null) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _cached = Resolve();
                    Resolved?.Invoke();
                }
                catch
                {
                    // Header detail is cosmetic; never let it take down startup.
                }
            });
        }

        private static SystemInfo Resolve()
        {
            return new SystemInfo(ReadCpuName(), ReadGpuName(), ReadTotalRam(), ReadOsVersion());
        }

        /// <summary>
        /// Reads the processor name from the registry rather than Win32_Processor. The registry
        /// value is populated by the firmware at boot and costs microseconds; the WMI class costs
        /// hundreds of milliseconds on first touch.
        /// </summary>
        private static string ReadCpuName()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch { }
            return SystemInfo.Unknown.CpuName;
        }

        private static string ReadGpuName()
        {
            try
            {
                var gpus = TelemetryService.GetAvailableGpus();
                if (gpus.Count > 0)
                {
                    // Prefer a discrete adapter when several are present, matching the sensor
                    // selection heuristic used for telemetry.
                    foreach (var g in gpus)
                    {
                        if (g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            g.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                            g.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                            return g;
                    }
                    return gpus[0];
                }
            }
            catch { }
            return SystemInfo.Unknown.GpuName;
        }

        private static ulong ReadTotalRam()
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(status)) return status.ullTotalPhys;
            }
            catch { }
            return 0;
        }

        private static string ReadOsVersion()
        {
            try
            {
                // On .NET Core this reports the true OS version rather than a shimmed one, so no
                // RtlGetVersion P/Invoke is needed.
                var v = Environment.OSVersion.Version;
                string name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
                return $"{name} (build {v.Build})";
            }
            catch { }
            return SystemInfo.Unknown.OsVersion;
        }

        /// <summary>System uptime, from a monotonic tick count.</summary>
        public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

        public static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
            return $"{t.Minutes}m {t.Seconds}s";
        }

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
    }
}
