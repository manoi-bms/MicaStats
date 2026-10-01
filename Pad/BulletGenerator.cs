using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Draws a list item's <c>-</c>, <c>*</c> or <c>+</c> as <c>•</c>, outside fenced blocks and front
    /// matter. The element stands for exactly one character of the document, so the caret, selection
    /// and copying are unaffected; the Markdown colorizer paints it in the list-marker color.
    /// </summary>
    internal sealed class BulletGenerator : VisualLineElementGenerator
    {
        private const int ScanLength = 256;
        private readonly MarkdownDocumentCache _cache;
        private readonly Action<Exception> _onFailure;

        /// <param name="onFailure">Told when a line cannot be looked at; that line gets no bullet.</param>
        public BulletGenerator(MarkdownDocumentCache cache, Action<Exception>? onFailure = null)
        {
            _cache = cache;
            _onFailure = onFailure ?? (_ => { });
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                var line = document.GetLineByOffset(startOffset);
                var facts = _cache.FactsOf(document, line.LineNumber);
                if (facts.Fence != MdFence.None || facts.FrontMatter) return -1;

                int marker = MarkdownLineTokenizer.BulletOffset(document.GetText(line.Offset, Math.Min(line.Length, ScanLength)));
                if (marker < 0) return -1;
                int offset = line.Offset + marker;
                return offset >= startOffset ? offset : -1;
            }
            catch (Exception ex)
            {
                _onFailure(ex);
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            try
            {
                return new FormattedTextElement("•", 1);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
                return null;
            }
        }
    }
}
