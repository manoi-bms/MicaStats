using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Paints a soft amber box behind every find match, in the selection layer so the selection
    /// still shows on top. Draws only the matches inside the visible lines, found by binary
    /// search, so ten thousand matches cost no more than the dozen on screen.
    /// </summary>
    internal sealed class MatchHighlighter : IBackgroundRenderer
    {
        private static readonly Brush Fill = CreateFill();

        /// <summary>The matches to paint, sorted by offset.</summary>
        public IReadOnlyList<FindMatch> Matches { get; set; } = Array.Empty<FindMatch>();

        /// <summary>The layer painted in: the selection layer, so the selection stays on top.</summary>
        public KnownLayer Layer => KnownLayer.Selection;

        /// <summary>Paints the matches that fall inside the visible lines.</summary>
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Matches.Count == 0 || !textView.VisualLinesValid || textView.Document == null) return;
            var lines = textView.VisualLines;
            if (lines.Count == 0) return;

            int start = lines[0].FirstDocumentLine.Offset;
            int end = lines[lines.Count - 1].LastDocumentLine.EndOffset;
            int length = textView.Document.TextLength;

            for (int i = FindReplaceEngine.FirstAtOrAfter(Matches, start); i < Matches.Count && Matches[i].Offset <= end; i++)
            {
                var match = Matches[i];
                // Stale matches after an edit are redrawn on the next refresh; never paint past the text.
                if (match.Offset + match.Length > length) break;

                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
                builder.AddSegment(textView, new TextSegment { StartOffset = match.Offset, Length = match.Length });
                var geometry = builder.CreateGeometry();
                if (geometry != null) drawingContext.DrawGeometry(Fill, null, geometry);
            }
        }

        private static Brush CreateFill()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC8, 0x57));
            brush.Freeze();
            return brush;
        }
    }
}
