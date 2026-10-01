using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Shows a known <c>:name:</c> as its emoji (spec 5, R5): one element standing for the whole
    /// code, so the caret steps over it, Backspace or Delete next to it removes it whole, and
    /// copying copies the code. Not in fenced blocks or front matter. The glyph is drawn by the
    /// font fallback (Segoe UI Emoji), in one color. If looking for codes fails (a missing or corrupt
    /// table), it says so once and shows no emoji from then on: the codes stay text.
    /// </summary>
    internal sealed class EmojiGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Action<Exception> _onFailure;
        private readonly Func<string, IReadOnlyList<(int Start, int Length, string Glyph)>> _find;
        private bool _failed;

        /// <param name="onFailure">Told the first time looking for codes fails; from then on this generator shows none.</param>
        /// <param name="find">The codes of a line (a test seam); null uses <see cref="Emoji.Find"/>.</param>
        public EmojiGenerator(MarkdownDocumentCache cache, Action<Exception>? onFailure = null,
                              Func<string, IReadOnlyList<(int Start, int Length, string Glyph)>>? find = null)
        {
            _cache = cache;
            _onFailure = onFailure ?? (_ => { });
            _find = find ?? Emoji.Find;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (_failed) return -1;
            try
            {
                var document = CurrentContext.Document;
                int last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
                for (var line = document.GetLineByOffset(startOffset); line != null && line.LineNumber <= last; line = line.NextLine)
                {
                    var facts = _cache.FactsOf(document, line.LineNumber);
                    if (facts.Fence != MdFence.None || facts.FrontMatter) continue;
                    foreach (var (start, _, _) in _find(document.GetText(line)))
                    {
                        int offset = line.Offset + start;
                        if (offset >= startOffset) return offset;
                    }
                }
                return -1;
            }
            catch (Exception ex)
            {
                Failed(ex);
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (_failed) return null;
            try
            {
                var document = CurrentContext.Document;
                var line = document.GetLineByOffset(offset);
                foreach (var (start, length, glyph) in _find(document.GetText(line)))
                    if (line.Offset + start == offset) return new FormattedTextElement(glyph, length);
                return null;
            }
            catch (Exception ex)
            {
                Failed(ex);
                return null;
            }
        }

        private void Failed(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            try
            {
                _onFailure(ex);
            }
            catch (Exception)
            {
                // Reporting is best effort; it must not throw into rendering.
            }
        }
    }
}
