using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts the image previews of a line (an <see cref="ImageRow"/> in a <see cref="DiagramElement"/>)
    /// at the end of each line with images outside fences and front matter (spec 6.3, R9). Only a line
    /// that starts its visual line gets them, so lines folded away show none. A failure is logged once
    /// by the board and that line simply gets no previews.
    /// </summary>
    internal sealed class ImageGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly ImageBoard _board;
        private DocumentLine? _line;
        private IReadOnlyList<ImageRef> _images = Array.Empty<ImageRef>();

        public ImageGenerator(MarkdownDocumentCache cache, ImageBoard board)
        {
            _cache = cache;
            _board = board;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _line = null;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                if (!ReferenceEquals(document, _board.Document)) return -1;
                var line = CurrentContext.VisualLine.FirstDocumentLine;
                if (line.EndOffset < startOffset) return -1;
                return ImagesOf(document, line).Count > 0 ? line.EndOffset : -1;
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Image previews failed (" + ex.GetType().Name + ")");
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            try
            {
                var document = CurrentContext.Document;
                var line = CurrentContext.VisualLine.FirstDocumentLine;
                if (line.EndOffset != offset) return null;
                var images = ImagesOf(document, line);
                return images.Count == 0 ? null : new DiagramElement(_board.RowFor(line, images));
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Image previews failed (" + ex.GetType().Name + ")");
                return null;
            }
        }

        /// <summary>The images of <paramref name="line"/>, found once per visual line.</summary>
        private IReadOnlyList<ImageRef> ImagesOf(TextDocument document, DocumentLine line)
        {
            if (ReferenceEquals(line, _line)) return _images;
            _line = line;
            _images = Array.Empty<ImageRef>();
            if (line.Length > ImageSources.MaxLineLength) return _images;
            var facts = _cache.FactsOf(document, line.LineNumber);
            if (facts.Fence == MdFence.None && !facts.FrontMatter) _images = ImageSources.Find(document.GetText(line));
            return _images;
        }
    }
}
