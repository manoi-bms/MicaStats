using System.Collections.Generic;
using System.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// ACPI thermal zones, read through the Thermal Zone Information performance counters.
    ///
    /// <para>
    /// Windows exposes the same firmware data twice: <c>root\WMI</c>'s
    /// <c>MSAcpi_ThermalZoneTemperature</c>, which is ACL'd to administrators, and this
    /// counter set, which any user can read. Same source, different gate — which is the only
    /// reason this works unelevated.
    /// </para>
    ///
    /// <para>
    /// Labelled "System" and never marked <see cref="SensorReading.IsCpuDie"/>. Two load ramps
    /// from different thermal starting points showed it moving in opposite directions under
    /// identical CPU load: from a cold start with the fans idle it rose 64°C to 71°C, and from
    /// a warm start with the fans already running it fell 72°C to 69°C. It reports the fan
    /// control loop's output, not the die's state. Resolution is 1K and the lag is roughly
    /// 20 seconds, but the direction reversal is the disqualifying property — a sensor that
    /// can fall while the CPU heats cannot stand in for the CPU.
    /// </para>
    ///
    /// <para>
    /// It is still worth showing: it is what the cooling system actually reacts to, and the
    /// passive limit derived from it is a genuine throttling signal.
    /// </para>
    /// </summary>
    public sealed class ThermalZoneSource : ISensorSource
    {
        private const string Category = "Thermal Zone Information";

        private readonly List<Zone> _zones = new();
        private bool _initialised;

        public string Name => "ACPI";

        public bool IsAvailable { get; private set; } = true;

        /// <summary>ACPI reports tenths of a kelvin; the panel wants Celsius.</summary>
        public static double DeciKelvinToCelsius(double deciKelvin) => deciKelvin / 10.0 - 273.15;

        public IReadOnlyList<SensorReading> Read()
        {
            var readings = new List<SensorReading>();
            try
            {
                if (!_initialised) { Initialise(); _initialised = true; }

                foreach (var zone in _zones)
                {
                    double c = DeciKelvinToCelsius(zone.Temp.NextValue());
                    if (c > 0 && c < 150)
                        readings.Add(new SensorReading("zone." + zone.Label, "System (" + zone.Label + ")",
                            SensorCategory.Temperature, c, "°C", Name));

                    // Below 100 means ACPI has begun passively limiting the processor. This is
                    // a firmware decision, so it is true regardless of what the die reads.
                    float limit = zone.Passive.NextValue();
                    if (limit > 0 && limit < 100)
                        readings.Add(new SensorReading("zone." + zone.Label + ".passive",
                            "Passive limit", SensorCategory.Throttle, limit, "%", Name));

                    // A bitmask of why Windows is currently limiting the processor. Sharper
                    // than the temperature it accompanies: it states the firmware's decision
                    // outright instead of leaving it to be inferred from a damped reading.
                    float reasons = zone.Throttle.NextValue();
                    if (reasons > 0)
                        readings.Add(new SensorReading("zone." + zone.Label + ".throttle",
                            "Throttled", SensorCategory.Throttle, reasons, "", Name));
                }

                IsAvailable = _zones.Count > 0;
            }
            catch
            {
                // Desktops frequently expose no zone at all; that is not an error.
                IsAvailable = false;
            }
            return readings;
        }

        private void Initialise()
        {
            if (!PerformanceCounterCategory.Exists(Category)) return;
            var cat = new PerformanceCounterCategory(Category);

            foreach (string instance in cat.GetInstanceNames())
            {
                // Trim the ACPI path down to the zone name: "\_TZ.TZ01" -> "TZ01".
                int dot = instance.LastIndexOf('.');
                string label = dot >= 0 && dot < instance.Length - 1 ? instance[(dot + 1)..] : instance;

                _zones.Add(new Zone(
                    new PerformanceCounter(Category, "High Precision Temperature", instance, readOnly: true),
                    new PerformanceCounter(Category, "% Passive Limit", instance, readOnly: true),
                    new PerformanceCounter(Category, "Throttle Reasons", instance, readOnly: true),
                    label));
            }
        }

        private sealed record Zone(
            PerformanceCounter Temp,
            PerformanceCounter Passive,
            PerformanceCounter Throttle,
            string Label);
    }
}
