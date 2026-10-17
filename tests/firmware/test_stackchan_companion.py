"""Host-side contract tests for the optional UIFlow 2 StackChan program.

The real ``M5``, StackChan, and BLE modules only exist on the device.  These
tests replace them with small recording fakes so the safety boundary and
stream protocol can be checked without accessing hardware.
"""

from __future__ import annotations

import binascii
import importlib.util
import json
import math
from pathlib import Path
import sys
import types
import unittest


class FakeLcd:
    def __init__(self):
        self.fills = []
        self.lines = []
        self.rounds = []
        self.circles = []
        self.texts = []
        self.blits = []
        self.physical_fills = []
        self.physical_lines = []

    def width(self):
        return 320

    def height(self):
        return 240

    def textWidth(self, text, font):
        return int(len(text) * font * 0.55)

    def fontHeight(self, font):
        return font

    def setTextSize(self, size):
        pass

    def setTextColor(self, foreground, background):
        self.text_color = (foreground, background)

    def drawString(self, text, x, y, font):
        self.texts.append((text, x, y, font, self.text_color))

    def fillRect(self, x, y, width, height, color):
        self.fills.append((x, y, width, height, color))
        self.physical_fills.append((x, y, width, height, color))

    def drawLine(self, x1, y1, x2, y2, color):
        self.lines.append((x1, y1, x2, y2, color))
        self.physical_lines.append((x1, y1, x2, y2, color))

    def drawRawBuf(self, buf, x, y, width, height, pixels, swap=False):
        self.blits.append((x, y, width, height, pixels, swap, bytes(buf)))
        # Expose buffered geometry to legacy clipping regressions, separately
        # from actual LCD operations so an erase/draw flicker cannot hide.
        frame = FakeFrameBuffer.instances[id(buf)]
        for op in frame.operations:
            if op[0] == "fill":
                self.fills.append((x, y, width, height, from_rgb565(op[1])))
            else:
                _, x1, y1, x2, y2, color = op
                self.lines.append((x + x1, y + y1, x + x2, y + y2, from_rgb565(color)))

    def fillRoundRect(self, x, y, width, height, radius, color):
        self.rounds.append(("fill", x, y, width, height, radius, color))

    def drawRoundRect(self, x, y, width, height, radius, color):
        self.rounds.append(("border", x, y, width, height, radius, color))

    def drawRect(self, x, y, width, height, color):
        self.rounds.append(("border", x, y, width, height, 0, color))

    def fillCircle(self, x, y, radius, color):
        self.circles.append((x, y, radius, color))


