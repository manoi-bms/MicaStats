# Built-in Sensor Monitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface every thermal, fan, power and throttle reading MicaStats can obtain without a kernel driver or elevation, in a new SENSORS card, and stop leaving a silent blank where the CPU temperature belongs.

**Architecture:** A new `Services/Sensors/` namespace built around one small `ISensorSource` interface. A `SensorRegistry` owns the sources, applies per-source backoff, and produces one snapshot per telemetry tick. Sources fall into three groups: the ACPI thermal zone (perf counters), GPU adapters (D3DKMT), and CPU-die publishers (other tools' shared memory / WMI). Only publisher sources may set `IsCpuDie`, which is what makes it structurally impossible for a chassis zone reading to be presented as a CPU temperature.

**Tech Stack:** C# 12, .NET 8 (`net8.0-windows`, WPF), xUnit 2.9.2. No new NuGet packages — `System.Diagnostics.PerformanceCounter` 8.0.0 and `System.Management` 8.0.0 are already referenced.

## Global Constraints

- **Build/test SDK:** there is no system-wide SDK. Use `C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe` (8.0.424). `C:\Program Files\dotnet\dotnet.exe` is runtime-only and fails with "No .NET SDKs were found".
- **Test command:** `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" test tests\Kil0bitSystemMonitor.Tests\Kil0bitSystemMonitor.Tests.csproj`
- **Baseline:** 380 tests pass before this plan starts. Every task must leave the whole suite green.
- **No elevation, no kernel driver, ever.** Any change that would require admin is out of scope.
- **No new NuGet packages.**
- **Namespace:** `Kil0bitSystemMonitor.Services.Sensors` (publishers in `.Publishers`).
- **A zone or GPU reading must never be reported as a CPU die temperature.** Enforced by `IsCpuDie`, tested in Task 8.
- **Culture:** every `ToString` on a number or date uses `CultureInfo.InvariantCulture`. The dev machine runs a Thai locale whose default calendar stamps years as 2569.
- **The project sets `UseWindowsForms` and `UseWPF` together.** In any file touching UI types, alias `Color`, `Control`, `Button`, `HorizontalAlignment` explicitly — see `Helpers/ToastButton.cs:9-14`. Files in `Services/Sensors/` should need no UI types at all.
- **Version:** bump to `1.8.0` in Task 11 only.

---

### Task 1: Sensor primitives

**Files:**
- Create: `Services/Sensors/SensorReading.cs`
- Create: `Services/Sensors/ISensorSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `SensorCategory` enum; `SensorReading` record `(string Id, string Label, SensorCategory Category, double Value, string Unit, string Source, bool IsCpuDie = false)`; `ISensorSource` with `string Name { get; }`, `bool IsAvailable { get; }`, `IReadOnlyList<SensorReading> Read()`.

- [ ] **Step 1: Write the failing test**

```csharp
using Kil0bitSystemMonitor.Services.Sensors;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SensorsTests
    {
        [Fact]
        public void A_reading_is_not_a_cpu_die_reading_unless_it_says_so()
        {
            var r = new SensorReading("zone.0", "System", SensorCategory.Temperature, 69.0, "°C", "ACPI");
            Assert.False(r.IsCpuDie);
        }

        [Fact]
        public void A_publisher_may_mark_a_reading_as_cpu_die()
        {
            var r = new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature,
                                      71.0, "°C", "Core Temp", IsCpuDie: true);
            Assert.True(r.IsCpuDie);
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" test tests\Kil0bitSystemMonitor.Tests\Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~SensorsTests"`
Expected: FAIL — `The type or namespace name 'Sensors' does not exist`.

- [ ] **Step 3: Write the implementation**

`Services/Sensors/SensorReading.cs`:

```csharp
namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>What kind of quantity a reading carries, for grouping in the panel.</summary>
    public enum SensorCategory { Temperature, Fan, Power, Throttle }

    /// <summary>
    /// One value from one source at one instant.
    ///
    /// <para>
    /// <see cref="IsCpuDie"/> defaults to false and may only be set true by a publisher
    /// source - a tool with a kernel driver reporting the CPU's own die sensor. The ACPI
    /// thermal zone and the GPU adapters both produce plausible-looking Celsius values that
    /// are not the die: the zone sits downstream of the fan control loop and was measured
    /// falling from 72C to 69C while 24 threads saturated the CPU, and the integrated GPU
    /// moved 2C over the same ramp. Selecting on this flag rather than on category is what
    /// stops either of them being shown as a CPU temperature.
    /// </para>
    /// </summary>
    public sealed record SensorReading(
        string Id,
        string Label,
        SensorCategory Category,
        double Value,
        string Unit,
        string Source,
        bool IsCpuDie = false);
}
```

`Services/Sensors/ISensorSource.cs`:

```csharp
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// One place readings come from. Implementations own their own failure handling: a source
    /// whose backing tool is absent returns an empty list, never throws, so one dead source
    /// cannot stall a telemetry tick.
    /// </summary>
    public interface ISensorSource
    {
        /// <summary>Shown as the reading's provenance, e.g. "Core Temp".</summary>
        string Name { get; }

        /// <summary>False once the source has been probed and found absent.</summary>
        bool IsAvailable { get; }

        /// <summary>Current readings, or an empty list when unavailable.</summary>
        IReadOnlyList<SensorReading> Read();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the filtered command from Step 2. Expected: 2 passed. Then the full suite: 382 passed.

- [ ] **Step 5: Commit**

```bash
git add Services/Sensors/SensorReading.cs Services/Sensors/ISensorSource.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): reading and source primitives"
```

---

### Task 2: Fix the D3DKMT layout and add AdapterPerfSource

This task carries the highest-value change in the plan: the shipped D3DKMT path has never worked.

**Files:**
- Create: `Services/Sensors/AdapterPerfData.cs` (interop types + layout)
- Create: `Services/Sensors/AdapterPerfSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: `SensorReading`, `SensorCategory`, `ISensorSource` from Task 1.
- Produces: `AdapterPerfData.QueryTypePerfData` (= 62), `AdapterPerfData.QueryTypePerfDataCaps` (= 63), struct `D3DKMT_ADAPTER_PERFDATA`, `AdapterPerfSource : ISensorSource`.

**Background the implementer needs:** `Services/TelemetryService.cs:1235-1274` declares `KMTQAITYPE_ADAPTERPERFDATA = 35` and a ~96-byte, 13-field struct. Both are wrong. Measured by sweeping query types 0-89 against buffer sizes 8-160 on three adapters: the correct type is **62**, the block is **64 bytes**, and `Temperature` (deci-Celsius) sits at **offset 56**. The wrong values produce `STATUS_INVALID_PARAMETER` (`0xC000000D`) on every call, which the old code swallows.

- [ ] **Step 1: Write the failing test**

Add to `SensorsTests.cs` (add `using System.Runtime.InteropServices;` at the top):

```csharp
        // These assertions are why the shipped path failed silently: the kernel validates
        // PrivateDriverDataSize, and a wrong size is rejected with the same status as a wrong
        // query type. Pin both.
        [Fact]
        public void The_adapter_perf_block_matches_the_measured_layout()
        {
            Assert.Equal(64, Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>());
            Assert.Equal(56, Marshal.OffsetOf<D3DKMT_ADAPTER_PERFDATA>(
                nameof(D3DKMT_ADAPTER_PERFDATA.Temperature)).ToInt32());
            Assert.Equal(48, Marshal.OffsetOf<D3DKMT_ADAPTER_PERFDATA>(
                nameof(D3DKMT_ADAPTER_PERFDATA.FanRPM)).ToInt32());
            Assert.Equal(62, AdapterPerfData.QueryTypePerfData);
        }

        // Runs on whatever hardware CI has, including none. The contract is that it never
        // throws and never invents a reading.
        [Fact]
        public void The_adapter_source_never_throws_and_reports_only_plausible_temperatures()
        {
            var readings = new AdapterPerfSource().Read();

            foreach (var r in readings)
            {
                Assert.False(r.IsCpuDie);
                if (r.Category == SensorCategory.Temperature)
                    Assert.InRange(r.Value, 1.0, 150.0);
            }
        }
```

- [ ] **Step 2: Run it to verify it fails**

Run the filtered command. Expected: FAIL — `D3DKMT_ADAPTER_PERFDATA` not found.

- [ ] **Step 3: Write the interop types**

`Services/Sensors/AdapterPerfData.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// D3DKMT adapter performance data: GPU temperature, fan and power without a driver or
    /// elevation, for any vendor.
    ///
    /// <para>
    /// The query type and struct layout here were established by measurement, not from a
    /// header. Sweeping query types 0-89 against buffer sizes 8-160 on three adapters (an
    /// integrated Radeon 890M, a discrete RTX PRO 1000, and a render-only adapter) found type
    /// 62 answering with a 64-byte block whose offset 56 holds a plausible deci-Celsius on
    /// both real GPUs. The published layout in d3dkmthk.h does not match what this driver
    /// stack accepts, and the kernel validates PrivateDriverDataSize, so a struct that merely
    /// looks right fails with STATUS_INVALID_PARAMETER exactly like a wrong query type.
    /// </para>
    /// </summary>
    public static class AdapterPerfData
    {
        public const int QueryTypeRegistryInfo = 8;
        public const int QueryTypePerfData = 62;
        public const int QueryTypePerfDataCaps = 63;

        [DllImport("gdi32.dll")] public static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPresentMoveRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    /// <summary>Explicit offsets: the size is what the kernel validates. See AdapterPerfData.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct D3DKMT_ADAPTER_PERFDATA
    {
        [FieldOffset(0)] public uint PhysicalAdapterIndex;   // INPUT
        [FieldOffset(8)] public ulong MemoryFrequency;
        [FieldOffset(16)] public ulong MaxMemoryFrequency;
        [FieldOffset(24)] public ulong MaxMemoryFrequencyOC;
        [FieldOffset(32)] public ulong MemoryBandwidthUtilized;
        [FieldOffset(48)] public uint FanRPM;
        [FieldOffset(52)] public uint Power;                 // tenths of a percent of max
        [FieldOffset(56)] public uint Temperature;           // deci-Celsius
        [FieldOffset(60)] public byte PowerLimitThrottle;
        [FieldOffset(61)] public byte TemperatureLimitThrottle;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct D3DKMT_ADAPTER_PERFDATACAPS
    {
        [FieldOffset(0)] public uint PhysicalAdapterIndex;
        [FieldOffset(8)] public ulong MaxMemoryBandwidth;
        [FieldOffset(16)] public ulong MaxMemoryFrequency;
        [FieldOffset(24)] public uint MaxFanRPM;
        [FieldOffset(28)] public uint TemperatureMax;
        [FieldOffset(32)] public uint TemperatureWarning;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }
}
```

- [ ] **Step 4: Write the source**

`Services/Sensors/AdapterPerfSource.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Kil0bitSystemMonitor.Services.Sensors.AdapterPerfData;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// Every GPU's temperature, fan, power draw and throttle state, read directly from the
    /// display kernel. Replaces the nvidia-smi subprocess the app kept alive for the same
    /// reading, and gives AMD-only machines a GPU temperature they never had.
    /// </summary>
    public sealed class AdapterPerfSource : ISensorSource
    {
        private readonly List<(LUID Luid, string Name)> _adapters = new();
        private bool _enumerated;

        public string Name => "Display kernel";

        public bool IsAvailable { get; private set; } = true;

        public IReadOnlyList<SensorReading> Read()
        {
            var readings = new List<SensorReading>();
            try
            {
                if (!_enumerated) { Enumerate(); _enumerated = true; }

                foreach (var (luid, name) in _adapters)
                {
                    if (!TryReadPerf(luid, out var pd)) continue;

                    string id = "gpu." + name;
                    double celsius = pd.Temperature / 10.0;

                    // A driver that fills only some fields must not yield a 0C reading.
                    if (celsius > 0 && celsius < 150)
                        readings.Add(new SensorReading(id + ".temp", name,
                            SensorCategory.Temperature, celsius, "°C", Name));

                    if (pd.FanRPM > 0)
                        readings.Add(new SensorReading(id + ".fan", name + " fan",
                            SensorCategory.Fan, pd.FanRPM, "RPM", Name));

                    if (pd.Power > 0)
                        readings.Add(new SensorReading(id + ".power", name + " power",
                            SensorCategory.Power, pd.Power / 10.0, "%", Name));

                    if (pd.TemperatureLimitThrottle != 0)
                        readings.Add(new SensorReading(id + ".tthrottle", name + " thermal limit",
                            SensorCategory.Throttle, 1, "", Name));

                    if (pd.PowerLimitThrottle != 0)
                        readings.Add(new SensorReading(id + ".pthrottle", name + " power limit",
                            SensorCategory.Throttle, 1, "", Name));
                }

                IsAvailable = readings.Count > 0;
            }
            catch
            {
                // gdi32 absent, or the display stack is mid-reset; report nothing this tick.
                IsAvailable = false;
            }
            return readings;
        }

        private void Enumerate()
        {
            var e = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;

            int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * size);
                    string name = ReadAdapterName(info.hAdapter);
                    if (!string.IsNullOrWhiteSpace(name)) _adapters.Add((info.AdapterLuid, name));
                }
            }
            finally { Marshal.FreeHGlobal(e.pAdapters); }
        }

        private static string ReadAdapterName(uint hAdapter)
        {
            int size = Marshal.SizeOf<D3DKMT_ADAPTERREGISTRYINFO>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = hAdapter,
                    Type = QueryTypeRegistryInfo,
                    pPrivateDriverData = buf,
                    PrivateDriverDataSize = (uint)size,
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return "";
                return Marshal.PtrToStructure<D3DKMT_ADAPTERREGISTRYINFO>(buf).AdapterString ?? "";
            }
            catch { return ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static bool TryReadPerf(LUID luid, out D3DKMT_ADAPTER_PERFDATA pd)
        {
            pd = default;
            var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return false;

            int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 }, buf, false);

                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter,
                    Type = QueryTypePerfData,
                    pPrivateDriverData = buf,
                    PrivateDriverDataSize = (uint)size,
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
                pd = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buf);
                return true;
            }
            catch { return false; }
            finally
            {
                Marshal.FreeHGlobal(buf);
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Expected: 4 passed.

- [ ] **Step 6: Verify against real hardware**

Run the scratchpad probe: `tempprobe\bin\Release\net8.0-windows\tempprobe.exe`

Expected on the dev machine: adapter [0] `AMD Radeon(TM) 890M Graphics` at 48-51 °C, adapter [1] `NVIDIA RTX PRO 1000` at 50-51 °C, adapter [2] `query failed` (render-only, correct to skip). If temperatures read 0 or absurd, the layout is wrong — stop and re-measure rather than proceeding.

- [ ] **Step 7: Commit**

```bash
git add Services/Sensors/AdapterPerfData.cs Services/Sensors/AdapterPerfSource.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "fix(sensors): correct D3DKMT query type and layout, add adapter source"
```

---

### Task 3: ThermalZoneSource

**Files:**
- Create: `Services/Sensors/ThermalZoneSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: Task 1 primitives.
- Produces: `ThermalZoneSource : ISensorSource`; `public static double DeciKelvinToCelsius(double deciKelvin)`.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void Deci_kelvin_converts_to_celsius()
        {
            // 3432 dK was the live reading when this was measured: 343.2K = 70.05C.
            Assert.Equal(70.05, ThermalZoneSource.DeciKelvinToCelsius(3432), 2);
            Assert.Equal(0.0, ThermalZoneSource.DeciKelvinToCelsius(2731.5), 1);
        }

        [Fact]
        public void The_zone_never_claims_to_be_the_cpu_die()
        {
            foreach (var r in new ThermalZoneSource().Read()) Assert.False(r.IsCpuDie);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `ThermalZoneSource` not found.

- [ ] **Step 3: Write the implementation**

`Services/Sensors/ThermalZoneSource.cs`:

```csharp
using System.Collections.Generic;
using System.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// ACPI thermal zones, read through the Thermal Zone Information performance counters.
    ///
    /// <para>
    /// Windows exposes the same firmware data twice: root\WMI MSAcpi_ThermalZoneTemperature,
    /// which is ACL'd to administrators, and this counter set, which any user can read. Same
    /// source, different gate - which is why this works unelevated.
    /// </para>
    ///
    /// <para>
    /// Labelled "System" and never marked IsCpuDie. Two load ramps from different thermal
    /// starting points showed it moving in opposite directions under identical CPU load: from
    /// a cold start with fans idle it rose 64C to 71C, and from a warm start with fans already
    /// running it fell 72C to 69C. It sits downstream of the fan control loop and reports that
    /// loop's output. Resolution is 1K and the lag is roughly 20s.
    /// </para>
    /// </summary>
    public sealed class ThermalZoneSource : ISensorSource
    {
        private const string Category = "Thermal Zone Information";

        private readonly List<(PerformanceCounter Temp, PerformanceCounter Passive, string Label)> _zones = new();
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

                foreach (var (temp, passive, label) in _zones)
                {
                    double c = DeciKelvinToCelsius(temp.NextValue());
                    if (c > 0 && c < 150)
                        readings.Add(new SensorReading("zone." + label, "System (" + label + ")",
                            SensorCategory.Temperature, c, "°C", Name));

                    // Below 100 means ACPI has begun passively limiting the processor.
                    float limit = passive.NextValue();
                    if (limit > 0 && limit < 100)
                        readings.Add(new SensorReading("zone." + label + ".passive",
                            "Passive limit", SensorCategory.Throttle, limit, "%", Name));
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
                // Trim the ACPI path to the zone name: "\_TZ.TZ01" -> "TZ01".
                int dot = instance.LastIndexOf('.');
                string label = dot >= 0 && dot < instance.Length - 1 ? instance[(dot + 1)..] : instance;

                _zones.Add((
                    new PerformanceCounter(Category, "High Precision Temperature", instance, readOnly: true),
                    new PerformanceCounter(Category, "% Passive Limit", instance, readOnly: true),
                    label));
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 6 passed. If `% Passive Limit` throws `InvalidOperationException`, list the real counter names with
`Get-Counter -ListSet "Thermal Zone Information" | Select-Object -ExpandProperty Counter`
and use the exact string returned.

- [ ] **Step 5: Commit**

```bash
git add Services/Sensors/ThermalZoneSource.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): ACPI thermal zone source"
```

---

### Task 4: Move the existing publishers into the namespace

**Files:**
- Create: `Services/Sensors/Publishers/CoreTempSource.cs`
- Create: `Services/Sensors/Publishers/HardwareMonitorWmiSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: Task 1 primitives.
- Produces: `CoreTempSource : ISensorSource` with `public static double DecodeHottest(byte[] block)`; `HardwareMonitorWmiSource : ISensorSource`.

**Background:** `Services/CpuTemperatureProvider.cs:67-159` already contains both readers. Move the logic, and **separate decoding from acquisition** so the Core Temp block can be decoded from a synthetic byte array with no tool installed. Preserve the existing offsets (`OffTjMax = 1024`, `OffCoreCnt = 1536`, `OffCpuCnt = 1540`, `OffTemp = 1544`, `OffFlags = 2684`), the delta-to-TjMax handling and the Fahrenheit flag. Leave `CpuTemperatureProvider` working as-is; Task 9 reduces it.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void Core_temp_block_decodes_the_hottest_core()
        {
            var block = new byte[2686];
            BitConverter.GetBytes(4u).CopyTo(block, 1536);   // uiCoreCnt
            BitConverter.GetBytes(1u).CopyTo(block, 1540);   // uiCpuCnt
            BitConverter.GetBytes(61.0f).CopyTo(block, 1544);
            BitConverter.GetBytes(74.5f).CopyTo(block, 1548);
            BitConverter.GetBytes(58.0f).CopyTo(block, 1552);
            BitConverter.GetBytes(66.0f).CopyTo(block, 1556);

            Assert.Equal(74.5, CoreTempSource.DecodeHottest(block), 1);
        }

        [Fact]
        public void Core_temp_block_handles_delta_to_tjmax()
        {
            var block = new byte[2686];
            BitConverter.GetBytes(2u).CopyTo(block, 1536);
            BitConverter.GetBytes(1u).CopyTo(block, 1540);
            BitConverter.GetBytes(100u).CopyTo(block, 1024);  // TjMax = 100C
            BitConverter.GetBytes(40.0f).CopyTo(block, 1544); // 40 below TjMax = 60C
            BitConverter.GetBytes(25.0f).CopyTo(block, 1548); // 25 below TjMax = 75C
            block[2685] = 1;                                  // ucDeltaToTjMax

            Assert.Equal(75.0, CoreTempSource.DecodeHottest(block), 1);
        }

        [Fact]
        public void Core_temp_block_rejects_an_implausible_core_count()
        {
            var block = new byte[2686];
            BitConverter.GetBytes(9999u).CopyTo(block, 1536);
            Assert.Equal(-1, CoreTempSource.DecodeHottest(block), 1);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `CoreTempSource` not found.

- [ ] **Step 3: Write `CoreTempSource`**

Move the offsets and loop from `CpuTemperatureProvider.ReadCoreTemp` into two members: `DecodeHottest(byte[])`, pure and tested above; and `Read()`, which opens `CoreTempMappingObject` via `MemoryMappedFile.OpenExisting`, copies `MapLength` bytes into an array, calls `DecodeHottest`, and emits one reading with `IsCpuDie: true` and `Source = "Core Temp"`. Keep the 30-second backoff on `FileNotFoundException`.

- [ ] **Step 4: Write `HardwareMonitorWmiSource`**

Move `CpuTemperatureProvider.ReadHardwareMonitorWmi` into `Read()`, emitting one reading with `IsCpuDie: true` and `Source` set to `"LibreHardwareMonitor"` or `"OpenHardwareMonitor"` depending on which namespace answered. Keep the 60-second backoff and the `_workingWmiNamespace` memo.

- [ ] **Step 5: Run the tests to verify they pass**

Expected: 9 passed.

- [ ] **Step 6: Commit**

```bash
git add Services/Sensors/Publishers/ tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "refactor(sensors): move Core Temp and hardware-monitor WMI into sources"
```

---

### Task 5: HwInfoSource

**Files:**
- Create: `Services/Sensors/Publishers/HwInfoSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: Task 1 primitives.
- Produces: `HwInfoSource : ISensorSource` with `public static double DecodeCpuTemperature(byte[] block)`.

**Layout.** Shared memory `Global\HWiNFO_SENS_SM2`. Header, 48 bytes:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | uint | `dwSignature` — must equal `0x53695748` |
| 4 | uint | `dwVersion` |
| 8 | uint | `dwRevision` |
| 16 | long | `poll_time` |
| 24 | uint | `dwOffsetOfSensorSection` |
| 28 | uint | `dwSizeOfSensorElement` |
| 32 | uint | `dwNumSensorElements` |
| 36 | uint | `dwOffsetOfReadingSection` |
| 40 | uint | `dwSizeOfReadingElement` |
| 44 | uint | `dwNumReadingElements` |

Reading element, 320 bytes:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | uint | `tReading` — 1 = temperature |
| 4 | uint | `dwSensorIndex` |
| 8 | uint | `dwReadingID` |
| 12 | char[128] | `szLabelOrig` (ASCII, NUL-terminated) |
| 140 | char[128] | `szLabelUser` |
| 268 | char[16] | `szUnit` |
| 288 | double | `Value` |

**Use `dwOffsetOfReadingSection` and `dwSizeOfReadingElement` from the header as the stride** rather than assuming 320 — that is what HWiNFO documents, and it keeps the reader working across revisions.

Select the hottest reading where `tReading == 1` and `szLabelOrig` contains "CPU" (case-insensitive) and does not contain "GPU". Return -1 if none.

- [ ] **Step 1: Write the failing test**

```csharp
        // Builds a synthetic HWiNFO block so the decoder is testable with HWiNFO absent.
        private static byte[] HwInfoBlock(params (uint type, string label, double value)[] items)
        {
            const int headerSize = 48, elementSize = 320;
            var block = new byte[headerSize + elementSize * items.Length];

            BitConverter.GetBytes(0x53695748u).CopyTo(block, 0);
            BitConverter.GetBytes((uint)headerSize).CopyTo(block, 36);   // offset of readings
            BitConverter.GetBytes((uint)elementSize).CopyTo(block, 40);  // stride
            BitConverter.GetBytes((uint)items.Length).CopyTo(block, 44);

            for (int i = 0; i < items.Length; i++)
            {
                int at = headerSize + i * elementSize;
                BitConverter.GetBytes(items[i].type).CopyTo(block, at);
                System.Text.Encoding.ASCII.GetBytes(items[i].label).CopyTo(block, at + 12);
                BitConverter.GetBytes(items[i].value).CopyTo(block, at + 288);
            }
            return block;
        }

        [Fact]
        public void Hwinfo_block_picks_the_hottest_cpu_temperature()
        {
            var block = HwInfoBlock(
                (1u, "CPU Package", 71.0),
                (1u, "CPU Core Max", 78.5),
                (1u, "GPU Temperature", 91.0),   // must be ignored
                (3u, "CPU Fan", 2400.0));        // fan, not temperature

            Assert.Equal(78.5, HwInfoSource.DecodeCpuTemperature(block), 1);
        }

        [Fact]
        public void Hwinfo_block_with_a_wrong_signature_is_refused()
        {
            var block = HwInfoBlock((1u, "CPU Package", 71.0));
            BitConverter.GetBytes(0xDEADBEEFu).CopyTo(block, 0);

            Assert.Equal(-1, HwInfoSource.DecodeCpuTemperature(block), 1);
        }

        [Fact]
        public void Hwinfo_block_with_no_cpu_temperature_returns_unavailable()
        {
            Assert.Equal(-1, HwInfoSource.DecodeCpuTemperature(
                HwInfoBlock((1u, "GPU Temperature", 91.0))), 1);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `HwInfoSource` not found.

- [ ] **Step 3: Write the decoder**

```csharp
using System;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Sensors.Publishers
{
    /// <summary>
    /// CPU die temperature as published by HWiNFO's shared memory.
    ///
    /// <para>
    /// HWiNFO requires "Shared Memory Support" to be switched on, and current free builds
    /// time-limit it to a fixed period per session. An expired session looks exactly like an
    /// absent one, and both are normal: the source reports unavailable and the UI shows a
    /// dash. Neither is a defect to be worked around.
    /// </para>
    /// </summary>
    public sealed class HwInfoSource : ISensorSource
    {
        private const string MapName = @"Global\HWiNFO_SENS_SM2";
        private const uint Signature = 0x53695748;

        private const int OffSignature = 0;
        private const int OffReadingSection = 36;
        private const int OffReadingStride = 40;
        private const int OffReadingCount = 44;

        // Within one reading element.
        private const int ElemType = 0;
        private const int ElemLabelOrig = 12;
        private const int ElemLabelLength = 128;
        private const int ElemValue = 288;

        private const uint ReadingTypeTemperature = 1;

        /// <summary>Hottest CPU temperature in the block, or -1 if it holds none.</summary>
        public static double DecodeCpuTemperature(byte[] block)
        {
            if (block == null || block.Length < 48) return -1;
            if (BitConverter.ToUInt32(block, OffSignature) != Signature) return -1;

            // Trust the header's own stride rather than a hard-coded element size: it is what
            // HWiNFO documents, and it survives a revision that grows the element.
            uint start = BitConverter.ToUInt32(block, OffReadingSection);
            uint stride = BitConverter.ToUInt32(block, OffReadingStride);
            uint count = BitConverter.ToUInt32(block, OffReadingCount);
            if (stride < ElemValue + 8 || count == 0 || count > 8192) return -1;

            double hottest = -1;
            for (uint i = 0; i < count; i++)
            {
                long at = start + (long)i * stride;
                if (at + stride > block.Length) break;

                if (BitConverter.ToUInt32(block, (int)(at + ElemType)) != ReadingTypeTemperature) continue;

                string label = ReadAscii(block, (int)(at + ElemLabelOrig), ElemLabelLength);
                if (label.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (label.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                double value = BitConverter.ToDouble(block, (int)(at + ElemValue));
                if (value > 0 && value < 150 && value > hottest) hottest = value;
            }
            return hottest;
        }

        private static string ReadAscii(byte[] block, int at, int max)
        {
            int end = at;
            while (end < at + max && end < block.Length && block[end] != 0) end++;
            return Encoding.ASCII.GetString(block, at, end - at);
        }
    }
}
```

- [ ] **Step 4: Write the acquisition half**

Add to the same class: `Name => "HWiNFO"`, `IsAvailable`, and a `Read()` that opens `MapName` with `MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read)`, reads the 48-byte header via a view accessor, computes `start + stride * count`, copies exactly that many bytes into an array with `ReadArray<byte>`, calls `DecodeCpuTemperature`, and emits one `SensorReading` with `IsCpuDie: true` and `Source = "HWiNFO"` when the result is positive. On `FileNotFoundException` or any other exception, set a 60-second backoff and return an empty list.

- [ ] **Step 5: Run the tests to verify they pass**

Expected: 12 passed.

- [ ] **Step 6: Commit**

```bash
git add Services/Sensors/Publishers/HwInfoSource.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): read CPU temperature from HWiNFO shared memory"
```

---

### Task 6: AfterburnerSource

**Files:**
- Create: `Services/Sensors/Publishers/AfterburnerSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: Task 1 primitives.
- Produces: `AfterburnerSource : ISensorSource` with `public static double DecodeCpuTemperature(byte[] block)`.

**Layout.** Shared memory `MAHMSharedMemory`. Header, 32 bytes: `dwSignature` (uint, `0x4D48414D`) @0, `dwVersion` @4, `dwHeaderSize` @8, `dwNumEntries` @12, `dwEntrySize` @16, `time` @20, `dwNumGpuEntries` @24, `dwGpuEntrySize` @28. Entries begin at `dwHeaderSize` with stride `dwEntrySize`. Within an entry: `szSrcName` char[260] @0, `szSrcUnits` char[8] @260, `szLocalizedSrcName` char[260] @268, `szLocalizedSrcUnits` char[8] @528, `szRecommendedFormat` char[16] @536, `float data` @552.

Select entries whose `szSrcName` contains both "CPU" and "temp" (case-insensitive); take the maximum. Return -1 if none or on signature mismatch.

- [ ] **Step 1: Write the failing test**

```csharp
        private static byte[] AfterburnerBlock(params (string name, float value)[] items)
        {
            const int headerSize = 32, entrySize = 1324;
            var block = new byte[headerSize + entrySize * items.Length];

            BitConverter.GetBytes(0x4D48414Du).CopyTo(block, 0);
            BitConverter.GetBytes((uint)headerSize).CopyTo(block, 8);
            BitConverter.GetBytes((uint)items.Length).CopyTo(block, 12);
            BitConverter.GetBytes((uint)entrySize).CopyTo(block, 16);

            for (int i = 0; i < items.Length; i++)
            {
                int at = headerSize + i * entrySize;
                System.Text.Encoding.ASCII.GetBytes(items[i].name).CopyTo(block, at);
                BitConverter.GetBytes(items[i].value).CopyTo(block, at + 552);
            }
            return block;
        }

        [Fact]
        public void Afterburner_block_picks_the_hottest_cpu_temperature()
        {
            var block = AfterburnerBlock(
                ("CPU temperature", 64f),
                ("CPU1 temperature", 72f),
                ("GPU temperature", 88f),
                ("CPU usage", 41f));

            Assert.Equal(72.0, AfterburnerSource.DecodeCpuTemperature(block), 1);
        }

        [Fact]
        public void Afterburner_block_with_a_wrong_signature_is_refused()
        {
            var block = AfterburnerBlock(("CPU temperature", 64f));
            BitConverter.GetBytes(0u).CopyTo(block, 0);

            Assert.Equal(-1, AfterburnerSource.DecodeCpuTemperature(block), 1);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `AfterburnerSource` not found.

- [ ] **Step 3: Write the implementation**

Mirror `HwInfoSource`: a pure decoder plus a `Read()` that opens `MAHMSharedMemory`, with a 60-second backoff when absent.

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 14 passed.

- [ ] **Step 5: Commit**

```bash
git add Services/Sensors/Publishers/AfterburnerSource.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): read CPU temperature from MSI Afterburner shared memory"
```

---

### Task 7: AidaSource

**Files:**
- Create: `Services/Sensors/Publishers/AidaSource.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: Task 1 primitives.
- Produces: `AidaSource : ISensorSource` with `public static double DecodeCpuTemperature(string xml)`.

**Format.** Shared memory `AIDA64_SensorValues` holds a NUL-terminated ASCII fragment of repeated elements. It has no single root, so wrap it before parsing:

```xml
<temp><id>TCPU</id><label>CPU</label><value>66</value></temp>
<temp><id>TCPUPKG</id><label>CPU Package</label><value>71</value></temp>
```

Parse with `XDocument.Parse("<root>" + fragment + "</root>")`, take `temp` elements whose `label` contains "CPU" and not "GPU", parse `value` with `CultureInfo.InvariantCulture`, return the maximum or -1.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void Aida_fragment_picks_the_hottest_cpu_temperature()
        {
            const string xml =
                "<temp><id>TCPU</id><label>CPU</label><value>66</value></temp>" +
                "<temp><id>TCPUPKG</id><label>CPU Package</label><value>71</value></temp>" +
                "<temp><id>TGPU1</id><label>GPU Diode</label><value>84</value></temp>";

            Assert.Equal(71.0, AidaSource.DecodeCpuTemperature(xml), 1);
        }

        [Fact]
        public void Aida_fragment_that_is_malformed_returns_unavailable()
        {
            Assert.Equal(-1, AidaSource.DecodeCpuTemperature("<temp><label>CPU"), 1);
        }

        [Fact]
        public void Aida_fragment_with_no_cpu_temperature_returns_unavailable()
        {
            Assert.Equal(-1, AidaSource.DecodeCpuTemperature(
                "<temp><id>TGPU1</id><label>GPU Diode</label><value>84</value></temp>"), 1);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `AidaSource` not found.

- [ ] **Step 3: Write the implementation**

Wrap the parse in `try/catch (System.Xml.XmlException)` returning -1. `Read()` opens `AIDA64_SensorValues`, reads to the first NUL, decodes as ASCII, calls the decoder. 60-second backoff. Record in the class comment that AIDA64 requires shared memory to be enabled in its preferences.

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 17 passed.

- [ ] **Step 5: Commit**

```bash
git add Services/Sensors/Publishers/AidaSource.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): read CPU temperature from AIDA64 shared memory"
```

---

### Task 8: SensorRegistry

**Files:**
- Create: `Services/Sensors/SensorRegistry.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: all sources from Tasks 2-7.
- Produces: `SensorRegistry` with `SensorRegistry(IEnumerable<ISensorSource> sources)`, `IReadOnlyList<SensorReading> Snapshot()`, `double CpuDieTemperature { get; }` (-1 when none).

**Selection rule:** `CpuDieTemperature` is the value of the **first** reading in source order with `IsCpuDie == true` and `Category == Temperature`. Source order is constructor order, which Task 9 sets as Core Temp, HWiNFO, Afterburner, AIDA64, LHM/OHM. A reading with `IsCpuDie == false` can never be selected, regardless of category.

- [ ] **Step 1: Write the failing test**

```csharp
        private sealed class FakeSource : ISensorSource
        {
            private readonly SensorReading[] _readings;
            public int ReadCount { get; private set; }
            public bool ShouldThrow { get; set; }

            public FakeSource(string name, params SensorReading[] readings)
            { Name = name; _readings = readings; }

            public string Name { get; }
            public bool IsAvailable => true;

            public IReadOnlyList<SensorReading> Read()
            {
                ReadCount++;
                if (ShouldThrow) throw new InvalidOperationException("source is broken");
                return _readings;
            }
        }

        [Fact]
        public void A_zone_reading_is_never_selected_as_the_cpu_die_temperature()
        {
            var zone = new FakeSource("ACPI",
                new SensorReading("zone.TZ01", "System", SensorCategory.Temperature, 69.0, "°C", "ACPI"));
            var gpu = new FakeSource("Display kernel",
                new SensorReading("gpu.temp", "Radeon", SensorCategory.Temperature, 49.0, "°C", "D3DKMT"));

            var registry = new SensorRegistry(new ISensorSource[] { zone, gpu });
            registry.Snapshot();

            Assert.Equal(-1, registry.CpuDieTemperature, 1);
        }

        [Fact]
        public void The_first_publisher_in_order_wins()
        {
            var first = new FakeSource("Core Temp",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 71.0, "°C", "Core Temp", true));
            var second = new FakeSource("HWiNFO",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 68.0, "°C", "HWiNFO", true));

            var registry = new SensorRegistry(new ISensorSource[] { first, second });
            registry.Snapshot();

            Assert.Equal(71.0, registry.CpuDieTemperature, 1);
        }

        [Fact]
        public void A_throwing_source_does_not_stop_the_others()
        {
            var broken = new FakeSource("broken") { ShouldThrow = true };
            var good = new FakeSource("Core Temp",
                new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature, 71.0, "°C", "Core Temp", true));

            var registry = new SensorRegistry(new ISensorSource[] { broken, good });
            var readings = registry.Snapshot();

            Assert.Single(readings);
            Assert.Equal(71.0, registry.CpuDieTemperature, 1);
        }

        [Fact]
        public void A_throwing_source_is_not_probed_again_until_its_backoff_expires()
        {
            var broken = new FakeSource("broken") { ShouldThrow = true };
            var registry = new SensorRegistry(new ISensorSource[] { broken });

            for (int i = 0; i < 10; i++) registry.Snapshot();

            Assert.Equal(1, broken.ReadCount);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `SensorRegistry` not found.

- [ ] **Step 3: Write the implementation**

`Services/Sensors/SensorRegistry.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// Polls every source once per tick and answers the one question the rest of the app asks
    /// of them: what is the CPU die temperature, if anything can actually supply it.
    /// </summary>
    public sealed class SensorRegistry
    {
        /// <summary>
        /// Long enough that a permanently absent tool costs almost nothing, short enough that
        /// starting HWiNFO mid-session is noticed within a minute.
        /// </summary>
        private const int FailureBackoffSeconds = 60;

        private readonly List<ISensorSource> _sources;
        private readonly Dictionary<ISensorSource, DateTime> _retryAfter = new();

        private IReadOnlyList<SensorReading> _last = Array.Empty<SensorReading>();

        public SensorRegistry(IEnumerable<ISensorSource> sources) => _sources = sources.ToList();

        /// <summary>Hottest publisher-reported die temperature, or -1 when none is available.</summary>
        public double CpuDieTemperature { get; private set; } = -1;

        /// <summary>Every reading from this tick, in source order.</summary>
        public IReadOnlyList<SensorReading> Snapshot()
        {
            var all = new List<SensorReading>();
            DateTime now = DateTime.UtcNow;

            foreach (var source in _sources)
            {
                if (_retryAfter.TryGetValue(source, out var until) && now < until) continue;

                try
                {
                    all.AddRange(source.Read());
                }
                catch
                {
                    // A source that throws is a source with a broken assumption, not a
                    // transient miss. Stop asking it for a while rather than paying the
                    // exception on every tick.
                    _retryAfter[source] = now.AddSeconds(FailureBackoffSeconds);
                }
            }

            _last = all;

            // Selection is on IsCpuDie, never on category: the zone and the GPUs also report
            // Temperature, and neither is the die. First in source order wins, so constructor
            // order is preference order.
            var die = all.FirstOrDefault(r => r.IsCpuDie && r.Category == SensorCategory.Temperature);
            CpuDieTemperature = die?.Value ?? -1;

            return _last;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 21 passed.

- [ ] **Step 5: Commit**

```bash
git add Services/Sensors/SensorRegistry.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): registry with backoff and die-reading selection"
```

---

### Task 9: Wire the registry into telemetry

**Files:**
- Modify: `Models/SystemMetrics.cs:108` (add `Sensors`)
- Modify: `Services/CpuTemperatureProvider.cs` (reduce to a selector)
- Modify: `Services/TelemetryService.cs:45` (field), `:603-606` (tick), `:963-1274` (remove the broken D3DKMT block)
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: `SensorRegistry`.
- Produces: `SystemMetrics.Sensors` of type `IReadOnlyList<SensorReading>` defaulting to `Array.Empty<SensorReading>()`.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void Metrics_carry_an_empty_sensor_list_by_default()
        {
            var m = new Kil0bitSystemMonitor.Models.SystemMetrics();
            Assert.NotNull(m.Sensors);
            Assert.Empty(m.Sensors);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `SystemMetrics` has no `Sensors`.

- [ ] **Step 3: Add the field**

In `Models/SystemMetrics.cs`, after `CpuTemperature`:

```csharp
        /// <summary>
        /// Every reading the sensor registry produced this tick. Empty when no source is
        /// available, which is the normal state on a machine with no monitoring tool
        /// installed and no thermal zone.
        /// </summary>
        public IReadOnlyList<Services.Sensors.SensorReading> Sensors { get; set; }
            = Array.Empty<Services.Sensors.SensorReading>();
