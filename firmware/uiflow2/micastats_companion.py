"""MicaStats StackChan companion for the M5Stack UiFlow2 StackChan firmware.

Use the *StackChan* UiFlow2 image, then start this program temporarily over USB
or deploy it with Run Always. It supports USB console and BLE UART control;
it does not use factory BLE UUIDs or run alongside the factory firmware.

Incoming BLE and USB writes are independent UTF-8 byte streams.  Each JSON
object is followed by ``\n``.  BLE retains the factory-compatible unwrapped
format; USB requires a request ID and command envelope so a desktop client can
correlate every accepted request.
"""

import json
import framebuf
import math
import select
import sys
import time

import M5
from M5 import Widgets
from hardware.stackchan import StackChan

try:
    # Available in M5Stack's documented BLE-UART examples.
    from bleuart import BLEUARTServer
except ImportError:
    # Some current StackChan image manifests do not freeze the convenience
    # module.  Keep the NUS companion self-contained in that case.
    import bluetooth
    import struct

    _NUS_SERVICE = bluetooth.UUID("6E400001-B5A3-F393-E0A9-E50E24DCCA9E")
    _NUS_TX = (bluetooth.UUID("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"), bluetooth.FLAG_NOTIFY)
    _NUS_RX = (bluetooth.UUID("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"), bluetooth.FLAG_WRITE)

    class BLEUARTServer:
        """Minimal Nordic-UART server with the same read/write surface.

        This follows M5Stack's published BLEUART service UUIDs and behavior.
        It is only selected when their optional ``bleuart`` module is absent.
        """

        def __init__(self, name="", rxbuf=100):
            self._ble = bluetooth.BLE()
            self._ble.active(True)
            self._ble.irq(self._irq)
            ((self._tx_handle, self._rx_handle),) = self._ble.gatts_register_services(
                ((_NUS_SERVICE, (_NUS_TX, _NUS_RX)),)
            )
            self._ble.gatts_set_buffer(self._rx_handle, rxbuf, True)
            self._connections = set()
            self._rx = bytearray()
            self._advertise(name)

        def _irq(self, event, data):
            if event == 1:  # central connected
                self._connections.add(data[0])
            elif event == 2:  # central disconnected
                self._connections.discard(data[0])
                self._advertise(DEVICE_NAME)
            elif event == 3 and data[1] == self._rx_handle:  # GATTS write
                self._rx.extend(self._ble.gatts_read(self._rx_handle))

        def _advertise(self, name):
            name_bytes = name.encode()
            payload = bytearray(b"\x02\x01\x06")
            payload.extend(struct.pack("BB", len(name_bytes) + 1, 0x09))
            payload.extend(name_bytes)
            self._ble.gap_advertise(500000, adv_data=payload)

        def any(self):
            return len(self._rx)

        def read(self, size=None):
            if size is None:
                size = len(self._rx)
            result = self._rx[:size]
            self._rx = self._rx[size:]
            return result

        def write(self, data):
            for connection in self._connections:
                self._ble.gatts_notify(connection, self._tx_handle, data)


DEVICE_NAME = "MicaStatsStackChan"
MAX_LINE_BYTES = 2048
BLE_RX_BUFFER_BYTES = 4096
USB_READ_BURST = 256
BACKGROUND = 0x10151D
PANEL = 0x172230
EDGE = 0x2B3E52
FOREGROUND = 0xE9F2F6
ACCENT = 0x00D4FF
MUTED = 0xA5B8C7
MINT = 0x6EE7B7
LAVENDER = 0xC4B5FD
HISTORY_SAMPLES = 18
PAGE_INTERVAL_MS = 6000
FRAME_INTERVAL_MS = 80  # At most 12.5 small face frames/s; never delay USB reads.
STALE_AFTER_MS = 75000  # Includes the longest configured 60s update interval.
ALERT_KINDS = {
    "cpu-temperature": ("CPU temperature", "C"),
    "cpu-usage": ("CPU load", "%"),
    "memory-used": ("Memory used", "%"),
    "disk-free": ("Disk space low", "GB"),
    "gpu-usage": ("GPU load", "%"),
    "battery-health": ("Battery health", "%"),
    "battery-charge": ("Battery charge", "%"),
}
FACE_LEFT, FACE_TOP, FACE_WIDTH, FACE_HEIGHT = 20, 48, 84, 54
ACTIVITIES = ("Ready", "Quiet", "Alert", "AI working", "Meeting active", "PC busy")
ACTIVITY_HINTS = {
    "Ready": "Your PC is ready",
    "Quiet": "Keeping things quiet",
    "Alert": "Check MicaStats alerts",
    "AI working": "Thinking with you",
    "Meeting active": "Meeting in progress",
    "PC busy": "Your PC is working hard",
}

# The product documentation recommends 5..85 degrees for the physical tilt
# servo.  The factory payload uses tenths of a degree; this companion preserves
# that unit at its BLE boundary.
SAFE_YAW_TENTHS = (-450, 450)
SAFE_PITCH_TENTHS = (50, 850)

# Presence: the desktop detects faces in these frames on its GPU.  The robot
# only captures, streams and shows; frames are never stored on the device.
CAMERA_FRAMESIZE = "QVGA"
CAMERA_KEEPALIVE_MS = 10000  # Stop by itself if MicaStats goes quiet.
MAX_FRAME_FPS = 6
CAMERA_DOT = 0xF43F5E
SCENE_COLORS = {"welcome": 0xFDBA74, "guard": 0xF43F5E, "break": MINT, "doze": MUTED}
SCENE_RANK = {"doze": 0, "break": 1, "welcome": 2, "guard": 3}
SCENE_TITLES = {"welcome": "Welcome back", "guard": "Someone is behind you",
                "break": "Time for a break", "doze": "Resting"}


def clamp(value, minimum, maximum):
    return max(minimum, min(maximum, value))


def read_int(value):
    if isinstance(value, bool):
        return None
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError):
        return None


def as_int(value, fallback=0):
    result = read_int(value)
    return fallback if result is None else result


def read_float(value):
    if isinstance(value, bool):
        return None
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError):
        return None
    # MicroPython does not provide math.isfinite on every supported image.
    return None if result != result or abs(result) == float("inf") else result


def percent_text(value):
    number = read_float(value)
    if number is None or number < 0:
        return "--"
    return str(int(clamp(number, 0, 100) + 0.5)) + "%"


def parse_rgb(value):
    """Convert factory-style #RRGGBB text to UiFlow's 0xRRGGBB integer."""
    if not isinstance(value, str) or len(value) != 7 or value[0] != "#":
        return None
    try:
        return int(value[1:], 16)
    except ValueError:
        return None


class ScreenText:
    """One opaque text region, avoiding Widget.Label's global-background erase."""

    def __init__(self, text, x, y, width, height, font, foreground=FOREGROUND,
                 background=BACKGROUND, centered=False):
        self.x, self.y, self.width, self.height = x, y, width, height
        self.font, self.foreground, self.background = font, foreground, background
        self.centered = centered
        self.text = None
        self.setText(text)

    def setText(self, text):
        if text == self.text:
            return
        self.text = text
        M5.Lcd.setTextSize(1.0)
        # All generated strings are short ASCII. Bound a label even on a
        # version with wider font metrics, without drawing across panels.
        visible = text
        while visible and M5.Lcd.textWidth(visible, self.font) > self.width:
            visible = visible[:-1]
        width = M5.Lcd.textWidth(visible, self.font)
        x = self.x + (self.width - width) // 2 if self.centered else self.x
        M5.Lcd.fillRect(self.x, self.y, self.width, self.height, self.background)
        M5.Lcd.setTextColor(self.foreground, self.background)
        M5.Lcd.drawString(visible, x, self.y, self.font)


