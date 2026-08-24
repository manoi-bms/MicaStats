using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// One place readings come from.
    ///
    /// <para>
    /// Implementations own their own failure handling: a source whose backing tool is absent
    /// returns an empty list rather than throwing, so one dead source cannot stall a telemetry
    /// tick. Most machines will have several dead sources at once — that is the normal case,
    /// not an error state.
    /// </para>
    /// </summary>
    public interface ISensorSource
    {
        /// <summary>Shown as a reading's provenance, e.g. "Core Temp".</summary>
        string Name { get; }

        /// <summary>False once the source has been probed and found absent.</summary>
        bool IsAvailable { get; }

        /// <summary>Current readings, or an empty list when unavailable.</summary>
        IReadOnlyList<SensorReading> Read();
    }
}
