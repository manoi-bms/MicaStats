using System;
using System.Linq;
using Kil0bitSystemMonitor.Capture;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The scrolling capture's plumbing (scrolling capture spec 1 and 3): where the status card
    /// goes, the frame-to-image conversion, the shortcut, the mode and the texts the user sees.
    /// Nothing here moves the pointer, sends input or reads the screen.
    /// </summary>
    public class ScrollCaptureWiringTests
    {
        private static readonly PixelRect Work = new(0, 0, 1920, 1040);

        [Fact]
        public void The_card_goes_above_the_area_when_it_fits()
        {
            var area = new PixelRect(400, 300, 800, 500);

            var card = ScrollStatusPlacement.Place(area, Work, 360, 60);

            // Centred on the area, its bottom a gap above the area's top.
            Assert.Equal(new PixelRect(400 + (800 - 360) / 2, 300 - ScrollStatusPlacement.Gap - 60, 360, 60), card);
            Assert.True(card.Bottom <= area.Top);
        }

        [Fact]
        public void The_card_goes_below_the_area_when_there_is_no_room_above()
        {
            var area = new PixelRect(400, 20, 800, 500);

            var card = ScrollStatusPlacement.Place(area, Work, 360, 60);

            Assert.Equal(new PixelRect(620, 520 + ScrollStatusPlacement.Gap, 360, 60), card);
            Assert.True(card.Top >= area.Bottom);
        }

        [Fact]
        public void The_card_falls_back_to_the_work_area_corner_when_neither_fits()
        {
            var work = new PixelRect(1920, 40, 2560, 1360);
            var area = new PixelRect(1920, 60, 2560, 1320);

            var card = ScrollStatusPlacement.Place(area, work, 360, 60);

            Assert.Equal(new PixelRect(1920, 40, 360, 60), card);
        }

        [Fact]
        public void The_card_stays_on_the_monitor_when_the_area_hugs_an_edge()
        {
            var nearLeft = ScrollStatusPlacement.Place(new PixelRect(0, 300, 100, 400), Work, 360, 60);
            var nearRight = ScrollStatusPlacement.Place(new PixelRect(1860, 300, 60, 400), Work, 360, 60);

            Assert.Equal(0, nearLeft.X);
            Assert.Equal(1920 - 360, nearRight.X);
        }

        [Fact]
        public void A_frame_becomes_a_frozen_image_with_the_same_pixels()
        {
            UiThread.Run(() =>
            {
                var px = new int[3 * 2];
                for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFF000000) | (i * 0x101010 + 0x0A0B0C);
                var frame = new PixelFrame(3, 2, px);

                var image = ScreenCaptureEngine.ToBitmapSource(frame);

                Assert.True(image.IsFrozen);
                Assert.Equal(3, image.PixelWidth);
                Assert.Equal(2, image.PixelHeight);
                var back = new int[px.Length];
                image.CopyPixels(back, 3 * 4, 0);
                Assert.Equal(px[0], back[0]);
                Assert.Equal(px, back);
            });
        }

        [Fact]
        public void The_scrolling_shortcut_is_planned_after_the_screen_one_while_capture_shortcuts_are_on()
        {
            var on = new AppConfig { CaptureHotkeysEnabled = true, PadHotkey = "", AiAssistantEnabled = false };

            var plan = CaptureHotkeys.Plan(on);

            Assert.Equal(
                new[] { HotkeyTarget.CaptureRegion, HotkeyTarget.CaptureWindow, HotkeyTarget.CaptureScreen, HotkeyTarget.CaptureScrolling },
                plan.Select(p => p.Target));
            var entry = Assert.Single(plan, p => p.Target == HotkeyTarget.CaptureScrolling);
            Assert.Equal(on.CaptureHotkeyScrolling, entry.Spec);
            Assert.Equal("capture", entry.Area);
            Assert.Equal("Scrolling", entry.Label);
        }

        [Fact]
        public void The_scrolling_shortcut_is_left_out_while_capture_shortcuts_are_off()
        {
            var off = new AppConfig { CaptureHotkeysEnabled = false, PadHotkey = "Ctrl+Alt+N", AiAssistantEnabled = false };

            Assert.DoesNotContain(CaptureHotkeys.Plan(off), p => p.Target == HotkeyTarget.CaptureScrolling);
        }

        [Fact]
        public void The_scrolling_shortcut_defaults_to_ctrl_shift_4()
        {
            var cfg = new AppConfig();

            Assert.Equal("Ctrl+Shift+4", cfg.CaptureHotkeyScrolling);
            Assert.True(HotkeyParser.TryParse(cfg.CaptureHotkeyScrolling, out var mods, out uint vk));
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, mods);
            Assert.Equal((uint)'4', vk);
        }

        [Fact]
        public void Scrolling_is_a_capture_mode()
        {
            Assert.True(Enum.IsDefined(CaptureMode.Scrolling));
            Assert.Equal("Scrolling", CaptureMode.Scrolling.ToString());
        }

        [Fact]
        public void The_editor_note_tells_why_the_capture_stopped()
        {
            Assert.Null(CaptureService.ScrollNote(ScrollStop.End));
            Assert.Equal("Nothing scrolled in that area", CaptureService.ScrollNote(ScrollStop.Unscrollable));
            Assert.Equal("Stopped: the view changed in a way MicaStats could not follow", CaptureService.ScrollNote(ScrollStop.NoMatch));
            Assert.Equal("Stopped: the view changed in a way MicaStats could not follow", CaptureService.ScrollNote(ScrollStop.SizeChanged));
        }

        [Fact]
        public void The_card_counts_pixels_with_grouping_and_says_how_to_stop()
        {
            string dot = ((char)0x00B7).ToString();

            Assert.Equal("Scrolling capture: 3,400 px " + dot + " Esc to stop", ScrollStatusWindow.ProgressText(3400, escHeld: true));
            Assert.Equal("Scrolling capture: 20,000 px " + dot + " Stops at the end of the page", ScrollStatusWindow.ProgressText(20000, escHeld: false));
        }
    }
}
