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

        // ----- Where the wheel goes (final review) -------------------------------------------------

        private static readonly IntPtr Picked = new(0x1234), Other = new(0x5678);

        private static WheelWindowState Now(IntPtr under, IntPtr inFront, bool alive = true, bool minimised = false, bool ownedByPicked = false)
            => new(alive, minimised, under, ownedByPicked, inFront);

        [Theory]
        [InlineData(2, false, true)]      // under the pointer: the wheel reaches it, in front or not
        [InlineData(2, true, true)]
        [InlineData(0, true, true)]       // to the focused window: only once it is in front
        [InlineData(0, false, false)]
        [InlineData(1, true, true)]       // hybrid: desktop apps get the wheel by focus
        [InlineData(1, false, false)]
        [InlineData(null, true, true)]    // unknown, older Windows: focus routing
        [InlineData(null, false, false)]
        public void The_wheel_is_sent_only_where_Windows_will_deliver_it(int? routing, bool pickedInFront, bool sends)
        {
            var check = ScrollInputGuard.Check(Picked, routing, Now(Picked, pickedInFront ? Picked : Other));

            Assert.Equal(sends ? WheelCheck.Send : WheelCheck.NotInFront, check);
            Assert.Equal(routing != 2, ScrollInputGuard.NeedsForeground(routing));
        }

        [Fact]
        public void The_capture_stops_when_the_picked_window_is_no_longer_under_the_area()
        {
            Assert.Equal(WheelCheck.Covered, ScrollInputGuard.Check(Picked, 2, Now(Other, Other)));
            Assert.Equal(WheelCheck.Gone, ScrollInputGuard.Check(Picked, 2, Now(Picked, Picked, minimised: true)));
            Assert.Equal(WheelCheck.Gone, ScrollInputGuard.Check(Picked, 2, Now(IntPtr.Zero, Other, alive: false)));
            Assert.Equal(WheelCheck.NoWindow, ScrollInputGuard.Check(IntPtr.Zero, 2, Now(IntPtr.Zero, Other)));
            Assert.Equal(WheelCheck.NotInFront, ScrollInputGuard.Check(Picked, 0, Now(Picked, Other)));   // switched away
            // The picked window's own popup under the centre (a tooltip) is still that window.
            Assert.Equal(WheelCheck.Send, ScrollInputGuard.Check(Picked, 2, Now(Other, Other, ownedByPicked: true)));
        }

        [Fact]
        public void A_held_modifier_key_is_waited_out_for_a_while_then_stops()
        {
            Assert.Equal(ModifierGate.Send, ScrollInputGuard.Modifiers(held: false, waitedMs: 0));
            Assert.Equal(ModifierGate.Wait, ScrollInputGuard.Modifiers(held: true, waitedMs: 0));
            Assert.Equal(ModifierGate.Wait, ScrollInputGuard.Modifiers(held: true, waitedMs: ScrollInputGuard.ModifierWaitMs - 50));
            Assert.Equal(ModifierGate.Stop, ScrollInputGuard.Modifiers(held: true, waitedMs: ScrollInputGuard.ModifierWaitMs));
            Assert.Equal(ModifierGate.Send, ScrollInputGuard.Modifiers(held: false, waitedMs: ScrollInputGuard.ModifierWaitMs));
        }

        [Fact]
        public void A_scroll_that_could_not_be_sent_is_noted_in_the_editor()
        {
            Assert.Equal("MicaStats could not send scrolling to that window", CaptureService.ScrollNote(ScrollStop.InputLost));
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
