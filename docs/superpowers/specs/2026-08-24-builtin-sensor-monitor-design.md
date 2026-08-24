# Built-in sensor monitor

**Date:** 2026-08-24
**Status:** approved, ready for implementation planning

## Problem

The CPU card in the detail panel shows no temperature. The reading is an optional suffix on
the card header, appended only when `m.CpuTemperature > 0` (`ViewModels/StatsPanelViewModel.cs:439`),
so when no source can supply one the suffix silently disappears. The panel cannot distinguish
"unavailable" from "not a feature".

The underlying cause is that MicaStats runs unelevated with no kernel driver, and CPU die
sensors (AMD Tctl, Intel DTS) are ring-0 only. `Services/CpuTemperatureProvider.cs` therefore
reads what other tools publish — Core Temp shared memory, then LibreHardwareMonitor /
OpenHardwareMonitor WMI — and returns -1 when neither is running.

The goal is a built-in sensor monitor: MicaStats should surface every thermal, fan, power and
throttle reading it can obtain **without a driver and without elevation**, present them
honestly, and stop leaving a blank where a reading belongs.

## What was measured

Every decision below rests on measurements taken on the development machine
(Dell Pro Max 16 MC16255, AMD Ryzen AI 9 HX PRO 370, 24 logical processors, Radeon 890M
integrated + NVIDIA RTX PRO 1000 discrete, Windows 11).

### Sources probed

| Source | Unelevated | Result |
| --- | --- | --- |
| Core Temp shared memory (`CoreTempMappingObject`) | n/a | `FileNotFoundException` — not running |
| `root\LibreHardwareMonitor` WMI | n/a | Invalid namespace — not installed |
| `root\OpenHardwareMonitor` WMI | n/a | Invalid namespace — not installed |
| `MSAcpi_ThermalZoneTemperature` (`root\WMI`) | no | Access denied |
| `Thermal Zone Information` perf counters | **yes** | 1 zone, `\_TZ.TZ01` |
| `Win32_TemperatureProbe` | yes | 4 probes incl. "CPU Probe", all values `0x8000` (unknown) |
| `root\dcim\sysman` (Dell Command Monitor) | yes | Empty stub — not installed |
| `root\Dell`, `root\PEH` | no | Access denied |
| `Get-StorageReliabilityCounter` (NVMe temp) | no | Access denied on all 3 drives |
| `root\WMI BatteryTemperature` | yes | Class present, no instances |
| D3DKMT adapter perf data | **yes** | Works — see below |

`Win32_TemperatureProbe` declaring a "CPU Probe" with `Accuracy = 32768` (0x8000) confirms
these are SMBIOS Type 28 *declarations* with no live values, not a usable sensor.

### The ACPI thermal zone is not a CPU proxy

Two load ramps from different thermal starting points, 24 saturated threads:

| Run | Idle | Under sustained load |
| --- | --- | --- |
| 1 (cold start, fans idle) | 64.05 °C | rose to 71.05 °C over ~24 s |
| 2 (warm start, fans active) | 71–72 °C | **fell to 69.05 °C** |

The zone moved in opposite directions under identical CPU load. It sits downstream of the
fan control loop, so it reports the loop's output rather than the die's state. Resolution is
1 K (values quantize to whole kelvin) and the response lag is roughly 20 s.

**Decision:** the zone is a genuine and useful reading, but it is labelled *System* and is
never permitted to answer as a CPU die temperature.

### The integrated GPU is not a CPU proxy either

The Radeon 890M shares silicon with the CPU cores, which suggested it might track the die.
It does not:

| Sensor | Idle | 24 threads saturated |
| --- | --- | --- |
| Radeon 890M temperature | 49.0 °C | 49–51 °C (±2 °C) |
| Radeon 890M `Power` | 40–45 | 51–59 |
| RTX PRO 1000 temperature | 51.0 °C | 51.0 °C (idle throughout) |

