using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What adding a frame did.</summary>
    public enum StitchStep { Appended, Unchanged, NoMatch, WidthChanged }

    /// <summary>
    /// Joins the frames of a scrolling capture into one tall image (scrolling capture spec 4).
    /// Rows that stay put between the first two frames at the top and bottom are a header and
    /// a footer: the header is kept from the first frame, the footer from the last.
    ///
    /// <para>
    /// Between them, the shift is the one whose overlapping rows match best. Rows are compared
    /// by a hash that leaves out the rightmost <see cref="IgnoreRightColumns"/> pixels (a
    /// scrollbar), and uniform rows such as blank lines are not counted. A shift needs at least
    /// <see cref="MatchThreshold"/> of the counted rows to match, and at least
    /// <see cref="MinOverlapRows"/> counted rows.
    /// </para>
    /// </summary>
    public sealed class ScrollStitcher
    {
        public const int IgnoreRightColumns = 24;
        public const double MatchThreshold = 0.70;
        public const int MinOverlapRows = 8;
        public const int MinBandRows = 16;

        private readonly int _width;
        private readonly int _height;
        private readonly List<int[]> _band = new();
        private int[][] _header = Array.Empty<int[]>();
        private int[][] _footer = Array.Empty<int[]>();
        private PixelFrame _last;
        private int _top = -1, _bottom = -1;

        public ScrollStitcher(PixelFrame first)
        {
            _width = first.Width;
            _height = first.Height;
            _last = first;
        }

        /// <summary>The joined height so far.</summary>
        public int Height => _top < 0 ? _height : _header.Length + _band.Count + _footer.Length;

        public StitchStep Add(PixelFrame next, out int addedRows)
        {
            addedRows = 0;
            if (next.Width != _width || next.Height != _height) return StitchStep.WidthChanged;
            if (next.SameAs(_last)) return StitchStep.Unchanged;

            ulong[] prev = Hashes(_last), cur = Hashes(next);
            if (_top < 0)
            {
                int top = 0;
                while (top < _height && prev[top] == cur[top]) top++;
                int bottom = 0;
                while (bottom < _height - top && prev[_height - 1 - bottom] == cur[_height - 1 - bottom]) bottom++;
                if (_height - top - bottom < MinBandRows) { top = 0; bottom = 0; }
                _top = top;
                _bottom = bottom;
                _header = Rows(_last, 0, top);
                _band.AddRange(Rows(_last, top, _height - bottom));
                _footer = Rows(_last, _height - bottom, _height);
            }

            int band = _height - _top - _bottom;
            bool[] informative = Informative(next, _top, band);
            int dy = FindShift(prev.AsSpan(_top, band), cur.AsSpan(_top, band), informative);
            if (dy == 0) return StitchStep.Unchanged;
            if (dy < 0) return StitchStep.NoMatch;

            _band.AddRange(Rows(next, _top + band - dy, _top + band));
            _footer = Rows(next, _height - _bottom, _height);
            _last = next;
            addedRows = dy;
            return StitchStep.Appended;
        }

        /// <summary>Header, joined band, footer; the band is cut so the whole is at most <paramref name="maxHeight"/> rows.</summary>
        public PixelFrame Result(int maxHeight = int.MaxValue)
        {
            if (_top < 0) return _last;
            var rows = new List<int[]>(_header);
            int room = Math.Max(0, maxHeight - _header.Length - _footer.Length);
            for (int i = 0; i < _band.Count && i < room; i++) rows.Add(_band[i]);
            rows.AddRange(_footer);
            return PixelFrame.Stack(rows, _width);
        }

        /// <summary>
        /// The shift (rows the content moved up) that best lines <paramref name="next"/> up with
        /// <paramref name="prev"/>: 0 when not moving fits as well as any shift, -1 when none fits.
        /// </summary>
        internal static int FindShift(ReadOnlySpan<ulong> prev, ReadOnlySpan<ulong> next, bool[] nextInformative)
        {
            int band = prev.Length;
            int best = -1, bestMatches = 0;
            double bestRatio = 0;
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
                double ratio = (double)matches / counted;
                if (ratio < MatchThreshold) continue;
                if (best < 0 || ratio > bestRatio + 1e-9 || (Math.Abs(ratio - bestRatio) <= 1e-9 && matches > bestMatches))
                {
                    best = dy;
                    bestRatio = ratio;
                    bestMatches = matches;
                }
            }
            return best;
        }

        private ulong[] Hashes(PixelFrame frame)
        {
            int columns = _width > 2 * IgnoreRightColumns ? _width - IgnoreRightColumns : _width;
            var hashes = new ulong[frame.Height];
            for (int y = 0; y < frame.Height; y++)
            {
                ulong h = 14695981039346656037UL;
                var row = frame.Row(y);
                for (int x = 0; x < columns; x++)
                {
                    h ^= (uint)row[x];
                    h *= 1099511628211UL;
                }
                hashes[y] = h;
            }
            return hashes;
        }

        private bool[] Informative(PixelFrame frame, int from, int count)
        {
            int columns = _width > 2 * IgnoreRightColumns ? _width - IgnoreRightColumns : _width;
            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var row = frame.Row(from + i);
                int first = row[0];
                for (int x = 1; x < columns; x++)
                    if (row[x] != first) { result[i] = true; break; }
            }
            return result;
        }

        private static int[][] Rows(PixelFrame frame, int from, int to)
        {
            var rows = new int[Math.Max(0, to - from)][];
            for (int y = from; y < to; y++) rows[y - from] = frame.Row(y).ToArray();
            return rows;
        }
    }
}
