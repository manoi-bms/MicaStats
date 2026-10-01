using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Ask window's two palettes and its theme setting.</summary>
    public class AskPaletteTests
    {
        private static PadColor ColorOf(AskPalette palette, string name) =>
            (PadColor)typeof(AskPalette).GetProperty(name)!.GetValue(palette)!;

        /// <summary>The foreground over the background, both composited over the window background first.</summary>
        private static double Ratio(AskPalette palette, string foreground, string background)
        {
            PadColor window = palette.Background;
            PadColor back = ColorOf(palette, background).Over(window);
            PadColor fore = ColorOf(palette, foreground).Over(back);
            return PadColor.Contrast(fore, back);
        }

        public static IEnumerable<object[]> ContrastRules()
        {
            var rules = new (string Fg, string Bg, double Min)[]
            {
                ("Ink", "Background", 4.5), ("Ink", "Bubble", 4.5), ("Ink", "Surface", 4.5),
                ("Ink", "CodeBack", 4.5), ("Ink", "NoteBack", 4.5), ("Ink", "Chrome", 4.5),
                ("Accent", "Background", 4.5), ("SendGlyph", "SendBack", 4.5),
                ("Muted", "Background", 4.5),
            };
            foreach (string theme in new[] { PadThemes.Dark, PadThemes.Light })
                foreach (var rule in rules)
                    yield return new object[] { theme, rule.Fg, rule.Bg, rule.Min };
        }

        [Theory]
        [MemberData(nameof(ContrastRules))]
        public void Every_pairing_meets_its_contrast_rule(string theme, string foreground, string background, double minimum)
        {
            double ratio = Ratio(AskPalette.For(theme), foreground, background);
            Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Every_color_is_listed_once_under_its_ask_key(string theme)
        {
            var palette = AskPalette.For(theme);
            var properties = typeof(AskPalette).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(PadColor)).ToList();
            var resources = palette.Resources();

            Assert.Equal(properties.Count, resources.Count);
            Assert.Equal(resources.Count, resources.Select(r => r.Key).Distinct().Count());
            foreach (var property in properties)
            {
                var entry = Assert.Single(resources, r => r.Key == "Ask." + property.Name);
                Assert.Equal((PadColor)property.GetValue(palette)!, entry.Value);
                Assert.NotEqual(default(PadColor), entry.Value);
            }
        }

        [Fact]
        public void The_dark_palette_keeps_the_look_the_window_always_had()
        {
            var dark = AskPalette.Dark;
            Assert.True(dark.IsDark);
            Assert.Equal("#FF0E0E13", dark.Background.ToString());
            Assert.Equal("#FF141419", dark.Chrome.ToString());
            Assert.Equal("#FFEDEDF2", dark.Ink.ToString());
            Assert.Equal("#FF3FD2E4", dark.Accent.ToString());
            Assert.Equal("#FF1E2A33", dark.Bubble.ToString());
        }

        [Theory]
        [InlineData("Light", "Light")]
        [InlineData(" light ", "Light")]
        [InlineData("Dark", "Dark")]
        [InlineData("Purple", "Dark")]
        [InlineData("", "Dark")]
        [InlineData(null, "Dark")]
        public void For_reads_any_unknown_name_as_dark(string? name, string expected)
        {
            Assert.Equal(expected, AskPalette.For(name).Name);
            Assert.Equal(expected == "Dark", AskPalette.For(name).IsDark);
        }

        [Fact]
        public void The_theme_defaults_to_dark_and_a_config_without_it_loads()
        {
            Assert.Equal("Dark", new AppConfig().AskTheme);
            Assert.Equal("Dark", JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.AskTheme);
        }

        [Fact]
        public void The_theme_survives_a_save_and_load_and_is_independent_of_the_pad_theme()
        {
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { AskTheme = "Light" }))!;
            Assert.Equal("Light", back.AskTheme);
            Assert.Equal("Dark", back.PadTheme);

            var other = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadTheme = "Light" }))!;
            Assert.Equal("Dark", other.AskTheme);
        }

        [Theory]
        [InlineData("\"Light\"", "Light")]
        [InlineData("\"light\"", "Light")]
        [InlineData("\"Blue\"", "Dark")]
        [InlineData("\"\"", "Dark")]
        [InlineData("null", "Dark")]
        public void An_unknown_stored_theme_reads_as_dark(string json, string expected)
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"AskTheme\": " + json + "}")!;
            Assert.Equal(expected, config.AskTheme);
        }
    }
}
