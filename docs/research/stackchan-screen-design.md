# StackChan companion-screen research

**Question.** What can the MicaStats companion show on an M5Stack StackChan
running UiFlow 2.0, and which screen treatment will be clear, polished, and
honest on the physical device?

**Research date.** 2026-10-13. The UiFlow source observations below are pinned
to M5Stack's commit
[`69846518a8b0893987ba0512558c4b01c9161454`](https://github.com/m5stack/uiflow-micropython/commit/69846518a8b0893987ba0512558c4b01c9161454),
committed 2026-10-08. They are a compatibility baseline, not proof that an
owner's flashed image has the exact same frozen modules.

## Verified platform facts

| Fact | Evidence and implication |
| --- | --- |
| The StackChan display is a 2.0-inch, 320 x 240, 65,536-colour IPS LCD. | M5Stack's [product specification](https://docs.m5stack.com/en/StackChan) identifies the resolution and ILI9342C/E driver. A single landscape 320 x 240 layout is appropriate; do not design around a presumed high-density phone display. |
| `M5.Lcd.width()` and `M5.Lcd.height()` are exposed. | The pinned [M5 GFX binding](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/components/M5Unified/mpy_m5gfx.cpp) binds both methods. The implementation should still measure at startup and retain a 320 x 240 fallback, so an unexpected rotation is visible rather than clipped. |
| The primitives needed for a dashboard exist: `fillRect`, `drawRect`, `fillRoundRect`, `drawRoundRect`, `fillCircle`, `drawCircle`, `drawLine`, `textWidth`, and `fontHeight`. | Their MicroPython argument bindings are in the pinned [M5 GFX source](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/components/M5Unified/mpy_m5gfx.cpp). Rounded metric cards and small circular status marks need no image assets or added package. |
| `Widgets.Label(text, x, y, text_sz, text_c, bg_c, font, parent)` accepts a background-colour argument, but it is unsafe for changing text over a card. | The official [Label reference](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/widgets/label.rst) defines the constructor and `setText`. In pinned [`mpy_m5widgets.cpp`](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/components/M5Unified/mpy_m5widgets.cpp), `setText` does return early for an identical value, but its erase helper fills with the global `_bg_color_g`, rather than the label's `bg_c`. A shorter changed value can therefore leave a screen-background hole in a card. Use an explicit `fillRect` of the known text region followed by `M5.Lcd.drawString` for changing activity, percentages, battery, and recency text. Static labels are harmless but unnecessary. |
| The documented StackChan fonts are Montserrat 12, 14, 16, 18, 24, 40, 44, and 48, plus CJK 24-pixel fonts. | M5Stack lists those fonts in its pinned [StackChan controller documentation](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst); its StackChan [build configuration](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/boards/M5STACK_StackChan/mpconfigboard.cmake) enables Montserrat 12/14/16/18/24/40/44/48. Use `Widgets.FONTS.Montserrat12`, `Montserrat18`, and `Montserrat24` for this screen and verify them during temporary RAM startup. |
| DejaVu availability is version-sensitive. | In the same pinned GFX source, DejaVu 9/12/18/24 bindings are gated by `BOARD_ID == 25`, while the StackChan build is `BOARD_ID 27`. The currently working local script has used `DejaVu18`, which establishes that the owner's flashed image exposes it, but it conflicts with this upstream baseline. It must not be treated as the portable choice; use Montserrat and validate on the device. |
| The StackChan driver supplies an honest local power reading and base-touch state. | `StackChan.get_battery_voltage()` returns INA226 bus voltage in volts; `get_battery_current()` returns amps; `get_battery_power()` returns watts; `get_touch()` returns three logical touch levels. See the pinned [driver](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/libs/hardware/stackchan.py) and [API reference](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/docs/source/controllers/stackchan.rst). Show `BAT 4.12 V` or `BAT --`, never an invented battery percentage. |
| The hardware includes a capacitive display touch surface and a separate three-zone touch panel on the robot body. | The [product page](https://docs.m5stack.com/en/StackChan) documents both. The official UiFlow example maps the three body zones to RGB groups and debounces release for 300 ms in [`stackchan_tp_rgb_example.py`](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/examples/controllers/stackchan/stackchan_tp_rgb_example.py). Do not introduce a hidden touch action in the telemetry redesign: it would change the established companion control model and needs an explicit interaction design. |

The device's own UiFlow status bar is useful source-reference evidence: it keeps
time, network/cloud icons, and a battery indicator in a 20-pixel top strip and
refreshes changed values on a five-second cadence, rather than redrawing a full
screen every loop. See
[`status_bar.py`](https://raw.githubusercontent.com/m5stack/uiflow-micropython/69846518a8b0893987ba0512558c4b01c9161454/m5stack/modules/startup/stackchan/apps/status_bar.py).

## Information-design guidance

The display is too small for a desktop-style monitoring table. The recommended
screen follows three durable principles:

1. **One glance, one primary state.** The friendly face and current activity
   explain what the companion is doing before the user reads numbers. CPU, RAM,
   and GPU each get the same visual position and value treatment, so they can
   be scanned rather than parsed.
2. **Colour supports text; it does not carry the meaning alone.** W3C's
   [WCAG 2.2 contrast guidance](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)
   specifies a 4.5:1 minimum contrast ratio for normal text and 3:1 for
   large-scale text. It is a web standard rather than a device certification,
   but it is a sound adaptation: use light text on the dark surface, write
   `CPU`, `RAM`, and `GPU` beside every gauge, and reserve cyan for emphasis.
3. **Communicate recency, not a guessed link state.** A dashboard message is
   currently sent on the configured 2--60 second cadence even when values do
   not change, but manual face/RGB operation intentionally pauses automatic
   dashboard updates for 30 seconds. The screen may show `UPDATED 12s AGO`,
   measured from the last accepted dashboard record, and `USB` or `BLE` as the
   source of that record. It must never call a silent interval `DISCONNECTED`
   or `OFFLINE` without an explicit heartbeat/disconnect contract.

## Recommended 320 x 240 screen

This is a concrete portrait of the first implementation. Coordinates include
their visible bounds and leave a small outer gutter for the curved LCD bezel.

| Area | Coordinates | Content and treatment |
| --- | --- | --- |
| Header | `x=12..308`, `y=7..32` | `MicaStats` at left in Montserrat 18 cyan. A neutral outlined `USB` or `BLE` badge at right identifies the source of the most recent accepted record; it does not imply a connection guarantee. |
| Companion hero | `x=12`, `y=42`, `w=296`, `h=64` | One dark rounded card. Keep the expressive face inside `x=20..104`, `y=48..102`; place the allow-listed activity at `x=122`, `y=51` and its muted recency hint at `y=79`. This preserves the friendly StackChan character while making the state legible. |
| CPU card | `x=12`, `y=116`, `w=92`, `h=99` | Rounded panel. `CPU` label in 12, large percentage in 24, a six-pixel bar, and a small history drawn from the last 18 actual samples. `--` replaces unavailable input. |
| RAM card | `x=114`, `y=116`, `w=92`, `h=99` | Same geometry and hierarchy as CPU. |
| GPU card | `x=216`, `y=116`, `w=92`, `h=99` | Same geometry and hierarchy as CPU. |
| Footer | `x=12..308`, `y=222..239` | Truthful device-local text: `BAT 4.12 V` / `BAT --` on the left and compact source or recency context on the right. This must never display a calculated charge percent. |

Suggested non-semantic palette: near-black background `#10151D`, a slightly
blue card surface `#172230`, light text `#E9F2F6`, muted text `#8FA3AD`, cyan
accent `#00D4FF`, and a low-key card edge around `#2B3E52`. Before accepting
the palette, calculate its text contrast against its immediate background;
keep the existing high-contrast foreground if a proposed muted colour falls
short.

Use `fillRoundRect` once for each static card. For every dynamic field, erase
its bounded region with `fillRect`, then draw with `setTextColor`, `setTextSize`,
`drawString`, `textWidth`, and `fontHeight`; this avoids the `Widgets.Label`
erase defect above. Redraw the face only after an avatar message. Metric history
contains the last 18 actual dashboard samples, never invented interpolation.
There is no reason to animate ordinary telemetry; an occasional 140 ms update
blink after at least 6.5 seconds adds a restrained companion expression and is
suppressed in Quiet mode. It does not indicate fresh data. It must never become a
repeated full-screen flash. Existing RGB fades may continue independently from
LCD refresh. This revision adds no touch action or other interaction.

## Compatibility and acceptance checks

- Start the companion temporarily on the actual StackChan and verify
  `M5.Lcd.width()==320`, `height()==240`, and all three selected Montserrat
  font constants before relying on the new layout.
- Verify both populated values and unavailable values (`--`) fit inside all
  three cards. Use `textWidth` when centring variable-width percentages.
- Exercise a dashboard update, face update, RGB command, and a local battery
  read. The dashboard must leave the face/card boundaries intact; avatar lines
  must not overwrite the metric cards.
- Leave servo commands out of this visual change. The robot still needs its
  documented servo-zero calibration before any head-control validation.

## Retrieval sufficiency and limits

This note uses official M5Stack product documentation and pinned upstream
UiFlow source, plus the W3C accessibility guidance. It intentionally does not
adopt an external UI framework or asset pack. Apple/Google small-screen design
guidance was not required because the directly applicable hardware API and the
contrast/recency rules establish the implementation constraints. The only
version ambiguity is the observed DejaVu binding on the local image; live
RAM-start validation resolves that before deployment.