```

- [ ] **Step 4: Build the registry in TelemetryService**

Replace the `_cpuTempProvider` field at `Services/TelemetryService.cs:45` with:

```csharp
        // Order is preference order for the CPU die reading: Core Temp first because it is
        // the most precise, then the shared-memory tools, then WMI.
        private readonly Services.Sensors.SensorRegistry _sensors = new(new Services.Sensors.ISensorSource[]
        {
            new Services.Sensors.Publishers.CoreTempSource(),
            new Services.Sensors.Publishers.HwInfoSource(),
            new Services.Sensors.Publishers.AfterburnerSource(),
            new Services.Sensors.Publishers.AidaSource(),
            new Services.Sensors.Publishers.HardwareMonitorWmiSource(),
            new Services.Sensors.ThermalZoneSource(),
            new Services.Sensors.AdapterPerfSource(),
        });
```

At `:603-606`, replace the two temperature assignments with:

```csharp
            // One snapshot per tick, like the GPU Engine counters: every source is polled
            // once and the readings are shared by the panel, the taskbar and the alerts.
            metrics.Sensors = _sensors.Snapshot();
            metrics.CpuTemperature = (float)_sensors.CpuDieTemperature;
            metrics.GpuTemperature = GetGpuTemperature();
```

- [ ] **Step 5: Route GPU temperature through the source**

In `GetGpuTemperature()`, delete the `Method 0.5: D3DKMT` block at `:980-1020` — it can never succeed and is now replaced. Before the `nvidia-smi` fallback, take the hottest `SensorCategory.Temperature` reading from `metrics.Sensors` whose `Id` starts with `"gpu."`. Keep `nvidia-smi` and ADL as fallbacks below it.

- [ ] **Step 6: Reduce `CpuTemperatureProvider`**

Replace its body with a wrapper holding a `SensorRegistry` and returning `CpuDieTemperature`, preserving the `float Read()` signature so existing callers are unaffected. Delete `ReadCoreTemp` and `ReadHardwareMonitorWmi`, now owned by the sources.

- [ ] **Step 7: Run the whole suite**

Run: `& "C:\Users\Manoi\AppData\Local\Microsoft\dotnet\dotnet.exe" test tests\Kil0bitSystemMonitor.Tests\Kil0bitSystemMonitor.Tests.csproj`
Expected: 402 passed, 0 failed.

- [ ] **Step 8: Verify the app runs and reports GPU temperature**

Build and launch, open the detail panel, confirm the GPU card shows a temperature, then check no `nvidia-smi` child process is spawned:
`Get-Process nvidia-smi -ErrorAction SilentlyContinue`
Expected: no process.

- [ ] **Step 9: Commit**

```bash
git add Models/SystemMetrics.cs Services/TelemetryService.cs Services/CpuTemperatureProvider.cs tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(sensors): route telemetry through the sensor registry"
```

---

### Task 10: SENSORS card

**Files:**
- Modify: `ViewModels/StatsPanelViewModel.cs` (add `SensorRow` beside `DiskRow:38`, and `SensorRows`)
- Modify: `StatsPanelWindow.xaml` (new card)
- Test: `tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs` (add)

**Interfaces:**
- Consumes: `SystemMetrics.Sensors`.
- Produces: `public sealed class SensorRow : INotifyPropertyChanged` with `Label` (string, constructor), `Value` (string), `Detail` (string); `StatsPanelViewModel.SensorRows` as `ObservableCollection<SensorRow>`; `public static string FormatSensorValue(double value, string unit)`.

**Pattern to follow:** `SensorRow` mirrors `DiskRow` at `ViewModels/StatsPanelViewModel.cs:38-62` exactly — name in the constructor, mutable properties raising `PropertyChanged` behind an inequality guard. Do **not** use the private `Set<T>` helper at `:602`; it is a member of `StatsPanelViewModel`, not of the row types, and it returns `void`.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void An_absent_reading_renders_as_an_em_dash_not_a_zero()
        {
            Assert.Equal("—", StatsPanelViewModel.FormatSensorValue(-1, "°C"));
            Assert.Equal("71°C", StatsPanelViewModel.FormatSensorValue(71.0, "°C"));
            Assert.Equal("2400 RPM", StatsPanelViewModel.FormatSensorValue(2400, "RPM"));
        }
```

