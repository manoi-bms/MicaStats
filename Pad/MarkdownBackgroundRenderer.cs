using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// What Markdown draws behind the text: shading across fenced code lines, a bar at the left of
    /// quotes, and a line through horizontal rules. Only visible lines are drawn.
    /// </summary>
    internal sealed class MarkdownBackgroundRenderer : IBackgroundRenderer
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        public MarkdownBackgroundRenderer(MarkdownDocumentCache cache, Func<PadPalette> palette)
        {
            _cache = cache;
            _palette = palette;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (document == null || !textView.VisualLinesValid) return;
            var palette = _palette();
            double width = textView.ActualWidth;

            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine;
                var block = MarkdownLineTokenizer.BlockOf(document.GetText(line), _cache.KindOf(document, line.LineNumber));
                double top = visual.VisualTop - textView.VerticalOffset;
                double height = visual.Height;

                switch (block)
                {
                    case MdBlock.Fence:
                        drawingContext.DrawRectangle(BrushFor(palette.MdCodeBackground), null, new Rect(0, top, width, height));
                        break;
                    case MdBlock.Quote:
                        drawingContext.DrawRectangle(BrushFor(palette.MdQuoteBar), null, new Rect(0, top, 3, height));
                        break;
                    case MdBlock.Rule:
                        double y = Math.Round(top + height / 2) + 0.5;
                        drawingContext.DrawLine(new Pen(BrushFor(palette.MdRule), 1), new Point(0, y), new Point(width, y));
                        break;
                }
            }
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