class FakeStackChan:
    SERVO_ID_X = 1
    SERVO_ID_Y = 2

    def __init__(self, i2c=1, uart=1):
        self.i2c = i2c
        self.uart = uart
        self.calls = []
        self.rgb = {0: (0, 0, 0), 1: (0, 0, 0)}

    def set_servo_power(self, enabled):
        self.calls.append(("power", enabled))

    def set_servo_torque(self, servo_id, enabled):
        self.calls.append(("torque", servo_id, enabled))

    def set_servo_angle(self, servo_id, degrees, time_ms, speed):
        self.calls.append(("angle", servo_id, degrees, time_ms, speed))

    def set_rgb_color(self, strip, color):
        self.calls.append(("rgb", strip, color))
        self.rgb[strip] = ((color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF)

    def get_rgb_color(self, strip, led):
        return self.rgb[strip]

    def get_battery_voltage(self):
        return 4.12

    def get_touch(self):
        return 0


def from_rgb565(color):
    return ((color >> 11) * 255 // 31) << 16 | (((color >> 5) & 63) * 255 // 63) << 8 | (color & 31) * 255 // 31


class FakeFrameBuffer:
    """Small native-endian RGB565 fake with real pixels and clipped lines."""

    instances = {}

    def __init__(self, buf, width, height, mode):
        self.buf, self.width, self.height = buf, width, height
        self.operations = []
        self.instances[id(buf)] = self

    def fill(self, color):
        self.buf[:] = color.to_bytes(2, "little") * (self.width * self.height)
        self.operations = [("fill", color)]

    def line(self, x1, y1, x2, y2, color):
        self.operations.append(("line", x1, y1, x2, y2, color))
        dx, dy = abs(x2 - x1), -abs(y2 - y1)
        sx, sy = 1 if x1 < x2 else -1, 1 if y1 < y2 else -1
        error = dx + dy
        while True:
            if 0 <= x1 < self.width and 0 <= y1 < self.height:
                index = (y1 * self.width + x1) * 2
                self.buf[index:index + 2] = color.to_bytes(2, "little")
            if x1 == x2 and y1 == y2:
                break
            twice = error * 2
            if twice >= dy:
                error += dy
                x1 += sx
            if twice <= dx:
                error += dx
                y1 += sy

    def hline(self, x, y, width, color):
        self.line(x, y, x + width - 1, y, color)

    def ellipse(self, x, y, xr, yr, color, fill=False, mask=15):
        if not fill:
            for flag, sx, sy in ((1, 1, -1), (2, -1, -1), (4, -1, 1), (8, 1, 1)):
                if not mask & flag:
                    continue
                previous = None
                steps = max(8, xr * 2, yr * 2)
                for step in range(steps + 1):
                    angle = step * math.pi / (steps * 2)
                    point = (x + int(sx * xr * math.cos(angle)), y + int(sy * yr * math.sin(angle)))
                    if previous is not None:
                        self.line(previous[0], previous[1], point[0], point[1], color)
                    previous = point
            return
        for row in range(-yr, yr + 1):
            half = int(xr * math.sqrt(max(0, 1 - (row / max(1, yr)) ** 2)))
            left_mask, right_mask = (2, 1) if row < 0 else (4, 8)
            if mask & left_mask:
                self.hline(max(0, x - half), y + row, max(0, min(self.width - 1, x) - max(0, x - half) + 1), color)
            if mask & right_mask:
                self.hline(max(0, x), y + row, max(0, min(self.width - 1, x + half) - max(0, x) + 1), color)


class FakeBle:
    def __init__(self, name="", rxbuf=0):
        self.name = name
        self.rxbuf = rxbuf
        self.incoming = bytearray()
        self.writes = []

    def feed(self, data):
        self.incoming.extend(data)

    def any(self):
        return len(self.incoming)

    def read(self, size=None):
        size = len(self.incoming) if size is None else size
        result = bytes(self.incoming[:size])
        del self.incoming[:size]
        return result

    def write(self, data):
        self.writes.append(bytes(data))


class FakeTextInput:
    def __init__(self, text=""):
        self.text = text

    def feed(self, text):
        self.text += text

    def read(self, size=1):
        result = self.text[:size]
        self.text = self.text[size:]
        return result


class FakeTextOutput:
    def __init__(self):
        self.text = ""
        self.flushes = 0

    def write(self, text):
        self.text += text
        return len(text)

    def flush(self):
        self.flushes += 1

    def records(self):
        return [line for line in self.text.splitlines() if line]


class FakePoll:
    def __init__(self):
        self.registered = []
        self.timeouts = []

    def register(self, stream, event):
        self.registered.append((stream, event))

    def poll(self, timeout):
        self.timeouts.append(timeout)
        return [(0, 1)] if getattr(sys.stdin, "text", "") else []


class FirmwareTestCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lcd = FakeLcd()
        widgets = types.SimpleNamespace(
            fillScreen=lambda color: None,
            FONTS=types.SimpleNamespace(Montserrat12=12, Montserrat18=18, Montserrat24=24),
        )
        m5 = types.ModuleType("M5")
        m5.Widgets = widgets
        m5.Lcd = cls.lcd
        m5.begin = lambda: None
        m5.update = lambda: None

        hardware = types.ModuleType("hardware")
        stackchan = types.ModuleType("hardware.stackchan")
        stackchan.StackChan = FakeStackChan
        bleuart = types.ModuleType("bleuart")
        bleuart.BLEUARTServer = FakeBle
        fake_select = types.ModuleType("select")
        fake_select.POLLIN = 1
        fake_select.poll = FakePoll
        fake_framebuf = types.ModuleType("framebuf")
        fake_framebuf.RGB565 = 1
        fake_framebuf.FrameBuffer = FakeFrameBuffer

        fake_camera = types.ModuleType("camera")
        fake_camera.RGB565 = 2
        fake_camera.QVGA = 5
        fake_camera.calls = []
        fake_camera.jpeg = b"\xff\xd8MicaStats\xff\xd9"
        fake_camera.init = lambda pixformat, framesize: fake_camera.calls.append(("init", pixformat, framesize))
        fake_camera.deinit = lambda: fake_camera.calls.append(("deinit",))

        def capture_to_jpg(quality):
            fake_camera.calls.append(("capture", quality))
            return fake_camera.jpeg

        fake_camera.capture_to_jpg = capture_to_jpg
        fake_ubinascii = types.ModuleType("ubinascii")
        fake_ubinascii.b2a_base64 = binascii.b2a_base64

        fake_time = types.ModuleType("time")
        fake_time.now = 1000
        fake_time.ticks_ms = lambda: fake_time.now
        fake_time.ticks_diff = lambda later, earlier: later - earlier
        fake_time.sleep_ms = lambda milliseconds: None

        cls.replaced_modules = {
            name: sys.modules.get(name)
            for name in ("M5", "hardware", "hardware.stackchan", "bleuart", "select", "time", "framebuf",
                         "camera", "ubinascii")
        }
        sys.modules.update(
            {
                "camera": fake_camera,
                "ubinascii": fake_ubinascii,
                "M5": m5,
                "hardware": hardware,
                "hardware.stackchan": stackchan,
                "bleuart": bleuart,
                "select": fake_select,
                "time": fake_time,
                "framebuf": fake_framebuf,
            }
        )

        source = Path(__file__).parents[2] / "firmware" / "uiflow2" / "micastats_companion.py"
        spec = importlib.util.spec_from_file_location("micastats_companion_under_test", source)
        cls.firmware = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.firmware)
        cls.fake_time = fake_time
        cls.fake_camera = fake_camera

    @classmethod
    def tearDownClass(cls):
        for name, previous in cls.replaced_modules.items():
            if previous is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = previous

    def setUp(self):
        self.lcd.fills.clear()
        self.lcd.lines.clear()
        self.lcd.rounds.clear()
        self.lcd.circles.clear()
        self.lcd.texts.clear()
        self.lcd.blits.clear()
        self.lcd.physical_fills.clear()
        self.lcd.physical_lines.clear()
        self.fake_time.now = 1000
        self.app = self.firmware.Companion()
        self.app.stackchan = FakeStackChan()
        self.app.motion_calibrated = True  # A calibrated fixture; separate tests cover missing keys.
        self.app.calibration = {"x": "saved", "y": "saved"}
        self.app.ble = FakeBle()
        self.app.setup_display()
        # Legacy dashboard/stream fixtures explicitly exercise the overview.
        # Separate living-display fixtures verify the character-first startup.
        self.app.set_view("overview", self.fake_time.now)
        self.original_stdin = sys.stdin
        self.original_stdout = sys.stdout

    def tearDown(self):
        sys.stdin = self.original_stdin
        sys.stdout = self.original_stdout

    def feed_in_portable_chunks(self, data):
        for offset in range(0, len(data), 20):
            self.app.ble.feed(data[offset : offset + 20])
            self.app.consume_ble()

    def attach_usb(self, text=""):
        fake_input = FakeTextInput(text)
        fake_output = FakeTextOutput()
        sys.stdin = fake_input
        sys.stdout = fake_output
        self.app.usb_poll = FakePoll()
        return fake_input, fake_output

    def consume_all_usb(self, fake_input):
        while fake_input.text:
            self.app.consume_usb()


class PresenceCameraTests(FirmwareTestCase):
    def setUp(self):
        super().setUp()
        self.fake_camera.calls.clear()
        self.fake_input, self.output = self.attach_usb()

    def send(self, command, request_id="req"):
        self.fake_input.feed(json.dumps({"requestId": request_id, "command": command}) + "\n")
        self.consume_all_usb(self.fake_input)
        return [json.loads(line) for line in self.output.records() if line.startswith("{")][-1]

    def frames(self):
        return [line for line in self.output.records() if line.startswith("F")]

    def run_loops(self, until, step=50):
        while self.fake_time.now < until:
            self.app.loop()
            self.fake_time.now += step

    def test_camera_on_streams_frames_at_requested_rate(self):
        self.assertEqual("applied", self.send({"camera": {"on": True, "fps": 4, "quality": 50}})["type"])
        self.run_loops(2000)
        frames = self.frames()
        self.assertEqual(["F0", "F1", "F2", "F3"], [line.split(":")[0] for line in frames])
        self.assertEqual(self.fake_camera.jpeg, binascii.a2b_base64(frames[0].split(":", 1)[1]))
        self.assertIn(("capture", 50), self.fake_camera.calls)

    def test_camera_turns_off_without_keepalive(self):
        self.send({"camera": {"on": True, "fps": 2, "quality": 50}})
        self.run_loops(1000 + 10000 + 50)
        count = len(self.frames())
        self.assertIn(("deinit",), self.fake_camera.calls)
        self.run_loops(1000 + 13000)
        self.assertEqual(count, len(self.frames()))

    def test_usb_replies_start_on_a_fresh_line(self):
        # A picture line cut off by a closing port must not swallow the next reply.
        self.send({"camera": {"on": False}}, request_id="fresh")
        self.assertTrue(self.output.text.startswith("\n"))

    def test_camera_keepalive_expires_even_while_input_keeps_arriving(self):
        self.send({"camera": {"on": True, "fps": 2, "quality": 50}})
        while self.fake_time.now < 1000 + 10050:
            self.fake_input.feed("x")  # A partial line every loop: input is never idle.
            self.app.loop()
            self.fake_time.now += 50
        self.assertIn(("deinit",), self.fake_camera.calls)

    def test_scene_does_not_cover_an_alert_or_a_higher_scene(self):
        self.send({"presence": {"scene": "guard", "lines": [], "seconds": 30}})
        self.send({"presence": {"scene": "welcome", "lines": ["Hi"], "seconds": 10}})
        self.assertEqual("guard", self.app.scene["kind"])
        self.send({"presence": {"scene": "none", "lines": [], "seconds": 1}})
        self.send({"companion": {"cpu": 99, "ram": 10, "gpu": 5, "activity": "Alert",
                                 "alert": {"kind": "cpu-usage", "value": 99, "threshold": 90, "above": True}}})
        self.assertEqual("applied", self.send(
            {"presence": {"scene": "break", "lines": ["Stretch"], "seconds": 10}})["type"])
        self.assertIsNone(self.app.scene)
        self.assertEqual("alert", self.app.view)

    def test_camera_off_releases_gaze_and_scene(self):
        self.send({"camera": {"on": True, "fps": 2, "quality": 50},
                   "presence": {"gaze": {"x": 40, "y": 0}, "scene": "welcome", "lines": ["Hi"], "seconds": 30}})
        self.send({"camera": {"on": False}})
        self.assertIsNone(self.app.gaze_target)
        self.assertIsNone(self.app.scene)

    def test_camera_rejects_out_of_range_fps_and_quality(self):
        self.assertEqual("invalid-camera", self.send({"camera": {"on": True, "fps": 7, "quality": 50}})["type"])
        self.assertEqual("invalid-camera", self.send({"camera": {"on": True, "fps": 4, "quality": 90}})["type"])
        self.assertNotIn("init", [call[0] for call in self.fake_camera.calls])

    def test_usb_commands_are_read_before_capture(self):
        self.send({"camera": {"on": True, "fps": 6, "quality": 50}})
        self.run_loops(1100)
        captures = self.fake_camera.calls.count(("capture", 50))
        self.fake_input.feed(json.dumps({"requestId": "late", "command": {"camera": {"on": True, "fps": 6, "quality": 50}}}) + "\n")
        self.fake_time.now += 1000
        self.app.loop()
        self.assertEqual("late", json.loads([r for r in self.output.records() if r.startswith("{")][-1])["requestId"])
        self.assertEqual(captures, self.fake_camera.calls.count(("capture", 50)))

    def test_camera_indicator_drawn_while_on(self):
        self.send({"camera": {"on": True, "fps": 2, "quality": 50}})
        self.assertIn((251, 18, 3, 0xF43F5E), self.lcd.circles)
        self.send({"camera": {"on": False}})
        self.assertEqual((251, 18, 3), self.lcd.circles[-1][:3])
        self.assertNotEqual(0xF43F5E, self.lcd.circles[-1][3])

    def test_gaze_target_moves_eyes_both_axes(self):
        self.app.set_view("companion", self.fake_time.now)
        self.send({"presence": {"gaze": {"x": 50, "y": -40}}})
        self.run_loops(self.fake_time.now + 2000)
        self.assertGreater(self.app.gaze, 0)
        self.assertLess(self.app.gaze_y, 0)
        self.send({"presence": {"gaze": None}})
        self.assertIsNone(self.app.gaze_target)

    def test_scene_lines_are_ascii_and_bounded(self):
        self.assertEqual("invalid-presence", self.send(
            {"presence": {"scene": "welcome", "lines": ["สวัสดี"], "seconds": 5}})["type"])
        self.assertEqual("invalid-presence", self.send(
            {"presence": {"scene": "welcome", "lines": ["a", "b", "c", "d"], "seconds": 5}})["type"])
        self.assertEqual("invalid-presence", self.send(
            {"presence": {"scene": "party", "lines": [], "seconds": 5}})["type"])
        self.assertEqual("applied", self.send(
            {"presence": {"scene": "welcome", "lines": ["Welcome back", "2 alerts"], "seconds": 5}})["type"])
        self.assertIn("Welcome back", [text[0] for text in self.lcd.texts])

    def test_dashboard_and_face_do_not_paint_over_scene(self):
        self.send({"presence": {"scene": "welcome", "lines": ["Hi"], "seconds": 10}})
        self.lcd.texts.clear()
        self.lcd.blits.clear()
        self.send({"companion": {"cpu": 42, "ram": 10, "gpu": 5, "activity": "Ready"},
                   "leftEye": {"x": 5, "y": 0, "rotation": 0, "weight": 0, "size": 0}})
        self.assertEqual([], self.lcd.texts)
        self.assertEqual([], self.lcd.blits)
        self.assertEqual(42, self.app.metrics["cpu"])

    def test_alert_replaces_scene(self):
        self.send({"presence": {"scene": "break", "lines": ["Stretch"], "seconds": 30}})
        self.send({"companion": {"cpu": 99, "ram": 10, "gpu": 5, "activity": "Alert",
                                 "alert": {"kind": "cpu-usage", "value": 99, "threshold": 90, "above": True}}})
        self.assertIsNone(self.app.scene)
        self.assertEqual("alert", self.app.view)

    def test_scene_expires_after_seconds(self):
        self.send({"presence": {"scene": "break", "lines": ["Time to stretch"], "seconds": 2}})
        self.assertIsNotNone(self.app.scene)
        self.run_loops(self.fake_time.now + 2100)
        self.assertIsNone(self.app.scene)


class ActivityContractTests(FirmwareTestCase):
    def test_accepts_every_activity_value_emitted_by_micastats(self):
        activities = ("Ready", "Quiet", "Alert", "AI working", "Meeting active", "PC busy")

        for activity in activities:
            with self.subTest(activity=activity):
                self.app.apply_dashboard({"activity": activity})
                self.assertEqual(activity, self.app.activity_label.text)

    def test_ignores_activity_text_outside_the_public_allowlist(self):
        self.app.activity_label.setText("Ready")

        for private_text in ("prompt contents", "Meeting with Alice", "CPU alert: secret.exe"):
            with self.subTest(private_text=private_text):
                self.app.apply_dashboard({"activity": private_text})
                self.assertEqual("Ready", self.app.activity_label.text)


class MotionSafetyTests(FirmwareTestCase):
    def test_applies_yaw_and_pitch_at_each_safe_boundary(self):
        cases = (
            ("yawServo", -450, FakeStackChan.SERVO_ID_X),
            ("yawServo", 450, FakeStackChan.SERVO_ID_X),
            ("pitchServo", 50, FakeStackChan.SERVO_ID_Y),
            ("pitchServo", 850, FakeStackChan.SERVO_ID_Y),
        )

        for field, angle, servo_id in cases:
            with self.subTest(field=field, angle=angle):
                self.app.stackchan.calls.clear()
                result = self.app.apply_motion({field: {"angle": angle, "speed": 100}})
                self.assertEqual(["applied"], result)
                self.assertIn(("angle", servo_id, angle / 10.0, 0, 10), self.app.stackchan.calls)

    def test_rejects_out_of_range_motion_without_touching_servos(self):
        cases = (
            {"yawServo": {"angle": -451, "speed": 200}},
            {"yawServo": {"angle": 451, "speed": 200}},
            {"pitchServo": {"angle": 49, "speed": 200}},
            {"pitchServo": {"angle": 851, "speed": 200}},
            {"yawServo": {"angle": 0, "speed": 99}},
            {"yawServo": {"angle": 0, "speed": 301}},
            {"yawServo": {"angle": 0, "speed": 200, "rotate": 1}},
        )

        for payload in cases:
            with self.subTest(payload=payload):
                self.app.stackchan.calls.clear()
                result = self.app.apply_motion(payload)
                self.assertNotEqual(["applied"], result)
                self.assertEqual([], self.app.stackchan.calls)

    def test_setup_leaves_servo_power_and_torque_untouched(self):
        app = self.firmware.Companion()

        app.setup()

        servo_calls = [call for call in app.stackchan.calls if call[0] in ("power", "torque", "angle")]
        self.assertEqual([], servo_calls)


class RgbSafetyTests(FirmwareTestCase):
    def test_zero_duration_sets_the_strip_immediately(self):
        self.app.apply_rgb({"leftRgbColor": "#123456", "leftRgbDuration": 0})

        self.assertEqual([("rgb", 0, 0x123456)], self.app.stackchan.calls)
        self.assertIsNone(self.app.rgb_transitions[0])

    def test_positive_duration_schedules_a_bounded_fade(self):
        self.app.apply_rgb({"rightRgbColor": "#A0B0C0", "rightRgbDuration": 10})

        transition = self.app.rgb_transitions[1]
        self.assertEqual(10000, transition["duration"])
        self.assertEqual((0xA0, 0xB0, 0xC0), transition["target"])
        self.assertEqual([], self.app.stackchan.calls)

    def test_invalid_duration_is_ignored_without_changing_the_strip(self):
        invalid = (True, False, -0.01, 10.01, float("nan"), float("inf"), float("-inf"), "not-a-number")

        for duration in invalid:
            with self.subTest(duration=duration):
                self.app.stackchan.calls.clear()
                self.app.rgb_transitions[0] = None
                self.app.apply_rgb({"leftRgbColor": "#123456", "leftRgbDuration": duration})
                self.assertEqual([], self.app.stackchan.calls)
                self.assertIsNone(self.app.rgb_transitions[0])


class BleStreamTests(FirmwareTestCase):
    def test_accepts_a_json_line_fragmented_into_twenty_byte_writes(self):
        message = json.dumps(
            {"companion": {"cpu": 42, "ram": 29, "gpu": 18, "activity": "AI working"}},
            separators=(",", ":"),
        ).encode() + b"\n"

        self.feed_in_portable_chunks(message)

        self.assertEqual("AI working", self.app.activity_label.text)
        self.assertEqual([b'{"ok":true,"type":"applied"}\n'], self.app.ble.writes)

    def test_accepts_a_line_of_exactly_2048_bytes(self):
        prefix = b'{"companion":{"activity":"Ready"},"padding":"'
        suffix = b'"}'
        message = prefix + (b"x" * (self.firmware.MAX_LINE_BYTES - len(prefix) - len(suffix))) + suffix
        self.assertEqual(self.firmware.MAX_LINE_BYTES, len(message))

        self.feed_in_portable_chunks(message + b"\n")

        self.assertEqual("Ready", self.app.activity_label.text)
        self.assertNotIn(b'{"ok":true,"type":"line-too-long"}\n', self.app.ble.writes)

    def test_discards_an_oversized_line_then_recovers_at_the_next_line(self):
        oversized = b"x" * (self.firmware.MAX_LINE_BYTES + 1) + b"\n"
        valid = b'{"companion":{"activity":"Quiet"}}\n'

        self.feed_in_portable_chunks(oversized + valid)

        self.assertEqual("Quiet", self.app.activity_label.text)
        self.assertEqual(1, self.app.ble.writes.count(b'{"ok":true,"type":"line-too-long"}\n'))
        self.assertEqual(b'{"ok":true,"type":"applied"}\n', self.app.ble.writes[-1])


class UsbStreamTests(FirmwareTestCase):
    def test_one_tick_drains_a_complete_hello_without_blocking(self):
        request = json.dumps({"companionHello": 1, "requestId": "burst"}) + "\n"
        fake_input, fake_output = self.attach_usb(request)

        self.app.consume_usb()

        self.assertEqual("", fake_input.text)
        self.assertEqual("burst", json.loads(fake_output.records()[-1])["requestId"])
        self.assertTrue(all(timeout == 0 for timeout in self.app.usb_poll.timeouts))

    def test_large_input_yields_after_the_per_tick_burst_limit(self):
        fake_input, _ = self.attach_usb("x" * 2048)

        self.app.consume_usb()

        self.assertEqual(2048 - self.firmware.USB_READ_BURST, len(fake_input.text))

    def test_fragmented_hello_returns_a_correlated_usb_identity(self):
        request_id = 'probe-"quoted"-\\slash'
        request = json.dumps({"companionHello": 1, "requestId": request_id}) + "\n"
        fake_input, fake_output = self.attach_usb(request)

        self.consume_all_usb(fake_input)

        response = json.loads(fake_output.records()[-1])
        self.assertEqual(
            {
                "ok": True,
                "type": "hello",
                "device": "MicaStatsStackChan",
                "protocol": 1,
                "transport": "usb",
                "motionCalibrated": True,
                "calibration": {"x": "saved", "y": "saved"},
                "requestId": request_id,
            },
            response,
        )
        self.assertTrue(all(timeout == 0 for timeout in self.app.usb_poll.timeouts))

    def test_wrapped_control_returns_the_matching_request_id(self):
        request = {
            "requestId": "move-17",
            "command": {"yawServo": {"angle": 450, "speed": 200}},
        }
        fake_input, fake_output = self.attach_usb(json.dumps(request) + "\n")

        self.consume_all_usb(fake_input)

        self.assertIn(("angle", FakeStackChan.SERVO_ID_X, 45.0, 0, 20), self.app.stackchan.calls)
        self.assertEqual(
            {"ok": True, "type": "applied", "requestId": "move-17"},
            json.loads(fake_output.records()[-1]),
        )

    def test_rejected_motion_keeps_request_correlation(self):
        request = {
            "requestId": "unsafe-move",
            "command": {"pitchServo": {"angle": 851, "speed": 200}},
        }
        fake_input, fake_output = self.attach_usb(json.dumps(request) + "\n")

        self.consume_all_usb(fake_input)

        self.assertEqual([], self.app.stackchan.calls)
        self.assertEqual(
            {"ok": True, "type": "rejected-motion", "requestId": "unsafe-move"},
            json.loads(fake_output.records()[-1]),
        )

    def test_unwrapped_usb_commands_are_ignored_without_side_effects(self):
        fake_input, fake_output = self.attach_usb(
            json.dumps({"yawServo": {"angle": 0, "speed": 200}}) + "\n"
        )

        self.consume_all_usb(fake_input)

        self.assertEqual([], self.app.stackchan.calls)
        self.assertEqual([], fake_output.records())

    def test_invalid_request_ids_are_ignored_without_side_effects(self):
        invalid_ids = (None, "", "x" * 65, True, float("inf"))

        for request_id in invalid_ids:
            with self.subTest(request_id=request_id):
                self.app.stackchan.calls.clear()
                request = {"requestId": request_id, "command": {"leftRgbColor": "#123456"}}
                fake_input, fake_output = self.attach_usb(json.dumps(request) + "\n")
                self.consume_all_usb(fake_input)
                self.assertEqual([], self.app.stackchan.calls)
                self.assertEqual([], fake_output.records())

    def test_non_object_command_returns_correlated_invalid_message(self):
        request = {"requestId": "bad-command", "command": ["not", "an", "object"]}
        fake_input, fake_output = self.attach_usb(json.dumps(request) + "\n")

        self.consume_all_usb(fake_input)

        self.assertEqual([], self.app.stackchan.calls)
        self.assertEqual(
            {"ok": True, "type": "invalid-message", "requestId": "bad-command"},
            json.loads(fake_output.records()[-1]),
        )

    def test_ble_and_usb_keep_independent_partial_line_state(self):
        self.app.ble.feed(b'{"companion":{"activity":"AI working"}')
        self.app.consume_ble()
        fake_input, fake_output = self.attach_usb(
            json.dumps({"companionHello": 1, "requestId": "parallel"}) + "\n"
        )

        self.consume_all_usb(fake_input)
        self.app.ble.feed(b"}\n")
        self.app.consume_ble()

        self.assertEqual("AI working", self.app.activity_label.text)
        self.assertEqual("hello", json.loads(fake_output.records()[-1])["type"])
        self.assertEqual(b'{"ok":true,"type":"applied"}\n', self.app.ble.writes[-1])

    def test_usb_overflow_does_not_discard_a_partial_ble_record(self):
        self.app.ble.feed(b'{"companion":{"activity":"Quiet"}')
        self.app.consume_ble()
        oversized = "x" * (self.firmware.MAX_LINE_BYTES + 1) + "\n"
        hello = json.dumps({"companionHello": 1, "requestId": "after-overflow"}) + "\n"
        fake_input, fake_output = self.attach_usb(oversized + hello)

        self.consume_all_usb(fake_input)
        self.app.ble.feed(b"}\n")
        self.app.consume_ble()

        self.assertEqual("Quiet", self.app.activity_label.text)
        self.assertEqual("hello", json.loads(fake_output.records()[-1])["type"])

    def test_usb_accepts_an_enveloped_record_of_exactly_2048_bytes(self):
        prefix = '{"requestId":"limit","command":{"companion":{"activity":"Ready"},"padding":"'
        suffix = '"}}'
        request = prefix + ("x" * (self.firmware.MAX_LINE_BYTES - len(prefix) - len(suffix))) + suffix
        self.assertEqual(self.firmware.MAX_LINE_BYTES, len(request.encode()))
        fake_input, fake_output = self.attach_usb(request + "\n")

        self.consume_all_usb(fake_input)

        self.assertEqual("Ready", self.app.activity_label.text)
        self.assertEqual("applied", json.loads(fake_output.records()[-1])["type"])

    def test_setup_announces_usb_readiness_even_when_ble_cannot_start(self):
        original_server = self.firmware.BLEUARTServer
        fake_input, fake_output = self.attach_usb()

        class FailingBle:
            def __init__(self, *args, **kwargs):
                raise OSError("radio unavailable")

        try:
            self.firmware.BLEUARTServer = FailingBle
            app = self.firmware.Companion()
            app.setup()
            fake_input.feed(json.dumps({"companionHello": 1, "requestId": "usb-only"}) + "\n")
            while fake_input.text:
                app.consume_usb()
        finally:
            self.firmware.BLEUARTServer = original_server

        self.assertIn("MicaStatsStackChan USB ready", fake_output.records())
        response = json.loads(fake_output.records()[-1])
        self.assertEqual("hello", response["type"])
        self.assertEqual("usb-only", response["requestId"])


class DashboardRenderingTests(FirmwareTestCase):
    def test_formats_finite_metrics_as_clamped_percentages(self):
        self.app.apply_dashboard({"cpu": 101.25, "ram": 0, "gpu": 42.5})

        self.assertEqual("100%", self.app.cpu_label.text)
        self.assertEqual("0%", self.app.ram_label.text)
        self.assertEqual("43%", self.app.gpu_label.text)

    def test_formats_unknown_or_nonfinite_metrics_as_unavailable(self):
        self.app.apply_dashboard({"cpu": None, "ram": float("nan"), "gpu": float("inf")})

        self.assertEqual("--", self.app.cpu_label.text)
        self.assertEqual("--", self.app.ram_label.text)
        self.assertEqual("--", self.app.gpu_label.text)

        self.app.apply_dashboard({"cpu": -4})
        self.assertEqual("--", self.app.cpu_label.text)

    def test_face_drawing_stays_above_the_local_status_row(self):
        self.lcd.fills.clear()
        self.lcd.lines.clear()
        self.app.left_eye.update({"x": -100, "y": -100, "rotation": 900, "weight": 100, "size": 100})
        self.app.right_eye.update({"x": 100, "y": 100, "rotation": 2700, "weight": 100, "size": 100})
        self.app.mouth.update({"x": 100, "y": 100, "rotation": 900, "weight": 100, "size": 100})

        self.app.draw_face()

        self.assertTrue(self.lcd.lines)
        for x1, y1, x2, y2, color in self.lcd.lines:
            self.assertGreaterEqual(min(x1, x2), 20)
            self.assertLessEqual(max(x1, x2), 103)
            self.assertGreaterEqual(min(y1, y2), 48)
            self.assertLessEqual(max(y1, y2), 101)
        for x, y, width, height, color in self.lcd.fills:
            self.assertGreaterEqual(y, 48)
            self.assertLessEqual(y + height - 1, 101)

    def test_setup_places_the_local_status_label_below_the_face(self):
        app = self.firmware.Companion()

        app.setup()

        self.assertGreater(app.local_label.y, 215)
        self.assertLessEqual(app.local_label.y + app.local_label.height, 240)

    def test_initial_screen_has_waiting_guidance_without_invented_readings(self):
        self.assertEqual("Waiting", self.app.activity_label.text)
        self.assertEqual("Updated --", self.app.age_label.text)
        self.assertEqual("WAIT", self.app.transport_label.text)
        self.assertTrue(all(label.text == "--" for label in self.app.metric_labels.values()))
        self.assertTrue(all(not history for history in self.app.histories.values()))

    def test_all_zero_desktop_neutral_has_the_same_face_as_initial_screen(self):
        self.lcd.lines.clear()
        self.app.draw_face()
        initial = list(self.lcd.lines)
        avatar = {key: dict(x=0, y=0, rotation=0, weight=0, size=0)
                  for key in ("leftEye", "rightEye", "mouth")}
        self.app.apply_avatar({"leftEye": {"size": 18}, "mouth": {"size": 32}})
        self.lcd.lines.clear()
        self.app.apply_avatar(avatar)
        self.assertEqual(initial, self.lcd.lines)
        self.assertGreater(len(initial), 40)  # Visible filled eyes and smile.

    def test_history_contains_only_received_samples_and_stays_bounded(self):
        self.app.apply_dashboard({"cpu": 37})
        self.assertEqual([37], self.app.histories["cpu"])
        self.assertEqual([], self.app.histories["ram"])
        for value in range(80):
            self.app.apply_dashboard({"cpu": value})
        self.assertEqual(list(range(62, 80)), self.app.histories["cpu"])
        self.assertEqual(18, len(self.app.histories["cpu"]))

    def test_unknown_samples_break_trend_lines_instead_of_drawing_zero(self):
        for value in (25, None, 75):
            self.app.apply_dashboard({"cpu": value})
        self.lcd.lines.clear()
        self.lcd.circles.clear()
        self.app.draw_metric("cpu")
        trend_lines = [line for line in self.lcd.lines if line[-1] == self.firmware.ACCENT]
        self.assertEqual([], trend_lines)
        self.assertEqual(2, len(self.lcd.circles))
        self.assertTrue(all(point[-1] == self.firmware.ACCENT for point in self.lcd.circles))
        self.assertEqual([25, None, 75], self.app.histories["cpu"])

    def test_metric_update_never_erases_avatar_or_other_metrics(self):
        self.lcd.fills.clear()
        self.lcd.rounds.clear()
        self.app.apply_dashboard({"ram": 100})
        for x, y, width, height, color in self.lcd.fills:
            if y >= 222:
                self.assertLessEqual(y + height, 240)
                self.assertEqual(self.firmware.BACKGROUND, color)
                continue
            self.assertGreaterEqual(x, 114)
            self.assertLessEqual(x + width, 206)
            self.assertGreaterEqual(y, 147)
            self.assertLessEqual(y + height, 215)
            self.assertEqual(self.firmware.PANEL, color)
        for _, x, y, width, height, radius, color in self.lcd.rounds:
            self.assertGreaterEqual(x, 114)
            self.assertLessEqual(x + width, 206)

    def test_usb_hello_labels_source_without_marking_data_fresh(self):
        self.attach_usb()
        self.app.handle_usb_message(b'{"companionHello":1,"requestId":"screen"}')
        self.assertEqual("USB", self.app.transport_label.text)
        self.assertEqual("Updated --", self.app.age_label.text)
        self.assertIsNone(self.app.last_dashboard)

    def test_age_measures_dashboard_not_manual_face_or_led_requests(self):
        self.app.apply_dashboard({"cpu": 42, "activity": "Ready"})
        self.fake_time.now += 75000
        self.app.apply_avatar({"mouth": {"size": 32}})
        self.app.apply_rgb({"leftRgbColor": "#00D4FF"})
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("Updated 1m ago", self.app.age_label.text)
        self.assertEqual("Ready", self.app.activity_label.text)
        self.assertNotIn("offline", self.app.age_label.text.lower())
        self.assertNotIn("disconnected", self.app.age_label.text.lower())

    def test_invalid_activity_alone_does_not_refresh_dashboard_age(self):
        self.app.apply_dashboard({"activity": "private prompt"})
        self.assertIsNone(self.app.last_dashboard)
        self.assertEqual("Waiting", self.app.activity_label.text)

    def test_age_cap_stays_old_across_a_long_tick_wrap_then_resets_on_data(self):
        self.app.apply_dashboard({"cpu": 42})
        self.app.update_age(self.fake_time.now + 3600000)
        self.assertEqual("Updated 1h+ ago", self.app.age_label.text)
        self.app.update_age(self.fake_time.now - 1000)
        self.assertEqual("Updated 1h+ ago", self.app.age_label.text)
        self.app.apply_dashboard({"cpu": 42})
        self.assertEqual("Updated now", self.app.age_label.text)

    def test_unchanged_text_and_idle_ticks_do_not_repaint(self):
        self.lcd.texts.clear()
        self.app.apply_dashboard({"cpu": 42})
        first_count = len(self.lcd.texts)
        self.app.apply_dashboard({"cpu": 42})
        self.assertEqual(first_count, len(self.lcd.texts))
        self.lcd.fills.clear()
        self.lcd.lines.clear()
        self.lcd.rounds.clear()
        self.app.update_screen(self.fake_time.now)
        self.app.update_screen(self.fake_time.now + 20)
        self.assertEqual([], self.lcd.fills)
        self.assertEqual([], self.lcd.lines)
        self.assertEqual([], self.lcd.rounds)

    def test_quiet_cancels_blink_and_remains_still_without_motor_calls(self):
        self.fake_time.now += 6500
        self.app.update_screen(self.fake_time.now)
        self.assertTrue(self.app.blinking)
        self.app.apply_dashboard({"activity": "Quiet"})
        self.app.update_screen(self.fake_time.now)
        self.assertFalse(self.app.blinking)
        self.fake_time.now += 6500
        self.lcd.lines.clear()
        self.app.update_screen(self.fake_time.now)
        self.assertEqual([], self.lcd.lines)
        self.assertEqual([], self.app.stackchan.calls)

    def test_battery_shows_measured_voltage_without_a_charge_estimate(self):
        self.app.update_local_status()
        self.assertEqual("BAT 4.12V", self.app.local_label.text)
        for value in (None, float("nan"), float("inf"), -1, 0):
            self.app.stackchan.get_battery_voltage = lambda: value
            self.app.update_local_status()
            self.assertEqual("BAT --", self.app.local_label.text)

    def test_battery_read_failure_keeps_dashboard_running(self):
        def fail_read():
            raise OSError("I2C temporarily unavailable")
        self.app.stackchan.get_battery_voltage = fail_read
        self.app.update_local_status()
        self.app.apply_dashboard({"cpu": 42})
        self.assertEqual("BAT --", self.app.local_label.text)
        self.assertEqual("42%", self.app.cpu_label.text)


class PersonalityTests(FirmwareTestCase):
    def fresh(self, **fields):
        payload = {"cpu": 42, "ram": 54, "gpu": 17, "activity": "Ready"}
        payload.update(fields)
        self.app.apply_dashboard(payload)

    def test_cycles_overview_spotlight_companion_and_rotates_focus_metric(self):
        self.fresh()
        for expected, dwell in (("focus", 6000), ("companion", 6000), ("overview", 12000), ("focus", 6000)):
            self.fake_time.now += dwell
            self.fresh()
            self.app.update_screen(self.fake_time.now)
            self.assertEqual(expected, self.app.view)
        self.assertEqual(1, self.app.focus_index)
        self.assertEqual("54%", self.app.focus_label.text)
        self.assertEqual(5, len(self.app.histories["cpu"]))

    def test_companion_face_remains_inside_body_and_does_not_change_wire_features(self):
        self.fresh()
        before = dict(self.app.left_eye)
        self.app.set_view("companion", self.fake_time.now)
        self.lcd.lines.clear()
        self.app.left_eye.update({"x": -100, "y": 100, "rotation": 900, "size": 100})
        self.app.draw_face()
        self.assertTrue(self.lcd.lines)
        for x1, y1, x2, y2, color in self.lcd.lines:
            self.assertTrue(55 <= min(x1, x2) <= max(x1, x2) <= 264)
            self.assertTrue(48 <= min(y1, y2) <= max(y1, y2) <= 182)
        self.app.left_eye.update(before)
        self.fake_time.now = 5000
        self.app.update_screen(self.fake_time.now)
        self.assertEqual(before, self.app.left_eye)
        self.assertLessEqual(abs(self.app.gaze), 4)

    def test_abnormal_event_interrupts_rotation_and_shows_reading_and_threshold(self):
        self.fresh()
        self.app.set_view("companion", self.fake_time.now)
        alert = {"kind": "cpu-temperature", "value": 97.2, "threshold": 95, "above": True,
                 "label": "PRIVATE", "detail": "C:/private/drive"}
        self.fresh(activity="Alert", alert=alert)
        self.assertEqual("alert", self.app.view)
        self.assertEqual("CPU temperature", self.app.alert_name.text)
        self.assertEqual("97.2 C", self.app.alert_value.text)
        self.assertEqual("Above 95.0 C", self.app.alert_threshold.text)
        self.assertNotIn("PRIVATE", str(self.lcd.texts))
        self.assertNotIn("private/drive", str(self.app.alert))
        self.fake_time.now += 10000
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("alert", self.app.view)
        self.fresh()
        self.assertEqual("companion", self.app.view)
        self.assertIsNone(self.app.alert)
        self.fake_time.now += 12000
        self.fresh()
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("overview", self.app.view)

    def test_quiet_keeps_visual_alert_but_does_not_rotate_or_move(self):
        self.fresh(activity="Quiet", alert={"kind": "disk-free", "value": 4.5,
                   "threshold": 10, "above": False})
        self.assertEqual("alert", self.app.view)
        self.assertEqual("Below 10.0 GB", self.app.alert_threshold.text)
        self.fake_time.now += 20000
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("alert", self.app.view)
        self.assertEqual([], self.app.stackchan.calls)
        self.fresh(activity="Quiet")
        self.assertEqual("overview", self.app.view)
        self.fake_time.now += 10000
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("overview", self.app.view)

    def test_invalid_alerts_do_not_render_private_text_or_nonfinite_numbers(self):
        for fields in ({"kind": "secret rule"}, {"value": float("nan")},
                       {"threshold": -1}, {"above": 1}, {"value": 1000001}):
            alert = dict(kind="cpu-usage", value=97, threshold=90, above=True)
            alert.update(fields)
            self.fresh(activity="Alert", alert=alert)
            self.assertIsNone(self.app.alert)
            self.assertEqual("System event", self.app.alert_name.text)

    def test_leds_breathe_without_exceeding_requested_brightness(self):
        self.app.apply_rgb({"leftRgbColor": "#184020", "rightRgbColor": "#184020"})
        self.fresh()
        colors = []
        for now in (1000, 2000, 3000, 4000):
            self.fake_time.now = now
            self.app.update_ambient(now)
            colors.append(self.app.stackchan.rgb[0])
        self.assertGreater(len(set(colors)), 1)
        self.assertTrue(all(0 <= r <= 24 and 0 <= g <= 64 and 0 <= b <= 32 for r, g, b in colors))
        self.assertFalse(any(call[0] in ("power", "torque", "angle") for call in self.app.stackchan.calls))

    def test_alert_leds_alternate_slowly_and_manual_color_stops_animation(self):
        self.app.apply_rgb({"leftRgbColor": "#400800", "rightRgbColor": "#400800"})
        self.fresh(activity="Alert")
        self.app.update_ambient(1000)
        left, right = self.app.stackchan.rgb[0], self.app.stackchan.rgb[1]
        self.app.update_ambient(2000)
        self.assertEqual(left, self.app.stackchan.rgb[1])
        self.assertEqual(right, self.app.stackchan.rgb[0])
        self.app.apply_rgb({"leftRgbColor": "#002040"})
        self.app.stackchan.calls.clear()
        self.app.update_ambient(3000)
        self.assertEqual([], self.app.stackchan.calls)
        self.assertEqual((0, 32, 64), self.app.stackchan.rgb[0])

    def test_quiet_and_stale_leds_stay_constant_and_zero_brightness_stays_off(self):
        self.app.apply_rgb({"leftRgbColor": "#030405", "rightRgbColor": "#030405"})
        self.fresh(activity="Quiet")
        self.app.update_ambient(1000)
        self.app.stackchan.calls.clear()
        self.app.update_ambient(2000)
        self.assertEqual([], self.app.stackchan.calls)
        self.fresh()
        self.fake_time.now += 75000
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("overview", self.app.view)
        self.app.update_ambient(self.fake_time.now)
        self.app.stackchan.calls.clear()
        self.app.update_ambient(self.fake_time.now + 1000)
        self.assertEqual([], self.app.stackchan.calls)
        self.app.apply_rgb({"leftRgbColor": "#000000", "rightRgbColor": "#000000"})
        self.fresh()
        self.app.update_ambient(self.fake_time.now + 2000)
        self.assertEqual((0, 0, 0), self.app.stackchan.rgb[0])

    def test_real_alert_and_clear_can_preserve_a_manual_led_hold(self):
        self.app.apply_rgb({"leftRgbColor": "#001840", "rightRgbColor": "#402010"})
        self.fresh(activity="Alert", ambient=False,
                   alert={"kind": "cpu-usage", "value": 98, "threshold": 95, "above": True})
        self.assertEqual("alert", self.app.view)
        self.app.stackchan.calls.clear()
        self.app.update_ambient(2000)
        self.assertEqual([], self.app.stackchan.calls)
        self.fresh(ambient=False)
        self.assertEqual("companion", self.app.view)
        self.app.update_ambient(22000)
        self.assertEqual([], self.app.stackchan.calls)
        self.assertEqual((0, 24, 64), self.app.stackchan.rgb[0])
        self.assertEqual((64, 32, 16), self.app.stackchan.rgb[1])
        self.fresh()
        self.app.update_ambient(31000)
        self.assertTrue(self.app.stackchan.calls)

    def test_dashboard_on_focus_does_not_draw_old_overview_metric_regions(self):
        self.fresh()
        self.app.set_view("focus", self.fake_time.now)
        self.lcd.fills.clear()
        self.fresh(cpu=80, ram=93)
        self.assertEqual("80%", self.app.focus_label.text)
        self.assertTrue(all(y >= 122 or y == 79 for _, y, _, _, _ in self.lcd.fills))

    def test_missing_stored_calibration_blocks_all_motion_without_power_or_torque(self):
        self.app.motion_calibrated = False
        result = self.app.apply_motion({"yawServo": {"angle": 100, "speed": 100},
                                       "pitchServo": {"angle": 450, "speed": 100}})
        self.assertEqual(["calibration required"], result)
        self.assertEqual([], self.app.stackchan.calls)

    def test_calibration_check_reads_both_keys_without_writes_or_motor_queries(self):
        previous = sys.modules.get("esp32")
        reads = []
        class Nvs:
            def __init__(self, namespace):
                self.namespace = namespace
            def get_i32(self, key):
                reads.append((self.namespace, key))
                return values[key]
        sys.modules["esp32"] = types.SimpleNamespace(NVS=Nvs)
        try:
            for values, expected in (({"zero_pos_1": 450, "zero_pos_2": 125}, True),
                                     ({"zero_pos_1": 450}, False),
                                     ({"zero_pos_1": -1, "zero_pos_2": 125}, False)):
                self.app.check_calibration()
                self.assertEqual(expected, self.app.motion_calibrated)
            self.assertEqual(6, len(reads))
            self.assertEqual([], self.app.stackchan.calls)
        finally:
            if previous is None:
                sys.modules.pop("esp32", None)
            else:
                sys.modules["esp32"] = previous


class DynamicDisplayTests(FirmwareTestCase):
    def setUp(self):
        super().setUp()
        self.app.set_view("companion", self.fake_time.now)
        self.app.apply_dashboard({"cpu": 42, "ram": 54, "gpu": None, "activity": "Ready"})

    def frame(self, elapsed):
        self.fake_time.now = 1000 + elapsed
        self.app.update_screen(self.fake_time.now)

    def test_character_first_startup_keeps_unknown_readings_truthful(self):
        app = self.firmware.Companion()
        app.setup_display()
        self.assertEqual("companion", app.view)
        self.assertEqual("Waiting for MicaStats", app.companion_label.text)
        self.assertTrue(all(label.text == "--" for label in app.metric_labels.values()))
        self.assertTrue(all(not values for values in app.histories.values()))

    def test_eased_gaze_and_bob_generate_frames_without_mutating_manual_features(self):
        before = (dict(self.app.left_eye), dict(self.app.right_eye), dict(self.app.mouth))
        poses = []
        for elapsed in (1200, 1600, 2000, 2400, 2800, 3200):
            self.lcd.fills.clear()
            self.frame(elapsed)
            poses.append((self.app.gaze, self.app.bob))
            self.assertLessEqual(abs(self.app.gaze), 4)
            self.assertLessEqual(abs(self.app.bob), 2)
            self.assertTrue(all((y == 34 and height == 2) or
                                (x == 55 and y == 48 and width == 210 and height == 108) or y >= 222
                                for x, y, width, height, _ in self.lcd.fills))
        self.assertGreater(len(set(poses)), 4)
        self.assertEqual(before, (self.app.left_eye, self.app.right_eye, self.app.mouth))
        self.assertEqual([], self.app.stackchan.calls)
        self.assertEqual([42], self.app.histories["cpu"])

    def test_frames_are_rate_limited_while_usb_remains_available(self):
        self.frame(80)
        self.lcd.fills.clear()
        self.lcd.lines.clear()
        self.frame(100)
        self.frame(120)
        self.assertEqual([], self.lcd.fills)
        self.assertEqual([], self.lcd.lines)
        self.attach_usb()
        sys.stdin.feed('{"requestId":"during-frame","command":{"companion":{"cpu":63}}}\n')
        self.app.consume_usb()
        self.assertEqual("63%", self.app.cpu_label.text)
        self.assertEqual("during-frame", json.loads(sys.stdout.records()[-1])["requestId"])

    def test_blink_closes_holds_and_opens_in_separate_frames(self):
        opens = []
        for elapsed in (6500, 6580, 6660, 6740, 6820):
            self.frame(elapsed)
            opens.append(self.app.eye_open)
        self.assertEqual([100, 0, 0, 50, 100], opens)
        self.assertFalse(self.app.blinking)

    def test_activity_marks_are_distinct_and_do_not_draw_audio_or_ai_content(self):
        drawings = []
        for activity in ("Ready", "AI working", "Meeting active", "PC busy"):
            self.app.apply_dashboard({"activity": activity})
            self.lcd.lines.clear()
            self.lcd.circles.clear()
            self.app.face_canvas.clear()
            self.app.draw_activity_marks()
            self.app.face_canvas.present()
            drawings.append((list(self.lcd.lines), list(self.lcd.circles)))
        self.assertEqual(4, len(set(repr(drawing) for drawing in drawings)))
        self.assertEqual([], self.app.stackchan.calls)

    def test_companion_chips_update_only_received_values_and_unknowns(self):
        self.app.apply_dashboard({"cpu": 100, "gpu": float("nan")})
        self.assertEqual("100%", self.app.cpu_label.text)
        self.assertEqual("54%", self.app.ram_label.text)
        self.assertEqual("--", self.app.gpu_label.text)
        self.assertEqual([42, 100], self.app.histories["cpu"])
        self.assertEqual([54], self.app.histories["ram"])

    def test_character_dwell_progress_and_first_spotlight_use_cpu(self):
        self.frame(6000)
        self.assertEqual("companion", self.app.view)
        self.assertEqual(148, self.app.page_progress)
        self.frame(12000)
        self.assertEqual("overview", self.app.view)
        self.frame(18000)
        self.assertEqual("focus", self.app.view)
        self.assertEqual("42%", self.app.focus_label.text)
        self.frame(24000)
        self.assertEqual("companion", self.app.view)
        self.assertEqual(1, self.app.focus_index)

    def test_quiet_and_stale_stop_motion_and_hold_actual_readings(self):
        self.frame(2000)
        self.app.apply_dashboard({"activity": "Quiet"})
        self.frame(2500)
        self.lcd.lines.clear()
        self.frame(5000)
        self.assertEqual([], self.lcd.lines)
        self.app.apply_dashboard({"activity": "Ready"})
        self.app.set_view("companion", self.fake_time.now)
        self.frame(6000)
        self.frame(80000)
        self.assertEqual((0, 0, 100), (self.app.gaze, self.app.bob, self.app.eye_open))
        self.lcd.lines.clear()
        self.frame(85000)
        self.assertEqual([], self.lcd.lines)
        self.assertEqual("42%", self.app.cpu_label.text)
        self.assertEqual([], self.app.stackchan.calls)

    def test_alert_preempts_animation_then_border_becomes_steady(self):
        self.frame(2000)
        self.app.apply_dashboard({"activity": "Alert", "alert":
                                  {"kind": "cpu-temperature", "value": 97.2, "threshold": 95, "above": True}})
        self.assertEqual("alert", self.app.view)
        self.assertEqual("97.2 C", self.app.alert_value.text)
        colors = []
        for elapsed in (2250, 2500, 2750, 3000, 3250, 3500, 4000):
            self.frame(elapsed)
            colors.append(self.app.alert_border)
        self.assertGreater(len(set(colors)), 2)
        self.assertEqual(0xFF7043, colors[-1])
        self.lcd.rounds.clear()
        self.lcd.lines.clear()
        self.frame(4500)
        self.assertEqual([], self.lcd.rounds)
        self.assertEqual([], self.lcd.lines)
        self.assertEqual([], self.app.stackchan.calls)

    def test_quiet_alert_has_no_border_animation(self):
        self.app.apply_dashboard({"activity": "Quiet", "alert":
                                  {"kind": "disk-free", "value": 4.5, "threshold": 10, "above": False}})
        self.lcd.rounds.clear()
        for elapsed in (300, 600, 900, 1200, 2000):
            self.frame(elapsed)
        self.assertEqual([], self.lcd.rounds)
        self.assertEqual("Below 10.0 GB", self.app.alert_threshold.text)

    def test_animation_publishes_one_buffer_without_visible_erase_or_lines(self):
        self.lcd.physical_fills.clear()
        self.lcd.physical_lines.clear()
        self.lcd.blits.clear()
        self.frame(80)
        self.assertEqual([], self.lcd.physical_fills)
        self.assertEqual([], self.lcd.physical_lines)
        self.assertEqual(1, len(self.lcd.blits))
        x, y, width, height, pixels, swap, buf = self.lcd.blits[0]
        self.assertEqual((55, 48, 210, 108, 22680, True, 45360), (x, y, width, height, pixels, swap, len(buf)))
        self.assertGreater(len(set(buf)), 3)  # Completed eyes/mouth, not a blank erase frame.

    def test_buffer_is_native_rgb565_and_transmits_pixel_count(self):
        canvas = self.firmware.FaceCanvas(0, 0, 2, 2)
        canvas.frame.fill(canvas.color(0xFF0000))
        canvas.present()
        self.assertEqual((0, 0, 2, 2, 4, True, b'\x00\xf8' * 4), self.lcd.blits[-1])

    def test_upside_down_manual_eye_rotates_iris_glint_with_gaze(self):
        lines = []
        original = self.app.draw_face_line
        self.app.draw_face_line = lambda *args: lines.append(args)
        self.addCleanup(setattr, self.app, "draw_face_line", original)
        self.app.gaze = 2
        self.app.bob = 0
        feature = dict(self.app.left_eye)
        self.app.draw_eye(feature, 41)
        upright = lines[-1]
        feature["rotation"] = 1800
        self.app.draw_eye(feature, 41)
        rotated = lines[-1]
        self.assertEqual((63, 63), (upright[1], upright[3]))
        self.assertAlmostEqual(69, rotated[1])
        self.assertAlmostEqual(69, rotated[3])
        self.assertGreater(upright[2], 43)
        self.assertLess(rotated[2], 43)
        self.assertEqual(1800, feature["rotation"])

    def wrapping_clock(self):
        original = self.fake_time.ticks_diff
        self.fake_time.ticks_diff = lambda later, earlier: ((later - earlier + 524288) % 1048576) - 524288
        self.addCleanup(setattr, self.fake_time, "ticks_diff", original)

    def test_long_quiet_pause_rebases_wrapped_page_and_animation_clocks(self):
        self.wrapping_clock()
        self.app.apply_dashboard({"activity": "Quiet"})
        self.fake_time.now += 600000  # Beyond ticks_diff's signed half-period.
        self.app.apply_dashboard({"activity": "Ready"})
        self.app.update_screen(self.fake_time.now)
        self.assertEqual(self.fake_time.now, self.app.page_started)
        self.assertEqual(self.fake_time.now, self.app.animation_started)
        self.lcd.blits.clear()
        self.fake_time.now += 80
        self.app.update_screen(self.fake_time.now)
        self.assertEqual(1, len(self.lcd.blits))
        self.fake_time.now += 6000
        self.app.update_screen(self.fake_time.now)
        self.assertEqual("focus", self.app.view)

    def test_long_manual_pause_resumes_ambient_and_local_status_after_wrap(self):
        self.wrapping_clock()
        self.app.apply_rgb({"leftRgbColor": "#184020"})
        self.fake_time.now += 600000
        self.app.apply_dashboard({"activity": "Ready"})
        self.app.stackchan.calls.clear()
        self.app.update_ambient(self.fake_time.now)
        self.assertEqual(self.fake_time.now, self.app.last_ambient)
        self.assertTrue(any(call[0] == "rgb" for call in self.app.stackchan.calls))
        self.app.stackchan.get_battery_voltage = lambda: 4.05
        self.app.loop()
        self.assertEqual("BAT 4.05V", self.app.local_label.text)
        self.assertEqual(self.fake_time.now, self.app.last_local_update)


if __name__ == "__main__":
    unittest.main()