Add `using Kil0bitSystemMonitor.ViewModels;` to the test file.

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `FormatSensorValue` not found.

- [ ] **Step 3: Add `FormatSensorValue` and `SensorRow`**

```csharp
        /// <summary>
        /// Formats a reading for the card. A missing value is an em dash, never 0 - the whole
        /// point of the card is that "no source" and "cold" must not look the same.
        /// </summary>
        public static string FormatSensorValue(double value, string unit)
        {
            if (value < 0) return "—";
            string number = value.ToString("F0", CultureInfo.InvariantCulture);
            return unit == "RPM" ? number + " RPM" : number + unit;
        }
```

- [ ] **Step 4: Populate `SensorRows` in the update method**

Beside the CPU block at `:435`, rebuild `SensorRows` from `m.Sensors`: first a CPU die row (always present, showing `—` and a `Detail` naming Core Temp and HWiNFO when absent), then the remaining temperature rows, then fan and power rows, then a throttle summary row listing the labels of any `SensorCategory.Throttle` readings, or "none".

- [ ] **Step 5: Add the card to `StatsPanelWindow.xaml`**

Copy the structure of the TOP MEMORY card at `StatsPanelWindow.xaml:374-391`: a `StackPanel` with a `CardHeader` reading `SENSORS`, then an `ItemsControl` bound to `SensorRows` whose `DataTemplate` is a two-column `Grid` — `Label` left in `#CFEDEDF2` at `FontSize="10.5"`, `Value` right-aligned, `SemiBold`, `{StaticResource DataCyan}`. Bind `ToolTip` to `Detail`.

