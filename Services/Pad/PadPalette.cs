using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The names MicaPad's theme is stored under in <c>AppConfig.PadTheme</c>.</summary>
    public static class PadThemes
    {
        public const string Dark = "Dark";
        public const string Light = "Light";

        /// <summary>"Light" in any letter case, with or without spaces, is Light; anything else, null included, is Dark.</summary>
        public static string Normalize(string? value) =>
            string.Equals(value?.Trim(), Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
    }

    /// <summary>
    /// Every color MicaPad paints, for one theme. XAML reads them as <c>Pad.&lt;Name&gt;</c> brushes
    /// (see <see cref="Resources"/>); code that paints directly reads the properties. Only MicaPad
    /// uses this: the rest of MicaStats keeps its own colors.
    /// </summary>
    public sealed class PadPalette
    {
        private PadPalette() { }

        /// <summary>The theme name, <see cref="PadThemes.Dark"/> or <see cref="PadThemes.Light"/>.</summary>
        public string Name { get; private init; } = PadThemes.Dark;

        /// <summary>True for the dark theme; picks the ModernWpf theme and the title bar.</summary>
        public bool IsDark { get; private init; }

        /// <summary>Window, editor and history preview background.</summary>
        public PadColor Background { get; private init; }
        /// <summary>Tab strip, status bar, find bar and history pane.</summary>
        public PadColor Chrome { get; private init; }
        /// <summary>Rename box, closed-notes list, go-to-line box.</summary>
        public PadColor Popup { get; private init; }
        public PadColor PopupBorder { get; private init; }
        /// <summary>Thin separators between the bars.</summary>
        public PadColor Border { get; private init; }
        /// <summary>Flat button under the mouse.</summary>
        public PadColor Hover { get; private init; }
        /// <summary>The note's text.</summary>
        public PadColor Text { get; private init; }
        /// <summary>Default text of the window's own controls.</summary>
        public PadColor WindowText { get; private init; }
        /// <summary>Flat buttons and the history preview's text.</summary>
        public PadColor TextSoft { get; private init; }
        /// <summary>Secondary text: status bar, counts, hints.</summary>
        public PadColor Muted { get; private init; }
        /// <summary>Unsaved-file dot, primary actions, caret.</summary>
        public PadColor Accent { get; private init; }
        /// <summary>"Not saved — retrying" and find errors.</summary>
        public PadColor AlertRed { get; private init; }
        public PadColor TabHover { get; private init; }
        public PadColor TabActive { get; private init; }
        public PadColor TabTitle { get; private init; }
        public PadColor TabActiveTitle { get; private init; }
        /// <summary>The info bar that asks about files.</summary>
        public PadColor InfoBar { get; private init; }
        public PadColor InfoBarBorder { get; private init; }
        /// <summary>The "Viewing 14:30" banner above a history preview.</summary>
        public PadColor Banner { get; private init; }
        public PadColor BannerBorder { get; private init; }
        public PadColor LineNumbers { get; private init; }
        public PadColor Selection { get; private init; }
        public PadColor Caret { get; private init; }
        public PadColor CurrentLine { get; private init; }
        /// <summary>The box behind each find match.</summary>
        public PadColor FindMatch { get; private init; }

        /// <summary>Today's MicaPad colors.</summary>
        public static PadPalette Dark { get; } = new()
        {
            Name = PadThemes.Dark,
            IsDark = true,
            Background = PadColor.Parse("#0E0E13"),
            Chrome = PadColor.Parse("#141419"),
            Popup = PadColor.Parse("#1C1C24"),
            PopupBorder = PadColor.Parse("#33FFFFFF"),
            Border = PadColor.Parse("#1AFFFFFF"),
            Hover = PadColor.Parse("#1FFFFFFF"),
            Text = PadColor.Parse("#EDEDF2"),
            WindowText = PadColor.Parse("#E9EDEDF2"),
            TextSoft = PadColor.Parse("#CFEDEDF2"),
            Muted = PadColor.Parse("#88EDEDF2"),
            Accent = PadColor.Parse("#3FD2E4"),
            AlertRed = PadColor.Parse("#FF6B6B"),
            TabHover = PadColor.Parse("#16161C"),
            TabActive = PadColor.Parse("#1C1C24"),
            TabTitle = PadColor.Parse("#99EDEDF2"),
            TabActiveTitle = PadColor.Parse("#EDEDF2"),
            InfoBar = PadColor.Parse("#2A2410"),
            InfoBarBorder = PadColor.Parse("#55FFC857"),
            Banner = PadColor.Parse("#10283A"),
            BannerBorder = PadColor.Parse("#333FD2E4"),
            LineNumbers = PadColor.Parse("#66EDEDF2"),
            Selection = PadColor.Parse("#553FD2E4"),
            Caret = PadColor.Parse("#3FD2E4"),
            CurrentLine = PadColor.Parse("#0FFFFFFF"),
            FindMatch = PadColor.Parse("#40FFC857"),
        };

        /// <summary>
        /// Off-white page, near-black text. The accent is a darker cyan than the dark theme's,
        /// which is unreadable on white.
        /// </summary>
        public static PadPalette Light { get; } = new()
        {
            Name = PadThemes.Light,
            IsDark = false,
            Background = PadColor.Parse("#FBFBFD"),
            Chrome = PadColor.Parse("#F0F0F4"),
            Popup = PadColor.Parse("#FFFFFF"),
            PopupBorder = PadColor.Parse("#26000000"),
            Border = PadColor.Parse("#1A000000"),
            Hover = PadColor.Parse("#14000000"),
            Text = PadColor.Parse("#1B1B1F"),
            WindowText = PadColor.Parse("#1B1B1F"),
            TextSoft = PadColor.Parse("#2E2E36"),
            Muted = PadColor.Parse("#666670"),
            Accent = PadColor.Parse("#06707C"),
            AlertRed = PadColor.Parse("#C62828"),
            TabHover = PadColor.Parse("#E6E6EC"),
            TabActive = PadColor.Parse("#FFFFFF"),
            TabTitle = PadColor.Parse("#55555F"),
            TabActiveTitle = PadColor.Parse("#1B1B1F"),
            InfoBar = PadColor.Parse("#FFF4D6"),
            InfoBarBorder = PadColor.Parse("#80E0A800"),
            Banner = PadColor.Parse("#E3F4F7"),
            BannerBorder = PadColor.Parse("#4006707C"),
            LineNumbers = PadColor.Parse("#8A8A94"),
            Selection = PadColor.Parse("#3306707C"),
            Caret = PadColor.Parse("#06707C"),
            CurrentLine = PadColor.Parse("#0A000000"),
            FindMatch = PadColor.Parse("#66FFC857"),
        };

        /// <summary>The palette for a stored theme name; see <see cref="PadThemes.Normalize"/>.</summary>
        public static PadPalette For(string? theme) => PadThemes.Normalize(theme) == PadThemes.Light ? Light : Dark;

        /// <summary>Every color under the resource key MicaPad's XAML uses for it, <c>Pad.&lt;property name&gt;</c>.</summary>
        public IReadOnlyList<KeyValuePair<string, PadColor>> Resources() => new[]
        {
            Pair(nameof(Background), Background),
            Pair(nameof(Chrome), Chrome),
            Pair(nameof(Popup), Popup),
            Pair(nameof(PopupBorder), PopupBorder),
            Pair(nameof(Border), Border),
            Pair(nameof(Hover), Hover),
            Pair(nameof(Text), Text),
            Pair(nameof(WindowText), WindowText),
            Pair(nameof(TextSoft), TextSoft),
            Pair(nameof(Muted), Muted),
            Pair(nameof(Accent), Accent),
            Pair(nameof(AlertRed), AlertRed),
            Pair(nameof(TabHover), TabHover),
            Pair(nameof(TabActive), TabActive),
            Pair(nameof(TabTitle), TabTitle),
            Pair(nameof(TabActiveTitle), TabActiveTitle),
            Pair(nameof(InfoBar), InfoBar),
            Pair(nameof(InfoBarBorder), InfoBarBorder),
            Pair(nameof(Banner), Banner),
            Pair(nameof(BannerBorder), BannerBorder),
            Pair(nameof(LineNumbers), LineNumbers),
            Pair(nameof(Selection), Selection),
            Pair(nameof(Caret), Caret),
            Pair(nameof(CurrentLine), CurrentLine),
            Pair(nameof(FindMatch), FindMatch),
        };

        private static KeyValuePair<string, PadColor> Pair(string name, PadColor color) => new("Pad." + name, color);
    }
}
