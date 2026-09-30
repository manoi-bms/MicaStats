using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How heavy a Markdown run's text is; Keep leaves the weight as it is.</summary>
    public enum MdWeight
    {
        Keep,
        SemiBold,
        Bold,
    }

    /// <summary>
    /// What one Markdown style changes on the text it covers. A null color, <see cref="MdWeight.Keep"/>,
    /// a size factor of 1 and false leave that property as it is, so styles stack: bold inside a
    /// heading keeps the heading's size.
    /// </summary>
    public readonly record struct MdLook(PadColor? Foreground, PadColor? Background, double SizeFactor, MdWeight Weight, bool Italic, bool Strike);

    /// <summary>The look of each Markdown style in a palette (spec 2.3).</summary>
    public static class MarkdownStyles
    {
        /// <summary>Heading sizes, level 1 to 6, as a multiple of the editor font.</summary>
        public static IReadOnlyList<double> HeadingSizes { get; } = new[] { 1.6, 1.4, 1.25, 1.15, 1.05, 1.0 };

        public static MdLook LookOf(MdStyle style, PadPalette palette)
        {
            if (style >= MdStyle.Heading1 && style <= MdStyle.Heading6)
                return new MdLook(palette.MdHeading, null, HeadingSizes[style - MdStyle.Heading1], MdWeight.SemiBold, false, false);

            return style switch
            {
                MdStyle.Marker => new MdLook(palette.MdMarker, null, 1, MdWeight.Keep, false, false),
                MdStyle.Bold => new MdLook(null, null, 1, MdWeight.Bold, false, false),
                MdStyle.Italic => new MdLook(null, null, 1, MdWeight.Keep, true, false),
                MdStyle.BoldItalic => new MdLook(null, null, 1, MdWeight.Bold, true, false),
                MdStyle.Strike => new MdLook(null, null, 1, MdWeight.Keep, false, true),
                MdStyle.Code => new MdLook(null, palette.MdCodeBackground, 1, MdWeight.Keep, false, false),
                MdStyle.LinkText => new MdLook(palette.MdLink, null, 1, MdWeight.Keep, false, false),
                MdStyle.ListMarker => new MdLook(palette.MdListMarker, null, 1, MdWeight.Keep, false, false),
                MdStyle.TaskDone => new MdLook(palette.MdTaskDone, null, 1, MdWeight.Keep, false, true),
                MdStyle.QuoteText => new MdLook(palette.MdQuoteText, null, 1, MdWeight.Keep, false, false),
                // CodeBlock: its background is drawn per line by the background renderer.
                _ => new MdLook(null, null, 1, MdWeight.Keep, false, false),
            };
        }
    }
}
