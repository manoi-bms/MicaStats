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

    /// <summary>Where a Markdown run sits against the line's baseline.</summary>
    public enum MdBaseline
    {
        Normal,
        Superscript,
        Subscript,
    }

    /// <summary>
    /// What one Markdown style changes on the text it covers. A null color, <see cref="MdWeight.Keep"/>,
    /// a size factor of 1 and false leave that property as it is, so styles stack: bold inside a
    /// heading keeps the heading's size. Mono switches to the editor's monospace font while the
    /// reading font is on; Dotted adds a dotted underline.
    /// </summary>
    public readonly record struct MdLook(PadColor? Foreground, PadColor? Background, double SizeFactor, MdWeight Weight, bool Italic, bool Strike,
                                         bool Mono = false, MdBaseline Baseline = MdBaseline.Normal, bool Dotted = false);

    /// <summary>The look of each Markdown style in a palette (spec 2.3).</summary>
    public static class MarkdownStyles
    {
        /// <summary>Heading sizes, level 1 to 6, as a multiple of the editor font.</summary>
        public static IReadOnlyList<double> HeadingSizes { get; } = new[] { 1.6, 1.4, 1.25, 1.15, 1.05, 1.0 };

        /// <summary>The size of sub- and superscript and footnote references, as a multiple of the text around them.</summary>
        public const double SmallSize = 0.75;

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
                MdStyle.Code => new MdLook(palette.MdCode, palette.MdCodeBackground, 1, MdWeight.Keep, false, false, Mono: true),
                MdStyle.TableHeader => new MdLook(null, null, 1, MdWeight.Bold, false, false),
                MdStyle.FootnoteRef => new MdLook(palette.MdLink, null, SmallSize, MdWeight.Keep, false, false, Baseline: MdBaseline.Superscript),
                MdStyle.Subscript => new MdLook(null, null, SmallSize, MdWeight.Keep, false, false, Baseline: MdBaseline.Subscript),
                MdStyle.Superscript => new MdLook(null, null, SmallSize, MdWeight.Keep, false, false, Baseline: MdBaseline.Superscript),
                MdStyle.KbdText => new MdLook(null, palette.MdKbdBackground, 1, MdWeight.Keep, false, false, Mono: true),
                MdStyle.Abbreviation => new MdLook(null, null, 1, MdWeight.Keep, false, false, Dotted: true),
                MdStyle.MathText => new MdLook(palette.MdMath, null, 1, MdWeight.Keep, false, false),
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
