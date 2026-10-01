using System.Windows.Media;

// UseWindowsForms puts System.Drawing in scope; this name exists in both.
using FontFamily = System.Windows.Media.FontFamily;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The Ask window's fixed look in code: fonts, sizes and icon glyphs for the parts built at run
    /// time. Colors are not here: they come from <c>AskPalette</c> as <c>Ask.*</c> brushes, which
    /// code reads with <c>SetResourceReference</c> so a theme switch repaints existing turns.
    /// </summary>
    internal static class ChatPalette
    {
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
    }
}
