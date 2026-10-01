using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts a <see cref="DiagramElement"/> at the end of each closing fence line of a diagram block
    /// (spec section 4). A visual line can hold several document lines (a fold): the scan runs to
    /// its last one. A failure is logged once by the board and that line simply gets no picture;
    /// the rest of the Markdown formatting is untouched.
    /// </summary>
    internal sealed class DiagramGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly DiagramBoard _board;

        public DiagramGenerator(MarkdownDocumentCache cache, DiagramBoard board)
        {
            _cache = cache;
            _board = board;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                if (!ReferenceEquals(document, _board.Document)) return -1;
                int last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
                for (var line = document.GetLineByOffset(startOffset); line != null && line.LineNumber <= last; line = line.NextLine)
                {
                    if (line.EndOffset < startOffset) continue;
                    if (_cache.KindOf(document, line.LineNumber) != MdFence.Delimiter) continue;
                    int open = _cache.OpeningLineOf(document, line.LineNumber);
                    if (open == 0 || DiagramKinds.FromFence(document.GetText(document.GetLineByNumber(open))) == null) continue;

                    // A fold that ends exactly at this fence (a heading section whose last line is the
                    // diagram) must not show it: the picture belongs where the closing line starts a
                    // visual line (the normal case, and Hide code, which folds before it), or where the
                    // block's own opening line does (the block's fence fold).
                    var first = CurrentContext.VisualLine.FirstDocumentLine;
                    if (line == first || document.GetLineByNumber(open) == first) return line.EndOffset;
                }
                return -1;
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Diagram pictures failed (" + ex.GetType().Name + ")");
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            try
            {
                var line = CurrentContext.Document.GetLineByOffset(offset);
                if (line.EndOffset != offset || _board.BlockClosedBy(line) is not { } block) return null;
                return new DiagramElement(_board.PictureFor(line, block));
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Diagram pictures failed (" + ex.GetType().Name + ")");
                return null;
            }
        }
    }
}
