using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Bookmarks per document, as text anchors at line starts so they move with the text. A line
    /// that is deleted takes its anchor to the neighbouring line; two anchors on one line count once.
    /// </summary>
    internal sealed class BookmarkController
    {
        private readonly Dictionary<TextDocument, List<TextAnchor>> _anchors = new();

        /// <summary>Raised after a toggle, clear or load, so the margin repaints and the session saves.</summary>
        public event Action? Changed;

        /// <summary>Bookmarked lines of a document, sorted, each once.</summary>
        public IReadOnlyList<int> Lines(TextDocument document) =>
            _anchors.TryGetValue(document, out var list)
                ? list.Select(a => a.Line).Distinct().OrderBy(l => l).ToList()
                : Array.Empty<int>();

        public void Toggle(TextDocument document, int line)
        {
            var list = ListOf(document);
            int removed = list.RemoveAll(a => a.Line == line);
            if (removed == 0) list.Add(AnchorAt(document, line));
            Changed?.Invoke();
        }

        public void Clear(TextDocument document)
        {
            _anchors.Remove(document);
            Changed?.Invoke();
        }

        /// <summary>Loads saved lines; any past the end of the text are dropped.</summary>
        public void Load(TextDocument document, IEnumerable<int> lines)
        {
            var list = ListOf(document);
            list.Clear();
            foreach (int line in lines.Distinct())
                if (line >= 1 && line <= document.LineCount) list.Add(AnchorAt(document, line));
            Changed?.Invoke();
        }

        /// <summary>
        /// Puts the bookmarks recorded before an edit on the lines <paramref name="map"/> sends them
        /// to, for an edit the anchors cannot follow by themselves: a Move swaps a block with its
        /// neighbour line. Lines past the end are dropped.
        /// </summary>
        public void Remap(TextDocument document, IEnumerable<int> before, Func<int, int> map) => Load(document, before.Select(map));

        /// <summary>The next bookmarked line after <paramref name="line"/>, wrapping to the first; null without bookmarks.</summary>
        public int? Next(TextDocument document, int line)
        {
            var lines = Lines(document);
            if (lines.Count == 0) return null;
            foreach (int l in lines) if (l > line) return l;
            return lines[0];
        }

        /// <summary>The previous bookmarked line before <paramref name="line"/>, wrapping to the last; null without bookmarks.</summary>
        public int? Previous(TextDocument document, int line)
        {
            var lines = Lines(document);
            if (lines.Count == 0) return null;
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] < line) return lines[i];
            return lines[^1];
        }

        /// <summary>Forgets a document whose tab closed.</summary>
        public void Forget(TextDocument document) => _anchors.Remove(document);

        private List<TextAnchor> ListOf(TextDocument document)
        {
            if (!_anchors.TryGetValue(document, out var list)) _anchors[document] = list = new List<TextAnchor>();
            return list;
        }

        private static TextAnchor AnchorAt(TextDocument document, int line)
        {
            var anchor = document.CreateAnchor(document.GetLineByNumber(line).Offset);
            // Text inserted at the line start goes before the mark: Home then Enter moves the mark
            // down with its text, and typing at column 0 stays on the marked line.
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            anchor.SurviveDeletion = true;
            return anchor;
        }
    }
}