The graphics block has its own diode, thermally isolated enough that a fully loaded CPU moves
it 2 °C. Its `Power` field tracks CPU load well, because power is shared SoC-wide — but power
is not temperature.

### Two bugs in the shipped D3DKMT path

`Services/TelemetryService.cs` has never successfully read D3DKMT adapter data. Established
by sweeping query type 0–89 against buffer sizes 8–160 on all three adapters:

| | Shipped | Measured correct |
| --- | --- | --- |
| Query type | `35` (`KMTQAITYPE_QUERY_MULTIPLANEOVERLAY_DECODE_SUPPORT`) | **`62`** |
| Struct size | ~96 bytes, 13 fields | **64 bytes** |
| `Temperature` offset | 28 | **56** |

Every call returned `STATUS_INVALID_PARAMETER` (`0xC000000D`) and fell through to the
`nvidia-smi` subprocess fallback. Consequences: AMD-only machines get no GPU temperature at
all, and the app keeps an external process alive for a reading it could take directly.

With the corrected type and layout, both GPUs answer, and the block also yields `FanRPM`,
`Power`, `PowerLimitThrottle` and `TemperatureLimitThrottle`. Query type `63` returns a
40-byte capability block whose offset 28 holds a temperature ceiling (89.0 °C on the discrete
GPU).

The published layout in `d3dkmthk.h` does not match what this driver stack accepts, so the
struct is declared with explicit offsets taken from the measurement, not from the header.

### Conclusion

There is no driver-free path to CPU die temperature. Tctl stays behind ring-0. The feature is
therefore built from what *is* reachable, and the die reading remains dependent on a
publisher — with a much wider set of publishers supported than today.

## Decisions

1. **No kernel driver.** MicaStats continues to require no elevation. Embedding
   LibreHardwareMonitorLib would give true per-core Tctl but requires admin and ships a
   ring-0 driver that HVCI and Smart App Control machines block, in an installer that is not
   code-signed. Recorded in `ROADMAP.md` as an opt-in possibility, not taken here.
2. **Honest labelling over a plausible number.** A zone reading is never presented as a CPU
   temperature. This is enforced by type rather than convention, because the anti-correlation
   measured above makes a mislabelled zone actively misleading.
3. **Widen the publisher chain.** Add HWiNFO, MSI Afterburner and AIDA64 shared memory
   alongside Core Temp and LHM/OHM. MicaStats consumes what a tool the user already runs
   publishes; it still installs no driver of its own.
4. **Fix D3DKMT regardless.** The correction stands on its own merits and is a prerequisite
   for the GPU rows on the new card.

## Architecture

A new `Services/Sensors/` namespace, built around one small interface so each source is
independently testable and a dead source cannot stall a tick.

```
ISensorSource          Name · IsAvailable · Read() -> IEnumerable<SensorReading>
├─ ThermalZoneSource        perf counters: every zone + PercentPassiveLimit + ThrottleReasons
├─ AdapterPerfSource        D3DKMT type 62 x every adapter: temp, FanRPM, Power, throttle flags
└─ publishers (CPU die, in preference order)
   ├─ CoreTempSource        existing shared-memory reader, moved
   ├─ HwInfoSource          new — Global\HWiNFO_SENS_SM2
   ├─ AfterburnerSource     new — MAHMSharedMemory
   ├─ AidaSource            new — AIDA64_SensorValues
   └─ HardwareMonitorWmiSource   existing LHM/OHM reader, moved

SensorRegistry         owns the sources, per-source backoff, one snapshot per tick
```

### Components

**`SensorReading`** — a record: `Id`, `Label`, `Category` (Temperature / Fan / Power /
Throttle), `Value`, `Unit`, `Source`, `IsCpuDie`. Only publisher sources may set `IsCpuDie`;
`ThermalZoneSource` and `AdapterPerfSource` emit Temperature readings with `IsCpuDie = false`.
That flag, not the category, is what the registry selects on — which is what makes it
impossible for a zone to surface as a die reading.

