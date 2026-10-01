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
    /// What Markdown draws behind the text: shading across fenced code and front matter, a bar per
    /// quote level at the left (in the callout's color, over a tint of it, for a Wiki.js callout), and
    /// a line through horizontal rules. Only visible lines are drawn.
    /// </summary>
    internal sealed class MarkdownBackgroundRenderer : IBackgroundRenderer
    {
        /// <summary>A quote bar's width and the distance between the bars of nested quotes.</summary>
        internal const double QuoteBarWidth = 3;
        internal const double QuoteIndent = 8;

        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Action<Exception> _onFailure;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        /// <param name="onFailure">Told when drawing fails; nothing more is drawn this time.</param>
        public MarkdownBackgroundRenderer(MarkdownDocumentCache cache, Func<PadPalette> palette, Action<Exception>? onFailure = null)
        {
            _cache = cache;
            _palette = palette;
            _onFailure = onFailure ?? (_ => { });
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            try
            {
                DrawVisibleLines(textView, drawingContext);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        private void DrawVisibleLines(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (document == null || !textView.VisualLinesValid) return;
            var palette = _palette();
            double width = textView.ActualWidth;

            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine;
                var facts = _cache.FactsOf(document, line.LineNumber);
                string text = document.GetText(line);
                var block = MarkdownLineTokenizer.BlockOf(text, facts);
                double top = visual.VisualTop - textView.VerticalOffset;
                double height = visual.Height;

                switch (block)
                {
                    case MdBlock.Fence:
                    case MdBlock.FrontMatter:
                        drawingContext.DrawRectangle(BrushFor(palette.MdCodeBackground), null, new Rect(0, top, width, height));
                        break;
                    case MdBlock.Quote:
                        var bar = palette.MdQuoteBar;
                        if (facts.Callout != MdCallout.None)
                        {
                            bar = MarkdownStyles.CalloutColor(facts.Callout, palette);
                            drawingContext.DrawRectangle(BrushFor(bar with { A = MarkdownStyles.CalloutTintAlpha }), null, new Rect(0, top, width, height));
                        }
                        int depth = Math.Max(1, MarkdownLineTokenizer.QuoteDepth(text));
                        for (int level = 0; level < depth; level++)
                            drawingContext.DrawRectangle(BrushFor(bar), null, new Rect(level * QuoteIndent, top, QuoteBarWidth, height));
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
