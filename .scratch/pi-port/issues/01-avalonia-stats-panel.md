# 01 — MicaStats.Pi: Avalonia stats panel with Linux telemetry

Status: complete
Blocked by: —
Labels: complete

## Goal

A self-contained `MicaStats.Pi/` Avalonia project that runs on the Raspberry
Pi 5 (net8.0, linux-arm64, Xwayland) and renders the MicaStats stats-panel
experience: CPU, Memory, Temperature, Network, Disk and Processes cards in the
iStat Menus 7 dark house style (near-black plate, `#F01A1A22` rounded cards,
cyan `#3FD2E4` / red `#FF5147` two-hue data palette, uppercase dim headers
with the live value right-aligned, discrete-bar history graphs, ring gauge).

## Acceptance criteria

- [x] `dotnet build` succeeds for `MicaStats.Pi` on the Pi.
- [x] Window opens on the Pi desktop; all six cards render in house style.
- [x] CPU % computed from `/proc/stat` deltas; per-interface network rates from
      `/sys/class/net/<if>/statistics`; temperature from thermal zone 0; disk
      space from the root filesystem and I/O rates from `/proc/diskstats`;
      top processes from `/proc/[pid]/stat` deltas.
- [x] Truthful sensors: an unavailable reading renders as "—", never 0; history
      graphs plot only received samples (Series semantics ported: fixed ring,
      oldest-to-newest, autoscale peak with decay, availability states).
- [x] One 1 s UI-thread timer; no allocation storm in the steady-state path.

## Comments

- 2026-11-12: Created with spec. WPF app does not build on Linux; this project
  is intentionally dependency-free of the Windows csproj and copies the Series
  semantics with an attribution comment.
- 2026-11-12: Verified on the Pi 5 (Debian 13 trixie, labwc + Xwayland,
  .NET SDK 8.0 user-local, Avalonia 11.3.2). `dotnet build`: 0 warnings,
  0 errors, 24.7 s. On-device run screenshot (local evidence, gitignored:
  `artifacts/pi-port/on-device-screenshot.png`): CPU 18% with cyan history
  bars, load 6.23/4 cores/2.40 GHz; MEMORY ring 27% with Used 4.2 GB /
  Free 11.6 GB / Swap 0.0/2.0 GB; TEMPERATURE 55.1° history; NETWORK·ETH0
  mirrored cyan/red graph ▲5.7 kB/s ▼7.7 kB/s; DISK 10% bar, 420.7 GB free
  of 468.9 GB, live R/W rates; PROCESSES top-5 with MB and cyan CPU%.
  Footer uptime matches `uptime`(1).
