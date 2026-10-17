# Turzx 3.5" IPS USB-C Display (`USB35INCHIPSV2`) Specification

## Overview

MicaStats supports the attached **Turzx / Turing Smart Screen 3.5-inch IPS V2 (`USB35INCHIPSV2`)** secondary USB-C display (`USB\VID_1A86&PID_5722\USB35INCHIPSV2`, enumerated on Windows via `usbser.sys` as a COM port such as `COM10`).

The display receives a live cyber-instrument telemetry dashboard directly from `MetricsHistory` and `AlertMonitor` over USB serial, with dirty-region RGB565 rendering so incremental 1 Hz updates complete in <120 ms without blocking the UI thread.

## Hardware & Protocol Contract (`Revision A` / `USB35INCHIPSV2`)

- **USB Identification**:
  - Primary match: `VID_1A86&PID_5722` (CH552 CDC-ACM serial with PnP instance ID containing `USB35INCHIPSV2`).
  - Secondary match: `VID_1A86&PID_7523` (CH340 Revision B fallback).
- **Serial Link**:
  - `115200` baud, 8 data bits, no parity, 1 stop bit (`8N1`).
  - `DTR = on`, `RTS = on` (`EscapeCommFunction(SETDTR=5)` and `EscapeCommFunction(SETRTS=3)`).
- **Resolution & Orientations**:
  - Native Portrait: `320 × 480` (`Portrait = 0`, `ReversePortrait = 1`).
  - Landscape: `480 × 320` (`Landscape = 2`, `ReverseLandscape = 3`).
- **6-Byte Command Header (`PackCommand(x, y, ex, ey, cmd)`)**:
  - Given `x, y, ex = x + w - 1, ey = y + h - 1`:
    - `byte[0] = (byte)(x >> 2)`
    - `byte[1] = (byte)(((x & 3) << 6) | (y >> 4))`
    - `byte[2] = (byte)(((y & 15) << 4) | (ex >> 6))`
    - `byte[3] = (byte)(((ex & 63) << 2) | (ey >> 8))`
    - `byte[4] = (byte)(ey & 0xFF)`
    - `byte[5] = cmd`
- **Commands**:
  - `101` (`RESET`): `PackCommand(0, 0, 0, 0, 101)`
  - `102` (`CLEAR`): `PackCommand(0, 0, 0, 0, 102)`
  - `108` (`SCREEN_OFF`): `PackCommand(0, 0, 0, 0, 108)`
  - `109` (`SCREEN_ON`): `PackCommand(0, 0, 0, 0, 109)`
  - `110` (`SET_BRIGHTNESS`): `level = 255 - Round(Clamp(percent, 0, 100) / 100.0 * 255.0)` (Revision A inverted scale: `0` = 100% brightest, `255` = 0% darkest), packed as `PackCommand(level, 0, 0, 0, 110)`.
  - `121` (`SET_ORIENTATION`): 16-byte packet:
    - `byte[0..4] = 0`
    - `byte[5] = 121`
    - `byte[6] = (byte)((int)orientation + 100)` (`100` = Portrait, `101` = ReversePortrait, `102` = Landscape, `103` = ReverseLandscape)
    - `byte[7] = (byte)(width >> 8)`
    - `byte[8] = (byte)(width & 0xFF)`
    - `byte[9] = (byte)(height >> 8)`
    - `byte[10] = (byte)(height & 0xFF)`
    - `byte[11..15] = 0`
  - `197` (`DISPLAY_BITMAP`): `PackCommand(x, y, x + w - 1, y + h - 1, 197)` followed by `w * h * 2` bytes of little-endian RGB565 pixels (`low = rgb565 & 0xFF`, `high = rgb565 >> 8`), streamed in chunks of `w * 8` bytes.

## Architecture & Components

1. **`Services/Turzx/TurzxModels.cs`**:
   - `TurzxOrientation` (`Portrait = 0`, `ReversePortrait = 1`, `Landscape = 2`, `ReverseLandscape = 3`).
   - `TurzxViewMode` (`Dashboard = 0`, `Thermal = 1`, `StorageNet = 2`, `Clock = 3`).
   - `TurzxConnectionState` (`Off`, `Disconnected`, `Discovering`, `Connecting`, `Connected`, `Faulted`).
   - `TurzxDevice(string PortName, string Name, string InstanceId)`.
   - `TurzxRect(int X, int Y, int Width, int Height)`.
   - `TurzxSnapshot(TurzxConnectionState State, string Status, string PortName, string DeviceName, TurzxOrientation Orientation, TurzxViewMode ViewMode, int Brightness, string? Error)`.
2. **`Services/Turzx/TurzxProtocol.cs`**:
   - Pure, allocation-conscious packet builder for `PackCommand`, `BuildReset`, `BuildClear`, `BuildScreenOff`, `BuildScreenOn`, `BuildSetBrightness`, `BuildSetOrientation`, `RgbTo565`, and `BuildBitmapPayload` / `SliceSubRectBytes`.
3. **`Services/Turzx/TurzxUsbTransport.cs`**:
   - `ITurzxTransport` interface + `TurzxUsbTransport` implementation using `SetupAPI` (`SetupDiGetClassDevsW` for `Ports` GUID `{4d36e978-e325-11ce-bfc1-08002be10318}`) and Win32 `CreateFileW` (`115200 8N1`, `SetDtr`/`SetRts`).
   - Never uses `Get-PnpDeviceProperty` or WMI on the UI thread.
4. **`Services/Turzx/TurzxRenderer.cs`**:
   - Pure C# software RGB565 frame renderer (`480×320` Landscape and `320×480` Portrait) with built-in bitmap font, card borders, progress bars, ring gauges, sparklines, and alert banner overlay.
   - `ComputeDirtyRegions(ushort[]? previousFrame, ushort[] currentFrame, int width, int height, int bandHeight = 16)`: returns full screen on first frame or orientation/view change, `0` rects when identical, or compact horizontal bands for changed dynamic metrics.
   - `ToBgra32(ushort[] frame, int width, int height)`: converts RGB565 buffer to BGRA32 bytes for live preview in `TurzxPanel`.
5. **`Services/Turzx/TurzxDisplayService.cs` & `App.Turzx.cs`**:
   - Subscribes to `MetricsHistory.Updated` and `AlertMonitor.Raised`.
   - Serializes frame rendering and USB writes on a single background queue (`SemaphoreSlim(1, 1)`), dropping intermediate ticks if a full-screen write is still in progress.
   - Turns screen off (`108`) cleanly on `Dispose()` / app exit.
6. **`Companion/TurzxPanel.xaml` & `SettingsWindow`**:
   - Dedicated `Turzx` NavigationView item and panel in `SettingsWindow` with Enable toggle, Scan / Port selector (`COM10`), Connect / Disconnect, Auto-connect toggle, Orientation selector, View Mode selector, Brightness slider (`0–100%`), Test Pattern button, and live rendered preview.
   - Full English + Thai localization in `Services/Localization/Catalogs/turzx.json`.
