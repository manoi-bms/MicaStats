using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What adding a frame did.</summary>
    public enum StitchStep { Appended, Unchanged, NoMatch, WidthChanged }

    /// <summary>
    /// Joins the frames of a scrolling capture into one tall image (scrolling capture spec 4).
    ///
    /// <para>
    /// The first pair that really scrolled fixes the geometry. Rows that stay put at the top
    /// are a header, kept from the first frame. At the bottom, a margin is kept from the last
    /// frame only: the rows that stay put (a footer) and, when the scroll leaves room, more
    /// rows up to a quarter of the frame. Each step appends only rows from above that margin,
    /// so something floating near the bottom (a chat bubble, a cookie card, a footer clock)
    /// shows once instead of at every seam. Columns at the left and right edges that stay put
    /// while the rest scrolls are side panels: they are left out of the comparison and of the
    /// image, which is the scrolled content.
    /// </para>
    ///
    /// <para>
    /// Between them, the shift chosen is the one with the most matching rows, among the shifts
    /// where at least <see cref="MatchThreshold"/> of the counted rows match and at least
    /// <see cref="MinOverlapRows"/> rows are counted; ties go to the smaller shift. Rows are
    /// compared by a hash that leaves out a scrollbar strip at the right edge
    /// (<see cref="ScrollbarColumns"/>), and uniform rows such as blank lines are not counted.
    /// </para>
    /// </summary>
    public sealed class ScrollStitcher
    {
        /// <summary>The scrollbar strip left out of every comparison at 100% scaling; scaled with the monitor.</summary>
        public const int IgnoreRightColumns = 24;
        public const double MatchThreshold = 0.70;
        public const int MinOverlapRows = 8;
        public const int MinBandRows = 16;

        /// <summary>Fewer moving columns than this between side panels is not trusted: nothing is left out then.</summary>
        public const int MinMovingColumns = 16;

        private readonly int _width;
        private readonly int _height;
        private readonly int _scrollbar;
        private readonly List<int[]> _band = new();
        private int[][] _header = Array.Empty<int[]>();
        /// <summary>The last frame's bottom margin: rows of content, then the fixed footer.</summary>
        private int[][] _footer = Array.Empty<int[]>();
        private PixelFrame _last;
        private int _top = -1, _bottom = -1, _fixedFooter;
        /// <summary>Columns cut from the image at each edge (side panels; the right one includes the scrollbar strip).</summary>
        private int _left, _right;
        /// <summary>The columns compared: <c>[_from, _to)</c>.</summary>
        private int _from, _to;

        /// <param name="scale">The scale of the monitor the area is on: the scrollbar strip grows with it.</param>
        public ScrollStitcher(PixelFrame first, double scale = 1.0)
        {
            _width = first.Width;
            _height = first.Height;
            _last = first;
            _scrollbar = ScrollbarColumns(_width, scale);
            _from = 0;
            _to = _width - _scrollbar;
        }

        /// <summary>
        /// The scrollbar strip in physical pixels for a monitor scale: 24 at 100%, 42 at 175%.
        /// None on an area too narrow to spare it.
        /// </summary>
        public static int ScrollbarColumns(int width, double scale)
        {
            if (!(scale > 0)) scale = 1.0;
            int columns = (int)Math.Ceiling(IgnoreRightColumns * scale - 1e-6);
            return width > 2 * columns ? columns : 0;
        }

        /// <summary>The joined height so far.</summary>
        public int Height => _top < 0 ? _height : _header.Length + _band.Count + _footer.Length;

        public StitchStep Add(PixelFrame next, out int addedRows)
        {
            addedRows = 0;
            if (next.Width != _width || next.Height != _height) return StitchStep.WidthChanged;
            if (next.SameAs(_last)) return StitchStep.Unchanged;

            // The geometry is fixed from the first pair, but only once that pair really
            // scrolled: an Unchanged or NoMatch step commits nothing.
            bool first = _top < 0;
            int top = _top, bottom = _bottom, from = _from, to = _to, left = _left, right = _right;
            ulong[] prev = Hashes(_last, from, to), cur = Hashes(next, from, to);
            if (first)
            {
                top = 0;
                while (top < _height && prev[top] == cur[top]) top++;
                bottom = 0;
                while (bottom < _height - top && prev[_height - 1 - bottom] == cur[_height - 1 - bottom]) bottom++;
                if (_height - top - bottom < MinBandRows) { top = 0; bottom = 0; }

                (left, right) = FixedSides(_last, next, top, _height - bottom);
                if (left > 0 || right > 0)
                {
                    from = left;
                    to = _width - right;
                    prev = Hashes(_last, from, to);
                    cur = Hashes(next, from, to);
                }
            }

            int band = _height - top - bottom;
            int dy = FindShift(prev.AsSpan(top, band), cur.AsSpan(top, band), Informative(next, top, band, from, to));
            if (dy == 0) return StitchStep.Unchanged;
            if (dy < 0) return IsUnmoved(_last, next, _scrollbar) ? StitchStep.Unchanged : StitchStep.NoMatch;

            if (first)
            {
                _top = top;
                _fixedFooter = bottom;
                _bottom = BottomMargin(band, dy, bottom);
                _left = left;
                _right = right;
                _from = from;
                _to = to;
                _header = Rows(_last, 0, top);
                _band.AddRange(Rows(_last, top, _height - _bottom));
            }
            _band.AddRange(Rows(next, _height - _bottom - dy, _height - _bottom));
            _footer = Rows(next, _height - _bottom, _height);
            _last = next;
            addedRows = dy;
            return StitchStep.Appended;
        }

        /// <summary>
        /// Header, joined content, footer. When the whole is taller than
        /// <paramref name="maxHeight"/>, the content is cut: the fixed footer still closes the
        /// image, and the content rows of the bottom margin go with the cut.
        /// </summary>
        public PixelFrame Result(int maxHeight = int.MaxValue)
        {
            if (_top < 0) return _last;
            int soft = _footer.Length - _fixedFooter;
            int room = Math.Max(0, maxHeight - _header.Length - _fixedFooter);
            int content = Math.Min(room, _band.Count + soft);
            var rows = new List<int[]>(_header.Length + content + _fixedFooter);
            rows.AddRange(_header);
            for (int i = 0; i < content; i++) rows.Add(i < _band.Count ? _band[i] : _footer[i - _band.Count]);
            for (int i = soft; i < _footer.Length; i++) rows.Add(_footer[i]);
            return PixelFrame.Stack(rows, _width - _left - _right);
        }

        /// <summary>
        /// Whether <paramref name="after"/> shows the view of <paramref name="before"/> unmoved:
        /// identical, or with only a part of it changed in place (a GIF, a video, a spinner, a
        /// caret) while no shift either way lines the changed rows up and most of the frame
        /// still matches where it was. Used for "the top is reached" and "the end is reached".
        /// </summary>
        public static bool Unmoved(PixelFrame before, PixelFrame after, double scale = 1.0) =>
            IsUnmoved(before, after, ScrollbarColumns(before.Width, scale));

        private static bool IsUnmoved(PixelFrame before, PixelFrame after, int scrollbar)
        {
            if (before.Width != after.Width || before.Height != after.Height) return false;
            if (before.SameAs(after)) return true;

            int to = before.Width - scrollbar, h = before.Height;
            ulong[] a = Hashes(before, 0, to), b = Hashes(after, 0, to);
            int top = 0, bottom = 0;
            while (top < h && a[top] == b[top]) top++;
            while (bottom < h - top && a[h - 1 - bottom] == b[h - 1 - bottom]) bottom++;
            int band = h - top - bottom;
            if (band < MinBandRows) return true;   // a few rows changed in place: a caret, a clock

            // A scroll either way lines the changed rows up under some shift.
            if (FindShift(a.AsSpan(top, band), b.AsSpan(top, band), Informative(after, top, band, 0, to)) > 0) return false;
            if (FindShift(b.AsSpan(top, band), a.AsSpan(top, band), Informative(before, top, band, 0, to)) > 0) return false;

            // Nothing lines up: unmoved when the frame matches best where it was. A jump past
            // the view's height changes nearly every row and fails here.
            return FindShift(a, b, Informative(after, 0, h, 0, to)) == 0;
        }

        /// <summary>
        /// The shift (rows the content moved up) that best lines <paramref name="next"/> up with
        /// <paramref name="prev"/>: among the shifts where at least <see cref="MatchThreshold"/>
        /// of at least <see cref="MinOverlapRows"/> counted rows match, the one with the most
        /// matching rows, the smaller on a tie. 0 when not moving fits best, -1 when none fits.
        /// </summary>
        internal static int FindShift(ReadOnlySpan<ulong> prev, ReadOnlySpan<ulong> next, bool[] nextInformative)
        {
            int band = prev.Length;
            int best = -1, bestMatches = 0;
            for (int dy = 0; dy <= band - MinOverlapRows; dy++)
            {
                int counted = 0, matches = 0;
                for (int i = 0; i < band - dy; i++)
                {
                    if (!nextInformative[i]) continue;
                    counted++;
                    if (next[i] == prev[i + dy]) matches++;
                }
                if (counted < MinOverlapRows) continue;
                if ((double)matches / counted < MatchThreshold) continue;
                // Most matching rows, not the best ratio: a short accidental overlap (a repeated
                // motif) can match all of its few rows and must not beat the real shift.
                if (matches > bestMatches)
                {
                    best = dy;
                    bestMatches = matches;
                }
            }
            return best;
        }

        /// <summary>
        /// The bottom margin each step appends from above: the rows that stayed put, or more, up
        /// to a quarter of the frame. It is limited by the first shift so the content above it
        /// still overlaps by <see cref="MinOverlapRows"/> plus room for later steps to scroll
        /// half as far again; a large scroll with a small overlap keeps just the footer.
        /// </summary>
        private int BottomMargin(int band, int dy, int footer)
        {
            int headroom = dy / 2;
            int soft = Math.Min(_height / 4, band - dy - MinOverlapRows - headroom);
            return Math.Max(footer, soft);
        }

        /// <summary>
        /// Side panels: columns at the left and right edges that are identical in both frames over
        /// the rows <paramref name="fromRow"/> to <paramref name="toRow"/>, counted from each edge
        /// until a column differs. The right ones are looked for left of the scrollbar strip, and
        /// when found the strip is cut with them. A run of plain columns (a page's own blank
        /// margin) is not a panel: it does not disturb the comparison and stays in the image.
        /// </summary>
        private (int Left, int Right) FixedSides(PixelFrame a, PixelFrame b, int fromRow, int toRow)
        {
            int edge = _width - _scrollbar;
            int left = 0;
            bool leftDrawn = false;
            while (left < edge && SameColumn(a, b, left, fromRow, toRow, ref leftDrawn)) left++;
            if (!leftDrawn) left = 0;

            int right = 0;
            bool rightDrawn = false;
            while (edge - 1 - right >= left && SameColumn(a, b, edge - 1 - right, fromRow, toRow, ref rightDrawn)) right++;
            int rightCut = rightDrawn && right > 0 ? right + _scrollbar : 0;

            return _width - left - rightCut < MinMovingColumns ? (0, 0) : (left, rightCut);
        }

        /// <summary>Whether column <paramref name="x"/> is the same in both frames; sets <paramref name="drawn"/> when it is and is not one plain color.</summary>
        private static bool SameColumn(PixelFrame a, PixelFrame b, int x, int fromRow, int toRow, ref bool drawn)
        {
            int w = a.Width;
            int[] pa = a.Pixels, pb = b.Pixels;
            int plain = pa[fromRow * w + x];
            bool varies = false;
            for (int y = fromRow; y < toRow; y++)
            {
                int p = pa[y * w + x];
                if (p != pb[y * w + x]) return false;
                if (p != plain) varies = true;
            }
            if (varies) drawn = true;
            return true;
        }

        private static ulong[] Hashes(PixelFrame frame, int from, int to)
        {
            var hashes = new ulong[frame.Height];
            for (int y = 0; y < frame.Height; y++)
            {
                ulong h = 14695981039346656037UL;
                var row = frame.Row(y);
                for (int x = from; x < to; x++)
                {
                    h ^= (uint)row[x];
                    h *= 1099511628211UL;
                }
                hashes[y] = h;
            }
            return hashes;
        }

        /// <summary>Rows that are not one plain color within the compared columns: only they count when matching.</summary>
        private static bool[] Informative(PixelFrame frame, int fromRow, int count, int from, int to)
        {
            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var row = frame.Row(fromRow + i);
                int first = row[from];
                for (int x = from + 1; x < to; x++)
                    if (row[x] != first) { result[i] = true; break; }
            }
            return result;
        }

        /// <summary>Rows <paramref name="from"/> to <paramref name="to"/>, without the side panels.</summary>
        private int[][] Rows(PixelFrame frame, int from, int to)
        {
            int width = _width - _left - _right;
            var rows = new int[Math.Max(0, to - from)][];
            for (int y = from; y < to; y++) rows[y - from] = frame.Row(y).Slice(_left, width).ToArray();
            return rows;
        }
    }
}