**`ISensorSource`** — `Name`, `IsAvailable`, `Read()`. Each implementation owns its own
failure handling and backoff, and returns an empty sequence rather than throwing.

**`ThermalZoneSource`** — enumerates every instance of the `Thermal Zone Information`
category (not just the first), converting `High Precision Temperature` from deci-Kelvin.
Also emits `PercentPassiveLimit` and `ThrottleReasons` as Throttle-category readings.

**`AdapterPerfSource`** — enumerates adapters via `D3DKMTEnumAdapters2`, reads query type 62
into the 64-byte block for each, and emits temperature, fan RPM, power and the two throttle
flags per adapter. Adapters that return `STATUS_INVALID_PARAMETER` (such as the render-only
adapter on this machine) are skipped without noise.

**Publisher sources** — each reads one tool's shared memory or WMI and emits a CPU-die
temperature. Decoding is separated from acquisition so the decoders can be tested against
synthetic buffers with no tool installed.

**`SensorRegistry`** — holds the sources, produces one snapshot per tick, applies per-source
backoff, and selects the best CPU-die reading by publisher preference.

**`CpuTemperatureProvider`** — reduced to a thin selector over the registry's publisher
sources, preserving its current contract (hottest core, or -1).

### Data flow

`TelemetryService` tick → `SensorRegistry.Snapshot()` → fills `SystemMetrics.CpuTemperature`
(publishers only) and a new `SystemMetrics.Sensors` list → `MetricsHistory` and
`StatsPanelViewModel.SensorRows` → the SENSORS card. One snapshot per tick, matching the
existing single-GPU-Engine-snapshot pattern.

### Presentation

A new SENSORS card in the detail panel:

```
CPU die        —      (install Core Temp or HWiNFO)
System zone   69°C    sparkline
Radeon 890M   49°C    power 52%   power-limited
RTX PRO 1000  51°C    max 89°C
Throttling    power limit on iGPU
```

The CPU card header suffix and the taskbar TMP module are fed by the die reading when a
publisher supplies one, and show an honest `—` otherwise, matching how the GPU ring already
behaves. Unavailable rows carry a tooltip naming what would populate them.

## Error handling

Each source is isolated and carries its own backoff, reusing the existing 30 s / 60 s pattern
from `CpuTemperatureProvider`. A missing shared-memory object or WMI namespace costs one
failed probe per backoff interval, not one per tick. A source that throws is disabled for its
backoff period; it never propagates into the tick.

## Testing

- Decoder tests against synthetic buffers: HWiNFO header and reading layout, Afterburner
  entries, AIDA64 text, Core Temp delta-to-TjMax handling, deci-Kelvin conversion. These run
  with no tool installed.
- Registry tests pinning that a Temperature reading from `ThermalZoneSource` can never be
  selected as the CPU die value, and that publisher preference order is respected.
- Layout tests that would have caught the shipped bug:

  ```csharp
  Assert.Equal(64, Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>());
  Assert.Equal(56, Marshal.OffsetOf<D3DKMT_ADAPTER_PERFDATA>("Temperature").ToInt32());
  ```

- Backoff tests: a failing source is probed once per interval, not once per tick.
- A render-harness pass over the new card in both taskbar palettes, including the
  all-unavailable state, since that is the state most users will see first.

## Out of scope

- Throttle alerting. `ThrottleReasons` is a sharper signal than the damped temperature and is
  a natural follow-up, but no alert rule is added here.
- Per-core temperatures.
- NVMe drive temperatures — measured as admin-gated on all three drives.
- Elevation or any kernel driver.

## Known limitations to state plainly in the UI

- HWiNFO requires "Shared Memory Support" to be enabled, and current free builds time-limit
  it per session. It widens coverage but is not unconditional; the tooltip must say so rather
  than let an expired session look like a defect.
- The System zone is a fan-loop output. It is shown because it is real and it is what the
  cooling system reacts to, not because it approximates the die.
- Machines with no thermal zone (common on desktops) show no System row.
