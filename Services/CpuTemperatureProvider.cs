using System;
using System.IO.MemoryMappedFiles;
using System.Management;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Reads the real CPU package/core temperature without elevation.
    ///
    /// <para>
    /// The die sensors (AMD Tctl, Intel DTS) are ring-0 territory: every tool that shows them
    /// (Core Temp, HWiNFO, LibreHardwareMonitor) loads a kernel driver. An unelevated app
    /// cannot, so this provider reads what those tools publish instead, in order of fidelity:
    /// </para>
    /// <list type="number">
    /// <item>Core Temp's shared-memory block — the exact numbers Core Temp displays.</item>
    /// <item>LibreHardwareMonitor / OpenHardwareMonitor WMI, when either app is running.</item>
    /// </list>
    /// <para>
    /// The hottest core is reported, never an average. When no publisher is running this
    /// returns -1 and the UI shows the reading as unavailable rather than inventing one from
    /// an ACPI skin sensor that does not track the die.
    /// </para>
    /// </summary>
    internal sealed class CpuTemperatureProvider
    {
        // CORE_TEMP_SHARED_DATA layout (Core Temp shared memory documentation):
        // uint uiLoad[256]; uint uiTjMax[128]; uint uiCoreCnt; uint uiCpuCnt; float fTemp[256];
        // float fVID; float fCPUSpeed; float fFSBSpeed; float fMultiplier; char sCPUName[100];
        // byte ucFahrenheit; byte ucDeltaToTjMax;
        private const int OffTjMax = 256 * 4;
        private const int OffCoreCnt = OffTjMax + 128 * 4;      // 1536
        private const int OffCpuCnt = OffCoreCnt + 4;           // 1540
        private const int OffTemp = OffCpuCnt + 4;              // 1544
        private const int OffFlags = OffTemp + 256 * 4 + 4 * 4 + 100; // 2684: ucFahrenheit
        private const int MapLength = OffFlags + 2;

        private DateTime _coreTempRetryAfter = DateTime.MinValue;
        private DateTime _wmiRetryAfter = DateTime.MinValue;
        private string? _workingWmiNamespace;

        /// <summary>Hottest CPU core in Celsius, or -1 when no source is available.</summary>
        public float Read()
        {
            float t = ReadCoreTemp();
            if (t > 0) return t;

            t = ReadHardwareMonitorWmi();
            return t > 0 ? t : -1f;
        }

        private float ReadCoreTemp()
        {
            if (DateTime.UtcNow < _coreTempRetryAfter) return -1f;
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting("CoreTempMappingObject", MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, MapLength, MemoryMappedFileAccess.Read);

                uint coreCnt = view.ReadUInt32(OffCoreCnt);
                uint cpuCnt = view.ReadUInt32(OffCpuCnt);
                if (coreCnt == 0 || coreCnt > 128 || cpuCnt == 0 || cpuCnt > 4) return -1f;

                bool fahrenheit = view.ReadByte(OffFlags) != 0;
                bool deltaToTjMax = view.ReadByte(OffFlags + 1) != 0;

                int count = (int)Math.Min(coreCnt * cpuCnt, 256);
                float max = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    float value = view.ReadSingle(OffTemp + i * 4);
                    if (deltaToTjMax)
                    {
                        // Stored as distance below TjMax for this value's package.
                        uint tj = view.ReadUInt32(OffTjMax + (int)(i / coreCnt) * 4);
                        value = tj - value;
                    }
                    if (value > max) max = value;
                }
                if (max <= float.MinValue) return -1f;

                if (fahrenheit) max = (max - 32f) * 5f / 9f;
                return max > 0f && max < 150f ? max : -1f;
            }
            catch (System.IO.FileNotFoundException)
            {
                // Core Temp is not running; do not probe again for a while.
                _coreTempRetryAfter = DateTime.UtcNow.AddSeconds(30);
                return -1f;
            }
            catch
            {
                _coreTempRetryAfter = DateTime.UtcNow.AddSeconds(30);
                return -1f;
            }
        }

        private float ReadHardwareMonitorWmi()
        {
            if (DateTime.UtcNow < _wmiRetryAfter) return -1f;

            // Whichever namespace answered last time is tried alone; probing a missing WMI
            // namespace throws, and doing that twice a second would be wasteful.
            string[] namespaces = _workingWmiNamespace != null
                ? new[] { _workingWmiNamespace }
                : new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };

            foreach (string ns in namespaces)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(ns,
                        "SELECT Value, Identifier FROM Sensor WHERE SensorType='Temperature'");
                    using var results = searcher.Get();

                    float max = float.MinValue;
                    foreach (ManagementObject obj in results)
                    {
                        try
                        {
                            string id = obj["Identifier"] as string ?? "";
                            if (id.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            float value = Convert.ToSingle(obj["Value"]);
                            if (value > max) max = value;
                        }
                        finally { obj.Dispose(); }
                    }

                    if (max > 0f && max < 150f)
                    {
                        _workingWmiNamespace = ns;
                        return max;
                    }
                }
                catch
                {
                    // Namespace missing or empty; fall through to the next candidate.
                }
            }

            _workingWmiNamespace = null;
            _wmiRetryAfter = DateTime.UtcNow.AddSeconds(60);
            return -1f;
        }
    }
}
