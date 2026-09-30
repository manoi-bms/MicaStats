using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Shows a <see cref="HistoryDiff"/> in the read-only history preview (spec 5.2): each row as a
    /// line, removed lines on a faint red and added lines on a faint green (the palette's
    /// DiffRemoved and DiffAdded at <see cref="TintAlpha"/>), and a margin with the line's number in
    /// the old version and in the current text and its − or + glyph. The preview's own line numbers
    /// are hidden while it shows: they would count rows, not lines of either text.
    /// </summary>
    internal sealed class DiffPreview : IBackgroundRenderer
    {
        /// <summary>How strong a tint is: the palette's diff color at this alpha (tested to keep the text at 4.5:1).</summary>
        internal const byte TintAlpha = 0x38;

        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private readonly DiffMargin _margin;
        private int _digits = 1;

        public DiffPreview(TextEditor editor, Func<PadPalette> palette)
        {
            _editor = editor;
            _palette = palette;
            _margin = new DiffMargin(this);
        }

        /// <summary>The rows on screen; empty while the preview shows a version itself.</summary>
        public IReadOnlyList<DiffRow> Rows { get; private set; } = Array.Empty<DiffRow>();

        /// <summary>True while a compare is on screen.</summary>
        public bool IsShown { get; private set; }

        public KnownLayer Layer => KnownLayer.Background;

        /// <summary>The −/+ margin; for tests.</summary>
        internal AbstractMargin Margin => _margin;

        /// <summary>Puts the rows in the editor, with their tints and margin.</summary>
        public void Show(IReadOnlyList<DiffRow> rows)
        {
            Rows = rows;
            int widest = 1;
            foreach (var row in rows) widest = Math.Max(widest, Math.Max(row.OldLine ?? 0, row.NewLine ?? 0));
            _digits = widest.ToString(CultureInfo.InvariantCulture).Length;

            _editor.Text = string.Join("\n", rows.Select(r => r.Text));
            var area = _editor.TextArea;
            if (!area.TextView.BackgroundRenderers.Contains(this)) area.TextView.BackgroundRenderers.Insert(0, this);
            _editor.ShowLineNumbers = false;
            if (!area.LeftMargins.Contains(_margin)) area.LeftMargins.Insert(0, _margin);
            IsShown = true;
            _margin.InvalidateMeasure();
            _margin.InvalidateVisual();
            area.TextView.InvalidateLayer(KnownLayer.Background);
        }

        /// <summary>Takes the tints and the margin away and gives the preview its line numbers back.</summary>
        public void Hide()
        {
            if (!IsShown) return;
            var area = _editor.TextArea;
            area.TextView.BackgroundRenderers.Remove(this);
            area.LeftMargins.Remove(_margin);
            _editor.ShowLineNumbers = true;
            Rows = Array.Empty<DiffRow>();
            IsShown = false;
            area.TextView.InvalidateLayer(KnownLayer.Background);
        }

        /// <summary>The tint behind a row, or null for an unchanged one.</summary>
        public static PadColor? TintOf(DiffKind kind, PadPalette palette) => kind switch
        {
            DiffKind.Added => palette.DiffAdded with { A = TintAlpha },
            DiffKind.Removed => palette.DiffRemoved with { A = TintAlpha },
            _ => null,
        };

        /// <summary><c>+</c>, <c>−</c> (U+2212) or a space.</summary>
        public static string GlyphOf(DiffKind kind) => kind switch
        {
            DiffKind.Added => "+",
            DiffKind.Removed => "−",
            _ => " ",
        };

        /// <summary>The margin's numbers for a row: its old and its current line, each right-aligned to <paramref name="digits"/>.</summary>
        public static string NumbersOf(DiffRow row, int digits) => Number(row.OldLine, digits) + " " + Number(row.NewLine, digits);

        private static string Number(int? line, int digits) =>
            (line?.ToString(CultureInfo.InvariantCulture) ?? "").PadLeft(digits);

        /// <summary>The row a document line shows, or null past the rows.</summary>
        private DiffRow? RowAt(int lineNumber) =>
            lineNumber >= 1 && lineNumber <= Rows.Count ? Rows[lineNumber - 1] : null;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!IsShown || !textView.VisualLinesValid) return;
            var palette = _palette();
            var addedBrush = PadThemeApplier.ToBrush(palette.DiffAdded with { A = TintAlpha });
            var removedBrush = PadThemeApplier.ToBrush(palette.DiffRemoved with { A = TintAlpha });
            double width = Math.Max(textView.ActualWidth, 1);
            foreach (var visual in textView.VisualLines)
            {
                if (RowAt(visual.FirstDocumentLine.LineNumber) is not { } row || row.Kind == DiffKind.Unchanged) continue;
                double top = visual.VisualTop - textView.VerticalOffset;
                drawingContext.DrawRectangle(row.Kind == DiffKind.Added ? addedBrush : removedBrush, null,
                                             new Rect(0, top, width, visual.Height));
            }
        }

        /// <summary>The margin's width: two numbers and the glyph at the preview's font.</summary>
        internal double MarginWidth()
        {
            var sample = Text(new string('9', _digits * 2 + 1) + " +", PadThemeApplier.ToBrush(_palette().LineNumbers));
            return Math.Ceiling(sample.WidthIncludingTrailingWhitespace) + 12;
        }

        /// <summary>Draws the numbers and glyphs of the visible lines (the margin's OnRender; tests call it directly).</summary>
        internal void DrawMargin(DrawingContext drawingContext)
        {
            var view = _margin.TextView;
            if (!IsShown || view == null || !view.VisualLinesValid) return;
            var palette = _palette();
            var numbers = PadThemeApplier.ToBrush(palette.LineNumbers);
            var addedGlyph = PadThemeApplier.ToBrush(palette.DiffAdded);
            var removedGlyph = PadThemeApplier.ToBrush(palette.DiffRemoved);
            foreach (var visual in view.VisualLines)
            {
                if (RowAt(visual.FirstDocumentLine.LineNumber) is not { } row) continue;
                double y = visual.VisualTop - view.VerticalOffset;
                var text = Text(NumbersOf(row, _digits) + " ", numbers);
                drawingContext.DrawText(text, new Point(4, y));
                if (row.Kind == DiffKind.Unchanged) continue;
                drawingContext.DrawText(Text(GlyphOf(row.Kind), row.Kind == DiffKind.Added ? addedGlyph : removedGlyph),
                                        new Point(4 + text.WidthIncludingTrailingWhitespace, y));
            }
        }

        private FormattedText Text(string text, Brush brush) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(_editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                _editor.FontSize * 0.85, brush, VisualTreeHelper.GetDpi(_margin).PixelsPerDip);

        /// <summary>The margin itself: sized and drawn by its <see cref="DiffPreview"/>.</summary>
        private sealed class DiffMargin : AbstractMargin
        {
            private readonly DiffPreview _owner;

            public DiffMargin(DiffPreview owner) => _owner = owner;

            protected override Size MeasureOverride(Size availableSize) => new(_owner.MarginWidth(), 0);

            protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
            {
                if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
                base.OnTextViewChanged(oldTextView, newTextView);
                if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
                InvalidateVisual();
            }

            private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

            protected override void OnRender(DrawingContext drawingContext) => _owner.DrawMargin(drawingContext);
        }
    }
}
