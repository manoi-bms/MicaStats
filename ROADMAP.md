# MicaStats Roadmap

What would make MicaStats a genuinely *robust* system monitor — researched against
[iStat Menus 7](https://bjango.com/mac/istatmenus/) (the UX north star this app clones), the
Windows taskbar-monitor field ([TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor),
[XMeters](https://entropy6.com/xmeters/)), and the
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) sensor
library. Ordered by value, with feasibility notes tied to the current codebase.

**The bar "robust" has to clear here:** truthful sensors (a dash beats a fake zero), a layout
that never flickers or collides with the shell, sub-1% self overhead, and every pixel earning
its place — the principles the app already follows, extended rather than diluted.

---

## Tier 1 — Highest value next

### 1. Battery & Power module
The single biggest gap for a notebook machine, and a headline iStat Menus section. Taskbar
module (percent + charging glyph + mini bar) plus a hover card: charge, charging/discharging
state, time remaining, battery health (design vs full-charge capacity), cycle count, power
draw. *Feasibility:* `GetSystemPowerStatus` for the live state, WMI `Win32_Battery` /
`BatteryStaticData` for health — no drivers, fits the existing no-ring-0 rule.

### 2. Alerts & notifications
iStat Menus 7's signature capability: rules like "CPU above 90% for 30 s", "free disk below
10%", "CPU temperature above 95°", "network offline", "public IP changed" — raising Windows
toasts. TrafficMonitor offers threshold alerts; XMeters offers none, so this is also a
differentiator. *Feasibility:* the telemetry loop already ticks once per second; a rule is a
(metric, threshold, sustain-seconds) triple evaluated over the existing history ring buffers.
Needs a small rules editor page in Settings and toast plumbing (`AppNotificationManager`).

### 3. Deep sensors via optional LibreHardwareMonitorLib
Fan RPM, voltages, per-core temperatures and clocks, NVMe temperatures, GPU hot spot — the
data TrafficMonitor gets by bundling LibreHardwareMonitor. Ships as an **opt-in** "Sensors"
card and taskbar module, because LHM needs its kernel driver (admin); the default stays the
current truthful no-driver chain (Core Temp shared memory → LHM/OHM WMI → honest N/A).
*Feasibility:* `LibreHardwareMonitorLib` NuGet; the provider slots in beside
`CpuTemperatureProvider` with the same dead-source backoff pattern.

### 4. Combined mode
iStat Menus 7 reworked combined mode for cramped menu bars: one compact module summarising
several metrics, with the full detail in the dropdown. MicaStats now has the width-capping
ladder (Start-menu avoidance) — a combined module is its natural end state on a crowded
taskbar, instead of hiding trailing modules entirely.

### 5. Per-process disk and network breakdowns
CPU and Memory cards already list top processes; Disks and Network should match (top I/O
processes, top bandwidth processes). *Feasibility:* per-process I/O counters come cheap from
`NtQuerySystemInformation` (already used for the process list); per-process network needs ETW
(`Microsoft-Windows-Kernel-Network`), heavier — sample only while a panel is open, like the
process sampler already does.

## Tier 2 — UX/UI robustness

6. **Module reorder & per-module show/hide** — a drag-to-reorder list in Settings replacing
   the fixed NET→CPU→RAM→GPU→TMP→DSK order; the order also defines avoidance priority.
7. **Longer history with tabs** — persist rings to disk; 5 min / 1 h / 24 h graph ranges in
   the panel (iStat's timescale picker), with peak markers and axis labels.
8. **Theme depth** — light-taskbar palette, accent-hue picker for the cyan/red pair, and a
   high-contrast variant; keep the two-hue discipline that makes the look cohesive.
9. **Graph hover readouts** — exact value + timestamp tooltip when hovering a history bar.
10. **Localization** — resource-based strings, Thai first.
11. **Multi-monitor** — one overlay per taskbar with per-monitor position and module sets;
    the avoidance locator would extend from the primary tray to `Shell_SecondaryTrayWnd`.
12. **Accessibility** — UIA names for overlay modules so Narrator can read the metrics.

## Tier 3 — Platform robustness & distribution

13. **Self-diagnostics card** — which sensor providers are live (Core Temp, LHM WMI, perf
    counters, GPU engine), sample cadence, last error per source: "why is temp N/A" answered
    inside the app instead of a GitHub issue.
14. **Crash resilience** — watchdog auto-restart, safe-mode launch (default config) after
    two consecutive crashes, config backup/restore.
15. **Update checker** — poll GitHub Releases, toast when a newer version ships; later a
    winget manifest and code signing to retire the SmartScreen warning.
16. **Performance budget** — publish measured self-overhead in About; adaptive refresh
    (slower ticks on battery); keep the panel-only process sampling model.
17. **Portable mode** — config beside the exe for USB-stick use.

## Explicitly not planned

- **Fan control** — iStat Menus has it, but on Windows it requires a resident kernel driver
  with vendor-specific EC access. Against this app's no-ring-0 principle; FanControl already
  does it well.
- **Weather / world clocks / calendar** — iStat Menus carries them; the Windows taskbar
  already has Widgets and a clock. Scope creep with no monitoring value.

---

### Sources

- [iStat Menus 7.0 — MacRumors feature overview](https://www.macrumors.com/2024/07/31/istat-menus-7-0-brings-new-features/)
- [iStat Menus — official feature page](https://bjango.com/mac/istatmenus/)
- [iStat Menus 7 release history](https://updates.istatmenus.app/istatmenus7/updates/history/)
- [TrafficMonitor](https://trafficmonitor.org/) and its [SourceForge listing](https://sourceforge.net/app/trafficmonitor/)
- [XMeters — taskbar system monitoring](https://entropy6.com/xmeters/)
- [LibreHardwareMonitor on GitHub](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
