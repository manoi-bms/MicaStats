# MicaStats UIFlow2 StackChan companion

This is an optional companion for a commercial M5Stack StackChan. It needs the
official **StackChan UIFlow2 firmware**, then `micastats_companion.py` can be
uploaded with UiFlow2 and set to **Run Always**. It replaces the factory
firmware while installed; it is not an add-on to the factory StackChan app.

For temporary USB use, select **USB serial** in MicaStats, find the robot's COM
port, and click **Start temporary USB companion**. This requires the UIFlow2
StackChan image to be active; it cannot execute Python on the factory/Xiaozhi
image. A Windows serial write timeout can mean that the running image exposes
USB logs without accepting console input. Confirm the exact image selected in
M5Burner before retrying. MicaStats does not flash or replace that image.

Use the official [StackChan UiFlow2 guide](https://docs.m5stack.com/en/uiflow2/stackchan/program)
to flash the firmware and connect by USB or Wi-Fi Access Code. After flashing,
perform servo-zero calibration: open the official **Servo zero
calibration** project, position both axes manually as it shows, and tap
**Save**. The official product guide confirms that
M5Burner can restore the factory image afterwards: [Restore Factory
Firmware](https://docs.m5stack.com/en/StackChan/#restore-factory-firmware).

## Bluetooth protocol

The companion advertises as `MicaStatsStackChan` and exposes the official
UiFlow2 BLE-UART (Nordic UART Service):

| Role | UUID |
| --- | --- |
| Service | `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` |
| PC writes to robot | `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` |
| Robot notifies PC | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

It accepts UTF-8 JSON records terminated by `\n`. Treat BLE-UART as a byte
stream: a record may arrive across several writes. Keep writes at 20 bytes or
less for conservative BLE compatibility and send the terminating newline last.
The companion answers through the notify characteristic with a small JSON
acknowledgement. It uses M5Stack's `bleuart` module when present, and includes
a small compatible NUS fallback because the current StackChan firmware manifest
does not explicitly list that optional module.

```json
{"companion":{"cpu":42,"ram":29.4,"gpu":18,"activity":"PC busy"}}
```

The same record may include the factory control field names:

```json
{"yawServo":{"angle":300,"speed":250},"pitchServo":{"angle":450,"speed":200},"leftRgbColor":"#00FF00","rightRgbColor":"#0000FF","mouth":{"x":0,"y":0,"rotation":0,"weight":50,"size":70}}
```

`yawServo` and `pitchServo` use factory tenths-of-a-degree values. This
companion accepts X `-450..450`, Y `150..650`, and `speed` `100..300`; when
`speed` is omitted, it uses `200`. This is deliberately narrower than the
hardware range; M5Stack recommends keeping physical Y movement within 5°–85°.
Continuous `yawServo.rotate` is intentionally disabled. A valid motion command
is the only operation that powers the servo rail or enables torque.
It also requires both actual stored `servo/zero_pos_1` and `servo/zero_pos_2`
NVS values to be valid. Missing driver defaults are insufficient. USB hello
reports `motionCalibrated` and per-axis `calibration` status; MicaStats disables
head controls when the saved zeros cannot be verified. This check never saves
or resets calibration. Follow the official physical alignment instructions.

`leftEye`, `rightEye`, and `mouth` accept the factory avatar fields `x`, `y`,
`rotation`, `weight`, and `size`. Temporary animation offsets add eased glances,
smooth eyelids and a gentle bob without changing those signed fields. Quiet
suppresses display animation. The two RGB fields
control the independent six-LED strips. Optional factory-style
`leftRgbDuration` and `rightRgbDuration` values transition each strip over that
many seconds (`0..10`; zero applies immediately). Invalid durations are ignored.

For a stable display contract, `companion.activity` accepts exactly `Ready`,
`Quiet`, `Alert`, `AI working`, `Meeting active`, or `PC busy`. CPU, RAM, and GPU
are numeric utilization percentages, clamped to `0..100` and displayed with `%`.
Unavailable metrics are sent as `null` and displayed as `--`. Other activity
strings are ignored so arbitrary private text is never displayed.

## Companion screen

The large animated character is the startup and main view, with real CPU/RAM/GPU
readings beneath its face. Fresh data spends 12 seconds here, then 6 seconds on
the three-card overview and 6 seconds on a metric spotlight (CPU, RAM, GPU in
turn). A thin rail beneath the header shows time until the next view. Eased eye
glances, smooth eyelids, gentle bobbing and a small twinkle make Ready feel alive.
AI thinking dots, decorative meeting arcs and busy accents illustrate accepted
activity; they do not represent measured audio or AI progress. Face frames are
limited to 12.5 per second. They are drawn into reusable RGB565 buffers off screen
and transferred once complete, avoiding a visible erase between animation frames.
Native framebuffer ellipses keep normal expressions within the frame budget;
arbitrary manual rotations retain their original geometry. Alert entry adds
one soft border emphasis, holds the readable event card, then returns to the
character after clearing. Screen animation never issues motor commands.
Activity-colored LEDs breathe slowly.
AI uses offset strip breathing, meetings a slower cycle, and alerts a slow
alternating orange pulse. Colors never exceed the PC's configured brightness.
Quiet mode keeps the display and LEDs still. Data older than 75 seconds stops
rotation and animated effects; this covers the longest 60-second update interval.

MicaStats diagnostic events interrupt rotation for 20 seconds and show the
public metric, value, and configured threshold. These are recent event readings,
not a claim that the condition remains active. The optional dashboard field is:

```json
{"companion":{"cpu":42,"ram":54,"gpu":17,"activity":"Alert","alert":{"kind":"cpu-temperature","value":97.2,"threshold":95,"above":true}}}
```

Kinds are `cpu-temperature`, `cpu-usage`, `memory-used`, `disk-free`, `gpu-usage`,
`battery-health`, or `battery-charge`; numeric fields must be finite and in
`0..1000000`. Labels, paths and arbitrary alert text are excluded. A valid
activity without `alert` clears the event view. Quiet mode still shows events
while keeping LEDs dim and motors still. A real event can interrupt the normal
30-second manual hold; expiry sends a clearing dashboard even during that hold.
Manual RGB commands stop ambient effects until automatic dashboards resume.
During a manual hold, alert and clearing dashboards send `ambient:false` to
preserve the selected LED color and face; the event screen still takes priority.

Automatic head movement remains opt-in. After the first 90 seconds, gentle
look-around/nod/alert gestures share a 90-second cooldown and use only yaw
±12 degrees, pitch 40..50 degrees, and speed 100. No automatic gesture runs
during manual hold or quiet mode; missing calibration blocks all motion.

The 320x240 landscape screen uses the documented Montserrat fonts and a dark,
high-contrast layout: a source badge, friendly activity/face panel, three aligned
metric cards, then battery voltage and dashboard update age. Values round to
whole percentages for quick reading. Each card includes a utilization bar and
up to 18 actual received samples on a fixed 0..100 scale. Missing readings break
the trend line; the chart is sample history, not a time-labelled graph.

`BAT 4.12V` is the device driver's measured voltage, not a charge percentage.
Unavailable voltage shows `BAT --`. `USB` / `BLE` identifies the last accepted
command source; it does not guarantee an ongoing connection. `Updated Ns ago`
measures the last accepted dashboard record. Manual desktop face/LED commands
pause automatic telemetry for 30 seconds, so increasing age does not imply a
disconnect. New readings reset the age; face/LED commands do not.

Rendering changes only bounded regions and changed text, preserving responsive
USB polling and existing LED fades. Setup checks the actual landscape dimensions
and font APIs before announcing readiness. The cited design research lives in
[`docs/research/stackchan-screen-design.md`](../../docs/research/stackchan-screen-design.md).
The LED/calibration evidence is in
[`docs/research/stackchan-personality.md`](../../docs/research/stackchan-personality.md).
Buffered rendering and physical timing evidence are in
[`docs/research/stackchan-dynamic-ui.md`](../../docs/research/stackchan-dynamic-ui.md).

## USB serial protocol

When this program is running, the StackChan's normal USB serial port accepts
newline-terminated UTF-8 JSON. MicaStats first sends a correlated identity
probe:

```json
{"companionHello":1,"requestId":"probe-1"}
```

The companion replies with a JSON line identifying the protocol and transport:

```json
{"ok":true,"type":"hello","device":"MicaStatsStackChan","protocol":1,"transport":"usb","requestId":"probe-1"}
```

Control messages use an envelope. `requestId` must be a non-empty string of at
most 64 characters; `command` contains the same object accepted over Bluetooth.

```json
{"requestId":"move-17","command":{"yawServo":{"angle":300,"speed":200}}}
```

The response repeats the request ID and reports `applied`, `rejected-motion`,
or `invalid-message`. Both USB and Bluetooth accept records up to 2048 UTF-8
bytes and keep independent partial-record buffers.

The normal USB connection talks to this running program. MicaStats'
**Start temporary USB companion** action is different: it enters MicroPython's
raw REPL, interrupts the current program, and runs the bundled companion in
memory until reboot. It does not upload files, flash firmware, or issue a soft
reset. Reboot StackChan to leave that temporary session and start the saved
Run Always program again.
See MicroPython's official [raw REPL documentation](https://docs.micropython.org/en/latest/reference/repl.html#raw-mode-and-raw-paste-mode)
for the distinction.

## Evidence

- [Official StackChan UIFlow2 programming guide](https://docs.m5stack.com/en/uiflow2/stackchan/program) — dedicated firmware flash and USB/Wi-Fi programming paths.
- [Official StackChan product guide](https://docs.m5stack.com/en/StackChan/) — CoreS3 hardware, BLE/Wi-Fi capability, and the 5°–85° Y-axis recommendation.
- Official upstream UiFlow source at the pinned retrieval revision:
  - [`hardware.stackchan` API](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst)
  - [`BLEUARTServer` API](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/docs/source/system/bleuart.server.rst)
  - [NUS UUID declaration](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/bleuart/bleuart_server.py)

Run the script temporarily with **Start temporary USB companion**, or upload it
with UIFlow and choose **Run Always** for persistent startup. MicaStats can use
its USB or Bluetooth companion protocol once the script is running.
