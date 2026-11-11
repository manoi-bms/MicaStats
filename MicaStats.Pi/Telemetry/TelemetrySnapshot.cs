using System;
using System.Collections.Generic;

namespace MicaStats.Pi.Telemetry;

public sealed record ProcessSample(int Pid, string Name, float CpuPercent, double MemMb);

/// <summary>One tick's readings. Nullable fields mean "could not be read" — render a dash, not zero.</summary>
public sealed record TelemetrySnapshot
{
    public float CpuPercent { get; init; }
    public int CoreCount { get; init; }
    public float LoadAvg1 { get; init; }
    public double? CpuFreqMhz { get; init; }

    public float MemPercent { get; init; }
    public double MemUsedGb { get; init; }
    public double MemTotalGb { get; init; }
    public double SwapUsedGb { get; init; }
    public double SwapTotalGb { get; init; }

    public float? TempC { get; init; }

    public string NetInterface { get; init; } = "";
    public double NetUpBytesPerSec { get; init; }
    public double NetDownBytesPerSec { get; init; }

    public float DiskUsedPercent { get; init; }
    public double DiskFreeGb { get; init; }
    public double DiskTotalGb { get; init; }
    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }

    public TimeSpan Uptime { get; init; }

    /// <summary>Refreshed every few ticks; null means "not refreshed this tick".</summary>
    public IReadOnlyList<ProcessSample>? TopProcesses { get; init; }
}
