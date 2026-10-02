using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What adding a frame did.</summary>
    public enum StitchStep { Appended, Unchanged, NoMatch, WidthChanged }

    /// <summary>How a view changed between two looks, judged on the part that changed.</summary>
    public enum ViewChange
    {
        /// <summary>Identical, or changed in place only (a caret, a clock).</summary>
        Same,
        /// <summary>The changed part lines up under a shift up or down: it scrolled.</summary>
        Shifted,
        /// <summary>The changed part lines up under no shift: replaced, or animating.</summary>
        Replaced,
    }

    /// <summary>
    /// Joins the frames of a scrolling capture into one tall image (scrolling capture spec 4).
    ///
    /// <para>
    /// The first pair that really scrolled fixes the geometry. Rows that stay put at the top
    /// are a header, kept from the first frame; rows that stay put at the bottom are a footer.
    /// The shift is searched over every row between them. Appending works from a bottom
    /// margin kept from the last frame only: the footer and, when the scroll leaves room, more
    /// rows up to a quarter of the frame. Each step appends rows from above that margin, so
    /// something floating near the bottom (a chat bubble, a cookie card, a footer clock) shows
    /// once instead of at every seam.
    /// </para>
    ///
    /// <para>
    /// A side panel is a run of columns at the left or right edge that stays put while the rest
    /// scrolls and does not line up under the shift; columns that line up both ways (a blank
    /// gutter, a column that reads the same on every row of a list) are content. Panels are
    /// left out of the comparison and of the scrolled rows: with a header or footer the image
    /// keeps its full width and the panels' place in the scrolled rows is filled with their
    /// background; without one the image is cut to the scrolled part.
    /// </para>
    ///
    /// <para>
    /// The shift chosen has the best ratio of matching rows among the shifts where at least
    /// <see cref="MatchThreshold"/> of at least <see cref="MinOverlapRows"/> counted rows match
    /// and the matching rows are at least half the most any shift has (see
    /// <see cref="FindShift"/>). Rows are compared by a hash that leaves out a scrollbar strip at the right edge
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

        /// <summary>Fewer moving columns than this between still edges is not trusted: nothing is left out then.</summary>
        public const int MinMovingColumns = 16;

        private readonly int _width;
        private readonly int _height;
        private readonly int _scrollbar;
        /// <summary>The joined content rows, full width; the panels' columns are filled or cut in <see cref="Result"/>.</summary>
        private readonly List<int[]> _band = new();
        private int[][] _header = Array.Empty<int[]>();
        /// <summary>The last frame's bottom margin: rows of content, then the fixed footer.</summary>
        private int[][] _footer = Array.Empty<int[]>();
        private PixelFrame _last;
        private int _top = -1, _bottom = -1, _fixedFooter;
        /// <summary>Side panel columns at each edge (the right one with the scrollbar strip), and their backgrounds.</summary>
        private int _left, _right, _leftFill, _rightFill;
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
            int edge = _width - _scrollbar;
            int top, bottom, from = _from, to = _to;
            ulong[] prev, cur;
            if (first)
            {
                prev = Hashes(_last, 0, edge);
                cur = Hashes(next, 0, edge);
                (top, bottom) = StillRows(prev, cur);
                if (_height - top - bottom < MinBandRows) { top = 0; bottom = 0; }

                // Columns that stay put at the edges are left out of the search; which of them
                // are panels is settled once the shift is known.
                (int left, int right) = StillColumns(_last, next, top, _height - bottom, edge);
                from = left;
                to = edge - right;
                if (from > 0 || to < edge)
                {
                    prev = Hashes(_last, from, to);
                    cur = Hashes(next, from, to);
                }
            }
            else
            {
                top = _top;
                bottom = _fixedFooter;   // the search spans the band; the soft margin only shapes the output
                prev = Hashes(_last, from, to);
                cur = Hashes(next, from, to);
            }

            int band = _height - top - bottom;
            int dy = FindShift(prev.AsSpan(top, band), cur.AsSpan(top, band), Informative(next, top, band, from, to));
            if (dy == 0) return StitchStep.Unchanged;
            if (dy < 0) return StitchStep.NoMatch;

            if (first) Commit(next, top, bottom, band, dy, from, to, edge);

            // Append the new rows from above the margin. A step longer than the room there
            // leaves rows hidden under the header now: they are in the previous frame's margin.
            int start = _height - _bottom - dy;
            if (start < _top)
            {
                _band.AddRange(Rows(_last, _height - _bottom, _height - _bottom + (_top - start)));
                start = _top;
            }
            _band.AddRange(Rows(next, start, _height - _bottom));
            _footer = Rows(next, _height - _bottom, _height);
            _last = next;
            addedRows = dy;
            return StitchStep.Appended;
        }

        /// <summary>Fixes the geometry from the first pair that scrolled by <paramref name="dy"/>.</summary>
        private void Commit(PixelFrame next, int top, int bottom, int band, int dy, int from, int to, int edge)
        {
            // A panel stays put but does not line up under the shift. Each panel reaches from
            // its edge to its innermost such column; still columns beyond it are content.
            int left = 0;
            for (int x = from - 1; x >= 0; x--)
                if (!MatchesShifted(_last, next, x, top, band, dy)) { left = x + 1; break; }
            int right = 0;
            for (int x = to; x < edge; x++)
                if (!MatchesShifted(_last, next, x, top, band, dy)) { right = edge - x; break; }

            _top = top;
            _fixedFooter = bottom;
            _bottom = BottomMargin(band, dy, bottom);
            _left = left;
            _right = right > 0 ? right + _scrollbar : 0;
            _from = left;
            _to = edge - right;
            _leftFill = Background(_last, 0, left, top, _height - bottom);
            _rightFill = Background(_last, edge - right, edge, top, _height - bottom);
            _header = Rows(_last, 0, top);
            _band.AddRange(Rows(_last, top, _height - _bottom));
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

            bool panels = _left > 0 || _right > 0;
            bool fullWidth = !panels || _header.Length > 0 || _fixedFooter > 0;
            int width = fullWidth ? _width : _width - _left - _right;
            int[] Edge(int[] row) => fullWidth ? row : row[_left..(_width - _right)];
            int[] Scrolled(int[] row)
            {
                if (!panels) return row;
                if (!fullWidth) return row[_left..(_width - _right)];
                var filled = (int[])row.Clone();
                filled.AsSpan(0, _left).Fill(_leftFill);
                filled.AsSpan(_width - _right).Fill(_rightFill);
                return filled;
            }

            var rows = new List<int[]>(_header.Length + content + _fixedFooter);
            foreach (var row in _header) rows.Add(Edge(row));
            for (int i = 0; i < content; i++) rows.Add(Scrolled(i < _band.Count ? _band[i] : _footer[i - _band.Count]));
            for (int i = soft; i < _footer.Length; i++) rows.Add(Edge(_footer[i]));
            return PixelFrame.Stack(rows, width);
        }

        /// <summary>
        /// How <paramref name="after"/> differs from <paramref name="before"/>, judged on the part
        /// that changed: the rows and columns that stayed put at the edges are left out.
        /// </summary>
        public static ViewChange Compare(PixelFrame before, PixelFrame after, double scale = 1.0)
        {
            if (before.Width != after.Width || before.Height != after.Height) return ViewChange.Replaced;
            if (before.SameAs(after)) return ViewChange.Same;

            int h = before.Height, edge = before.Width - ScrollbarColumns(before.Width, scale);
            ulong[] a = Hashes(before, 0, edge), b = Hashes(after, 0, edge);
            var (top, bottom) = StillRows(a, b);
            int rows = h - top - bottom;
            if (rows == 0) return ViewChange.Same;   // only the scrollbar strip changed
            if (rows < MinBandRows)
                return SmallShift(a, b, before, after, top, h - bottom, edge) ? ViewChange.Shifted : ViewChange.Same;

            var (left, right) = StillColumns(before, after, top, h - bottom, edge);
            int from = left, to = edge - right;
            a = Hashes(before, from, to);
            b = Hashes(after, from, to);
            int down = FindShift(a.AsSpan(top, rows), b.AsSpan(top, rows), Informative(after, top, rows, from, to));
            if (down == 0) return ViewChange.Same;
            if (down > 0) return ViewChange.Shifted;
            return FindShift(b.AsSpan(top, rows), a.AsSpan(top, rows), Informative(before, top, rows, from, to)) > 0
                ? ViewChange.Shifted
                : ViewChange.Replaced;
        }

        /// <summary>Whether the view did not move between the two looks: <see cref="Compare"/> says <see cref="ViewChange.Same"/>.</summary>
        public static bool Unmoved(PixelFrame before, PixelFrame after, double scale = 1.0) =>
            Compare(before, after, scale) == ViewChange.Same;

        /// <summary>
        /// Whether <paramref name="after"/>, a look after scrolling up, shows the top: the view
        /// did not move, or the part that changed lines up under no shift and lies inside
        /// <paramref name="selfMotion"/>, where the view was seen changing while nothing scrolled
        /// (a GIF, a video, a spinner). Otherwise a changed part counts as moved: one step up
        /// can replace a whole scrolling pane.
        /// </summary>
        public static bool TopReached(PixelFrame before, PixelFrame after, PixelRect? selfMotion, double scale = 1.0)
        {
            switch (Compare(before, after, scale))
            {
                case ViewChange.Same: return true;
                case ViewChange.Shifted: return false;
            }
            if (selfMotion is not PixelRect motion) return false;
            var changed = before.DiffBox(after, before.Width - ScrollbarColumns(before.Width, scale));
            return changed is not PixelRect c
                || (c.Left >= motion.Left && c.Top >= motion.Top && c.Right <= motion.Right && c.Bottom <= motion.Bottom);
        }

        /// <summary>
        /// The shift (rows the content moved up) that best lines <paramref name="next"/> up with
        /// <paramref name="prev"/>. Candidates are the shifts where at least
        /// <see cref="MatchThreshold"/> of at least <see cref="MinOverlapRows"/> counted rows match
        /// and whose matching rows are at least half the most any candidate has; among them the
        /// best ratio wins, then more matching rows, then the smaller shift. 0 when not moving
        /// fits best, -1 when none fits.
        /// </summary>
        internal static int FindShift(ReadOnlySpan<ulong> prev, ReadOnlySpan<ulong> next, bool[] nextInformative)
        {
            int band = prev.Length;
            int shifts = Math.Max(0, band - MinOverlapRows + 1);
            var matchesAt = new int[shifts];
            var countedAt = new int[shifts];
            int mostMatches = 0;
            for (int dy = 0; dy < shifts; dy++)
            {
                int counted = 0, matches = 0;
                for (int i = 0; i < band - dy; i++)
                {
                    if (!nextInformative[i]) continue;
                    counted++;
                    if (next[i] == prev[i + dy]) matches++;
                }
                if (counted < MinOverlapRows || (double)matches / counted < MatchThreshold) continue;
                matchesAt[dy] = matches;
                countedAt[dy] = counted;
                mostMatches = Math.Max(mostMatches, matches);
            }

            // Neither measure alone: a short accidental overlap (a repeated motif) matches all of
            // its few rows, and on a repeating grid a wrong shift with a longer overlap matches
            // more rows than the real one. So: enough support first, then the best fit.
            int best = -1;
            double bestRatio = 0;
            for (int dy = 0; dy < shifts; dy++)
            {
                if (countedAt[dy] == 0 || 2 * matchesAt[dy] < mostMatches) continue;
                double ratio = (double)matchesAt[dy] / countedAt[dy];
                if (best < 0 || ratio > bestRatio + 1e-9
                    || (Math.Abs(ratio - bestRatio) <= 1e-9 && matchesAt[dy] > matchesAt[best]))
                {
                    best = dy;
                    bestRatio = ratio;
                }
            }
            return best;
        }

        /// <summary>Rows identical at the top and at the bottom of two frames' hashes.</summary>
        private static (int Top, int Bottom) StillRows(ulong[] a, ulong[] b)
        {
            int h = a.Length, top = 0, bottom = 0;
            while (top < h && a[top] == b[top]) top++;
            while (bottom < h - top && a[h - 1 - bottom] == b[h - 1 - bottom]) bottom++;
            return (top, bottom);
        }

        /// <summary>
        /// Columns identical in both frames over rows <paramref name="fromRow"/> to
        /// <paramref name="toRow"/>, counted from the left edge and from <paramref name="edge"/>
        /// (the scrollbar strip's left) inwards; none when fewer than
        /// <see cref="MinMovingColumns"/> are left between them.
        /// </summary>
        private static (int Left, int Right) StillColumns(PixelFrame a, PixelFrame b, int fromRow, int toRow, int edge)
        {
            int left = 0;
            while (left < edge && SameColumn(a, b, left, fromRow, toRow)) left++;
            int right = 0;
            while (edge - 1 - right >= left && SameColumn(a, b, edge - 1 - right, fromRow, toRow)) right++;
            return edge - left - right < MinMovingColumns ? (0, 0) : (left, right);
        }

        private static bool SameColumn(PixelFrame a, PixelFrame b, int x, int fromRow, int toRow)
        {
            int w = a.Width;
            int[] pa = a.Pixels, pb = b.Pixels;
            for (int y = fromRow; y < toRow; y++)
                if (pa[y * w + x] != pb[y * w + x]) return false;
            return true;
        }

        /// <summary>Whether column <paramref name="x"/> of <paramref name="next"/> lines up with <paramref name="prev"/>'s moved up by <paramref name="dy"/> over the band's overlap.</summary>
        private static bool MatchesShifted(PixelFrame prev, PixelFrame next, int x, int top, int band, int dy)
        {
            int w = prev.Width;
            int[] pp = prev.Pixels, pn = next.Pixels;
            for (int y = top; y < top + band - dy; y++)
                if (pn[y * w + x] != pp[(y + dy) * w + x]) return false;
            return true;
        }

        /// <summary>
        /// For a change of fewer than <see cref="MinBandRows"/> rows: whether some non-zero shift
        /// lines every informative changed row of one frame up with a row of the other, as when a
        /// short line of text on a blank view moved a few rows.
        /// </summary>
        private static bool SmallShift(ulong[] a, ulong[] b, PixelFrame before, PixelFrame after, int fromRow, int toRow, int edge)
        {
            bool[] afterInk = Informative(after, fromRow, toRow - fromRow, 0, edge);
            bool[] beforeInk = Informative(before, fromRow, toRow - fromRow, 0, edge);
            int h = a.Length;
            for (int s = -(h - 1); s < h; s++)
            {
                if (s == 0) continue;
                if (LinesUp(b, a, afterInk, fromRow, s) || LinesUp(a, b, beforeInk, fromRow, s)) return true;
            }
            return false;

            static bool LinesUp(ulong[] rows, ulong[] other, bool[] ink, int fromRow, int s)
            {
                bool any = false;
                for (int i = 0; i < ink.Length; i++)
                {
                    if (!ink[i]) continue;
                    int y = fromRow + i, j = y + s;
                    if (j < 0 || j >= other.Length || rows[y] != other[j]) return false;
                    any = true;
                }
                return any;
            }
        }

        /// <summary>
        /// The bottom margin each step appends from above: the rows that stayed put, or more, up
        /// to a quarter of the frame. It is limited by the first shift so the room above it holds
        /// a step half as long again; a longer step takes its first rows from the previous frame.
        /// </summary>
        private int BottomMargin(int band, int dy, int footer)
        {
            int headroom = dy / 2;
            int soft = Math.Min(_height / 4, band - dy - MinOverlapRows - headroom);
            return Math.Max(footer, soft);
        }

        /// <summary>The most common color in columns <paramref name="x0"/> to <paramref name="x1"/>, rows <paramref name="y0"/> to <paramref name="y1"/>: a panel's background.</summary>
        private static int Background(PixelFrame frame, int x0, int x1, int y0, int y1)
        {
            if (x1 <= x0 || y1 <= y0) return 0;
            var counts = new Dictionary<int, int>();
            int best = 0, bestCount = 0;
            for (int y = y0; y < y1; y++)
            {
                var row = frame.Row(y);
                for (int x = x0; x < x1; x++)
                {
                    counts.TryGetValue(row[x], out int n);
                    counts[row[x]] = ++n;
                    if (n > bestCount) { bestCount = n; best = row[x]; }
                }
            }
            return best;
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

        /// <summary>Rows <paramref name="from"/> to <paramref name="to"/>, full width.</summary>
        private static int[][] Rows(PixelFrame frame, int from, int to)
        {
            var rows = new int[Math.Max(0, to - from)][];
            for (int y = from; y < to; y++) rows[y - from] = frame.Row(y).ToArray();
            return rows;
        }
    }
}
