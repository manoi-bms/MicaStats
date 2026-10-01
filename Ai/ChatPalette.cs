using System.Windows.Media;

// UseWindowsForms puts System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The Ask window's look in code (MicaPad's dark palette): brushes, fonts and icon glyphs for
    /// the parts built at run time. AskWindow.xaml and ChatStyles.xaml write the same values.
    /// </summary>
    internal static class ChatPalette
    {
        public static readonly Brush Ink = Frozen(0xFF, 0xED, 0xED, 0xF2);
        public static readonly Brush Muted = Frozen(0x88, 0xED, 0xED, 0xF2);
        public static readonly Brush Accent = Frozen(0xFF, 0x3F, 0xD2, 0xE4);
        public static readonly Brush Amber = Frozen(0xFF, 0xE8, 0xA5, 0x3C);
        public static readonly Brush Divider = Frozen(0x1A, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Bubble = Frozen(0xFF, 0x1E, 0x2A, 0x33);
        public static readonly Brush ChipBack = Frozen(0x14, 0xFF, 0xFF, 0xFF);
        public static readonly Brush NoteBack = Frozen(0x1F, 0xE8, 0xA5, 0x3C);
        public static readonly Brush InlineCodeBack = Frozen(0x22, 0xFF, 0xFF, 0xFF);
        public static readonly Brush CodeBack = Frozen(0xFF, 0x16, 0x16, 0x1C);

        public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
        public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>Body text size; line height is about 1.5 times it.</summary>
        public const double TextSize = 14;
        public const double LineHeight = 21;
        public const double CodeSize = 12.5;

        /// <summary>A checkmark before each tool chip.</summary>
        public const string CheckGlyph = "\uE73E";

        /// <summary>The warning sign on a note.</summary>
        public const string WarningGlyph = "\uE7BA";

        /// <summary>The Copy button.</summary>
        public const string CopyGlyph = "\uE8C8";

        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
