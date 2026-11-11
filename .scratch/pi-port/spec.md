# MicaStats.Pi — run the stats experience on Raspberry Pi (Linux/ARM64)

Status: complete (2026-11-12, see issues/01). Owner request (2026-11-12):
"checkout this repo and modify it to support pi", clarified to mean **running
MicaStats on the Raspberry Pi 5 itself** (Debian 13 trixie, labwc Wayland
desktop with Xwayland, .NET via user-local SDK).

## Scope

WPF cannot run on Linux, so Pi support is a **new Avalonia UI project**,
`MicaStats.Pi/`, in this repository — a trimmed port of the stats experience:

- The **stats panel** (dashboard window) in the established iStat Menus 7 dark
  design: rounded cards on a near-black plate, strict two-hue data palette
  (cyan `#3FD2E4` primary, red `#FF5147` counterpart), uppercase grey card
  headers with the live value at the right, discrete-bar history graphs.
- **Linux telemetry providers** replacing PerformanceCounter/WMI:
  `/proc/stat` (CPU total + per-core), `/proc/meminfo`, `/sys/class/thermal`
  (SoC temperature), `/sys/class/net/*/statistics` (rates),
  `/proc/diskstats` + `statvfs` (disk I/O and space), `/proc/loadavg`,
  `/proc/uptime`, top processes from `/proc/[pid]/stat`.
- Reuses the repo's telemetry *principles*: `Series` ring-buffer semantics
  (fixed capacity, oldest-to-newest, autoscale peak with decay, honest
  Availability — a dash beats a fake zero), 1 s tick, no steady-state
  allocation in the sample path.

Out of scope (Windows-only or separate efforts): taskbar overlay and
Shell_TrayWnd avoidance, MicaPad, AI/Ask, Meeting Assistant, screen capture,
StackChan/Turzx USB companions, GPU engine counters (Pi exposes no usable GPU
usage metric without root; show honest N/A), installer/wix.

## Non-goals

- No shared compilation with `Kil0bitSystemMonitor.csproj` (WPF project does
  not build on Linux; the Pi project is self-contained and copies the small
  amount of logic it needs, noting origins).
- No layer-shell/panel docking on Wayland (Avalonia lacks layer-shell; the Pi
  already has a waybar panel — this app is the dashboard window).

## Acceptance

1. `dotnet build MicaStats.Pi` succeeds on the Pi (linux-arm64).
2. The window opens on the Pi desktop (Xwayland), renders CPU, Memory,
   Temperature, Network, Disk and Processes cards in the house style.
3. Values are truthful: CPU% matches `/proc/stat` deltas, unavailable sensors
   render as "—", history graphs plot only received samples.
4. Self-overhead stays modest (single 1 s timer, no per-tick allocation storms).

## Verification evidence

Recorded in issues/01's Comments after build + on-device screenshot.
