# StackChan personality: display rotation, LEDs, motion, and alerts

**Research date:** 2026-10-13. This note uses official M5Stack documentation
and UiFlow MicroPython source pinned to
[`69846518a8b0893987ba0512558c4b01c9161454`](https://github.com/m5stack/uiflow-micropython/commit/69846518a8b0893987ba0512558c4b01c9161454)
(2026-10-08). It is an API baseline; the local StackChan's earlier boot report
that servo-zero NVS data is missing is stronger evidence about this particular
robot than a generic source default.

**New device evidence:** the 2026-10-13 personality USB probe read the actual
NVS keys and reported both X and Y as `saved` with `motionCalibrated:true`.
This supersedes the earlier missing-key boot report. See
`artifacts/stackchan-personality/hardware-probe.json`. Presence is verified;
physical alignment and an actual motor gesture are separate checks.

## Direct answer

**Rotating dashboard screens and LED personality can proceed without servo
motion. Verify saved calibration and physical alignment before head movement.** The official UiFlow
driver substitutes built-in zero positions when NVS calibration keys are absent,
so a successful-looking `get_servo_angle()` result would not prove that the
physical head is calibrated. First perform the manufacturer-prescribed manual
calibration and then repeat a read-only NVS check. No programmatic motion,
torque, servo power, calibration save, or calibration reset should be sent as
part of the diagnostic.

## Official facts

| Capability | Verified behaviour | MicaStats implication |
| --- | --- | --- |
| Calibration persistence | `set_servo_zero()` writes both current servo positions to ESP32 NVS namespace `servo`, keys `zero_pos_1` and `zero_pos_2`; `set_servo_zero(reset=True)` instead writes code defaults. | Both calls are persistent writes and are excluded from automatic setup or diagnostics. The documented manual process is to pose X with the display aligned to the base, pose Y with the display perpendicular to the base, then tap **Save**. [Official StackChan UiFlow guide and API](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst), [pinned driver implementation](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/hardware/stackchan.py). |
| Missing calibration behaviour | On initialization, the driver attempts `NVS.get_i32` for each zero key. Missing, invalid, or out-of-range values cause it to log the condition and use code defaults (`450` X, `125` Y) at runtime. | A defaulted runtime angle is not a calibration verdict. The existing device log reporting missing servo-zero data blocks movement. [Pinned driver source](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/hardware/stackchan.py). |
| Read-back | `get_servo_angle(id)` reads servo position and converts it using the runtime zero; it returns `None` when the position read fails. The official read example enables the rail but explicitly leaves torque off so the head remains freely movable. | It is useful **after** known calibration for observing pose. It cannot by itself prove calibration, and enabling the rail is a device state change, so it is outside the read-only diagnostic. [API reference](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst), [official read example](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/examples/controllers/stackchan/stackchan_servo_read_example.py). |
| Servo power and torque | Servo rail power and torque are distinct operations. The public driver initializes with the servo rail off; the API exposes `set_servo_power(enable)` and `set_servo_torque(id, enable)`. | A personality service must never energize either servo merely because a dashboard, LED, or alert state arrives. [API reference](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst), [pinned driver](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/hardware/stackchan.py). |
| Movement API | `set_servo_angle` accepts X about -135°..135° and Y 0°..90°; its public speed range is 0..100. `set_servo_x_pwm` is continuous X-axis motion, with user range -100..100. | These are driver capability limits, not a recommended animated gesture envelope. Do not use continuous X PWM for personality. Retain MicaStats' existing narrower envelope after calibration and require explicit user opt-in for automatic head movement. [Official API](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst). |
| LEDs | The product has 12 WS2812C LEDs. The public driver provides one-colour-all, one-colour-per-logical-strip, and one-colour-per-pixel forms of `set_rgb_color`. It exposes two strips of six pixels; strip 1's logical index is physically reversed by the driver. | A 12-pixel progress, chase, or bilateral alert pattern is possible without a new package. Keep pattern semantics stable despite the strip-1 reversal by addressing the public logical indices. [Product specification](https://docs.m5stack.com/en/StackChan), [official API](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst), [pinned driver mapping](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/hardware/stackchan.py). |

## Safe read-only calibration diagnostic

The public API has no `has_servo_zero()` method. The narrowest truthful
diagnostic reads the two documented NVS keys directly and does not construct a
`StackChan` object, toggle its rail, query its servos, or write NVS:

```python
import esp32

nvs = esp32.NVS("servo")
status = {}
for axis, key in (("X", "zero_pos_1"), ("Y", "zero_pos_2")):
    try:
        value = nvs.get_i32(key)
        status[axis] = "saved" if 0 <= value <= 1000 else "invalid"
    except OSError:
        status[axis] = "missing"
print(status)
```

This is source-derived rather than a published M5Stack sample: the namespace,
key names, value range, and `get_i32` call are the exact driver implementation.
It establishes only that usable values are present. It does **not** establish
that the saved zero still matches the head's physical alignment. If either key
is missing or invalid, do not move the head. If both are saved, compare the
unpowered physical pose with the official calibration positions before deciding
whether to use motion; if the pose is not correct, run M5Stack's explicit
manual calibration project and Save action rather than resetting defaults.

## Personality plan after the gate

These are MicaStats design recommendations, not manufacturer-prescribed
patterns. They use the supported APIs above and retain user control.

| Situation | Screen | LED pattern | Head |
| --- | --- | --- | --- |
| Normal monitoring | Rotate a friendly face/summary panel with the numeric dashboard on a slow, predictable dwell; preserve the current activity and `UPDATED` recency on each page. | Soft symmetric cyan/teal breathing or a single gentle progress sweep. | Still. |
| AI working | Face with concise `AI WORKING` state; keep no user content or prompt text on the robot. | Slow bilateral cyan chase, stopped when the state ends. | Still unless calibrated and automatic movement is separately enabled. |
| Attention needed | A concise allow-listed status such as `ALERT` and the dashboard values; no private alert contents. | Brief amber bilateral pulse, then return to a low-key steady state. | Still by default. |
| Abnormal/critical event | A clear `CHECK MicaStats` state and the current telemetry page, rather than a scrolling error dump. | A short, bounded red indication followed by a steady low-brightness red/amber status; restore normal state when the event clears. | Still. |

Use LED animation as a finite state machine: a new event replaces the prior
pattern, Quiet mode dims or suppresses noncritical animation, and disconnect or
unknown state returns the LEDs to the configured idle state. Per-pixel writes
are supported, but the public driver refreshes each `set_rgb_color(strip,
index, color)` call; use a modest update cadence and do not depend on private
driver batching.

After calibration is physically verified, a future opt-in gesture should remain
small, slow, finite, and cancellable: begin with a single nod or look toward the
active metric, return to a neutral pose, release torque/power, and do not repeat
while an alert persists. The existing MicaStats safe envelope is intentionally
narrower than the driver's capability range and should remain the controlling
policy.

## Decision gate

- **Screens and LEDs:** safe to implement and validate now; they do not require
  servo power or torque.
- **Automatic head movement:** both NVS keys are now verified saved. Physical
  calibration alignment remains a separate prerequisite before a motor test.
- **First movement after calibration:** only with explicit automatic-motion
  opt-in, visible clearance around the head, a bounded one-shot gesture, and
  immediate return to neutral/off. No continuous PWM personality motion.
