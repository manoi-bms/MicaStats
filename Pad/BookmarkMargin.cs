using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>A narrow margin left of the line numbers with an accent dot on each bookmarked line.</summary>
    internal sealed class BookmarkMargin : AbstractMargin
    {
        private readonly Func<IReadOnlyList<int>> _lines;
        private readonly Func<PadPalette> _palette;

        public BookmarkMargin(Func<IReadOnlyList<int>> lines, Func<PadPalette> palette)
        {
            _lines = lines;
            _palette = palette;
        }

        protected override Size MeasureOverride(Size availableSize) => new(12, 0);

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext drawingContext)
        {
            var view = TextView;
            if (view == null || !view.VisualLinesValid) return;
            var lines = new HashSet<int>(_lines());
            if (lines.Count == 0) return;

            var brush = PadThemeApplier.ToBrush(_palette().Accent);
            foreach (var visual in view.VisualLines)
            {
                if (!lines.Contains(visual.FirstDocumentLine.LineNumber)) continue;
                // The first row: a wrapped line's number sits there, not halfway down the paragraph.
                double y = visual.VisualTop - view.VerticalOffset + visual.TextLines[0].Height / 2;
                drawingContext.DrawEllipse(brush, null, new Point(6, y), 3.5, 3.5);
            }
        }
    }
}
