namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>What kind of quantity a reading carries, for grouping in the panel.</summary>
    public enum SensorCategory { Temperature, Fan, Power, Throttle }

    /// <summary>
    /// One value from one source at one instant.
    ///
    /// <para>
    /// <see cref="IsCpuDie"/> defaults to false and may only be set true by a publisher
    /// source — a tool with a kernel driver reporting the CPU's own die sensor. The ACPI
    /// thermal zone and the GPU adapters both produce plausible-looking Celsius values that
    /// are not the die: the zone sits downstream of the fan control loop and was measured
    /// falling from 72°C to 69°C while 24 threads saturated the CPU, and the integrated GPU
    /// moved 2°C over the same ramp despite sharing silicon with the cores. Selecting on this
    /// flag rather than on <see cref="Category"/> is what stops either of them being shown as
    /// a CPU temperature.
    /// </para>
    /// </summary>
    /// <param name="Id">Stable key, e.g. "zone.TZ01" or "gpu.AMD Radeon(TM) 890M Graphics.temp".</param>
    /// <param name="Label">What the panel shows, e.g. "System (TZ01)".</param>
    /// <param name="Value">The reading, in <paramref name="Unit"/>.</param>
    /// <param name="Source">Provenance, e.g. "Core Temp" — shown so a number can be traced.</param>
    public sealed record SensorReading(
        string Id,
        string Label,
        SensorCategory Category,
        double Value,
        string Unit,
        string Source,
        bool IsCpuDie = false);
}
