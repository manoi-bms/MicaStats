"""Render the actual companion code with synthetic readings and a Pillow LCD.

Developer-only preview: requires an already installed Pillow and a Montserrat
TTF supplied with --font. The robot/app needs neither dependency nor font file.
The TTF preview approximates the built-in bitmap fonts; verify on hardware too.
"""

import argparse
import importlib.util
from pathlib import Path
import sys

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]


def rgb(color):
    # Direct primitives and buffered faces share the real LCD's RGB565 precision.
    return (((color >> 19) & 31) * 255 // 31,
            ((color >> 10) & 63) * 255 // 63,
            ((color >> 3) & 31) * 255 // 31)


class PreviewLcd:
    def __init__(self, fonts):
        self.image = Image.new("RGB", (320, 240))
        self.draw = ImageDraw.Draw(self.image)
        self.fonts = fonts

    def width(self):
        return 320

    def height(self):
        return 240

    def setTextSize(self, size):
        pass

    def setTextColor(self, foreground, background):
        self.foreground = foreground

    def textWidth(self, text, font):
        return int(self.fonts[font].getlength(text)) + 1

    def fontHeight(self, font):
        return font

    def drawString(self, text, x, y, font):
        self.draw.text((x, y), text, font=self.fonts[font], fill=rgb(self.foreground), anchor="lt")

    def fillRect(self, x, y, width, height, color):
        if width > 0 and height > 0:
            self.draw.rectangle((x, y, x + width - 1, y + height - 1), fill=rgb(color))

    def fillRoundRect(self, x, y, width, height, radius, color):
        self.draw.rounded_rectangle((x, y, x + width - 1, y + height - 1), radius, fill=rgb(color))

    def drawRoundRect(self, x, y, width, height, radius, color):
        self.draw.rounded_rectangle((x, y, x + width - 1, y + height - 1), radius, outline=rgb(color))

    def drawRect(self, x, y, width, height, color):
        self.draw.rectangle((x, y, x + width - 1, y + height - 1), outline=rgb(color))

    def fillCircle(self, x, y, radius, color):
        self.draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=rgb(color))

    def drawLine(self, x1, y1, x2, y2, color):
        self.draw.line((x1, y1, x2, y2), fill=rgb(color))

    def drawRawBuf(self, buf, x, y, width, height, pixels, swap=False):
        if pixels != width * height or len(buf) != pixels * 2 or not swap:
            raise ValueError("Invalid native RGB565 face buffer contract")
        frame = Image.frombytes("RGB", (width, height), bytes(buf), "raw", "BGR;16")
        self.image.paste(frame, (x, y))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--font", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/stackchan-screen/previews")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    Image.init()  # Load encoders before the device harness replaces time/select.
    fonts = {size: ImageFont.truetype(str(args.font), size) for size in (12, 18, 24)}
    for font in fonts.values():
        try:
            font.set_variation_by_axes([400])
        except OSError:
            pass  # A static Montserrat Regular TTF has no variation axes.

    harness_path = ROOT / "tests/firmware/test_stackchan_companion.py"
    spec = importlib.util.spec_from_file_location("stackchan_preview_fakes", harness_path)
    harness = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(harness)
    harness.FirmwareTestCase.setUpClass()
    firmware = harness.FirmwareTestCase.firmware
    lcd = PreviewLcd(fonts)

    firmware.M5.Lcd = lcd
    firmware.Widgets.fillScreen = lambda color: lcd.fillRect(0, 0, 320, 240, color)
    clock = harness.FirmwareTestCase.fake_time
    original_stdout = sys.stdout
    sys.stdout = harness.FakeTextOutput()
    try:
        states = ("waiting", "ready", "overview", "focus", "companion", "alert", "ai", "meeting", "busy", "quiet", "quiet-alert", "aged")
        panels = []
        for state in states:
            clock.now = 1000
            app = firmware.Companion()
            app.setup()
            if state != "waiting":
                app.handle_usb_message(b'{"companionHello":1,"requestId":"preview"}')
                # Synthetic sample series; only the actual renderer produces
                # charts, bars, labels and the face.
                for sample in range(18):
                    clock.now += 2000
                    activity = {"busy": "PC busy", "ai": "AI working", "meeting": "Meeting active", "quiet": "Quiet"}.get(state, "Ready")
                    app.apply_dashboard({"cpu": 19 + (sample * 7) % 35, "ram": 48 + sample / 3, "gpu": 9 + (sample * 3) % 20, "activity": activity})
                if state == "busy":
                    app.apply_dashboard({"cpu": 96, "ram": 87, "gpu": 100, "activity": "PC busy"})
                if state == "unavailable":
                    app.apply_dashboard({"cpu": None, "ram": 54, "gpu": None})
                    app.stackchan.get_battery_voltage = lambda: None
                    app.update_local_status()
                if state == "aged":
                    clock.now += 75000
                faces = {
                    "ai": {"leftEye": {"x": -8, "size": -12}, "rightEye": {"x": 8, "size": -12}, "mouth": {"y": 5, "size": -18}},
                    "busy": {"leftEye": {"y": 5, "rotation": 180, "size": -8}, "rightEye": {"y": 5, "rotation": 3420, "size": -8}, "mouth": {"y": 8, "rotation": 1800, "size": -24}},
                    "meeting": {"leftEye": {"y": -4, "size": 18}, "rightEye": {"y": -4, "size": 18}, "mouth": {"y": 8, "size": 32}},
                    "quiet": {"leftEye": {"y": 8, "weight": 20, "size": -45}, "rightEye": {"y": 8, "weight": 20, "size": -45}, "mouth": {"y": 12, "size": -30}},
                }
                avatar = {}
                for feature in ("leftEye", "rightEye", "mouth"):
                    avatar[feature] = dict(x=0, y=0, rotation=0, weight=0, size=0)
                    avatar[feature].update(faces.get(state, {}).get(feature, {}))
                app.apply_avatar(avatar)
                app.update_screen(clock.now)
                clock.now += 400
                app.update_screen(clock.now)  # Capture normal open-eye state.
                if state == "overview":
                    app.set_view("overview", clock.now)
                elif state not in ("quiet", "aged"):
                    app.set_view("companion", clock.now)
                if state == "focus":
                    app.set_view(state, clock.now)
                if state in ("alert", "quiet-alert"):
                    app.apply_dashboard({"activity": "Quiet" if state == "quiet-alert" else "Alert",
                                         "alert": {"kind": "disk-free" if state == "quiet-alert" else "cpu-temperature",
                                                   "value": 4.5 if state == "quiet-alert" else 97.2,
                                                   "threshold": 10 if state == "quiet-alert" else 95,
                                                   "above": state != "quiet-alert"}})
            panels.append(lcd.image.copy())
            lcd.image.save(args.output / (state + ".png"))
        sheet = Image.new("RGB", (1280, 720))
        for index, panel in enumerate(panels):
            sheet.paste(panel, ((index % 4) * 320, (index // 4) * 240))
        sheet.save(args.output / "states.png")

        # Render a real 12/6/6-second cycle, then illustrative activity changes
        # and a synthetic threshold alert. No invented telemetry frames/samples.
        clock.now = 1000
        app = firmware.Companion()
        app.setup()
        app.handle_usb_message(b'{"companionHello":1,"requestId":"animation"}')
        frames = []
        for elapsed in range(0, 36000, 80):
            clock.now = 1000 + elapsed
            if elapsed % 2000 == 0:
                activity = "Ready" if elapsed < 24000 else "AI working" if elapsed < 28000 else "Meeting active" if elapsed < 32000 else "Alert"
                dashboard = {"cpu": 25 + (elapsed // 2000 * 7) % 30, "ram": 54, "gpu": 12, "activity": activity}
                if activity == "Alert":
                    dashboard["alert"] = {"kind": "cpu-temperature", "value": 97.2, "threshold": 95, "above": True}
                app.apply_dashboard(dashboard)
            app.update_screen(clock.now)
            frames.append(lcd.image.copy())
        frames[0].save(args.output / "living-companion.gif", save_all=True, append_images=frames[1:],
                       duration=80, loop=0, optimize=True, disposal=1)
    finally:
        sys.stdout = original_stdout
        harness.FirmwareTestCase.tearDownClass()
    print(args.output / "states.png")


if __name__ == "__main__":
    main()
