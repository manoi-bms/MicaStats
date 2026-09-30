using System.Linq;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Which global shortcuts are registered, and the overlay menu's Ask entry. Planned without
    /// any Win32 call, so no test registers a real system-wide hotkey.
    /// </summary>
    public class AiHotkeyTests
    {
        [Fact]
        public void The_ai_shortcut_is_planned_only_while_the_assistant_is_on()
        {
            var off = new AppConfig { AiAssistantEnabled = false, AiHotkey = "Ctrl+Alt+A" };
            Assert.DoesNotContain(CaptureHotkeys.Plan(off), p => p.Target == HotkeyTarget.Ai);

            var on = new AppConfig { AiAssistantEnabled = true, AiHotkey = "Ctrl+Alt+A" };
            var entry = Assert.Single(CaptureHotkeys.Plan(on), p => p.Target == HotkeyTarget.Ai);

            Assert.Equal("Ctrl+Alt+A", entry.Spec);
            Assert.Equal("ai", entry.Area);
            Assert.Equal("Ask MicaStats", entry.Label);
        }

        [Fact]
        public void An_empty_ai_shortcut_plans_nothing()
        {
            var cfg = new AppConfig { AiAssistantEnabled = true, AiHotkey = "" };

            Assert.DoesNotContain(CaptureHotkeys.Plan(cfg), p => p.Target == HotkeyTarget.Ai);
        }

        [Fact]
        public void The_existing_shortcuts_are_planned_as_before()
        {
            var cfg = new AppConfig { CaptureHotkeysEnabled = true, PadHotkey = "Ctrl+Alt+N", AiAssistantEnabled = false };

            var plan = CaptureHotkeys.Plan(cfg);

            Assert.Equal(
                new[] { HotkeyTarget.CaptureRegion, HotkeyTarget.CaptureWindow, HotkeyTarget.CaptureScreen, HotkeyTarget.Pad },
                plan.Select(p => p.Target));
            Assert.Equal(
                new[] { cfg.CaptureHotkeyRegion, cfg.CaptureHotkeyWindow, cfg.CaptureHotkeyFullScreen, "Ctrl+Alt+N" },
                plan.Select(p => p.Spec));
            Assert.Empty(CaptureHotkeys.Plan(new AppConfig { CaptureHotkeysEnabled = false, PadHotkey = "", AiAssistantEnabled = false }));
        }

        [Fact]
        public void The_overlay_menu_offers_ask_only_while_the_assistant_is_on()
        {
            Assert.Null(OverlayWindow.AskMenuText(new AppConfig { AiAssistantEnabled = false }));
            Assert.Equal("Ask MicaStats\u2026\tCtrl+Alt+A",
                OverlayWindow.AskMenuText(new AppConfig { AiAssistantEnabled = true, AiHotkey = "ctrl+alt+a" }));
            Assert.Equal("Ask MicaStats\u2026",
                OverlayWindow.AskMenuText(new AppConfig { AiAssistantEnabled = true, AiHotkey = "" }));
        }
    }
}