class FaceCanvas:
    """Reusable native RGB565 frame; publish once, with no visible erase pass."""

    def __init__(self, left, top, width, height):
        self.left, self.top, self.width, self.height = left, top, width, height
        self.pixels = bytearray(width * height * 2)
        self.frame = framebuf.FrameBuffer(self.pixels, width, height, framebuf.RGB565)
        self.colors = {}

    def color(self, rgb):
        if rgb not in self.colors:
            self.colors[rgb] = ((rgb >> 19) & 31) << 11 | ((rgb >> 10) & 63) << 5 | ((rgb >> 3) & 31)
        return self.colors[rgb]

    def clear(self):
        self.frame.fill(self.color(PANEL))

    def drawLine(self, x1, y1, x2, y2, color):
        self.frame.line(x1 - self.left, y1 - self.top, x2 - self.left, y2 - self.top, self.color(color))

    def fillCircle(self, x, y, radius, color):
        for row in range(-radius, radius + 1):
            half = int(math.sqrt(radius * radius - row * row))
            self.frame.hline(x - self.left - half, y - self.top + row, half * 2 + 1, self.color(color))

    def present(self):
        # UIFlow drawRawBuf takes a pixel count (not byte count). framebuf
        # stores native RGB565; the binding swaps it into LCD wire order.
        M5.Lcd.drawRawBuf(self.pixels, self.left, self.top, self.width, self.height,
                          self.width * self.height, True)


