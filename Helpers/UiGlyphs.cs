using System;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// The icon vocabulary for MicaStats' panels, in one place.
    ///
    /// <para>
    /// Glyphs come from <b>Segoe Fluent Icons</b> — the Windows 11 system icon font — with
    /// <b>Segoe MDL2 Assets</b> as the Windows 10 fallback, matching the convention the settings
    /// window already uses. Every codepoint here was chosen by rendering the font's own contact
    /// sheets and picking by eye, so none of them can turn out to be a missing-glyph box.
    /// </para>
    ///
    /// <para>
    /// Centralised rather than typed as <c>&amp;#xE950;</c> literals at each use site: the same
    /// section must carry the same icon in the stats panel, the hardware inspector and anywhere
    /// added later, and a single table is the only way that stays true.
    /// </para>
    ///
    /// <para>
    /// The glyphs are built from hex codepoints rather than pasted characters deliberately.
    /// A pasted Private Use Area character renders as nothing in an editor — the source would
    /// read <c>Cpu = ""</c> — and its meaning would depend on the file's encoding surviving
    /// every tool that touches it. Hex keeps the source ASCII, greppable and unambiguous.
    /// XAML binds these through <c>{x:Static}</c>; C# builders reference them directly.
    /// </para>
    /// </summary>
    public static class UiGlyphs
    {
        /// <summary>Font stack for any element rendering these glyphs. Fluent first, MDL2 for Windows 10.</summary>
        public const string FontStack = "Segoe Fluent Icons, Segoe MDL2 Assets";

        private static string Glyph(int codepoint) => char.ConvertFromUtf32(codepoint);

        // ----- Telemetry sections (shared by the panel cards and the inspector tabs) ---------

        /// <summary>U+E950 — processor die with pins.</summary>
        public static readonly string Cpu = Glyph(0xE950);

        /// <summary>U+E964 — memory module.</summary>
        public static readonly string Memory = Glyph(0xE964);

        /// <summary>U+E7F4 — display, standing in for the graphics adapter.</summary>
        public static readonly string Gpu = Glyph(0xE7F4);

        /// <summary>U+E8CB — paired up/down arrows, the ↑/↓ metaphor the network module uses.</summary>
        public static readonly string Network = Glyph(0xE8CB);

        /// <summary>U+EDA2 — hard drive.</summary>
        public static readonly string Disk = Glyph(0xEDA2);

        /// <summary>U+E9CA — thermometer.</summary>
        public static readonly string Temperature = Glyph(0xE9CA);

        /// <summary>U+E8FD — bulleted list, for the process table.</summary>
        public static readonly string Processes = Glyph(0xE8FD);

        // ----- Structural / inspector sections ----------------------------------------------

        /// <summary>U+E977 — tower and monitor: "this machine", for identity and mainboard.</summary>
        public static readonly string Machine = Glyph(0xE977);

        /// <summary>U+E81E — stacked layers, for core topology.</summary>
        public static readonly string Layers = Glyph(0xE81E);

        /// <summary>U+E917 — timer, for clock speeds.</summary>
        public static readonly string Clock = Glyph(0xE917);

        /// <summary>U+E943 — curly braces, for instruction-set extensions.</summary>
        public static readonly string Code = Glyph(0xE943);

        /// <summary>U+E9E9 — sliders, for firmware settings.</summary>
        public static readonly string Firmware = Glyph(0xE9E9);

        /// <summary>U+E713 — gear, for the operating-system section.</summary>
        public static readonly string System = Glyph(0xE713);

        /// <summary>U+E770 — bare display, for the monitor/mode section.</summary>
        public static readonly string Display = Glyph(0xE770);

        /// <summary>U+E9D9 — pulse trace, for the live environment section.</summary>
        public static readonly string Activity = Glyph(0xE9D9);

        /// <summary>U+E946 — information circle.</summary>
        public static readonly string Info = Glyph(0xE946);

        /// <summary>U+E7BA — warning triangle, shown when a section could not be read.</summary>
        public static readonly string Warning = Glyph(0xE7BA);

        /// <summary>U+E9F9 — document with a chart, for the saved hardware report.</summary>
        public static readonly string Report = Glyph(0xE9F9);

        /// <summary>U+ED25 — folder, for opening the data directory.</summary>
        public static readonly string Folder = Glyph(0xED25);

        /// <summary>U+E72C — refresh arrow.</summary>
        public static readonly string Refresh = Glyph(0xE72C);

        /// <summary>
        /// True when <paramref name="glyph"/> is a single character in the Unicode Private Use
        /// Area (U+E000..U+F8FF), where every icon-font glyph lives. A codepoint outside it is a
        /// typo that would render as an empty box, so the icon tables are asserted against this.
        /// </summary>
        public static bool IsIconGlyph(string? glyph) =>
            glyph is { Length: 1 } && glyph[0] >= 0xE000 && glyph[0] <= 0xF8FF;
    }
}
