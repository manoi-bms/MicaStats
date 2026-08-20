# kil0bit System Monitor — User Guide (v3.0.0)

A complete guide to using, customizing, and mastering your hardware telemetry overlay.

---

## 🛠️ Getting Started

### 1. Installation
The app is provided as a unified installer:
- **`Kil0bitSystemMonitor-Setup.exe`**: A high-performance setup that handles your Start Menu, Desktop shortcuts, and ensures the app is registered correctly for startup.

Download the latest version from [GitHub Releases](https://github.com/kil0bit-kb/kil0bit-system-monitor/releases) and launch.

---

## 🖥️ The Overlay

The overlay is a slim, elegant pill that sits directly on your **Windows 11 taskbar**. It displays real-time telemetry from your hardware:

### 📈 Included Metrics
- **CPU**: Total processor load percentage.
- **RAM**: Real-time memory pressure.
- **NET**: Combined Upload and Download speeds.
- **GPU**: Raw load and temperatures from your graphics processor.
- **DISK**: Real-time activity and storage usage for multiple drives simultaneously.

### 🍏 iStat-Style Taskbar (default)
The overlay ships in a stacked layout modelled on iStat Menus for macOS: every metric is its own
module with a small dim label above a bold value, and network shows paired **↑ upload / ↓ download**
lines (red up, cyan down). With **Live Graphs** on, each module gains a full-height mini bar chart,
and network gets a mirrored up/down graph around a dashed axis — the iStat signature.

Prefer the classic single-row layout? Turn off **iStat Taskbar (stacked)** in **Appearance**; the
classic mode keeps all per-section colour customisation. The stacked mode uses a fixed palette
(grey labels, white values, cyan graphs) so it always looks cohesive.

### 🚫 Start Menu Avoidance
A centred Windows 11 taskbar moves its Start button **left** as apps open, so a fixed overlay
would eventually sit underneath it. The stacked overlay watches the taskbar's own buttons
(widgets, Start, tray) and stays inside the free corridor between them — **sliding left into
unused space first**, so it keeps every module whenever the corridor has room, and drifting
back to your chosen spot as apps close. Only when the whole corridor genuinely cannot hold
everything does it shed content: sparkline graphs first, then trailing modules one by one —
network and CPU survive longest — with a small **⋯** marker showing that modules are elided
rather than missing. Everything hidden stays one hover or click away in the panels. Turn
this off with **Avoid Start Menu** in **Appearance**.

### 📉 Live Graphs
Turn on **Live Graphs** in **Appearance** to draw a small bar chart of recent history beside each
reading, so you can see a trend rather than just an instant value. Graphs work in stacked, Standard
and Compact label modes, and a sensor that cannot be read shows a flat baseline rather than a
misleading zero.

---

## 📊 The Stats Panel

**Click the overlay** to open the stats panel — an iStat Menus-style dropdown of dark rounded
cards with the detail the taskbar has no room for:

- **CPU card**: stacked User/System history bars, a ● User / ● System legend, one ring gauge per
  logical processor, and uptime.
- **Memory card**: MEMORY and COMMIT ring gauges, a memory-pressure history graph, and a
  Used / Free / Committed / Cached breakdown with top memory consumers.
- **GPU card**: usage and temperature rings above a history graph.
- **Network card**: big upload/download readings and a mirrored ↑/↓ graph around a dashed axis,
  with window peaks.
- **Disks card**: activity history for the busiest drive plus a row per selected drive.
- **Processes card**: top CPU consumers with their memory use.

Everything follows the iStat two-hue rule — cyan for the primary series, red for its counterpart
(System time, Upload) — on near-black cards.

**Or just hover**: pause over any taskbar module and its own compact dropdown opens — CPU (with
top processes), Memory, GPU, Network or Disks — then retargets as you slide along the taskbar,
exactly like iStat Menus. It never steals focus; clicking inside pins it as the full panel. Turn
this off with **Hover Panels** in **Appearance**.

Every card ends with **quick-action buttons** that open the matching Windows tool — Task Manager
and Resource Monitor from CPU and Memory, Display Settings from GPU, Network Settings and
Connections from Network, Disk Management and Storage Settings from Disks.

Press **Esc** or click anywhere else to dismiss it. Process sampling only runs while the panel is
open, so a closed panel costs nothing.

If you would rather clicking never opened the panel, turn off **Panel on Click** in **Appearance**;
the panel stays available from the right-click menu as **Show Stats Panel**.

---

### 🖱️ Overlay Controls
- **Click**: Opens the stats panel.
- **Drag & Move**: Press and drag the overlay to reposition it. A click that does not move opens
  the panel instead, so both gestures share the same button. *(Dragging requires **Lock Position**
  OFF.)*
- **Snap to Taskbar**: When enabled, the overlay snaps to the taskbar area. Disable this to **free-float** the overlay anywhere on your screen.
- **Toggle Lock**: Right-click the overlay and select **Lock Position** to prevent any accidental movement.
- **Settings**: Right-click to quickly jump into the dashboard.

---

## 🏠 Home Dashboard

The Home dashboard is your high-level control center. It features four primary quick-links:
1. **General**: Configure startup behavior and app lifecycle.
2. **Monitoring**: Select which hardware sensors to track.
3. **Appearance**: Customize font, colors, and styling.
4. **About**: View version history and developer links.

---

## ⚙️ Core Configuration

Open the **Settings Window** to customize your experience:

### 🚀 General Settings
- **Hardware Overlay**: Toggle the entire overlay on or off.
- **Snap to Taskbar**: Enable to snap to the taskbar; disable to **unlock** it so you can position the overlay anywhere on your desktop.
- **Launch on Startup**: Enable this to start monitoring automatically when you log in to Windows.
- **Lock Position**: Lock the overlay in its current location.
- **Hide in Full Screen**: Automatically hides the overlay when a full-screen application or game is active to prevent distractions.
- **Keep on Top**: Forces the overlay to stay above all other windows.
- **Refresh Rate**: Customize how often the sensors update (from 500ms for high precision to 5s for ultra-low overhead).

### 📊 Monitoring & Sensors
- **Sensor Selection**: Choose which metrics you want to see (CPU, RAM, NET, GPU, DISK).
- **Network Adapter**: If you have multiple network cards (Wi-Fi, Ethernet, VPN), pick the one you want to track.
- **Multi-Disk Selection**: In v3.0, you can select multiple drives simultaneously. The overlay will dynamically adjust to show activity (C:DK, D:DK, etc.) for each drive you select. Pick up to 9 drives for a balanced 3x3 layout.

### 🎨 Appearance & Design
- **Accent Color**: Pick a color that matches your Windows theme.
- **Font Selection**: Choose from high-legibility fonts (Segoe UI, Outfit, Inter).
- **Design Mode**: Toggle between **Standard** and **Compact** modes for different levels of detail.

---

## ❓ Troubleshooting

**Q: The overlay is missing!**  
A: Go to **Monitoring** settings and ensure at least one sensor is toggled **ON**.

**Q: Why doesn't the app start with Windows?**  
A: Ensure "Launch on Windows Startup" is enabled in **General** settings. This registers the app in your user registry for a seamless boot experience.

**Q: Windows says "Unknown Publisher" or "SmartScreen" prevents it from running.**  
A: This happens because the app is a local, independent release. Click **"More Info"** and then **"Run Anyway"**. v3.0 is a lightweight, zero-bloat build with no telemetry or external tracking.

**Q: My network speed shows 0 KB/s.**  
A: In **Monitoring** settings, select the correct active Network Adapter from the dropdown menu.

---

## 🌐 Community & Support

Built with ❤️ by **KB - kil0bit**.
For feedback, bug reports, or feature requests, visit the [GitHub Repository](https://github.com/kil0bit-kb/kil0bit-system-monitor).
