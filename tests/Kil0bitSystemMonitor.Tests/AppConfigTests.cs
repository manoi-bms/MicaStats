using System.Collections.Generic;
using Kil0bitSystemMonitor.Models;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Every AppConfig notification triggers a full config-file rewrite and an overlay repaint,
    /// so a setter that fires on an unchanged value turns an idempotent assignment into disk I/O.
    /// These tests pin that behaviour down.
    /// </summary>
    public class AppConfigTests
    {
        private static List<string> Track(AppConfig c)
        {
            var names = new List<string>();
            c.PropertyChanged += (s, e) => names.Add(e.PropertyName ?? "");
            return names;
        }

        [Fact]
        public void Assigning_a_different_bool_notifies()
        {
            var c = new AppConfig();
            var seen = Track(c);

            c.ShowOverlay = !c.ShowOverlay;

            Assert.Contains(nameof(AppConfig.ShowOverlay), seen);
        }

        [Fact]
        public void Assigning_the_same_bool_does_not_notify()
        {
            var c = new AppConfig { ShowOverlay = true };
            var seen = Track(c);

            c.ShowOverlay = true;

            Assert.Empty(seen);
        }

        [Fact]
        public void Assigning_the_same_double_does_not_notify()
        {
            // AlignToTaskbarCenter assigns Y on every WM_SETTINGCHANGE, almost always with the
            // value it already holds. Unguarded, each of those rewrote the whole config file.
            var c = new AppConfig { Y = 1040 };
            var seen = Track(c);

            c.Y = 1040;

            Assert.Empty(seen);
        }

        [Fact]
        public void Assigning_a_different_double_notifies()
        {
            var c = new AppConfig { Y = 1040 };
            var seen = Track(c);

            c.Y = 1041;

            Assert.Contains(nameof(AppConfig.Y), seen);
        }

        [Fact]
        public void Assigning_the_same_string_does_not_notify()
        {
            var c = new AppConfig { NetworkAdapter = "Wi-Fi" };
            var seen = Track(c);

            c.NetworkAdapter = "Wi-Fi";

            Assert.Empty(seen);
        }

        [Fact]
        public void Assigning_the_same_null_string_does_not_notify()
        {
            var c = new AppConfig { NetLabelColorHex = null };
            var seen = Track(c);

            c.NetLabelColorHex = null;

            Assert.Empty(seen);
        }

        [Fact]
        public void Clearing_a_section_override_notifies_once()
        {
            var c = new AppConfig { NetLabelColorHex = "#FF0000" };
            var seen = Track(c);

            c.NetLabelColorHex = null;

            Assert.Equal(new[] { nameof(AppConfig.NetLabelColorHex) }, seen);
        }

        [Fact]
        public void Changing_a_colour_also_notifies_its_derived_property()
        {
            var c = new AppConfig { AccentColorHex = "#FFFFFF" };
            var seen = Track(c);

            c.AccentColorHex = "#FF00FF";

            Assert.Contains(nameof(AppConfig.AccentColorHex), seen);
            Assert.Contains(nameof(AppConfig.AccentColor), seen);
        }

        [Fact]
        public void Reassigning_the_same_colour_notifies_neither_the_hex_nor_the_derived_property()
        {
            var c = new AppConfig { AccentColorHex = "#FF00FF" };
            var seen = Track(c);

            c.AccentColorHex = "#FF00FF";

            Assert.Empty(seen);
        }

        [Fact]
        public void Column_spacing_is_clamped_to_its_supported_range()
        {
            var c = new AppConfig();

            c.ColumnSpacing = 999;
            Assert.Equal(20, c.ColumnSpacing);

            c.ColumnSpacing = -5;
            Assert.Equal(0, c.ColumnSpacing);
        }

        [Fact]
        public void A_clamped_assignment_that_does_not_change_the_value_does_not_notify()
        {
            var c = new AppConfig();
            c.ColumnSpacing = 20;
            var seen = Track(c);

            c.ColumnSpacing = 50; // clamps to 20, which it already is

            Assert.Empty(seen);
        }

        [Fact]
        public void Graph_history_seconds_is_clamped()
        {
            var c = new AppConfig();

            c.GraphHistorySeconds = 1;
            Assert.Equal(10, c.GraphHistorySeconds);

            c.GraphHistorySeconds = 10_000;
            Assert.Equal(300, c.GraphHistorySeconds);
        }

        [Fact]
        public void Graphs_are_off_by_default_so_the_overlay_looks_unchanged_after_upgrade()
        {
            var c = new AppConfig();
            Assert.False(c.ShowGraphs);
        }

        [Fact]
        public void Display_style_remains_the_label_width_axis()
        {
            // ShowGraphs is deliberately separate from DisplayStyle so compact labels and graphs
            // can be combined.
            var c = new AppConfig { DisplayStyle = "Compact", ShowGraphs = true };

            Assert.Equal("Compact", c.DisplayStyle);
            Assert.True(c.ShowGraphs);
        }

        [Fact]
        public void New_configs_carry_the_current_schema_version()
        {
            var c = new AppConfig();
            Assert.Equal(AppConfig.CurrentVersion, c.ConfigVersion);
        }

        [Fact]
        public void Hover_panels_default_on()
        {
            var c = new AppConfig();
            Assert.True(c.HoverPanels);
        }

        [Fact]
        public void Stacked_taskbar_defaults_on_so_old_configs_inherit_the_new_look()
        {
            // Configs saved before the field existed deserialize without it and must land on
            // true, which is what makes the iStat layout the default after an upgrade.
            var c = new AppConfig();
            Assert.True(c.StackedTaskbar);

            var seen = Track(c);
            c.StackedTaskbar = false;
            Assert.Contains(nameof(AppConfig.StackedTaskbar), seen);
        }
    }
}
