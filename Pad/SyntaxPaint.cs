using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Paints one highlighting color from the MicaPad palette, for whole-file highlighting and for
    /// the colors inside fenced code alike: a named color takes its category's palette color; an
    /// unnamed one keeps its hue, nudged until it reads at 4.5:1 (<see cref="SyntaxColors.Resolve"/>).
    /// Bold, italic, underline and strikethrough are kept; definition backgrounds are dropped.
    /// </summary>
    internal static class SyntaxPaint
    {
        public static void Apply(VisualLineElement element, HighlightingColor color, PadPalette palette,
                                 ITextRunConstructionContext context, Func<PadColor, Brush> brushFor)
        {
            var properties = element.TextRunProperties;

            PadColor? original = color.Foreground?.GetColor(context) is Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
            if (SyntaxColors.Resolve(color.Name, original, palette) is PadColor paint)
                properties.SetForegroundBrush(brushFor(paint));

            if (color.FontWeight != null || color.FontStyle != null)
            {
                var face = properties.Typeface;
                properties.SetTypeface(new Typeface(face.FontFamily, color.FontStyle ?? face.Style, color.FontWeight ?? face.Weight, face.Stretch));
            }
            if (color.Underline == true) properties.SetTextDecorations(TextDecorations.Underline);
            if (color.Strikethrough == true) properties.SetTextDecorations(TextDecorations.Strikethrough);
        }
    }
}
