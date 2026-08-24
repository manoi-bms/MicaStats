using Kil0bitSystemMonitor.Services.Sensors;
using Kil0bitSystemMonitor.Services.Sensors.Publishers;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Reads the real CPU package/core temperature without elevation.
    ///
    /// <para>
    /// The die sensors (AMD Tctl, Intel DTS) are ring-0 territory: every tool that shows them
    /// loads a kernel driver. An unelevated app cannot, so this reads what those tools publish
    /// instead — Core Temp, HWiNFO, MSI Afterburner and AIDA64 shared memory, then
    /// LibreHardwareMonitor / OpenHardwareMonitor WMI, in that order of preference.
    /// </para>
    ///
    /// <para>
    /// The hottest core is reported, never an average. When no publisher is running this
    /// returns -1 and the UI shows the reading as unavailable rather than inventing one from
    /// an ACPI zone that does not track the die — measurement showed that zone moving in the
    /// opposite direction to the die under sustained load, because it follows the fan control
    /// loop rather than the silicon.
    /// </para>
    ///
    /// <para>
    /// The reading logic itself lives in <see cref="Sensors"/>; this type remains as the
    /// narrow "give me a die temperature" entry point its existing callers expect.
    /// </para>
    /// </summary>
    internal sealed class CpuTemperatureProvider
    {
        private readonly SensorRegistry _registry = new(new ISensorSource[]
        {
            new CoreTempSource(),
            new HwInfoSource(),
            new AfterburnerSource(),
            new AidaSource(),
            new HardwareMonitorWmiSource(),
        });

        /// <summary>Hottest CPU core in Celsius, or -1 when no source is available.</summary>
        public float Read()
        {
            _registry.Snapshot();
            return (float)_registry.CpuDieTemperature;
        }
    }
}
