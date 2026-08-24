using System;
using System.Collections.Generic;
using System.Management;

namespace Kil0bitSystemMonitor.Services.Sensors.Publishers
{
    /// <summary>
    /// CPU die temperature from LibreHardwareMonitor or OpenHardwareMonitor, whichever is
    /// running. Both publish a WMI namespace while open; neither leaves anything behind when
    /// closed, so an absent namespace is the ordinary case rather than a fault.
    /// </summary>
    public sealed class HardwareMonitorWmiSource : ISensorSource
    {
        private static readonly string[] Candidates =
        {
            @"root\LibreHardwareMonitor",
            @"root\OpenHardwareMonitor",
        };

        private DateTime _retryAfter = DateTime.MinValue;
        private string? _workingNamespace;

        public string Name { get; private set; } = "LibreHardwareMonitor";

        public bool IsAvailable { get; private set; } = true;

        public IReadOnlyList<SensorReading> Read()
        {
            if (DateTime.UtcNow < _retryAfter) return Array.Empty<SensorReading>();

            // Whichever namespace answered last time is tried alone: probing a missing WMI
            // namespace throws, and doing that twice a second would be wasteful.
            string[] namespaces = _workingNamespace != null ? new[] { _workingNamespace } : Candidates;

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
                        _workingNamespace = ns;
                        Name = ns.EndsWith("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase)
                            ? "LibreHardwareMonitor"
                            : "OpenHardwareMonitor";
                        IsAvailable = true;

                        return new[]
                        {
                            new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature,
                                              max, "°C", Name, IsCpuDie: true),
                        };
                    }
                }
                catch
                {
                    // Namespace missing or empty; fall through to the next candidate.
                }
            }

            _workingNamespace = null;
            _retryAfter = DateTime.UtcNow.AddSeconds(60);
            IsAvailable = false;
            return Array.Empty<SensorReading>();
        }
    }
}