class Companion:
    def __init__(self):
        self.stackchan = None
        self.ble = None
        self.usb_poll = None
        self.input_buffer = bytearray()
        self.discarding_overlong_line = False
        self.usb_input_buffer = bytearray()
        self.usb_discarding_overlong_line = False
        self.last_local_update = time.ticks_ms()
        self.last_screen_update = 0
        self.last_dashboard = None
        self.dashboard_old = False
        self.activity = None
        self.transport = None
        self.metrics = {"cpu": None, "ram": None, "gpu": None}
        self.histories = {"cpu": [], "ram": [], "gpu": []}
        self.blinking = False
        self.last_blink = time.ticks_ms()
        self.blink_started = 0
        self.screen_width = 320
        self.screen_height = 240
        self.rgb_transitions = {0: None, 1: None}
        self.rgb_targets = {0: 0, 1: 0}
        self.rgb_last = {0: None, 1: None}
        self.ambient_enabled = False
        self.last_ambient = 0
        self.view = "companion"
        self.page_started = time.ticks_ms()
        self.focus_index = 0
        self.alert = None
        self.gaze = 0
        self.gaze_y = 0
        self.gaze_target = None
        self.scene = None
        self.camera = None
        self.camera_on = False
        self.camera_fps = 1
        self.camera_quality = 50
        self.camera_seq = 0
        self.camera_refreshed = 0
        self.camera_last_frame = 0
        self.camera_frame_due = False
        self.camera_failure_pending = False
        self.bob = 0
        self.eye_open = 100
        self.expression_phase = 0
        self.animation_started = time.ticks_ms()
        self.last_animation = time.ticks_ms()
        self.animation_key = None
        self.page_progress = -1
        self.alert_border = 0xFF7043
        self.last_progress = time.ticks_ms()
        self.motion_calibrated = False
        self.calibration = {"x": "unknown", "y": "unknown"}
        self.large_canvas = FaceCanvas(55, 48, 210, 108)
        self.small_canvas = FaceCanvas(FACE_LEFT, FACE_TOP, FACE_WIDTH, FACE_HEIGHT)
        self.face_canvas = self.large_canvas
        self.left_eye = {"x": 0, "y": 0, "rotation": 0, "weight": 0, "size": 0}
        self.right_eye = {"x": 0, "y": 0, "rotation": 0, "weight": 0, "size": 0}
        self.mouth = {"x": 0, "y": 0, "rotation": 0, "weight": 0, "size": 0}

    def setup(self):
        M5.begin()
        self.screen_width = M5.Lcd.width()
        self.screen_height = M5.Lcd.height()
        # This is the documented StackChan landscape orientation. Do not
        # silently draw a clipped dashboard on a differently rotated screen.
        if self.screen_width != 320 or self.screen_height != 240:
            raise ValueError("StackChan companion requires a 320x240 landscape LCD")
        self.setup_display()

        # StackChan initializes the servo rail off.  This program never powers
        # it or enables torque until a motion command is received.
        self.check_calibration()
        self.stackchan = StackChan(i2c=1, uart=1)
        self.usb_poll = select.poll()
        self.usb_poll.register(sys.stdin, select.POLLIN)
        sys.stdout.write(DEVICE_NAME + " USB ready\n")
        try:
            sys.stdout.flush()
        except Exception:
            pass

        # Bluetooth is optional. USB stays available without the radio/helper.
        try:
            self.ble = BLEUARTServer(name=DEVICE_NAME, rxbuf=BLE_RX_BUFFER_BYTES)
        except Exception:
            self.ble = None
        self.update_local_status()

    def check_calibration(self):
        # The driver silently substitutes defaults for missing NVS zero keys.
        # Read the actual stored keys; never save/reset zeros or energize motors.
        try:
            import esp32
            nvs = esp32.NVS("servo")
            for axis, key in (("x", "zero_pos_1"), ("y", "zero_pos_2")):
                try:
                    value = nvs.get_i32(key)
                    self.calibration[axis] = "saved" if 0 <= value <= 1000 else "invalid"
                except Exception:
                    self.calibration[axis] = "missing"
        except Exception:
            self.calibration = {"x": "unknown", "y": "unknown"}
        self.motion_calibrated = all(value == "saved" for value in self.calibration.values())

    def setup_display(self):
        Widgets.fillScreen(BACKGROUND)
        small, medium = Widgets.FONTS.Montserrat12, Widgets.FONTS.Montserrat18
        self.title_label = ScreenText("MicaStats", 12, 8, 190, 24, medium, ACCENT)
        M5.Lcd.drawRoundRect(258, 7, 50, 23, 7, EDGE)
        self.transport_label = ScreenText("WAIT", 262, 11, 42, 15, small, MUTED, centered=True)
        self.draw_companion()
        M5.Lcd.drawLine(12, 220, 307, 220, EDGE)
        # Outline is a battery symbol only; no unmeasured charge estimate.
        M5.Lcd.drawRoundRect(12, 225, 14, 9, 2, MUTED)
        M5.Lcd.fillRect(26, 228, 2, 3, MUTED)
        self.local_label = ScreenText("BAT --", 34, 222, 111, 16, small, MUTED)
        self.age_label = ScreenText("Updated --", 164, 222, 144, 16, small, MUTED)
        self.draw_page_dots()

    def draw_companion(self):
        M5.Lcd.fillRoundRect(12, 42, 296, 132, 8, PANEL)
        M5.Lcd.drawRoundRect(12, 42, 296, 132, 8, EDGE)
        self.companion_label = ScreenText(self.activity or "Waiting for MicaStats", 24, 158, 272, 16,
                                          Widgets.FONTS.Montserrat12, MUTED, PANEL, centered=True)
        self.activity_label = self.companion_label
        self.metric_labels = {}
        for key, x, color in (("cpu", 12, ACCENT), ("ram", 114, MINT), ("gpu", 216, LAVENDER)):
            M5.Lcd.fillRoundRect(x, 182, 92, 33, 8, PANEL)
            ScreenText(key.upper(), x + 7, 192, 30, 16, Widgets.FONTS.Montserrat12, color, PANEL)
            self.metric_labels[key] = ScreenText(percent_text(self.metrics[key]), x + 36, 187, 52, 23,
                                                 Widgets.FONTS.Montserrat18, FOREGROUND, PANEL, centered=True)
        self.cpu_label, self.ram_label, self.gpu_label = (self.metric_labels[key] for key in ("cpu", "ram", "gpu"))
        self.draw_face()

    def draw_overview(self):
        small, medium, large = (Widgets.FONTS.Montserrat12,
                                Widgets.FONTS.Montserrat18, Widgets.FONTS.Montserrat24)
        M5.Lcd.fillRoundRect(12, 42, 296, 64, 8, PANEL)
        M5.Lcd.drawRoundRect(12, 42, 296, 64, 8, EDGE)
        M5.Lcd.drawLine(111, 54, 111, 94, EDGE)
        self.activity_label = ScreenText(self.activity or "Waiting", 122, 51, 174, 23, medium, background=PANEL)
        self.hint_label = ScreenText(ACTIVITY_HINTS.get(self.activity, "MicaStats > StackChan"),
                                     122, 79, 174, 16, small, MUTED, PANEL)
        self.metric_labels = {}
        for key, x, color in (("cpu", 12, ACCENT), ("ram", 114, MINT), ("gpu", 216, LAVENDER)):
            if self.view == "focus":
                break
            M5.Lcd.fillRoundRect(x, 116, 92, 99, 8, PANEL)
            M5.Lcd.drawRoundRect(x, 116, 92, 99, 8, EDGE)
            ScreenText(key.upper(), x + 10, 126, 72, 16, small, color, PANEL)
            self.metric_labels[key] = ScreenText(percent_text(self.metrics[key]), x + 8, 147, 76, 29, large,
                                                 background=PANEL, centered=True)
            self.draw_metric(key)
        if self.view == "overview":
            self.cpu_label = self.metric_labels["cpu"]
            self.ram_label = self.metric_labels["ram"]
            self.gpu_label = self.metric_labels["gpu"]
        self.draw_face()

    def draw_page_dots(self):
        for index, view in enumerate(("overview", "focus", "companion")):
            M5.Lcd.fillCircle(224 + index * 10, 21, 2, ACCENT if self.view == view else EDGE)

    def set_view(self, view, now):
        if self.view == view:
            return
        self.view = view
        self.page_started = now
        self.page_progress = -1
        # Redraw only the body when its layout changes; USB polling and footer
        # remain independent. Never repaint the full LCD in the active loop.
        M5.Lcd.fillRect(0, 40, 320, 178, BACKGROUND)
        self.title_label.setText({"focus": "Metric spotlight", "companion": "Your companion",
                                  "alert": "MicaStats alert"}.get(view, "MicaStats"))
        self.draw_page_dots()
        if view in ("overview", "focus"):
            self.draw_overview()
            if view == "focus":
                key = ("cpu", "ram", "gpu")[self.focus_index]
                M5.Lcd.fillRoundRect(12, 116, 296, 99, 8, PANEL)
                M5.Lcd.drawRoundRect(12, 116, 296, 99, 8, EDGE)
                ScreenText(key.upper() + " load", 24, 124, 170, 23,
                           Widgets.FONTS.Montserrat18, ACCENT, PANEL)
                self.focus_label = ScreenText(percent_text(self.metrics[key]), 208, 122, 88, 29,
                                               Widgets.FONTS.Montserrat24, background=PANEL, centered=True)
                self.draw_focus()
        elif view == "companion":
            self.draw_companion()
        elif view == "alert":
            self.alert_border = 0xFF7043
            M5.Lcd.fillRoundRect(12, 42, 296, 173, 8, PANEL)
            M5.Lcd.drawRoundRect(12, 42, 296, 173, 8, 0xFF7043)
            ScreenText("Recent alert", 24, 51, 272, 23, Widgets.FONTS.Montserrat18, 0xFFAB91, PANEL)
            self.alert_name = ScreenText("System event", 24, 82, 272, 29,
                                         Widgets.FONTS.Montserrat24, background=PANEL)
            self.alert_value = ScreenText("Check MicaStats", 24, 118, 272, 29,
                                          Widgets.FONTS.Montserrat24, 0xFFAB91, PANEL)
            self.alert_threshold = ScreenText("", 24, 155, 272, 23,
                                              Widgets.FONTS.Montserrat18, MUTED, PANEL)
            ScreenText("Open Diagnostics for details", 24, 188, 272, 16,
                       Widgets.FONTS.Montserrat12, MUTED, PANEL)
            self.draw_alert()

    def draw_focus(self):
        key = ("cpu", "ram", "gpu")[self.focus_index]
        self.focus_label.setText(percent_text(self.metrics[key]))
        color = {"cpu": ACCENT, "ram": MINT, "gpu": LAVENDER}[key]
        M5.Lcd.fillRoundRect(24, 157, 272, 6, 3, EDGE)
        value = self.metrics[key]
        if value is not None and value > 0:
            width = clamp(int(272 * value / 100 + .5), 1, 272)
            M5.Lcd.fillRoundRect(24, 157, width, 6, min(3, width // 2), color)
        M5.Lcd.fillRect(24, 174, 272, 37, PANEL)
        M5.Lcd.drawLine(24, 208, 295, 208, EDGE)
        history = self.histories[key]
        start = 24 + (HISTORY_SAMPLES - len(history)) * 16
        previous = None
        for index, sample in enumerate(history):
            if sample is None:
                previous = None
                continue
            point = (start + index * 16, 207 - int(sample * 31 / 100 + .5))
            if previous is not None:
                M5.Lcd.drawLine(previous[0], previous[1], point[0], point[1], color)
            M5.Lcd.fillCircle(point[0], point[1], 1, color)
            previous = point

    @staticmethod
    def read_alert(incoming):
        if not isinstance(incoming, dict) or incoming.get("kind") not in ALERT_KINDS:
            return None
        value, threshold = read_float(incoming.get("value")), read_float(incoming.get("threshold"))
        above = incoming.get("above")
        if value is None or threshold is None or not 0 <= value <= 1000000 or not 0 <= threshold <= 1000000 or not isinstance(above, bool):
            return None
        # Copy only the defined public fields. Ignore labels, paths and detail.
        return {"kind": incoming["kind"], "value": value, "threshold": threshold, "above": above}

    def draw_alert(self):
        if self.alert is None:
            self.alert_name.setText("System event")
            self.alert_value.setText("Check MicaStats")
            self.alert_threshold.setText("")
            return
        name, unit = ALERT_KINDS[self.alert["kind"]]
        self.alert_name.setText(name)
        self.alert_value.setText("{:.1f} {}".format(self.alert["value"], unit))
        self.alert_threshold.setText(("Above " if self.alert["above"] else "Below ") +
                                    "{:.1f} {}".format(self.alert["threshold"], unit))

    def draw_face(self):
        if self.view == "alert" or self.scene is not None:
            return
        self.face_canvas = self.large_canvas if self.view == "companion" else self.small_canvas
        self.face_canvas.clear()
        self.draw_eye(self.left_eye, 41)
        self.draw_eye(self.right_eye, 83)
        self.draw_mouth(self.mouth)
        if self.view == "companion":
            self.draw_activity_marks()
        self.face_canvas.present()

    def draw_face_line(self, x1, y1, x2, y2, color, thickness=2):
        # Extreme feature offsets or rotations must not erase dashboard labels.
        x1, x2 = clamp(x1, 20, 103), clamp(x2, 20, 103)
        y1, y2 = clamp(y1, 48, 101), clamp(y2, 48, 101)
        if self.view == "companion":
            for offset in range(thickness):
                self.face_canvas.drawLine(55 + int((x1 - 20) * 2.5), min(155, 48 + int((y1 - 48) * 2) + offset),
                                55 + int((x2 - 20) * 2.5), min(155, 48 + int((y2 - 48) * 2) + offset), color)
        else:
            self.face_canvas.drawLine(int(x1), int(y1), int(x2), int(y2), color)

    def draw_face_ellipse(self, x, y, major, minor, color, filled=True, mask=15):
        # Upright presets use one native primitive instead of Python scan
        # lines. The framebuffer clips extreme factory offsets itself.
        if self.view == "companion":
            x, y = 55 + int((x - 20) * 2.5), 48 + int((y - 48) * 2)
            major, minor = int(major * 2.5), int(minor * 2)
        self.face_canvas.frame.ellipse(int(x - self.face_canvas.left), int(y - self.face_canvas.top),
                                      int(major), int(minor), self.face_canvas.color(color), filled, mask)

    @staticmethod
    def feature_position(feature, base_x, base_y):
        # Factory avatar coordinates are signed feature offsets.  Keep them
        # bounded so malformed packets cannot draw outside the display.
        x = base_x + clamp(as_int(feature.get("x")), -100, 100) // 8
        y = base_y + clamp(as_int(feature.get("y")), -100, 100) // 8
        return x, y

    def draw_eye(self, feature, base_x):
        x, y = self.feature_position(feature, base_x, 66)
        x += self.gaze
        y += self.bob + self.gaze_y
        size = as_int(feature.get("size"), 50)
        # Desktop/factory Neutral sends all-zero features. Zero is the normal
        # appearance; signed preset sizes describe expressive variants.
        size = 50 if size == 0 else size
        major = clamp(abs(size) // 5, 6, 15)
        weight = as_int(feature.get("weight"), 50)
        weight = 50 if weight == 0 else weight
        minor = clamp(abs(weight) // 6, 2, 9)
        if size < 0:
            minor = max(1, minor // 3)
        minor = max(1, int(minor * self.eye_open / 100))
        angle = clamp(as_int(feature.get("rotation")), 0, 3600)
        if angle in (0, 3600):
            if 0 < size < 40 and self.eye_open > 40:
                for offset in range(2):
                    self.draw_face_ellipse(x, y + offset, major, 3, FOREGROUND, False, 3)
            else:
                self.draw_face_ellipse(x, y, major, minor, FOREGROUND)
                if size >= 40 and minor >= 4:
                    iris_x = x + self.gaze * .35
                    self.draw_face_ellipse(iris_x, y, 3, minor - 2, ACCENT)
                    self.draw_face_line(iris_x - 1, y - 3, iris_x, y - 3, FOREGROUND)
            return
        rotation = math.pi * angle / 1800
        cos_angle, sin_angle = math.cos(rotation), math.sin(rotation)
        if 0 < size < 40 and self.eye_open > 40:
            # Happy presets retain their small positive eye size as a soft
            # closed-eye arch. Signed busy/sleepy presets stay slim ovals.
            for offset in range(2):
                previous = None
                for step in range(9):
                    dx = -major + 2 * major * step / 8
                    dy = -3 * (1 - (2 * step / 8 - 1) ** 2)
                    px = x + int(dx * cos_angle - dy * sin_angle)
                    py = y + int(dx * sin_angle + dy * cos_angle) + offset
                    if previous is not None:
                        self.draw_face_line(previous[0], previous[1], px, py, FOREGROUND)
                    previous = (px, py)
            return
        # Scan lines fill a rotated oval using only the documented primitive.
        resolution = 2 if self.view == "companion" else 1
        for step in range(-minor * resolution, minor * resolution + 1):
            row = step / resolution
            half = major * math.sqrt(max(0, 1 - (row / minor) ** 2))
            self.draw_face_line(x - half * cos_angle - row * sin_angle,
                                y - half * sin_angle + row * cos_angle,
                                x + half * cos_angle - row * sin_angle,
                                y + half * sin_angle + row * cos_angle, FOREGROUND, 1)
        # A cyan iris and a pale glint give open eyes a clear point of attention.
        # Both use the same signed factory geometry plus temporary gaze offsets.
        if size >= 40 and minor >= 4:
            for row in range(-minor + 2, minor - 1):
                half = int(3 * math.sqrt(max(0, 1 - (row / max(1, minor - 1)) ** 2)))
                left, right = self.gaze * .35 - half, self.gaze * .35 + half
                self.draw_face_line(x + left * cos_angle - row * sin_angle,
                                    y + left * sin_angle + row * cos_angle,
                                    x + right * cos_angle - row * sin_angle,
                                    y + right * sin_angle + row * cos_angle, ACCENT)
            glint = self.gaze * .35
            self.draw_face_line(x + (glint - 1) * cos_angle + 3 * sin_angle,
                                y + (glint - 1) * sin_angle - 3 * cos_angle,
                                x + glint * cos_angle + 3 * sin_angle,
                                y + glint * sin_angle - 3 * cos_angle, FOREGROUND)

    def draw_mouth(self, feature):
        x, y = self.feature_position(feature, 62, 85)
        y += self.bob
        size = as_int(feature.get("size"), 50)
        size = 50 if size == 0 else size
        width = clamp(abs(size) // 2, 8, 36)
        weight = as_int(feature.get("weight"), 50)
        weight = 50 if weight == 0 else weight
        thickness = clamp(abs(weight) // 25 + 1, 1, 4)
        radians = math.pi * clamp(as_int(feature.get("rotation")), 0, 3600) / 1800
        cos_angle, sin_angle = math.cos(radians), math.sin(radians)
        # Positive mouth sizes have a gentle smile. Factory feature rotation
        # still rotates the whole curve, including concerned upside-down lips.
        curve = 4 if 0 < size < 45 else 2 if size >= 45 or 1600 <= as_int(feature.get("rotation")) <= 2000 else 0
        if as_int(feature.get("rotation")) in (0, 1800, 3600):
            for offset in range(thickness):
                if curve:
                    self.draw_face_ellipse(x, y + offset, width / 2, curve, ACCENT, False,
                                           3 if as_int(feature.get("rotation")) == 1800 else 12)
                else:
                    self.draw_face_line(x - width / 2, y + offset, x + width / 2, y + offset, ACCENT)
            return
        for offset in range(thickness):
            previous = None
            for step in range(9):
                dx = int(-width / 2 + width * step / 8)
                dy = int(curve * (1 - (2 * step / 8 - 1) ** 2))
                px = x + int(dx * cos_angle - dy * sin_angle)
                py = y + int(dx * sin_angle + dy * cos_angle) + offset
                if previous is not None:
                    self.draw_face_line(previous[0], previous[1], px, py, ACCENT)
                previous = (px, py)

    def draw_activity_marks(self):
        # These are illustrations of an accepted activity, never measured
        # audio levels, progress, utilization, or an inferred AI result.
        if self.activity == "AI working":
            for index in range(3):
                self.face_canvas.fillCircle(147 + index * 13, 146, 3,
                                  LAVENDER if self.expression_phase % 3 == index else EDGE)
        elif self.activity == "Meeting active":
            color = MINT if self.expression_phase % 4 < 2 else MUTED
            for side in (-1, 1):
                center = 160 + side * 84
                self.face_canvas.drawLine(center, 87, center + side * 3, 91, color)
                self.face_canvas.drawLine(center + side * 3, 91, center, 95, color)
        elif self.activity == "PC busy":
            color = 0xFFCC80 if self.expression_phase % 4 < 2 else MUTED
            self.face_canvas.drawLine(145, 146, 155, 146, color)
            self.face_canvas.drawLine(165, 146, 175, 146, color)
        elif self.activity == "Ready":
            # A quiet twinkle, far from the metrics and transport badge.
            color = ACCENT if self.expression_phase % 8 == 0 else EDGE
            self.face_canvas.drawLine(248, 63, 248, 71, color)
            self.face_canvas.drawLine(244, 67, 252, 67, color)

    @staticmethod
    def update_feature(target, incoming):
        if not isinstance(incoming, dict):
            return False
        changed = False
        for field in ("x", "y", "rotation", "weight", "size"):
            if field in incoming:
                value = read_int(incoming[field])
                if value is not None:
                    lower, upper = (-100, 100)
                    if field == "rotation":
                        lower, upper = (0, 3600)
                    elif field == "weight":
                        lower, upper = (0, 100)
                    bounded = clamp(value, lower, upper)
                    if target[field] != bounded:
                        target[field] = bounded
                        changed = True
        return changed

    def apply_avatar(self, payload):
        # This retains the five documented factory feature fields, while
        # rendering them with simple primitives rather than the factory avatar.
        changed = self.update_feature(self.left_eye, payload.get("leftEye"))
        changed = self.update_feature(self.right_eye, payload.get("rightEye")) or changed
        changed = self.update_feature(self.mouth, payload.get("mouth")) or changed
        if changed:
            self.draw_face()

    def set_servo_angle(self, servo_id, tenths, lower, upper, speed):
        degrees = tenths / 10.0
        # UiFlow's driver takes a 0..100 user speed rather than the factory
        # firmware's 0..1000 speed.  Preserve the intended proportion.
        ui_speed = clamp(speed // 10, 0, 100)
        self.stackchan.set_servo_power(True)
        self.stackchan.set_servo_torque(servo_id, True)
        self.stackchan.set_servo_angle(servo_id, degrees, 0, ui_speed)

    @staticmethod
    def validate_servo(config, lower, upper):
        if not isinstance(config, dict):
            return "ignored"
        if "rotate" in config:
            return "continuous rotation is disabled"
        if "angle" not in config:
            return "angle is required"
        angle = read_int(config["angle"])
        speed = 200 if "speed" not in config else read_int(config["speed"])
        if angle is None or speed is None:
            return "angle and speed must be integers"
        if angle < lower or angle > upper:
            return "angle outside companion safe range"
        if speed < 100 or speed > 300:
            return "speed must be 100..300"
        return "applied"

    def apply_motion(self, payload):
        if not isinstance(payload, dict):
            return []
        requests = []
        results = []
        if "yawServo" in payload:
            config = payload["yawServo"]
            result = self.validate_servo(config, *SAFE_YAW_TENTHS)
            results.append(result)
            requests.append((config, self.stackchan.SERVO_ID_X, SAFE_YAW_TENTHS, result))
        if "pitchServo" in payload:
            config = payload["pitchServo"]
            result = self.validate_servo(config, *SAFE_PITCH_TENTHS)
            results.append(result)
            requests.append((config, self.stackchan.SERVO_ID_Y, SAFE_PITCH_TENTHS, result))

        # Validate every requested axis before touching either servo.  A packet
        # containing one safe and one unsafe axis is rejected atomically.
        if any(result not in ("applied", "ignored") for result in results):
            return results
        if any(result == "applied" for result in results) and not self.motion_calibrated:
            return ["calibration required"]
        for config, servo_id, bounds, result in requests:
            if result == "applied":
                angle = read_int(config["angle"])
                speed = 200 if "speed" not in config else read_int(config["speed"])
                self.set_servo_angle(servo_id, angle, bounds[0], bounds[1], speed)
        return results

    def current_strip_rgb(self, strip):
        try:
            r, g, b = self.stackchan.get_rgb_color(strip, 0)
            return (int(r), int(g), int(b))
        except Exception:
            return (0, 0, 0)

    def schedule_rgb(self, strip, color, duration_value):
        duration = read_float(duration_value)
        if duration is None or duration < 0 or duration > 10:
            return
        self.rgb_targets[strip] = color
        self.rgb_last[strip] = None
        if duration == 0:
            self.rgb_transitions[strip] = None
            self.stackchan.set_rgb_color(strip, color)
            return
        duration_ms = clamp(int(duration * 1000), 1, 10000)
        self.rgb_transitions[strip] = {
            "start": self.current_strip_rgb(strip),
            "target": ((color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF),
            "started": time.ticks_ms(),
            "duration": duration_ms,
        }

    def update_rgb_transitions(self):
        now = time.ticks_ms()
        for strip in (0, 1):
            transition = self.rgb_transitions[strip]
            if transition is None:
                continue
            elapsed = time.ticks_diff(now, transition["started"])
            percent = clamp(elapsed, 0, transition["duration"]) / transition["duration"]
            channels = []
            for start, target in zip(transition["start"], transition["target"]):
                channels.append(int(start + (target - start) * percent))
            color = (channels[0] << 16) | (channels[1] << 8) | channels[2]
            self.stackchan.set_rgb_color(strip, color)
            if elapsed >= transition["duration"]:
                self.rgb_transitions[strip] = None

    def apply_rgb(self, payload):
        left = parse_rgb(payload.get("leftRgbColor"))
        right = parse_rgb(payload.get("rightRgbColor"))
        if left is not None:
            self.ambient_enabled = False
            self.schedule_rgb(0, left, payload.get("leftRgbDuration", 0))
        if right is not None:
            self.ambient_enabled = False
            self.schedule_rgb(1, right, payload.get("rightRgbDuration", 0))

    def apply_dashboard(self, dashboard):
        if not isinstance(dashboard, dict):
            return
        if self.scene is not None:
            if dashboard.get("activity") == "Alert" or self.read_alert(dashboard.get("alert")) is not None:
                # Alerts outrank every presence card.
                self.scene = None
            else:
                self.record_dashboard(dashboard)
                return
        received = False
        for key in ("cpu", "ram", "gpu"):
            if key not in dashboard:
                continue
            received = True
            value = read_float(dashboard[key])
            value = None if value is None or value < 0 else clamp(value, 0, 100)
            self.metrics[key] = value
            history = self.histories[key]
            history.append(value)
            if len(history) > HISTORY_SAMPLES:
                del history[0]
            if self.view in ("overview", "companion"):
                self.metric_labels[key].setText(percent_text(value))
                if self.view == "overview":
                    self.draw_metric(key)
        activity = dashboard.get("activity")
        if activity in ACTIVITIES:
            received = True
            self.activity = activity
            self.alert = self.read_alert(dashboard.get("alert"))
            if self.view in ("overview", "focus"):
                self.activity_label.setText(activity)
                self.hint_label.setText(ACTIVITY_HINTS[activity])
            elif self.view == "companion":
                self.companion_label.setText(activity)
        if "alert" in dashboard:
            self.alert = self.read_alert(dashboard["alert"])
        if received:
            self.last_dashboard = time.ticks_ms()
            self.dashboard_old = False
            self.ambient_enabled = dashboard.get("ambient", True) is True
            self.update_age(self.last_dashboard)
            if self.alert is not None or self.activity == "Alert":
                self.set_view("alert", self.last_dashboard)
                self.draw_alert()
            elif self.activity == "Quiet":
                self.set_view("overview", self.last_dashboard)
            elif self.view == "alert":
                self.set_view("companion", self.last_dashboard)
            elif self.view == "focus":
                self.draw_focus()

    def record_dashboard(self, dashboard):
        """Keep readings while a presence card holds the screen; the redraw shows them."""
        for key in ("cpu", "ram", "gpu"):
            if key in dashboard:
                value = read_float(dashboard[key])
                value = None if value is None or value < 0 else clamp(value, 0, 100)
                self.metrics[key] = value
                self.histories[key].append(value)
                if len(self.histories[key]) > HISTORY_SAMPLES:
                    del self.histories[key][0]
        if dashboard.get("activity") in ACTIVITIES:
            self.activity = dashboard["activity"]
        self.last_dashboard = time.ticks_ms()
        self.dashboard_old = False
        self.ambient_enabled = dashboard.get("ambient", True) is True

    def dashboard_fresh(self, now):
        return self.last_dashboard is not None and not self.dashboard_old and 0 <= time.ticks_diff(now, self.last_dashboard) < STALE_AFTER_MS

    def update_ambient(self, now):
        elapsed = time.ticks_diff(now, self.last_ambient)
        if not self.ambient_enabled or 0 <= elapsed < 100:
            return
        self.last_ambient = now
        fresh = self.dashboard_fresh(now)
        for strip in (0, 1):
            if self.rgb_transitions[strip] is not None:
                continue
            # Smooth 4s breathing; alerts use a distinct slow 2s alternating
            # pulse. Scale the host's brightness-limited colors, never boost.
            if not fresh or self.activity == "Quiet":
                factor = 1
            else:
                period = 2000 if self.view == "alert" else 6000 if self.activity == "Meeting active" else 4000
                phase = (now % period) / period * 2 * math.pi
                if self.view == "alert" and strip == 1:
                    phase += math.pi
                elif self.activity == "AI working" and strip == 1:
                    phase += math.pi / 2
                factor = .25 + .75 * (1 - math.cos(phase)) / 2
            target = self.rgb_targets[strip]
            color = (int(((target >> 16) & 255) * factor) << 16 |
                     int(((target >> 8) & 255) * factor) << 8 | int((target & 255) * factor))
            if color != self.rgb_last[strip]:
                self.stackchan.set_rgb_color(strip, color)
                self.rgb_last[strip] = color

    def draw_metric(self, key):
        x, color = {"cpu": (12, ACCENT), "ram": (114, MINT), "gpu": (216, LAVENDER)}[key]
        value = self.metrics[key]
        M5.Lcd.fillRoundRect(x + 10, 179, 72, 6, 3, EDGE)
        if value is not None and value > 0:
            width = clamp(int(72 * value / 100 + 0.5), 1, 72)
            M5.Lcd.fillRoundRect(x + 10, 179, width, 6, min(3, width // 2), color)
        # A fixed 0..100 vertical scale and received-sample spacing; unknown
        # samples break a line rather than implying zero or extrapolated data.
        M5.Lcd.fillRect(x + 10, 190, 72, 20, PANEL)
        M5.Lcd.drawLine(x + 10, 208, x + 81, 208, EDGE)
        history = self.histories[key]
        start = x + 10 + (HISTORY_SAMPLES - len(history)) * 4
        previous = None
        for index, sample in enumerate(history):
            if sample is None:
                previous = None
                continue
            point = (start + index * 4, 207 - int(sample * 16 / 100 + 0.5))
            if previous is not None:
                M5.Lcd.drawLine(previous[0], previous[1], point[0], point[1], color)
            # Keep isolated real samples visible on either side of a gap.
            M5.Lcd.fillCircle(point[0], point[1], 1, color)
            previous = point

    def set_transport(self, transport):
        if self.transport != transport:
            self.transport = transport
            self.transport_label.setText(transport)
        if self.last_dashboard is None and self.view in ("overview", "focus"):
            self.hint_label.setText("Waiting for PC stats")
        elif self.last_dashboard is None and self.view == "companion":
            self.companion_label.setText("Waiting for PC stats")

    def update_age(self, now):
        if self.last_dashboard is None:
            text = "Updated --"
        else:
            seconds = 3600 if self.dashboard_old else max(0, time.ticks_diff(now, self.last_dashboard) // 1000)
            if seconds < 1:
                text = "Updated now"
            elif seconds < 60:
                text = "Updated " + str(seconds) + "s ago"
            elif seconds < 3600:
                text = "Updated " + str(seconds // 60) + "m ago"
            else:
                # Preserve the one-hour cap across the MicroPython tick wrap
                # after very long periods without a new dashboard record.
                self.dashboard_old = True
                text = "Updated 1h+ ago"
        self.age_label.setText(text)

    def update_screen(self, now):
        # A paused clock can outlive MicroPython's half-period. Rebase it
        # instead of treating its negative difference as a multi-day delay.
        for name in ("page_started", "last_animation", "animation_started", "last_blink",
                     "last_progress", "last_screen_update"):
            if time.ticks_diff(now, getattr(self, name)) < 0:
                setattr(self, name, now)
        if time.ticks_diff(now, self.last_screen_update) >= 1000:
            self.last_screen_update = now
            self.update_age(now)
        if self.scene is not None:
            # A presence card holds the body; rotation and face frames would paint over it.
            elapsed = time.ticks_diff(now, self.scene["started"])
            if elapsed < 0:
                self.scene["started"] = now
            elif elapsed >= self.scene["duration"]:
                self.clear_scene(now)
            return
        fresh = self.dashboard_fresh(now)
        cycling = fresh and self.activity != "Quiet" and self.view != "alert"
        dwell = PAGE_INTERVAL_MS * (2 if self.view == "companion" else 1)
        elapsed = max(0, time.ticks_diff(now, self.page_started))
        if cycling and elapsed >= dwell:
            next_view = {"overview": "focus", "focus": "companion", "companion": "overview"}[self.view]
            if self.view == "focus":
                self.focus_index = (self.focus_index + 1) % 3
            self.set_view(next_view, now)
            elapsed = 0
            dwell = PAGE_INTERVAL_MS * (2 if self.view == "companion" else 1)
        if time.ticks_diff(now, self.last_progress) >= 250:
            self.last_progress = now
            progress = min(296, elapsed * 296 // dwell) if cycling else 0
            if progress != self.page_progress:
                self.page_progress = progress
                M5.Lcd.fillRect(12, 34, 296, 2, EDGE if cycling else BACKGROUND)
                if progress:
                    M5.Lcd.fillRect(12, 34, progress, 2, ACCENT)
            if self.view == "alert":
                # One gentle 1.6s border emphasis on entry, then a steady
                # warning. Quiet/stale states retain the steady readable card.
                glow = max(0, math.sin(elapsed * math.pi / 1600)) if fresh and self.activity != "Quiet" and elapsed < 1600 else 0
                color = 0xFF0000 | (112 + int(59 * glow)) << 8 | (67 + int(78 * glow))
                if color != self.alert_border:
                    self.alert_border = color
                    M5.Lcd.drawRoundRect(12, 42, 296, 173, 8, color)
        animated = self.activity != "Quiet" and self.view != "alert" and (fresh or self.last_dashboard is None)
        if not animated:
            changed = (self.gaze != 0 or self.gaze_y != 0 or self.bob != 0 or self.eye_open != 100 or
                       self.expression_phase != 0)
            self.gaze, self.gaze_y, self.bob, self.eye_open, self.expression_phase = 0, 0, 0, 100, 0
            self.blinking = False
            self.last_blink = now
            if changed:
                self.draw_face()
            self.animation_key = (0, 0, 0, 100, 0)
            return
        if time.ticks_diff(now, self.last_animation) < FRAME_INTERVAL_MS:
            return
        self.last_animation = now
        age = time.ticks_diff(now, self.animation_started)
        if age < 0:
            self.animation_started = now
            age = 0
        if self.gaze_target is not None:
            # Look at the person MicaStats sees; ease so camera steps never jump.
            target_x, target_y = self.gaze_target[0] * 6 / 100, self.gaze_target[1] * 4 / 100
            self.gaze = int((self.gaze + (target_x - self.gaze) * 0.5) * 10) / 10
            self.gaze_y = int((self.gaze_y + (target_y - self.gaze_y) * 0.5) * 10) / 10
        else:
            self.gaze_y = 0
            # Eased glances have explicit pauses; avoid a repetitive pendulum.
            position = (age * (2 if self.activity == "PC busy" else 1)) % 14000
            stops = ((0, 0), (1200, 0), (2400, -4), (4600, -4), (5800, 0),
                     (7800, 0), (9000, 4), (11200, 4), (12400, 0), (14000, 0))
            for index in range(1, len(stops)):
                if position <= stops[index][0]:
                    t0, start = stops[index - 1]
                    t1, end = stops[index]
                    fraction = (position - t0) / (t1 - t0)
                    ease = fraction * fraction * (3 - 2 * fraction)
                    self.gaze = int((start + (end - start) * ease) * 10) / 10
                    break
        self.bob = int(20 * math.sin(age * 2 * math.pi / 4000)) / 10
        self.expression_phase = (age // 400) % 8 if self.view == "companion" else 0
        if self.blinking:
            blink_age = time.ticks_diff(now, self.blink_started)
            if blink_age >= 320:
                self.blinking = False
                self.eye_open = 100
            elif blink_age < 80:
                self.eye_open = 100 - blink_age * 100 // 80
            elif blink_age < 160:
                self.eye_open = 0
            else:
                self.eye_open = (blink_age - 160) * 100 // 160
        elif time.ticks_diff(now, self.last_blink) >= 6500:
            self.last_blink = now
            self.blink_started = now
            self.blinking = True
        key = (self.gaze, self.gaze_y, self.bob, self.eye_open, self.expression_phase)
        if key != self.animation_key:
            self.animation_key = key
            self.draw_face()

    def draw_camera_dot(self):
        # Always visible while the camera runs, so it is never on unnoticed.
        M5.Lcd.fillCircle(251, 18, 3, CAMERA_DOT if self.camera_on else BACKGROUND)

    def apply_camera(self, config):
        if not isinstance(config, dict) or not isinstance(config.get("on"), bool):
            return "invalid-camera"
        if not config["on"]:
            self.stop_camera()
            return "applied"
        fps, quality = read_int(config.get("fps")), read_int(config.get("quality"))
        if fps is None or quality is None or not 1 <= fps <= MAX_FRAME_FPS or not 30 <= quality <= 80:
            return "invalid-camera"
        if not self.camera_on:
            try:
                import camera
                camera.init(pixformat=camera.RGB565, framesize=getattr(camera, CAMERA_FRAMESIZE))
            except Exception:
                self.camera_failure_pending = True
                return "applied"
            self.camera = camera
            self.camera_on = True
            self.camera_frame_due = True
            self.draw_camera_dot()
        self.camera_fps, self.camera_quality = fps, quality
        self.camera_refreshed = time.ticks_ms()
        return "applied"

    def stop_camera(self):
        if not self.camera_on:
            return
        self.camera_on = False
        try:
            self.camera.deinit()
        except Exception:
            pass
        self.draw_camera_dot()
        # Presence is over: the eyes return to their own glances and no card lingers.
        self.gaze_target = None
        self.clear_scene(time.ticks_ms())

    def expire_camera(self, now):
        # Runs every loop, even while USB input keeps arriving, so a silent PC
        # can never leave the camera on.
        if self.camera_on and time.ticks_diff(now, self.camera_refreshed) >= CAMERA_KEEPALIVE_MS:
            self.stop_camera()

    def update_camera(self, now):
        if not self.camera_on:
            return
        elapsed = time.ticks_diff(now, self.camera_last_frame)
        if not self.camera_frame_due and 0 <= elapsed < 1000 // self.camera_fps:
            return
        self.camera_frame_due = False
        self.camera_last_frame = now
        try:
            import ubinascii
            jpeg = self.camera.capture_to_jpg(self.camera_quality)
            # One ASCII line per frame; base64 never contains the LF terminator.
            sys.stdout.write("F" + str(self.camera_seq) + ":" + ubinascii.b2a_base64(jpeg).decode())
            try:
                sys.stdout.flush()
            except Exception:
                pass
        except Exception:
            self.camera_failure_pending = True
            self.stop_camera()
            return
        self.camera_seq = (self.camera_seq + 1) % 65536

    def apply_presence(self, config):
        if not isinstance(config, dict):
            return "invalid-presence"
        gaze = config.get("gaze", False)
        if gaze is not False and gaze is not None:
            if not isinstance(gaze, dict):
                return "invalid-presence"
            x, y = read_int(gaze.get("x")), read_int(gaze.get("y"))
            if x is None or y is None or not -100 <= x <= 100 or not -100 <= y <= 100:
                return "invalid-presence"
            gaze = (x, y)
        scene = config.get("scene")
        if scene is not None:
            lines = config.get("lines", [])
            seconds = read_int(config.get("seconds", 1))
            if scene not in SCENE_COLORS and scene != "none":
                return "invalid-presence"
            if not isinstance(lines, list) or len(lines) > 3 or seconds is None or not 1 <= seconds <= 60:
                return "invalid-presence"
            for line in lines:
                if not isinstance(line, str) or len(line) > 32 or any(not 32 <= ord(c) < 127 for c in line):
                    return "invalid-presence"
        # Validated; apply both parts together.
        if gaze is not False:
            self.gaze_target = gaze
        if scene == "none":
            self.clear_scene(time.ticks_ms())
        elif scene is not None and self.view != "alert" and (
                self.scene is None or SCENE_RANK[scene] >= SCENE_RANK[self.scene["kind"]]):
            # Alerts outrank every card; a lower card never covers a higher one.
            self.show_scene(scene, lines, seconds, time.ticks_ms())
        return "applied"

    def show_scene(self, kind, lines, seconds, now):
        self.scene = {"kind": kind, "started": now, "duration": seconds * 1000}
        color = SCENE_COLORS[kind]
        M5.Lcd.fillRect(0, 40, 320, 178, BACKGROUND)
        M5.Lcd.fillRoundRect(12, 42, 296, 173, 8, PANEL)
        M5.Lcd.drawRoundRect(12, 42, 296, 173, 8, color)
        ScreenText(SCENE_TITLES[kind], 24, 56, 272, 29, Widgets.FONTS.Montserrat24, color, PANEL, centered=True)
        for index, line in enumerate(lines):
            ScreenText(line, 24, 100 + index * 30, 272, 23, Widgets.FONTS.Montserrat18,
                       FOREGROUND, PANEL, centered=True)

    def clear_scene(self, now):
        if self.scene is None:
            return
        self.scene = None
        view, self.view = self.view, None
        self.set_view(view, now)

    def acknowledge(self, kind):
        try:
            self.ble.write(("{\"ok\":true,\"type\":\"" + kind + "\"}\n").encode())
        except Exception:
            # A disconnected peer is normal; it must not stop the dashboard.
            pass

    @staticmethod
    def valid_request_id(value):
        return isinstance(value, str) and 0 < len(value) <= 64

    @staticmethod
    def write_usb_response(kind, request_id, **fields):
        response = {"ok": True, "type": kind}
        response.update(fields)
        response["requestId"] = request_id
        try:
            # Use only the baseline MicroPython json.dumps surface.  It still
            # escapes request IDs safely, without relying on optional kwargs.
            # The leading newline ends any picture line a closing port cut off, so the
            # reply always arrives on a line of its own.
            sys.stdout.write("\n" + json.dumps(response) + "\n")
            try:
                sys.stdout.flush()
            except Exception:
                pass
        except Exception:
            # A closed serial monitor must not stop the on-device dashboard.
            pass

    def apply_message(self, payload):
        """Apply one already-decoded factory-style command and return its ACK."""
        # Factory avatar / motion / RGB field names are intentionally retained.
        if "camera" in payload:
            result = self.apply_camera(payload["camera"])
            if result != "applied":
                return result
        if "presence" in payload:
            result = self.apply_presence(payload["presence"])
            if result != "applied":
                return result
        avatar = payload.get("avatar", payload)
        motion = payload.get("motion", payload)
        rgb = payload.get("rgb", payload)
        motion_results = []
        if isinstance(motion, dict):
            motion_results = self.apply_motion(motion)
        if any(result not in ("applied", "ignored") for result in motion_results):
            return "rejected-motion"
        if isinstance(avatar, dict):
            self.apply_avatar(avatar)
        if isinstance(rgb, dict):
            self.apply_rgb(rgb)
        self.apply_dashboard(payload.get("companion"))
        return "applied"

    def handle_message(self, raw_line):
        try:
            payload = json.loads(raw_line.decode())
        except (ValueError, UnicodeError):
            self.acknowledge("invalid-json")
            return
        if not isinstance(payload, dict):
            self.acknowledge("invalid-message")
            return

        # BLE has no pairing secret, so only the USB link may drive the camera.
        payload.pop("camera", None)
        payload.pop("presence", None)
        kind = self.apply_message(payload)
        if kind == "applied":
            self.set_transport("BLE")
        self.acknowledge(kind)

    def handle_usb_message(self, raw_line):
        try:
            payload = json.loads(raw_line.decode())
        except (ValueError, UnicodeError):
            return
        if not isinstance(payload, dict):
            return

        request_id = payload.get("requestId")
        if not self.valid_request_id(request_id):
            return

        hello = payload.get("companionHello")
        if isinstance(hello, int) and not isinstance(hello, bool) and hello == 1:
            self.set_transport("USB")
            self.write_usb_response(
                "hello",
                request_id,
                device=DEVICE_NAME,
                protocol=1,
                transport="usb",
                motionCalibrated=self.motion_calibrated,
                calibration=self.calibration,
            )
            return

        command = payload.get("command")
        if not isinstance(command, dict):
            self.write_usb_response("invalid-message", request_id)
            return
        kind = self.apply_message(command)
        if kind == "applied":
            self.set_transport("USB")
        fields = {}
        if self.camera_failure_pending:
            self.camera_failure_pending = False
            fields["camera"] = "failed"
        self.write_usb_response(kind, request_id, **fields)

    def consume_ble(self):
        if self.ble is None or self.ble.any() <= 0:
            return
        for byte in self.ble.read():
            if self.discarding_overlong_line:
                if byte == 10:
                    self.discarding_overlong_line = False
                continue
            if byte == 10:
                line = bytes(self.input_buffer).strip()
                self.input_buffer = bytearray()
                if line:
                    self.handle_message(line)
            elif len(self.input_buffer) >= MAX_LINE_BYTES:
                # Ignore every remaining byte through the terminating newline;
                # never reinterpret an overlong tail as a new command.
                self.input_buffer = bytearray()
                self.discarding_overlong_line = True
                self.acknowledge("line-too-long")
            else:
                self.input_buffer.append(byte)

    def consume_usb(self):
        """Read a bounded burst; returns True when any input was waiting."""
        if self.usb_poll is None:
            return False
        waiting = False
        # Drain a bounded burst so a maximum-size command completes well
        # inside the desktop timeout, while still returning to the UI loop.
        for _ in range(USB_READ_BURST):
            if not self.usb_poll.poll(0):
                break
            incoming = sys.stdin.read(1)
            if not incoming:
                break
            waiting = True
            if isinstance(incoming, str):
                incoming = incoming.encode("utf-8")

            for byte in incoming:
                if self.usb_discarding_overlong_line:
                    if byte == 10:
                        self.usb_discarding_overlong_line = False
                    continue
                if byte == 10:
                    line = bytes(self.usb_input_buffer).strip()
                    self.usb_input_buffer = bytearray()
                    if line:
                        self.handle_usb_message(line)
                elif len(self.usb_input_buffer) >= MAX_LINE_BYTES:
                    self.usb_input_buffer = bytearray()
                    self.usb_discarding_overlong_line = True
                else:
                    self.usb_input_buffer.append(byte)
        return waiting

    def update_local_status(self):
        try:
            voltage = read_float(self.stackchan.get_battery_voltage())
        except Exception:
            voltage = None
        battery = "--" if voltage is None or voltage <= 0 else "{:.2f}V".format(voltage)
        self.local_label.setText("BAT " + battery)

    def loop(self):
        M5.update()
        self.consume_ble()
        commands_waiting = self.consume_usb()
        self.update_rgb_transitions()
        now = time.ticks_ms()
        self.expire_camera(now)
        # Commands always win over a capture, so frames never add ACK latency.
        if not commands_waiting:
            self.update_camera(now)
        self.update_screen(now)
        self.update_ambient(now)
        local_elapsed = time.ticks_diff(now, self.last_local_update)
        if local_elapsed < 0 or local_elapsed >= 2000:
            self.last_local_update = now
            self.update_local_status()


def main():
    app = Companion()
    app.setup()
    while True:
        app.loop()
        time.sleep_ms(20)


if __name__ == "__main__":
    main()
