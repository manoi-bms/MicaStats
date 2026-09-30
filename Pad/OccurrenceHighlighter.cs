using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Soft boxes behind every occurrence of the selected word, in the background layer so find
    /// matches and the selection draw on top. Only occurrences in the visible lines are drawn.
    /// </summary>
    internal sealed class OccurrenceHighlighter : IBackgroundRenderer
    {
        /// <summary>Occurrence offsets, sorted.</summary>
        public IReadOnlyList<int> Offsets { get; set; } = Array.Empty<int>();

        /// <summary>The word's length.</summary>
        public int Length { get; set; }

        public Brush Fill { get; set; } = PadThemeApplier.ToBrush(PadPalette.Dark.Occurrence);

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Offsets.Count == 0 || !textView.VisualLinesValid || textView.Document == null) return;
            var lines = textView.VisualLines;
            if (lines.Count == 0) return;

            int start = lines[0].FirstDocumentLine.Offset;
            int end = lines[lines.Count - 1].LastDocumentLine.EndOffset;
            int textLength = textView.Document.TextLength;

            int first = BinarySearchFirstAtOrAfter(start);
            for (int i = first; i < Offsets.Count && Offsets[i] <= end; i++)
            {
                if (Offsets[i] + Length > textLength) break;
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
                builder.AddSegment(textView, new TextSegment { StartOffset = Offsets[i], Length = Length });
                var geometry = builder.CreateGeometry();
                if (geometry != null) drawingContext.DrawGeometry(Fill, null, geometry);
            }
        }

        private int BinarySearchFirstAtOrAfter(int offset)
        {
            int lo = 0, hi = Offsets.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Offsets[mid] < offset) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }
}
