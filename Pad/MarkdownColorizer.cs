using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Markdown styled source (spec 2.3): each run of a line gets the look
    /// <see cref="MarkdownStyles.LookOf"/> gives it — heading sizes, bold, italic, strike, link and
    /// marker colors. Only how text is drawn changes; the document is never touched.
    /// </summary>
    internal sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        public MarkdownColorizer(MarkdownDocumentCache cache, Func<PadPalette> palette)
        {
            _cache = cache;
            _palette = palette;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var document = CurrentContext.Document;
            var md = MarkdownLineTokenizer.Tokenize(document.GetText(line), _cache.KindOf(document, line.LineNumber));
            var palette = _palette();
            double baseSize = CurrentContext.GlobalTextRunProperties.FontRenderingEmSize;

            foreach (var span in md.Spans)
            {
                var look = MarkdownStyles.LookOf(span.Style, palette);
                int start = line.Offset + span.Start;
                ChangeLinePart(start, start + span.Length, element => Apply(element, look, baseSize));
            }
        }

        private void Apply(VisualLineElement element, MdLook look, double baseSize)
        {
            var properties = element.TextRunProperties;
            if (look.Foreground is PadColor foreground) properties.SetForegroundBrush(BrushFor(foreground));
            if (look.Background is PadColor background) properties.SetBackgroundBrush(BrushFor(background));
            if (look.SizeFactor != 1) properties.SetFontRenderingEmSize(baseSize * look.SizeFactor);

            if (look.Weight != MdWeight.Keep || look.Italic)
            {
                var face = properties.Typeface;
                var weight = look.Weight switch
                {
                    MdWeight.Bold => FontWeights.Bold,
                    MdWeight.SemiBold => FontWeights.SemiBold,
                    _ => face.Weight,
                };
                properties.SetTypeface(new Typeface(face.FontFamily, look.Italic ? FontStyles.Italic : face.Style, weight, face.Stretch));
            }

            if (look.Strike)
            {
                var decorations = properties.TextDecorations == null
                    ? new TextDecorationCollection()
                    : new TextDecorationCollection(properties.TextDecorations);
                decorations.Add(TextDecorations.Strikethrough);
                decorations.Freeze();
                properties.SetTextDecorations(decorations);
            }
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
