using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's two palettes: colors parse and print, both themes are complete, and every pairing meets its contrast rule.</summary>
    public class PadPaletteTests
    {
        [Theory]
        [InlineData("#3FD2E4", 0xFF, 0x3F, 0xD2, 0xE4)]
        [InlineData("#553FD2E4", 0x55, 0x3F, 0xD2, 0xE4)]
        [InlineData("3fd2e4", 0xFF, 0x3F, 0xD2, 0xE4)]
        public void Parse_reads_rgb_and_argb(string hex, int a, int r, int g, int b)
        {
            Assert.Equal(new PadColor((byte)a, (byte)r, (byte)g, (byte)b), PadColor.Parse(hex));
        }

        [Theory]
        [InlineData("#12345")]
        [InlineData("#GG0000")]
        [InlineData("")]
        public void Parse_rejects_what_is_not_a_color(string hex)
        {
            Assert.Throws<FormatException>(() => PadColor.Parse(hex));
        }

        [Fact]
        public void ToString_prints_argb_in_upper_case()
        {
            Assert.Equal("#553FD2E4", PadColor.Parse("#553fd2e4").ToString());
            Assert.Equal("#FF0E0E13", PadColor.Parse("#0E0E13").ToString());
        }

        [Fact]
        public void Black_on_white_is_21_to_1()
        {
            Assert.Equal(21.0, PadColor.Contrast(PadColor.Parse("#000000"), PadColor.Parse("#FFFFFF")), 2);
        }

        [Fact]
        public void A_translucent_color_is_judged_as_it_is_painted()
        {
            // Half-white over black is #808080: luminance 0.2159, so (0.2159 + 0.05) / 0.05 = 5.32.
            var halfWhite = PadColor.Parse("#80FFFFFF");
            Assert.Equal(PadColor.Parse("#808080"), halfWhite.Over(PadColor.Parse("#000000")));
            Assert.Equal(5.32, PadColor.Contrast(halfWhite, PadColor.Parse("#000000")), 2);
        }

        public static IEnumerable<object[]> ContrastRules()
        {
            var rules = new (string Fg, string Bg, double Min)[]
            {
                ("Text", "Background", 7), ("WindowText", "Background", 7), ("WindowText", "Chrome", 7),
                ("WindowText", "Popup", 7), ("WindowText", "InfoBar", 7), ("WindowText", "Banner", 7),
                ("TextSoft", "Background", 7), ("TabActiveTitle", "TabActive", 7),
                ("Muted", "Background", 4.5), ("Muted", "Chrome", 4.5), ("Muted", "Popup", 4.5),
                ("TabTitle", "Chrome", 4.5),
                ("Accent", "Background", 4.5), ("Accent", "Chrome", 4.5), ("Accent", "Popup", 4.5),
                ("Accent", "InfoBar", 4.5), ("Accent", "Banner", 4.5), ("Accent", "TabActive", 4.5),
                ("AlertRed", "Background", 4.5), ("AlertRed", "Chrome", 4.5),
                ("LineNumbers", "Background", 3),
                ("SyntaxComment", "Background", 4.5), ("SyntaxString", "Background", 4.5),
                ("SyntaxKeyword", "Background", 4.5), ("SyntaxNumber", "Background", 4.5),
                ("SyntaxType", "Background", 4.5), ("SyntaxPreprocessor", "Background", 4.5),
                ("SyntaxTag", "Background", 4.5), ("SyntaxAttribute", "Background", 4.5),
                ("SyntaxOperator", "Background", 4.5),
                ("LogError", "Background", 4.5), ("LogWarning", "Background", 4.5),
                ("LogInfo", "Background", 4.5), ("LogDebug", "Background", 4.5),
                ("DiffAdded", "Background", 4.5), ("DiffRemoved", "Background", 4.5),
                ("MdHeading", "Background", 4.5), ("MdLink", "Background", 4.5),
                ("MdQuoteText", "Background", 4.5), ("MdListMarker", "Background", 4.5),
                ("MdTaskDone", "Background", 4.5), ("MdMarker", "Background", 3),
                ("Text", "MdCodeBackground", 7),
            };
            foreach (string theme in new[] { PadThemes.Dark, PadThemes.Light })
                foreach (var rule in rules)
                    yield return new object[] { theme, rule.Fg, rule.Bg, rule.Min };
        }

        [Theory]
        [MemberData(nameof(ContrastRules))]
        public void Every_palette_meets_its_contrast_rules(string theme, string foreground, string background, double minimum)
        {
            var palette = PadPalette.For(theme);
            double ratio = PadColor.Contrast(ColorOf(palette, foreground), ColorOf(palette, background));
            Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Every_color_is_listed_once_under_its_resource_key(string theme)
        {
            var palette = PadPalette.For(theme);
            var properties = typeof(PadPalette).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(PadColor)).ToList();
            var resources = palette.Resources();

            Assert.Equal(49, properties.Count);
            Assert.Equal(properties.Count, resources.Count);
            Assert.Equal(resources.Count, resources.Select(r => r.Key).Distinct().Count());
            foreach (var property in properties)
            {
                var entry = Assert.Single(resources, r => r.Key == "Pad." + property.Name);
                Assert.Equal((PadColor)property.GetValue(palette)!, entry.Value);
                Assert.NotEqual(default(PadColor), entry.Value);
            }
        }

        [Fact]
        public void The_dark_palette_keeps_todays_look()
        {
            var dark = PadPalette.Dark;
            Assert.True(dark.IsDark);
            Assert.Equal("#FF0E0E13", dark.Background.ToString());
            Assert.Equal("#FF141419", dark.Chrome.ToString());
            Assert.Equal("#FF3FD2E4", dark.Accent.ToString());
            Assert.Equal("#FFEDEDF2", dark.Text.ToString());
            Assert.Equal("#553FD2E4", dark.Selection.ToString());
        }

        [Fact]
        public void The_light_palette_is_light_with_a_darker_accent()
        {
            var light = PadPalette.Light;
            Assert.False(light.IsDark);
            Assert.Equal("#FFFBFBFD", light.Background.ToString());
            Assert.Equal("#FF1B1B1F", light.Text.ToString());
            Assert.Equal("#FF06707C", light.Accent.ToString());
        }

        [Theory]
        [InlineData("Light", "Light")]
        [InlineData("light", "Light")]
        [InlineData(" LIGHT ", "Light")]
        [InlineData("Dark", "Dark")]
        [InlineData("dark", "Dark")]
        [InlineData("blue", "Dark")]
        [InlineData("", "Dark")]
        [InlineData(null, "Dark")]
        public void Theme_names_normalize_to_dark_or_light(string? value, string expected)
        {
            Assert.Equal(expected, PadThemes.Normalize(value));
        }

        [Fact]
        public void For_picks_the_palette_by_normalized_name()
        {
            Assert.Same(PadPalette.Light, PadPalette.For("light"));
            Assert.Same(PadPalette.Dark, PadPalette.For("anything"));
            Assert.Same(PadPalette.Dark, PadPalette.For(null));
        }

        private static PadColor ColorOf(PadPalette palette, string name) =>
            (PadColor)typeof(PadPalette).GetProperty(name)!.GetValue(palette)!;
    }
}
