# M5Stack StackChan: hardware, Bluetooth control, and MicaStats integration

**Research and implementation date:** 2026-10-13. This note combines official
documentation, passive local USB observation, a native Windows BLE scan, and the
resulting MicaStats implementation. No robot control command, reset, or flash was sent.

The owner confirms M5Stack hardware. Installed firmware and version remain unknown.
The best first integration candidate is **factory-firmware BLE control in the DANCE
app**, subject to confirming that mode and its GATT service on this device. USB
serial communication was verified locally; Bluetooth control was verified in upstream
source, not exercised on the attached robot.

## Hardware relevant to MicaStats

M5Stack's retail StackChan uses CoreS3 and an ESP32-S3. Its controller has 16 MB
flash, 8 MB PSRAM, Wi-Fi and Bluetooth LE. The 320 x 240 touch display, speaker,
dual microphones, camera, two feedback servos and 12 RGB LEDs make it suitable for
a desktop companion. These are product specifications, not a device-specific inventory
read from COM8. [Official product overview](https://github.com/m5stack/StackChan)

## What the connected device proves

Windows currently reports one serial interface at `COM8` with
`USB\VID_303A&PID_1001&MI_00`, plus an Espressif native USB JTAG interface (`MI_02`).
A passive `System.IO.Ports.SerialPort` read at 115200 baud, with DTR/RTS disabled and
no bytes written, opened and received 126 characters in eight seconds. The capture did
not contain a StackChan, ESP, firmware, or project identity marker. It is not exposed as
a Windows USB microphone or speaker.

Espressif documents `303A:1001` as its USB JTAG/serial-debug unit and describes the
ESP32-S3 integrated controller as a fixed-function CDC serial console plus JTAG/debug
and flashing interface. That identifies an Espressif USB function, **not a StackChan
model or its installed firmware**. [Espressif hardware identification](https://docs.espressif.com/projects/rust/no_std-training/02_1_hardware.html)
and [ESP32-S3 USB Serial/JTAG behaviour](https://docs.espressif.com/projects/esp-idf/en/v4.4.7/esp32s3/api-guides/usb-serial-jtag-console.html)

## Firmware boundary

M5Stack factory firmware is the primary reference, but its installation on this device
has not been identified. Other rows are conditional alternatives.

| Firmware / product | What is documented | Consequence for MicaStats |
| --- | --- | --- |
| **M5Stack StackChan K151 / CoreS3 factory firmware** | M5Stack documents a CoreS3/ESP32-S3 robot with Wi-Fi/BLE, camera, display, microphones, speaker, two feedback servos, LEDs, and USB-C data. It supports Arduino, UiFlow2, PlatformIO and ESP-IDF programming. [Product documentation](https://docs.m5stack.com/en/StackChan) | It is a plausible hardware match, but the observed VID/PID does not prove it. The product pages publish no host-to-device USB serial command API. |
| **Factory BLE in DANCE mode** | The DANCE app starts a BLE server and applies received face, motion and RGB data. [DANCE implementation](https://github.com/m5stack/StackChan/blob/1b5765599fba8aaad1811d9a79358ccc7051f5f3/firmware/main/apps/app_dance/app_dance.cpp) | Preferred first control route: a PC BLE client can use the existing firmware when this app and service are available. A numeric stats dashboard is a separate display extension. |
| **M5Stack factory server / mobile-app path** | The upstream source repository publishes a server WebSocket used by *device and app* clients. Its frames include expression (`ControlAvatar`) and motion (`ControlMotion`), but require a device-side network session and an RSA-MAC authorization token. The repository explicitly warns that published updates can lag the released firmware. [Protocol and caveat](https://github.com/m5stack/StackChan/blob/main/server/README.MD) | This is not a documented direct-COM API for a Windows app. Do not send those frames over COM8 or assume the device is enrolled in a compatible server. |
| **Community `stack-chan/stack-chan` firmware** | This independent project says explicitly that M5Stack's factory firmware and its Stack-chan firmware are separate implementations. Current releases support `m5stackchan_cores3` and describe an MCP Server sample; the release notes warn that its sample lacks bearer authentication. [Firmware distinction](https://github.com/stack-chan/stack-chan/blob/develop/firmware/README.md), [v1.1.0 release](https://github.com/stack-chan/stack-chan/releases/tag/v1.1.0) | Network MCP can be a later integration option only after positively identifying this firmware and configuring its network/authentication. It is not evidence that stock M5 firmware accepts MCP or COM commands. |
| **XiaoZhi / Xiaozhi-derived firmware** | The upstream ESP32 project documents WebSocket and MQTT+UDP transports. Its MCP messages are JSON-RPC payloads carried inside those network transports; it is a backend-to-device protocol, not a generic USB serial protocol. [XiaoZhi project](https://github.com/78/xiaozhi-esp32), [WebSocket protocol](https://github.com/78/xiaozhi-esp32/blob/main/docs/websocket.md), [MQTT+UDP protocol](https://github.com/78/xiaozhi-esp32/blob/main/docs/mqtt-udp.md) | Use this route only if the device is confirmed to run a compatible XiaoZhi build and is configured to connect to a local/backend endpoint. Firmware variants and server configuration are required. |

## Supported interfaces versus assumptions

There is **no verified documented stock-firmware serial command** for `set face`,
`display stats`, `move`, `speak`, or LED control. The COM endpoint can carry console
logs, and Espressif says the controller *can* implement a bidirectional serial console,
but application commands exist only when the installed firmware implements them.

M5Stack does document how a *replacement Arduino program* can drive its hardware:
`M5StackChan.Motion.move/moveX/moveY/rotateX`, display drawing, touch, and the stated
safe servo range. The Y-axis is recommended at 5–85 degrees; extremes can stall or
damage it. [Servo API and safety](https://docs.m5stack.com/en/arduino/stackchan/servo)
[display API](https://docs.m5stack.com/en/arduino/stackchan/display). Its official
ESPHome configuration likewise covers display, audio, camera, sensors, servos and
lighting, which confirms these are firmware-owned capabilities rather than PC USB
commands. [ESPHome integration](https://docs.m5stack.com/en/homeassistant/devices/stackchan)

## How Bluetooth control works

This is BLE GATT, not Bluetooth Classic serial or a Bluetooth COM port. M5Stack's
mobile app explicitly supports BLE robot control. In the researched factory source,
opening the **DANCE** app starts the robot's BLE server; its loop applies incoming
motion, face-feature and LED changes. Exact availability depends on the installed
firmware. [Official app](https://github.com/m5stack/StackChan/blob/1b5765599fba8aaad1811d9a79358ccc7051f5f3/app/README.md),
[DANCE app source](https://github.com/m5stack/StackChan/blob/1b5765599fba8aaad1811d9a79358ccc7051f5f3/firmware/main/apps/app_dance/app_dance.cpp)

The primary service UUID is `e2e5e5e0-1234-5678-1234-56789abcdef0`.

| Characteristic | UUID | What the PC writes |
| --- | --- | --- |
| Motion | `e2e5e5e1-1234-5678-1234-56789abcdef0` | JSON with yaw/pitch servo parameters |
| Avatar | `e2e5e5e2-1234-5678-1234-56789abcdef0` | JSON with eye/mouth feature parameters |
| Configuration | `e2e5e5e3-1234-5678-1234-56789abcdef0` | Device configuration; outside the proposed first milestone |
| RGB | `e2e5e5e4-1234-5678-1234-56789abcdef0` | JSON with left/right LED colors |

These UUIDs and the 2048-byte JSON limit are defined in the
[official BLE header](https://github.com/m5stack/StackChan/blob/1b5765599fba8aaad1811d9a79358ccc7051f5f3/firmware/main/hal/utils/bleprph/bleprph.h).
Setup mode uses an alternate service UUID ending in `e5ff`; finding that service
alone does not establish that DANCE-mode control is active.

Small payload examples, derived from the
[actual JSON parser](https://github.com/m5stack/StackChan/blob/1b5765599fba8aaad1811d9a79358ccc7051f5f3/firmware/main/stackchan/json/json_helper.cpp):

```json
{"mouth":{"size":8}}
```

Write this 20-byte UTF-8 value to the **Avatar** characteristic to change a face
feature; it does not command the motors. An RGB command uses keys such as
`leftRgbColor` and `rightRgbColor`. Motion uses `yawServo` and `pitchServo`;
angles in this protocol are tenths of a degree, so `450` means 45 degrees.
The supported face interface changes geometric features, not arbitrary screen text.

Larger values need attention to the negotiated ATT payload size and the firmware's
fragment framing, whose header starts `AA 55 C3 01`. The maximum JSON size is not
the same as a single GATT write size. Follow the
[firmware assembler](https://github.com/m5stack/StackChan/blob/1b5765599fba8aaad1811d9a79358ccc7051f5f3/firmware/main/hal/hal_ble.cpp)
and the official app for larger commands. The simple sample below stays within
the 20-byte fallback payload.

### Illustrative Windows C# control sample

This function expects the caller to have discovered the primary service and its
Avatar characteristic. It uses Windows GATT APIs, as described in Microsoft's
[GATT client guide](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client).
It is an original illustrative snippet, rather than a standalone program. The
implemented client is linked below; this short snippet was not executed on the robot.

```csharp
using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

public static async Task SetSmallMouthAsync(GattCharacteristic avatar)
{
    if (avatar.Uuid != Guid.Parse("e2e5e5e2-1234-5678-1234-56789abcdef0"))
        throw new ArgumentException("Select the StackChan avatar characteristic.");
    if (!avatar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
        throw new InvalidOperationException("Avatar writes are unavailable.");

    using var buffer = new DataWriter();
    buffer.UnicodeEncoding = UnicodeEncoding.Utf8;
    buffer.WriteString("{\"mouth\":{\"size\":8}}");
    var status = await avatar.WriteValueAsync(
        buffer.DetachBuffer(), GattWriteOption.WriteWithResponse);
    if (status != GattCommunicationStatus.Success)
        throw new InvalidOperationException($"StackChan write failed: {status}");
}
```

The full client would discover devices, let the user select their StackChan,
open the primary service, resolve the characteristic UUIDs, and manage reconnects
and disposal. A Windows SDK-enabled C# target is needed to compile these WinRT
namespaces. The application now targets `net8.0-windows10.0.19041.0` to use the
Windows projections; no additional application package was added.

## Ready first-party firmware examples

These official files are pinned to
`m5stack/StackChan-BSP@8d4d6fc3b7a6be379c6317c45a02a30bff8c492e` (the current `main`
commit when researched). They are a base for a deliberately installed custom firmware,
not a PC-side serial API for stock firmware.

| Upstream example | Demonstrates | MicaStats use |
| --- | --- | --- |
| [`examples/INA226/INA226.ino`](https://github.com/m5stack/StackChan-BSP/blob/8d4d6fc3b7a6be379c6317c45a02a30bff8c492e/examples/INA226/INA226.ino) | Initialization, display text, battery voltage/current. | Local status/battery panel. |
| [`examples/RGB_LED/RGB_LED.ino`](https://github.com/m5stack/StackChan-BSP/blob/8d4d6fc3b7a6be379c6317c45a02a30bff8c492e/examples/RGB_LED/RGB_LED.ino) | 12 LEDs, `setRgbColor`, `refreshRgb`. | Green/amber/red health state. |
| [`examples/Servo/BasicMovement/BasicMovement.ino`](https://github.com/m5stack/StackChan-BSP/blob/8d4d6fc3b7a6be379c6317c45a02a30bff8c492e/examples/Servo/BasicMovement/BasicMovement.ino) | `goHome`, bounded movement and X rotation. | Later opt-in gestures. |
| [`examples/Servo/HomeCalibration/HomeCalibration.ino`](https://github.com/m5stack/StackChan-BSP/blob/8d4d6fc3b7a6be379c6317c45a02a30bff8c492e/examples/Servo/HomeCalibration/HomeCalibration.ino) | Home calibration. | Separate prerequisite before motion. |

The product page records a 2026-08-07 LCD revision from ILI9342C to ILI9342E that
requires M5GFX >= 0.2.27. Use that newer minimum for a new build rather than older BSP
README/library minima. [Product documentation](https://docs.m5stack.com/en/StackChan)

## Original MicaStats integration proposal

1. **BLE connection and face/LED controls first.** Add a Settings entry to select a
   StackChan and report whether the control service is available. Use face changes and
   LED color for normal, warning, critical and AI-working states. The first runtime
   prerequisite is the compatible DANCE app/service, not a firmware replacement.
2. **Use existing application events.** Subscribe a small app-owned companion service
   to `TelemetryService.MetricsUpdated` and `AlertMonitor.Raised`. Send coalesced state
   changes from a background queue so BLE delays cannot block sampling or the UI.
3. **Add deliberate gestures later.** Reuse the documented calibration and movement
   examples; account for tenths-of-degree BLE values. Keep motion and sound separately
   selectable so quiet operation remains useful.
4. **Add a numeric dashboard only with a display extension.** The verified factory
   BLE commands do not include arbitrary text or a metrics screen. That feature needs
   an additional firmware app/handler or an independently verified display API. A
   versioned USB or BLE stats protocol would be a custom design, not a stock command.
5. **Treat voice as a separate milestone.** The observed USB interface is not audio.
   Robot speech requires its supported audio transport; face/LED BLE control does not
   imply that microphone or speaker audio can be streamed through these characteristics.

Current repository seams:

| Purpose | Existing file |
| --- | --- |
| Metric ticks and snapshots | [TelemetryService](../../Services/TelemetryService.cs), [SystemMetrics](../../Models/SystemMetrics.cs) |
| Alert events | [AlertMonitor](../../Services/Diagnostics/AlertMonitor.cs) |
| Lifetime and settings | [App](../../App.xaml.cs), [ConfigService](../../Services/ConfigService.cs), [SettingsWindow](../../SettingsWindow.xaml.cs) |
| Existing speech workflow | [MeetingSpeech](../../Services/Conference/MeetingSpeech.cs) |

The proposal led to the implementation below. The app-owned service uses its
existing history snapshots and alert events, serializes writes, and rejects queued
manual commands while a command is in flight.

## UIFlow 2.0 and the implemented companion

UIFlow 2.0 is officially supported for StackChan. Installing its dedicated image
replaces the factory firmware. After flashing, the official guide requires manual
servo-zero calibration using its calibration project before motion. Programming
is supported through USB or Wi-Fi Access Code.
[Official UIFlow 2.0 guide](https://docs.m5stack.com/en/uiflow2/stackchan/program),
[factory restoration](https://docs.m5stack.com/en/StackChan/#restore-factory-firmware).

The optional [companion script](../../firmware/uiflow2/micastats_companion.py) uses
the documented `hardware.stackchan` driver for servo power/torque/angles, independent
RGB strips, battery voltage and touch. It draws a simple face plus CPU/RAM/GPU
percentages and six fixed activity labels; it receives no AI prompts or meeting text.
Its [installation guide](../../firmware/uiflow2/README.md) includes calibration,
protocol examples, and factory restoration.
[Pinned driver API](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst).

The UIFlow profile advertises exactly `MicaStatsStackChan` and exposes Nordic UART
Service `6e400001-b5a3-f393-e0a9-e50e24dcca9e`, with writable RX `...0002` and TX
notify `...0003`. Commands are UTF-8 JSON followed by LF, split into at most
20-byte writes. M5Stack's UART helper advertises the name without the service UUID,
so discovery recognizes that exact name and verifies NUS on connection. A small
compatible fallback is included for images lacking the optional helper module.
[Official BLE UART source](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/bleuart/bleuart_server.py).

MicaStats now provides **Settings > StackChan** with Bluetooth discovery, explicit
device selection, connect/disconnect/cancel, persisted opt-in reconnect, face presets
and advanced features, independent RGB strips and fades, bounded head movement,
and gestures. Companion reactions cover load, alerts, AI and meetings. Quiet mode
blocks head commands and dims LEDs; automatic head movement is off by default.
The factory profile has face/RGB/motion control; the optional UIFlow profile adds
the live numeric dashboard. Neither profile implements audio streaming or camera
control. Cancelling a gesture cancels remaining app commands, not physical motion
already accepted by the robot.

Implementation: [transport](../../Services/StackChan/StackChanBleTransport.cs),
[protocol](../../Services/StackChan/StackChanProtocol.cs),
[service](../../Services/StackChan/StackChanService.cs),
[settings](../../Companion/StackChanPanel.xaml), and
[app lifetime](../../App.StackChan.cs). Factory packets over 20 bytes use the
documented fragment header and a serial writer. GATT WriteWithResponse confirms
ATT receipt; the app does not claim a robot execution acknowledgement.

## USB companion control

MicaStats also supports **USB serial** in Settings > StackChan. Present Espressif
COM ports are enumerated through Windows SetupAPI; the USB parent serial number
provides a stable identity separate from Bluetooth. The local robot was initially
COM8 with identity `USB:7C4FADAE3D14`, matching the screen ID supplied by the owner.

Normal Connect sends a versioned hello to an already-running companion. The
separate Start temporary USB companion action interrupts the current Python
program, enters raw REPL, transfers the bundled script into RAM, and waits for
its ready line and correlated hello. It does not reset, flash, write robot files,
or change servo calibration. The script is lost when the robot restarts.
[UIFlow USB execution](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/quick-reference/usb-mode.rst),
[MicroPython raw-paste protocol](https://docs.micropython.org/en/v1.25.0/reference/repl.html#raw-mode-and-raw-paste-mode).

The StackChan board disables UART REPL. USB control uses `sys.stdin` and
`sys.stdout`, with `select.poll(0)` and bounded 256-character bursts per tick;
it does not take over the UART used by the hardware driver.
[Pinned board configuration](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/boards/M5STACK_StackChan/mpconfigboard.h),
[MicroPython polling](https://docs.micropython.org/en/latest/library/select.html).

USB messages are JSON plus LF, with at most 2048 UTF-8 bytes before LF. Each
command carries an unpredictable request ID; only the matching acknowledgement
counts as success. Rejection, cancellation, or incomplete I/O retires the port.
Face, RGB, bounded head commands, gestures and numeric/activity reactions use
the existing companion service. Automatic reconnect only joins a running script;
it never performs temporary startup.

```json
{"companionHello":1,"requestId":"example-hello"}
{"ok":true,"type":"hello","device":"MicaStatsStackChan","protocol":1,"transport":"usb","requestId":"example-hello"}
{"requestId":"example-dashboard","command":{"companion":{"cpu":42,"ram":29.4,"gpu":18,"activity":"PC busy"}}}
{"ok":true,"type":"applied","requestId":"example-dashboard"}
```

The first USB hardware check found a limitation on the then-running image:
COM8 is present, its exact identity is verified, and serial output is readable,
but incoming writes fail with Windows `ERROR_SEM_TIMEOUT`. This also occurs
through an independent Windows `System.IO.Ports` probe. Observed `SystemInfo:
free sram` logs match the factory/Xiaozhi implementation's logs; that is evidence
of a likely firmware mismatch, not proof of its exact installed version.
[Matching upstream StackChan logs](https://github.com/m5stack/StackChan/issues/104),
[Espressif USB console requirements](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-guides/usb-serial-jtag-console.html).
No companion handshake or control command was accepted in that initial check.

After the owner re-burned UIFlow, the same robot appeared on **COM9** as
`VID_303A/PID_811A`, serial `7C4FADAE3D140000`. This PID and product name are
defined in the official StackChan UIFlow board configuration. Discovery recognizes
the profile and normalizes its serial to the same stable USB MAC identity.
[Official StackChan UIFlow USB identity](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/boards/M5STACK_StackChan/mpconfigboard.h).

Fresh native hardware validation then **passed**: temporary startup and the
versioned hello completed in about six seconds, and dashboard, face and RGB
commands received matching applied acknowledgements. No head command was sent.
Startup logs report missing servo-zero data, so head motion remains untested
and requires the official calibration procedure. The owner performed the firmware
re-burn; MicaStats did not flash, reset, or write files to the robot.
Evidence: `artifacts/stackchan-usb/hardware-probe.json`.

Implementation: [USB transport](../../Services/StackChan/StackChanUsbTransport.cs),
[connection routing](../../Services/StackChan/StackChanTransport.cs), and the
[USB startup guide](../../firmware/uiflow2/README.md).

## Initial Bluetooth verification and remaining limits

- USB PnP status, COM8 access and incoming serial data were verified locally.
- BLE UUIDs, DANCE activation and payload schemas were checked in official source
  pinned to `m5stack/StackChan@1b5765599fba8aaad1811d9a79358ccc7051f5f3`.
- The first shell BLE probe could not subscribe to WinRT advertisement events.
  A subsequent compiled native Windows probe completed an eight-second scan with
  **zero compatible advertisements**. Bluetooth adapter PnP status was OK. This
  does not establish whether the robot is in DANCE mode or running the optional
  companion. Evidence: `artifacts/stackchan-companion/hardware-scan.json`.
- The installed firmware version remains unknown. The source repository can lag
  released firmware, so on-device discovery is still required before claiming control.
- The implemented app compiles, and all 83 StackChan-focused .NET tests pass.
  The full suite passed 6,416 tests with one browser/Mermaid render timeout; the
  affected three cases and all StackChan tests passed together on retry (86 tests).
- Firmware APIs were checked against pinned official source, with a separate
  interoperability review. Firmware flashing and physical robot control remain
  unverified; no device command was sent during this work.
- All 15 hardware-free firmware tests pass, covering the activity contract, motion
  rejection without side effects, finite RGB fades, stream reassembly and overflow
  recovery, percentage formatting, and drawing bounds. The Release build stages
  under `artifacts/stackchan-companion/Release` with zero warnings/errors.
- The local installed app was updated after normal Quit, with backups and all
  64 staged file hashes verified. Running-app UI checks confirmed the StackChan
  page, Thai status, UIFlow guide action and disabled disconnected controls. The
  live app scan also reports no compatible robot; physical verification remains
  open. Detailed evidence is `artifacts/stackchan-companion/validation.md`.
