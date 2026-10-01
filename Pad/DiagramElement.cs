using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The picture under a closing fence line (spec section 4, R9): at the line's end, a line
    /// break and then the picture, standing for no document text. The fence text stays ordinary,
    /// editable text above it. The caret stops only in front of the element, so it never sits
    /// after the picture; copying and the document are unaffected.
    /// </summary>
    internal sealed class DiagramElement : VisualLineElement
    {
        public DiagramElement(UIElement picture) : base(2, 0) => Picture = picture;

        public UIElement Picture { get; }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            startVisualColumn == VisualColumn
                ? new TextEndOfLine(1)
                : new InlineObjectRun(1, TextRunProperties, Picture);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            if (direction == LogicalDirection.Forward) return visualColumn < VisualColumn ? VisualColumn : -1;
            return visualColumn > VisualColumn ? VisualColumn : -1;
        }
    }
}
