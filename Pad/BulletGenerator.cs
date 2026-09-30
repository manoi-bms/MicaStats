using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Draws a list item's <c>-</c>, <c>*</c> or <c>+</c> as <c>•</c>. The element stands for exactly
    /// one character of the document, so the caret, selection and copying are unaffected; the
    /// Markdown colorizer paints it in the list-marker color.
    /// </summary>
    internal sealed class BulletGenerator : VisualLineElementGenerator
    {
        private const int ScanLength = 256;
        private readonly MarkdownDocumentCache _cache;

        public BulletGenerator(MarkdownDocumentCache cache) => _cache = cache;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(startOffset);
            if (_cache.KindOf(document, line.LineNumber) != MdFence.None) return -1;

            int marker = MarkdownLineTokenizer.BulletOffset(document.GetText(line.Offset, Math.Min(line.Length, ScanLength)));
            if (marker < 0) return -1;
            int offset = line.Offset + marker;
            return offset >= startOffset ? offset : -1;
        }

        public override VisualLineElement ConstructElement(int offset) => new FormattedTextElement("•", 1);
    }
}
