using System.Text.Json;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's settings: defaults, upgrade from an older config, and bounds.</summary>
    public class PadConfigTests
    {
        [Fact]
        public void The_defaults_match_the_spec()
        {
            var config = new AppConfig();

            Assert.Equal("Ctrl+Alt+N", config.PadHotkey);
            Assert.Equal("Cascadia Mono", config.PadFontFamily);
            Assert.Equal(14, config.PadFontSize);
            Assert.True(config.PadWordWrap);
            Assert.True(config.PadShowLineNumbers);
            Assert.Equal(90, config.PadHistoryDays);
            Assert.True(config.PadReopenAtLogin);
        }

        [Fact]
        public void An_older_config_without_pad_settings_gets_the_defaults()
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!;

            Assert.False(config.ShowCpu);
            Assert.Equal("Ctrl+Alt+N", config.PadHotkey);
            Assert.Equal(90, config.PadHistoryDays);
        }

        [Fact]
        public void Pad_settings_survive_a_round_trip()
        {
            var config = new AppConfig
            {
                PadHotkey = "Ctrl+Shift+N",
                PadFontFamily = "Consolas",
                PadFontSize = 16,
                PadWordWrap = false,
                PadShowLineNumbers = false,
                PadHistoryDays = 180,
                PadReopenAtLogin = false,
            };

            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

            Assert.Equal("Ctrl+Shift+N", back.PadHotkey);
            Assert.Equal("Consolas", back.PadFontFamily);
            Assert.Equal(16, back.PadFontSize);
            Assert.False(back.PadWordWrap);
            Assert.False(back.PadShowLineNumbers);
            Assert.Equal(180, back.PadHistoryDays);
            Assert.False(back.PadReopenAtLogin);
        }

        [Fact]
        public void Values_are_kept_in_range()
        {
            var config = new AppConfig { PadHistoryDays = 1, PadFontSize = 100, PadFontFamily = " " };
            Assert.Equal(7, config.PadHistoryDays);
            Assert.Equal(48, config.PadFontSize);
            Assert.Equal("Cascadia Mono", config.PadFontFamily);

            config.PadHistoryDays = 99999;
            config.PadFontSize = 2;
            config.PadHotkey = null!;
            Assert.Equal(3650, config.PadHistoryDays);
            Assert.Equal(8, config.PadFontSize);
            Assert.Equal("", config.PadHotkey);
        }

        [Fact]
        public void The_default_hotkey_parses()
        {
            Assert.True(HotkeyParser.TryParse(new AppConfig().PadHotkey, out var modifiers, out uint key));
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, modifiers);
            Assert.Equal((uint)'N', key);
        }

        [Fact]
        public void The_theme_defaults_to_dark()
        {
            Assert.Equal("Dark", new AppConfig().PadTheme);
            Assert.Equal("Dark", JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.PadTheme);
        }

        [Fact]
        public void The_theme_survives_a_round_trip()
        {
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadTheme = "Light" }))!;
            Assert.Equal("Light", back.PadTheme);
        }

        [Theory]
        [InlineData("\"light\"", "Light")]
        [InlineData("\"LIGHT\"", "Light")]
        [InlineData("\"blue\"", "Dark")]
        [InlineData("\"\"", "Dark")]
        [InlineData("null", "Dark")]
        public void A_hand_edited_theme_reads_as_dark_or_light(string json, string expected)
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"PadTheme\": " + json + "}")!;
            Assert.Equal(expected, config.PadTheme);
        }
    }
}