**Verify every `StaticResource` you reference exists in this file.** `StaticResource` is not compile-checked, and a missing style crashes the panel at runtime with a green build — this exact mistake shipped once already with `SpecValue`.

- [ ] **Step 6: Run the whole suite**

Expected: 403 passed.

- [ ] **Step 7: Render the card**

Run the render harness and inspect the new card in both taskbar palettes, including the all-unavailable state — that is what a machine with no tool installed shows, so it must look deliberate rather than broken.

- [ ] **Step 8: Commit**

```bash
git add ViewModels/StatsPanelViewModel.cs StatsPanelWindow.xaml tests/Kil0bitSystemMonitor.Tests/SensorsTests.cs
git commit -m "feat(panel): SENSORS card"
```

---

### Task 11: Honest absence, docs, release

**Files:**
- Modify: `ViewModels/StatsPanelViewModel.cs:439` (CPU header)
- Modify: `OverlayWindow.cs:1189-1192` (taskbar TMP)
- Modify: `README.md`, `README.th.md`, `GUIDE.md`
- Modify: `Kil0bitSystemMonitor.csproj:15` (version)

**Interfaces:**
- Produces: `public static string BuildCpuHeader(int usagePercent, float ghz, double dieCelsius)`.

- [ ] **Step 1: Write the failing test**

