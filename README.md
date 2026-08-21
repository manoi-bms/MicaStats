<div align="center">

# MicaStats

**A compact, modern system monitor designed for Windows 11.**

Monitor CPU, memory, GPU, network, and disk activity through a clean taskbar overlay and a menu-style Windows 11 dashboard.

[Features](#features) · [Screenshots](#screenshots) · [Installation](#installation) · [Build from source](#build-from-source) · [Contributing](#contributing) · [Credits](#credits-and-attribution)

![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)
![UI](https://img.shields.io/badge/UI-WPF-5C2D91?style=flat-square)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)
![Status](https://img.shields.io/badge/status-active%20development-orange?style=flat-square)

</div>

---

## About MicaStats

MicaStats is an open-source system-monitoring application for Windows 11. It provides real-time hardware and performance information in a compact interface that remains visible without occupying a full application window.

This project is based on [kil0bit System Monitor](https://github.com/kil0bit-kb/kil0bit-system-monitor) and introduces a redesigned, menu-style interface inspired by the compact presentation of modern desktop monitoring utilities.

The visual design has been adapted specifically for Windows 11, with Fluent-style typography, spacing, rounded surfaces, transparency, and Mica-inspired presentation.

> [!NOTE]
> MicaStats is maintained independently from the upstream project. Problems specific to this fork should be reported in this repository rather than to the upstream maintainer.

---

## Why MicaStats Exists

I recently got a new notebook that came preinstalled with Windows 11. Once I finished migrating everything over from Windows 10, one thing was clearly missing from my daily setup: a robust, real-time system monitor living right in the taskbar.

I also have a MacBook, and on macOS [iStat Menus](https://bjango.com/mac/istatmenus/) has long been my favorite utility — compact metric modules in the menu bar, each with its own beautifully dense dropdown. Nothing on Windows felt quite like it.

So I used [Claude Code](https://claude.com/claude-code) to recreate that UX/UI on Windows: the stacked label-over-value taskbar modules, the per-section hover dropdowns, the ring gauges, the mirrored up/down network graphs, and the two-tone cyan/red data palette are all modeled on the iStat Menus experience, rebuilt natively for the Windows 11 taskbar.

---

## Features

### Real-time monitoring

* **CPU** — Current total processor utilization
* **Memory** — RAM usage and memory pressure
* **GPU** — Graphics processor load and available temperature information
* **Network** — Real-time upload and download throughput
* **Disk** — Activity monitoring for one or more storage devices

Sensor availability may vary depending on the installed hardware, device drivers, Windows performance counters, and system configuration.

### Windows 11 interface

* Compact, menu-style monitoring panels
* Fluent Design–inspired visual hierarchy
* Mica-style surfaces and transparency
* Rounded Windows 11–style controls
* Clear typography for at-a-glance monitoring
* Detailed and compact display modes
* High-DPI and multi-resolution support

### Taskbar and desktop overlay

* Display selected metrics near the Windows taskbar
* Snap the overlay to the taskbar
* Use the overlay as a free-floating desktop panel
* Drag the overlay to a preferred position
* Lock the overlay position
* Keep the overlay above other windows
* Automatically hide it during full-screen applications
* Auto-avoid the Start button: the stacked overlay slides into free taskbar space, squeezes its sparklines to fill the remaining corridor exactly, and only then sheds content (graphs, then trailing modules behind a ⋯ marker) so the centred Windows 11 Start button, widgets button and tray never end up underneath it

### Customization

* Select which metrics are displayed
* Choose the active network adapter
* Select multiple disks for monitoring
* Configure the monitoring refresh interval
* Customize fonts and accent colors
* Enable automatic startup with Windows
* Switch between compact and detailed layouts

---

## What This Fork Changes

Compared with the original kil0bit System Monitor, MicaStats focuses on a different presentation and interaction model:

* Redesigned monitoring panels with a compact menu-style appearance
* Updated spacing, typography, and information hierarchy
* Windows 11–oriented Fluent and Mica visual treatment
* Faster access to individual monitoring categories
* Cleaner separation between summary information and detailed metrics
* A more consistent appearance across the taskbar overlay and settings dashboard

The underlying monitoring functionality remains derived from the original project, while the interface and user experience are being developed independently.

---

Curious where MicaStats is heading? The researched feature roadmap lives in [ROADMAP.md](ROADMAP.md).

## Screenshots

All images below are rendered by the actual application code with representative data.

### Taskbar Overlay — iStat-style stacked modules

Each metric is its own module: a dim label over a bold value, dense history bars, mini level bars on CPU / RAM / GPU, one combined storage zone with a bar per drive, and paired ↑/↓ network lines around a mirrored graph.

<p align="center">
  <img src="Assets/preview/istat-taskbar.png" width="850" alt="MicaStats taskbar overlay with live graphs" />
</p>

With **Live Graphs** switched off, the same layout in its most compact form:

<p align="center">
  <img src="Assets/preview/istat-taskbar-compact.png" width="500" alt="MicaStats compact taskbar overlay" />
</p>

### Stats Panel

Click the overlay for the full dropdown: stacked User/System CPU history, one ring gauge per logical processor, MEMORY and COMMIT rings with a memory history graph and a Used / Free / Committed / Cached breakdown, GPU rings with VRAM, a mirrored network graph with session totals, per-drive storage rows, and top processes.

<p align="center">
  <img src="Assets/preview/stats-panel.png" width="420" alt="MicaStats stats panel" />
</p>

### Hardware Inspector

The **Hardware** button on the stats panel opens a CPU-Z-style inspector. CPU identity comes straight from the CPUID instruction (vendor, family/model/stepping, instruction sets, hybrid P/E core split, per-level caches); mainboard, BIOS and per-module RAM detail come from the raw SMBIOS firmware tables; graphics adapters report driver and full VRAM; disks show bus (NVMe/SATA), kind (SSD/HDD) and health — under a live core-speed strip. **Save Report** writes it all to a text file.

<p align="center">
  <img src="Assets/preview/hardware.png" width="420" alt="MicaStats hardware inspector" />
</p>

### Per-Section Hover Dropdowns

Pause over any taskbar module and its own compact dropdown opens, retargeting as you slide between modules — the iStat Menus interaction, on Windows.

<p align="center">
  <img src="Assets/preview/hover-cpu.png" width="300" alt="CPU hover dropdown" />
  <img src="Assets/preview/hover-memory.png" width="300" alt="Memory hover dropdown" />
  <img src="Assets/preview/hover-network.png" width="300" alt="Network hover dropdown" />
</p>

---

## System Requirements

### Running MicaStats

* Windows 11 is the primary supported platform
* Windows 10 build 19041 or later may work, but is not the primary visual target
* .NET 8 Desktop Runtime when using a framework-dependent release
* Compatible Windows performance counters and hardware drivers

### Building MicaStats

* Windows 10 or Windows 11
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* Visual Studio 2022 with the **.NET desktop development** workload, or another compatible .NET development environment
* Git

---

## Installation

### Download a Release

1. Open the [Releases](https://github.com/manoi-bms/MicaStats/releases) page.
2. Download the latest installer or portable package.
3. Extract the package when necessary.
4. Run MicaStats.
5. Select the metrics you want to display from the Monitoring settings.

> [!IMPORTANT]
> Uninstall or close an older build before installing a new version, particularly when switching from the original kil0bit System Monitor to MicaStats.

> [!NOTE]
> Community builds may display a Microsoft Defender SmartScreen warning when the executable has not been code-signed. Review the release source and checksums before running downloaded software.

When no packaged release is available, build the application directly from source.

---

## Build from Source

### Clone the Repository

```powershell
git clone https://github.com/manoi-bms/MicaStats.git
cd MicaStats
```

### Restore Dependencies

```powershell
dotnet restore
```

### Build a Release Version

```powershell
dotnet build --configuration Release
```

The compiled files will normally be created under:

```text
bin/Release/net8.0-windows/
```

### Run from Source

```powershell
dotnet run
```

### Publish a Framework-Dependent Windows Build

```powershell
dotnet publish `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output ./publish
```

The published application will be created in the `publish` directory.

---

## Technology

| Component           | Technology                                   |
| ------------------- | -------------------------------------------- |
| Language            | C#                                           |
| Runtime             | .NET 8                                       |
| Desktop framework   | Windows Presentation Foundation              |
| UI library          | ModernWpfUI                                  |
| Windows integration | Win32 APIs                                   |
| Performance data    | Windows performance counters and system APIs |
| Graphics            | WPF and native Windows rendering             |
| Configuration       | Local application settings                   |

MicaStats uses a native Windows desktop stack rather than a browser-based desktop runtime.

---

## Basic Usage

After launching MicaStats:

1. Open the settings dashboard.
2. Go to **Monitoring**.
3. Enable CPU, memory, GPU, network, or disk modules.
4. Select the correct network adapter when network traffic shows zero.
5. Configure the refresh interval.
6. Choose compact or detailed presentation.
7. Enable **Snap to Taskbar** for a taskbar-integrated layout.
8. Disable **Snap to Taskbar** to position the overlay freely on the desktop.
9. Enable **Lock Position** after placing the overlay.
10. Enable **Launch on Startup** when MicaStats should start automatically with Windows.

Right-click the overlay to access settings and common overlay actions.

---

## Troubleshooting

### The overlay is not visible

Confirm that at least one monitoring module is enabled. Also check whether the overlay is positioned outside the visible desktop area or hidden by the full-screen detection option.

### Network speed remains at zero

Open the Monitoring settings and select the active Ethernet, Wi-Fi, VPN, or virtual network adapter.

### GPU information is unavailable

GPU metrics depend on the graphics hardware, driver implementation, and Windows performance-counter support. Update the GPU driver and restart MicaStats.

### The application does not start with Windows

Disable and re-enable the startup option. Also verify that Windows has not disabled the application under **Settings → Apps → Startup**.

### Windows displays a SmartScreen warning

Unsigned community applications can trigger SmartScreen. Download builds only from this repository’s official Releases page or build the source yourself.

### The overlay moves unexpectedly

Place the overlay in the desired location and enable **Lock Position**.

---

## Contributing

Contributions are welcome.

Before beginning a substantial change, open an issue to describe the proposal and confirm that it fits the project direction.

### Development Workflow

1. Fork this repository.
2. Create a feature branch:

```bash
git checkout -b feature/your-feature-name
```

3. Make and test your changes.
4. Commit with a clear description:

```bash
git commit -m "Add: description of the change"
```

5. Push the branch:

```bash
git push origin feature/your-feature-name
```

6. Open a pull request.

Please keep pull requests focused and include screenshots for visible interface changes.

---

## Reporting Bugs

Use the [GitHub issue tracker](https://github.com/manoi-bms/MicaStats/issues) to report defects.

Include:

* Windows version and build number
* MicaStats version or commit hash
* CPU and GPU model
* Display scale and resolution
* Steps required to reproduce the problem
* Expected and actual behavior
* Relevant screenshots or logs

For security-sensitive reports, do not publish exploit details in a public issue. Contact the maintainer privately using the contact method listed in the repository profile.

---

## Credits and Attribution

MicaStats is a derivative work based on:

**[kil0bit System Monitor](https://github.com/kil0bit-kb/kil0bit-system-monitor)**
Created by **KB – kil0bit**

Original portions:

```text
Copyright (c) 2026 KB - kil0bit
```

MicaStats modifications:

```text
Copyright (c) 2026 Chaiyaporn Suratemeekul (manoi-bms)
```

MicaStats is created and maintained by **Chaiyaporn Suratemeekul** ([@manoi-bms](https://github.com/manoi-bms)), with the iStat Menus-style UX/UI developed with the help of [Claude Code](https://claude.com/claude-code).

The original project is distributed under the MIT License. MicaStats retains the original copyright and license notice as required by that license.

The upstream author is not responsible for modifications, releases, support, or defects introduced by this fork.

---

## Independent Project Notice

MicaStats is an independent open-source project.

It is not affiliated with or endorsed by Bjango, iStat Menus, Apple, Microsoft, or the maintainer of the original kil0bit System Monitor project. Product and company names are used only to identify the platforms or applications being discussed.

MicaStats uses its own name, icons, screenshots, visual assets, and branding.

---

## License

This project is available under the [MIT License](LICENSE).

The `LICENSE` file must retain the original kil0bit System Monitor copyright notice. A separate copyright notice for MicaStats modifications may be added without removing the upstream notice.

---

<div align="center">

**MicaStats — Your system, at a glance.**

[Releases](https://github.com/manoi-bms/MicaStats/releases) ·
[Issues](https://github.com/manoi-bms/MicaStats/issues) ·
[Upstream Project](https://github.com/kil0bit-kb/kil0bit-system-monitor)

</div>
