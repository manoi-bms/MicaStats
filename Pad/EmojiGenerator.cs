using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Shows a known <c>:name:</c> as its emoji (spec 5, R5): one element standing for the whole
    /// code, so the caret steps over it, Backspace or Delete next to it removes it whole, and
    /// copying copies the code. Not in fenced blocks or front matter. The glyph is drawn by the
    /// font fallback (Segoe UI Emoji), in one color.
    /// </summary>
    internal sealed class EmojiGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Action<Exception> _onFailure;

        /// <param name="onFailure">Told when a line cannot be looked at; that line gets no emoji.</param>
        public EmojiGenerator(MarkdownDocumentCache cache, Action<Exception>? onFailure = null)
        {
            _cache = cache;
            _onFailure = onFailure ?? (_ => { });
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                int last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
                for (var line = document.GetLineByOffset(startOffset); line != null && line.LineNumber <= last; line = line.NextLine)
                {
                    var facts = _cache.FactsOf(document, line.LineNumber);
                    if (facts.Fence != MdFence.None || facts.FrontMatter) continue;
                    foreach (var (start, _, _) in Emoji.Find(document.GetText(line)))
                    {
                        int offset = line.Offset + start;
                        if (offset >= startOffset) return offset;
                    }
                }
                return -1;
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
                var document = CurrentContext.Document;
                var line = document.GetLineByOffset(offset);
                foreach (var (start, length, glyph) in Emoji.Find(document.GetText(line)))
                    if (line.Offset + start == offset) return new FormattedTextElement(glyph, length);
                return null;
            }
            catch (Exception ex)
            {
                _onFailure(ex);
                return null;
            }
        }
    }
}
