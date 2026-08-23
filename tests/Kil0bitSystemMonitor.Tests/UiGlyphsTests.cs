using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.HardwareInfo;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Guards the icon vocabulary. A wrong codepoint does not crash — it renders as an empty
    /// box on the user's screen, which no other test would catch — so the table is asserted to
    /// be inside the Private Use Area (where icon-font glyphs live) and free of duplicates
    /// that would make two different sections look identical.
    /// </summary>
    public class UiGlyphsTests
    {
        private static IEnumerable<(string Name, string Value)> AllGlyphs() =>
            typeof(UiGlyphs)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(string) && f.Name != nameof(UiGlyphs.FontStack))
                .Select(f => (f.Name, (string)f.GetValue(null)!));

        [Fact]
        public void Table_is_not_empty()
        {
            Assert.True(AllGlyphs().Count() >= 15);
        }

        [Fact]
        public void Every_glyph_is_a_single_private_use_area_character()
        {
            foreach (var (name, value) in AllGlyphs())
            {
                Assert.True(UiGlyphs.IsIconGlyph(value),
                    $"{name} is not a single Private Use Area character (would render as a box)");
            }
        }

        [Fact]
        public void Glyphs_are_distinct_so_sections_stay_distinguishable()
        {
            var dupes = AllGlyphs()
                .GroupBy(g => g.Value)
                .Where(g => g.Count() > 1)
                .Select(g => string.Join(" == ", g.Select(x => x.Name)))
                .ToList();
            Assert.True(dupes.Count == 0, "duplicate glyphs: " + string.Join("; ", dupes));
        }

        [Fact]
        public void Font_stack_prefers_fluent_then_falls_back_to_mdl2()
        {
            Assert.Equal("Segoe Fluent Icons, Segoe MDL2 Assets", UiGlyphs.FontStack);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("AB")]
        [InlineData("A")]
        public void Non_icon_values_are_rejected(string? value)
        {
            Assert.False(UiGlyphs.IsIconGlyph(value));
        }
    }

    /// <summary>
    /// The hardware inspector renders one icon per tab and per group box. These assert the
    /// snapshot model actually carries them, so a new section cannot silently ship iconless.
    /// </summary>
    public class HardwareIconTests
    {
        [Fact]
        public void Tab_and_group_default_to_no_icon_but_accept_one()
        {
            Assert.Equal("", new HardwareTab("CPU").Icon);
            Assert.Equal("", new SpecGroup("PROCESSOR").Icon);
            Assert.Equal(UiGlyphs.Cpu, new HardwareTab("CPU", UiGlyphs.Cpu).Icon);
            Assert.Equal(UiGlyphs.Memory, new SpecGroup("CACHES", UiGlyphs.Memory).Icon);
        }

        [Fact]
        public void Icons_do_not_leak_into_the_saved_text_report()
        {
            // The report is plain text for pasting into support threads; an icon-font codepoint
            // there would show as a box in any editor.
            var snap = new HardwareSnapshot { GeneratedAt = new System.DateTime(2026, 8, 23) };
            var tab = new HardwareTab("CPU", UiGlyphs.Cpu);
            tab.Groups.Add(new SpecGroup("PROCESSOR", UiGlyphs.Cpu).Add("Name", "AMD Ryzen 9"));
            snap.Tabs.Add(tab);

            string text = HardwareReportWriter.Write(snap, "1.3.2");

            Assert.Contains("[CPU — PROCESSOR]", text);
            Assert.DoesNotContain(UiGlyphs.Cpu, text);
            Assert.All(text, ch => Assert.False(ch >= 0xE000 && ch <= 0xF8FF));
        }
    }
}
