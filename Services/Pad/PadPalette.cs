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

        /// <summary>The soft box behind each occurrence of the selected word (below find matches).</summary>
        public PadColor Occurrence { get; private init; }

        /// <summary>The fill of a stored-credential pill.</summary>
        public PadColor PillBack { get; private init; }

        /// <summary>The outline of a stored-credential pill.</summary>
        public PadColor PillBorder { get; private init; }

        /// <summary>The lock and label of a pill for a stored credential.</summary>
        public PadColor PillText { get; private init; }

        /// <summary>The lock and label of a pill whose credential is missing.</summary>
        public PadColor PillMissingText { get; private init; }

        /// <summary>Syntax colors (spec 2.2): one per category, each at least 4.5:1 on the background.</summary>
        public PadColor SyntaxComment { get; private init; }
        public PadColor SyntaxString { get; private init; }
        public PadColor SyntaxKeyword { get; private init; }
        public PadColor SyntaxNumber { get; private init; }
        public PadColor SyntaxType { get; private init; }
        public PadColor SyntaxPreprocessor { get; private init; }
        public PadColor SyntaxTag { get; private init; }
        public PadColor SyntaxAttribute { get; private init; }
        public PadColor SyntaxOperator { get; private init; }
        /// <summary>Log levels (MicaPad's Log.xshd).</summary>
        public PadColor LogError { get; private init; }
        public PadColor LogWarning { get; private init; }
        public PadColor LogInfo { get; private init; }
        public PadColor LogDebug { get; private init; }
        /// <summary>Added and removed lines (Diff files; Part 5's history compare).</summary>
        public PadColor DiffAdded { get; private init; }
        public PadColor DiffRemoved { get; private init; }
        /// <summary>Markdown styled source (spec 2.3).</summary>
        public PadColor MdHeading { get; private init; }
        /// <summary>The dimmed # ** ` > markers.</summary>
        public PadColor MdMarker { get; private init; }
        /// <summary>Behind inline code and fenced blocks.</summary>
        public PadColor MdCodeBackground { get; private init; }
        public PadColor MdLink { get; private init; }
        public PadColor MdQuoteBar { get; private init; }
        public PadColor MdQuoteText { get; private init; }
        public PadColor MdListMarker { get; private init; }
        public PadColor MdTaskDone { get; private init; }
        public PadColor MdRule { get; private init; }

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
            Occurrence = PadColor.Parse("#2E3FD2E4"),
            PillBack = PadColor.Parse("#12303A"),
            PillBorder = PadColor.Parse("#2A6A75"),
            PillText = PadColor.Parse("#3FD2E4"),
            PillMissingText = PadColor.Parse("#9A9AA3"),
            SyntaxComment = PadColor.Parse("#6A9955"),
            SyntaxString = PadColor.Parse("#CE9178"),
            SyntaxKeyword = PadColor.Parse("#569CD6"),
            SyntaxNumber = PadColor.Parse("#B5CEA8"),
            SyntaxType = PadColor.Parse("#4EC9B0"),
            SyntaxPreprocessor = PadColor.Parse("#C586C0"),
            SyntaxTag = PadColor.Parse("#4FC1FF"),
            SyntaxAttribute = PadColor.Parse("#9CDCFE"),
            SyntaxOperator = PadColor.Parse("#D4D4D4"),
            LogError = PadColor.Parse("#FF6B6B"),
            LogWarning = PadColor.Parse("#E8A53C"),
            LogInfo = PadColor.Parse("#3FD2E4"),
            LogDebug = PadColor.Parse("#88EDEDF2"),
            DiffAdded = PadColor.Parse("#6BD968"),
            DiffRemoved = PadColor.Parse("#FF7B72"),
            MdHeading = PadColor.Parse("#7FE3F0"),
            MdMarker = PadColor.Parse("#66EDEDF2"),
            MdCodeBackground = PadColor.Parse("#1B1B24"),
            MdLink = PadColor.Parse("#3FD2E4"),
            MdQuoteBar = PadColor.Parse("#663FD2E4"),
            MdQuoteText = PadColor.Parse("#B0EDEDF2"),
            MdListMarker = PadColor.Parse("#3FD2E4"),
            MdTaskDone = PadColor.Parse("#88EDEDF2"),
            MdRule = PadColor.Parse("#33FFFFFF"),
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
            Occurrence = PadColor.Parse("#2406707C"),
            PillBack = PadColor.Parse("#DCEFF1"),
            PillBorder = PadColor.Parse("#9FD0D6"),
            PillText = PadColor.Parse("#06707C"),
            PillMissingText = PadColor.Parse("#5C5C66"),
            SyntaxComment = PadColor.Parse("#1E7A1E"),
            SyntaxString = PadColor.Parse("#A31515"),
            SyntaxKeyword = PadColor.Parse("#0000E0"),
            SyntaxNumber = PadColor.Parse("#07704A"),
            SyntaxType = PadColor.Parse("#1D6B80"),
            SyntaxPreprocessor = PadColor.Parse("#8A00B0"),
            SyntaxTag = PadColor.Parse("#800000"),
            SyntaxAttribute = PadColor.Parse("#B00000"),
            SyntaxOperator = PadColor.Parse("#3B3B3B"),
            LogError = PadColor.Parse("#C62828"),
            LogWarning = PadColor.Parse("#8A5200"),
            LogInfo = PadColor.Parse("#06707C"),
            LogDebug = PadColor.Parse("#666670"),
            DiffAdded = PadColor.Parse("#1A7F37"),
            DiffRemoved = PadColor.Parse("#C62828"),
            MdHeading = PadColor.Parse("#0B4F59"),
            MdMarker = PadColor.Parse("#8A8A94"),
            MdCodeBackground = PadColor.Parse("#EEEEF3"),
            MdLink = PadColor.Parse("#06707C"),
            MdQuoteBar = PadColor.Parse("#6606707C"),
            MdQuoteText = PadColor.Parse("#55555F"),
            MdListMarker = PadColor.Parse("#06707C"),
            MdTaskDone = PadColor.Parse("#666670"),
            MdRule = PadColor.Parse("#26000000"),
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
            Pair(nameof(Occurrence), Occurrence),
            Pair(nameof(PillBack), PillBack),
            Pair(nameof(PillBorder), PillBorder),
            Pair(nameof(PillText), PillText),
            Pair(nameof(PillMissingText), PillMissingText),
            Pair(nameof(SyntaxComment), SyntaxComment),
            Pair(nameof(SyntaxString), SyntaxString),
            Pair(nameof(SyntaxKeyword), SyntaxKeyword),
            Pair(nameof(SyntaxNumber), SyntaxNumber),
            Pair(nameof(SyntaxType), SyntaxType),
            Pair(nameof(SyntaxPreprocessor), SyntaxPreprocessor),
            Pair(nameof(SyntaxTag), SyntaxTag),
            Pair(nameof(SyntaxAttribute), SyntaxAttribute),
            Pair(nameof(SyntaxOperator), SyntaxOperator),
            Pair(nameof(LogError), LogError),
            Pair(nameof(LogWarning), LogWarning),
            Pair(nameof(LogInfo), LogInfo),
            Pair(nameof(LogDebug), LogDebug),
            Pair(nameof(DiffAdded), DiffAdded),
            Pair(nameof(DiffRemoved), DiffRemoved),
            Pair(nameof(MdHeading), MdHeading),
            Pair(nameof(MdMarker), MdMarker),
            Pair(nameof(MdCodeBackground), MdCodeBackground),
            Pair(nameof(MdLink), MdLink),
            Pair(nameof(MdQuoteBar), MdQuoteBar),
            Pair(nameof(MdQuoteText), MdQuoteText),
            Pair(nameof(MdListMarker), MdListMarker),
            Pair(nameof(MdTaskDone), MdTaskDone),
            Pair(nameof(MdRule), MdRule),
        };

        private static KeyValuePair<string, PadColor> Pair(string name, PadColor color) => new("Pad." + name, color);
    }
}
