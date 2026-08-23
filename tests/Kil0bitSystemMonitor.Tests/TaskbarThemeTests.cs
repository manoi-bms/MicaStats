using System.Drawing;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The overlay paints straight onto the taskbar with no plate of its own, so its colours
    /// are only correct relative to what is behind them. These assert readability as a number
    /// rather than trusting that the values looked right in an editor.
    /// </summary>
    public class OverlayPaletteTests
    {
        /// <summary>WCAG AA for normal text. The readings are what the user is trying to read.</summary>
        private const double TextTarget = 4.5;

        /// <summary>
        /// WCAG AA for large text and non-text graphics. The dim module labels ("CPU", "RAM")
        /// are deliberately quieter than the readings they caption, and the graph bars are
        /// shapes rather than glyphs.
        /// </summary>
        private const double SecondaryTarget = 3.0;

        public static TheoryData<string> PaletteNames => new() { "dark", "light" };

        private static OverlayPalette Named(string name) =>
            name == "light" ? OverlayPalette.Light : OverlayPalette.Dark;

        [Theory]
        [MemberData(nameof(PaletteNames))]
        public void Readings_clear_the_text_threshold(string name)
        {
            var palette = Named(name);
            double ratio = Contrast.Ratio(palette.Value, palette.Backdrop);
            Assert.True(ratio >= TextTarget,
                $"{name}: value contrast {ratio:F2} is under {TextTarget}");
        }

        [Theory]
        [MemberData(nameof(PaletteNames))]
        public void Labels_and_graphs_clear_the_secondary_threshold(string name)
        {
            var palette = Named(name);

            double label = Contrast.Ratio(palette.Label, palette.Backdrop);
            double graph = Contrast.Ratio(palette.Graph, palette.Backdrop);
            double alt = Contrast.Ratio(palette.GraphAlt, palette.Backdrop);

            Assert.True(label >= SecondaryTarget, $"{name}: label contrast {label:F2}");
            Assert.True(graph >= SecondaryTarget, $"{name}: graph contrast {graph:F2}");
            Assert.True(alt >= SecondaryTarget, $"{name}: alt graph contrast {alt:F2}");
        }

        /// <summary>
        /// The actual bug. Every shipped colour was from the white family, so on a light
        /// taskbar the overlay was white on near-white — this pins how bad it was, and that
        /// the light palette is not merely different but readable.
        /// </summary>
        [Fact]
        public void The_dark_palette_is_unreadable_on_a_light_taskbar()
        {
            double broken = Contrast.Ratio(OverlayPalette.Dark.Value, OverlayPalette.LightBackdrop);
            double repaired = Contrast.Ratio(OverlayPalette.Light.Value, OverlayPalette.LightBackdrop);

            Assert.True(broken < 1.5, $"expected the old colour to be invisible, measured {broken:F2}");
            Assert.True(repaired > 10, $"expected the new colour to be plainly readable, measured {repaired:F2}");
        }

        /// <summary>Cyan on white is the other half of the same problem.</summary>
        [Fact]
        public void The_dark_accent_hue_is_unreadable_on_a_light_taskbar()
        {
            Assert.True(Contrast.Ratio(OverlayPalette.Dark.Graph, OverlayPalette.LightBackdrop) < 2.0);
            Assert.True(Contrast.Ratio(OverlayPalette.Light.Graph, OverlayPalette.LightBackdrop) >= 3.0);
        }

        /// <summary>
        /// A dark taskbar is what nearly everyone runs, and this change must not alter a single
        /// pixel there. The dark palette reproduces the values the overlay used before.
        /// </summary>
        [Fact]
        public void The_dark_palette_reproduces_the_shipped_colours_exactly()
        {
            Assert.Equal(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), OverlayPalette.Dark.Value);
            Assert.Equal(Color.FromArgb(0xB0, 0xA6, 0xAC, 0xB4), OverlayPalette.Dark.Label);
            Assert.Equal(Color.FromArgb(0xFF, 0x3F, 0xD2, 0xE4), OverlayPalette.Dark.Graph);
            Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0x51, 0x47), OverlayPalette.Dark.GraphAlt);
            Assert.Equal(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF), OverlayPalette.Dark.Track);
        }

        /// <summary>
        /// The defaults the config ships with must match the dark palette's, or a fresh install
        /// on a dark taskbar would change appearance for no reason.
        /// </summary>
        [Fact]
        public void Config_defaults_match_the_dark_palette()
        {
            var config = new AppConfig();
            Assert.Equal("#FFFFFF", config.AccentColorHex);
            Assert.Equal("#00CCFF", config.LabelColorHex);

            Assert.Equal(0xFFFFFF, OverlayPalette.Dark.DefaultAccent.ToArgb() & 0xFFFFFF);
            Assert.Equal(0x00CCFF, OverlayPalette.Dark.DefaultLabelAccent.ToArgb() & 0xFFFFFF);
        }

        [Fact]
        public void For_selects_by_appearance()
        {
            Assert.Same(OverlayPalette.Light, OverlayPalette.For(TaskbarAppearance.Light));
            Assert.Same(OverlayPalette.Dark, OverlayPalette.For(TaskbarAppearance.Dark));
        }
    }

    public class ContrastTests
    {
        [Fact]
        public void Identical_colours_have_no_contrast()
        {
            Assert.Equal(1.0, Contrast.Ratio(Color.FromArgb(255, 40, 40, 40),
                                             Color.FromArgb(255, 40, 40, 40)), 3);
        }

        [Fact]
        public void Black_on_white_is_the_maximum()
        {
            double ratio = Contrast.Ratio(Color.Black, Color.White);
            Assert.True(ratio > 20.9 && ratio < 21.1, $"measured {ratio:F2}");
        }

        /// <summary>
        /// Alpha is the whole point: the dim label is 69% opaque, and judging it at full
        /// strength would report a contrast the user never actually sees.
        /// </summary>
        [Fact]
        public void Translucent_foregrounds_are_flattened_first()
        {
            var half = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            var onBlack = Contrast.Composite(half, Color.Black);

            Assert.InRange(onBlack.R, 126, 129);
            Assert.Equal(255, onBlack.A);

            // Opaque white would be 21:1 on black; at half alpha it is far less.
            Assert.True(Contrast.Ratio(half, Color.Black) < 12);
        }

        [Fact]
        public void A_fully_transparent_foreground_is_the_backdrop()
        {
            var invisible = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            Assert.Equal(1.0, Contrast.Ratio(invisible, Color.Black), 3);
        }
    }

    public class TaskbarThemeReadTests
    {
        /// <summary>
        /// Reading the live machine. The assertion is only that it answers rather than throws
        /// and that the palette matches — the actual setting belongs to whoever runs the tests.
        /// </summary>
        [Fact]
        public void Reads_the_current_setting_without_throwing()
        {
            var appearance = TaskbarTheme.Read();
            Assert.True(appearance is TaskbarAppearance.Dark or TaskbarAppearance.Light);
            Assert.Same(OverlayPalette.For(TaskbarTheme.Current), TaskbarTheme.Palette);
        }

        [Fact]
        public void Refreshing_when_nothing_changed_reports_no_change()
        {
            TaskbarTheme.Refresh();
            Assert.False(TaskbarTheme.Refresh());
        }
    }
}
