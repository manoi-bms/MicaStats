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

        [Fact]
        public void Markdown_formatting_is_on_by_default_and_round_trips()
        {
            Assert.True(new AppConfig().PadMarkdown);
            Assert.True(JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.PadMarkdown);
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadMarkdown = false }))!;
            Assert.False(back.PadMarkdown);
        }

        [Fact]
        public void Auto_close_is_on_by_default_and_round_trips()
        {
            Assert.True(new AppConfig().PadAutoClose);
            Assert.True(JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.PadAutoClose);
            Assert.False(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadAutoClose = false }))!.PadAutoClose);
        }

        [Fact]
        public void Gpu_drawing_is_off_by_default_and_round_trips()
        {
            Assert.False(new AppConfig().UseGpuRendering);
            Assert.False(JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.UseGpuRendering);
            Assert.True(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { UseGpuRendering = true }))!.UseGpuRendering);
        }

        [Fact]
        public void Startup_switches_wpf_to_software_drawing_unless_the_gpu_is_asked_for()
        {
            string app = System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "App.xaml.cs"));
            int render = app.IndexOf("RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly", System.StringComparison.Ordinal);
            int firstWindow = app.IndexOf("m_dummyWindow = new Window()", System.StringComparison.Ordinal);
            Assert.True(render > 0, "App.OnStartup must switch WPF to software drawing");
            Assert.True(render < firstWindow, "the switch must come before the first WPF window is created");
            Assert.Contains("if (!config.Config.UseGpuRendering)", app, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Maintenance_encrypts_plain_files_before_pruning()
        {
            string app = System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "App.xaml.cs"));
            int encrypt = app.IndexOf("store.EncryptPlainFiles()", StringComparison.Ordinal);
            int prune = app.IndexOf("store.PruneAll(", StringComparison.Ordinal);

            Assert.True(encrypt > 0 && encrypt < prune);
        }
    }
}