```csharp
        [Fact]
        public void The_cpu_header_states_the_temperature_is_unavailable_rather_than_omitting_it()
        {
            Assert.Equal("31% · 3.42 GHz · —", StatsPanelViewModel.BuildCpuHeader(31, 3.42f, -1));
            Assert.Equal("31% · 3.42 GHz · 71°", StatsPanelViewModel.BuildCpuHeader(31, 3.42f, 71));
        }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: FAIL — `BuildCpuHeader` not found.

- [ ] **Step 3: Extract and fix the header builder**

```csharp
        /// <summary>
        /// The CPU card header. An absent temperature shows an em dash rather than vanishing:
        /// the old behaviour silently dropped the suffix, so the panel could not distinguish
        /// "no source installed" from "this app does not do temperatures".
        /// </summary>
        public static string BuildCpuHeader(int usagePercent, float ghz, double dieCelsius)
        {
            string header = usagePercent.ToString(CultureInfo.InvariantCulture) + "%";
            if (ghz > 0) header += " · " + ghz.ToString("F2", CultureInfo.InvariantCulture) + " GHz";
            header += " · " + (dieCelsius > 0
                ? ((int)dieCelsius).ToString(CultureInfo.InvariantCulture) + "°"
                : "—");
            return header;
        }
```

Call it from `:435-440`. Give the header a tooltip naming Core Temp and HWiNFO when the reading is absent.

- [ ] **Step 4: Fix the taskbar module**

At `OverlayWindow.cs:1189` the TMP column already falls back from CPU to GPU. Keep the fallback, but set the tooltip to name which sensor is shown, so a GPU number is not mistaken for a CPU number.

- [ ] **Step 5: Run the whole suite**

Expected: 404 passed. (Baseline 380, plus 24 added across Tasks 1-11.)

- [ ] **Step 6: Update the docs**

In `README.md` and `README.th.md`, document the SENSORS card, the supported publishers (Core Temp, HWiNFO, MSI Afterburner, AIDA64, LibreHardwareMonitor, OpenHardwareMonitor), and state plainly that CPU die temperature needs one of them because the sensor is ring-0. In `GUIDE.md` add a troubleshooting entry: "The CPU temperature shows a dash".

Write `%APPDATA%` paths using string concatenation rather than an embedded backslash escape — `GUIDE.md` previously acquired a literal carriage return that way.

- [ ] **Step 7: Bump the version**

`Kil0bitSystemMonitor.csproj:15` → `<Version>1.8.0</Version>`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(sensors): honest absence in header and taskbar; docs; v1.8.0"
```

- [ ] **Step 9: Release**

Follow the established sequence: full suite green → build the installer with ISCC → compute SHA-256 with `sha256sum` and cross-check with `certutil`, asserting the sidecar is non-empty → publish with `gh release create --latest` → verify `git rev-parse "v1.8.0^{commit}"` equals HEAD → refresh `deploy/` → restart the app.

`Get-FileHash` is not available in the `powershell` on PATH and silently produces an empty sidecar.

---

## Risks

**The three new publisher decoders are verified against synthetic buffers only.** None of HWiNFO, MSI Afterburner or AIDA64 is installed on the dev machine, so Tasks 5-7 prove the decoders parse the layouts *as documented* — not that the documented layouts match what those tools publish today. Before advertising them in the README, install at least HWiNFO (the most widely used) and confirm a live reading. If a layout differs, the decoder is a pure function over a byte array and only its offsets change. Until one is confirmed live, describe them in the release notes as supported rather than verified.

**Task 2 is independently valuable.** If the publisher work is cut, the D3DKMT correction alone fixes GPU temperature on AMD-only machines and retires the `nvidia-smi` subprocess.
