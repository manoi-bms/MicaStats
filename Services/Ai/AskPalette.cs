using System.Collections.Generic;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Every color the Ask MicaStats window paints, for one theme. XAML and code read them as
    /// <c>Ask.&lt;Name&gt;</c> brushes (see <see cref="Resources"/>). Its own theme, stored in
    /// <c>AppConfig.AskTheme</c>: switching it never changes MicaPad, and the MicaPad theme never
    /// changes it. The theme names "Dark" and "Light" are shared (<see cref="PadThemes"/>).
    /// </summary>
    public sealed class AskPalette
    {
        private AskPalette() { }

        /// <summary>The theme name, <see cref="PadThemes.Dark"/> or <see cref="PadThemes.Light"/>.</summary>
        public string Name { get; private init; } = PadThemes.Dark;

        /// <summary>True for the dark theme; picks the ModernWpf theme and the title bar.</summary>
        public bool IsDark { get; private init; }

        /// <summary>Window and transcript.</summary>
        public PadColor Background { get; private init; }
        /// <summary>Header and composer strip.</summary>
        public PadColor Chrome { get; private init; }
        public PadColor Divider { get; private init; }
        /// <summary>Text.</summary>
        public PadColor Ink { get; private init; }
        public PadColor Muted { get; private init; }
        /// <summary>Links, pills, the chip check, the focus border.</summary>
        public PadColor Accent { get; private init; }
        /// <summary>Composer box and prompt chips.</summary>
        public PadColor Surface { get; private init; }
        public PadColor Border { get; private init; }
        /// <summary>Your message.</summary>
        public PadColor Bubble { get; private init; }
        public PadColor ChipBack { get; private init; }
        /// <summary>The note's icon.</summary>
        public PadColor Amber { get; private init; }
        public PadColor NoteBack { get; private init; }
        public PadColor InlineCodeBack { get; private init; }
        public PadColor CodeBack { get; private init; }
        public PadColor SendBack { get; private init; }
        public PadColor SendGlyph { get; private init; }
        public PadColor StopBack { get; private init; }
        /// <summary>A suggested action, resting, under the mouse, and pressed.</summary>
        public PadColor PillBack { get; private init; }
        public PadColor PillHover { get; private init; }
        public PadColor PillPressed { get; private init; }
        public PadColor Selection { get; private init; }
        /// <summary>Flat and icon buttons under the mouse, and pressed.</summary>
        public PadColor Hover { get; private init; }
        public PadColor Pressed { get; private init; }
        /// <summary>A prompt chip while pressed.</summary>
        public PadColor SurfacePressed { get; private init; }

        /// <summary>The original dark look.</summary>
        public static AskPalette Dark { get; } = new()
        {
            Name = PadThemes.Dark,
            IsDark = true,
            Background = PadColor.Parse("#0E0E13"),
            Chrome = PadColor.Parse("#141419"),
            Divider = PadColor.Parse("#1AFFFFFF"),
            Ink = PadColor.Parse("#EDEDF2"),
            Muted = PadColor.Parse("#88EDEDF2"),
            Accent = PadColor.Parse("#3FD2E4"),
            Surface = PadColor.Parse("#1C1C24"),
            Border = PadColor.Parse("#33FFFFFF"),
            Bubble = PadColor.Parse("#1E2A33"),
            ChipBack = PadColor.Parse("#14FFFFFF"),
            Amber = PadColor.Parse("#E8A53C"),
            NoteBack = PadColor.Parse("#1FE8A53C"),
            InlineCodeBack = PadColor.Parse("#22FFFFFF"),
            CodeBack = PadColor.Parse("#16161C"),
            SendBack = PadColor.Parse("#3FD2E4"),
            SendGlyph = PadColor.Parse("#0E0E13"),
            StopBack = PadColor.Parse("#33FFFFFF"),
            PillBack = PadColor.Parse("#1A3FD2E4"),
            PillHover = PadColor.Parse("#333FD2E4"),
            PillPressed = PadColor.Parse("#4D3FD2E4"),
            Selection = PadColor.Parse("#553FD2E4"),
            Hover = PadColor.Parse("#14FFFFFF"),
            Pressed = PadColor.Parse("#24FFFFFF"),
            SurfacePressed = PadColor.Parse("#24242E"),
        };

        public static AskPalette Light { get; } = new()
        {
            Name = PadThemes.Light,
            IsDark = false,
            Background = PadColor.Parse("#FBFBFD"),
            Chrome = PadColor.Parse("#F0F0F4"),
            Divider = PadColor.Parse("#1A000000"),
            Ink = PadColor.Parse("#1B1B1F"),
            Muted = PadColor.Parse("#666670"),
            Accent = PadColor.Parse("#06707C"),
            Surface = PadColor.Parse("#FFFFFF"),
            Border = PadColor.Parse("#26000000"),
            Bubble = PadColor.Parse("#E3F1F3"),
            ChipBack = PadColor.Parse("#0F000000"),
            Amber = PadColor.Parse("#A05A00"),
            NoteBack = PadColor.Parse("#1FE8A53C"),
            InlineCodeBack = PadColor.Parse("#12000000"),
            CodeBack = PadColor.Parse("#F0F0F4"),
            SendBack = PadColor.Parse("#06707C"),
            SendGlyph = PadColor.Parse("#FFFFFF"),
            StopBack = PadColor.Parse("#1A000000"),
            PillBack = PadColor.Parse("#1406707C"),
            PillHover = PadColor.Parse("#2006707C"),
            PillPressed = PadColor.Parse("#3306707C"),
            Selection = PadColor.Parse("#3306707C"),
            Hover = PadColor.Parse("#0F000000"),
            Pressed = PadColor.Parse("#1A000000"),
            SurfacePressed = PadColor.Parse("#E8E8EE"),
        };

        /// <summary>The palette for a stored theme name; see <see cref="PadThemes.Normalize"/>.</summary>
        public static AskPalette For(string? theme) => PadThemes.Normalize(theme) == PadThemes.Light ? Light : Dark;

        /// <summary>Every color under the resource key the Ask XAML uses for it, <c>Ask.&lt;property name&gt;</c>.</summary>
        public IReadOnlyList<KeyValuePair<string, PadColor>> Resources() => new[]
        {
            Pair(nameof(Background), Background),
            Pair(nameof(Chrome), Chrome),
            Pair(nameof(Divider), Divider),
            Pair(nameof(Ink), Ink),
            Pair(nameof(Muted), Muted),
            Pair(nameof(Accent), Accent),
            Pair(nameof(Surface), Surface),
            Pair(nameof(Border), Border),
            Pair(nameof(Bubble), Bubble),
            Pair(nameof(ChipBack), ChipBack),
            Pair(nameof(Amber), Amber),
            Pair(nameof(NoteBack), NoteBack),
            Pair(nameof(InlineCodeBack), InlineCodeBack),
            Pair(nameof(CodeBack), CodeBack),
            Pair(nameof(SendBack), SendBack),
            Pair(nameof(SendGlyph), SendGlyph),
            Pair(nameof(StopBack), StopBack),
            Pair(nameof(PillBack), PillBack),
            Pair(nameof(PillHover), PillHover),
            Pair(nameof(PillPressed), PillPressed),
            Pair(nameof(Selection), Selection),
            Pair(nameof(Hover), Hover),
            Pair(nameof(Pressed), Pressed),
            Pair(nameof(SurfacePressed), SurfacePressed),
        };

        private static KeyValuePair<string, PadColor> Pair(string name, PadColor color) => new("Ask." + name, color);
    }
}
