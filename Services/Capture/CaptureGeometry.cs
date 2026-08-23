using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// A rectangle in <b>physical screen pixels</b>. Capture math never uses WPF's
    /// device-independent units: on a mixed-DPI desktop a DIP means something different on each
    /// monitor, and a screenshot that is one pixel off is a visibly wrong screenshot.
    /// </summary>
    public readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

        public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
            new(left, top, right - left, bottom - top);

        /// <summary>
        /// Builds a normalised rectangle from two drag points, so dragging up-left produces the
        /// same rectangle as dragging down-right.
        /// </summary>
        public static PixelRect FromPoints(int x1, int y1, int x2, int y2) =>
            FromEdges(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

        public bool Contains(int px, int py) => px >= Left && px < Right && py >= Top && py < Bottom;

        public bool IntersectsWith(PixelRect o) =>
            o.Left < Right && o.Right > Left && o.Top < Bottom && o.Bottom > Top;

        public PixelRect Intersect(PixelRect o)
        {
            int l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top);
            int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
            return r <= l || b <= t ? default : FromEdges(l, t, r, b);
        }

        public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

        public PixelRect Inflate(int d) => FromEdges(Left - d, Top - d, Right + d, Bottom + d);

        public override string ToString() => $"{Width}x{Height} @ {X},{Y}";
    }

    /// <summary>One display, its pixel bounds and scale factor.</summary>
    public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary, double Scale);

    /// <summary>A top-level window considered for click-to-capture, front-most first.</summary>
    public sealed record CaptureTarget(IntPtr Handle, PixelRect Bounds, string Title);

    /// <summary>
    /// Pure geometry behind the capture tools. Everything here is a function of numbers, so the
    /// awkward cases — dragging backwards, a selection running off the desktop, the loupe near a
    /// corner, snapping to a window edge — are unit-tested rather than discovered on screen.
    /// </summary>
    public static class CaptureGeometry
    {
        /// <summary>
        /// The bounding box of every monitor: the "virtual desktop". Monitors left of or above
        /// the primary have negative coordinates, so this origin is frequently not (0,0) — the
        /// classic source of screenshots that come out shifted.
        /// </summary>
        public static PixelRect VirtualBounds(IReadOnlyList<PixelRect> monitors)
        {
            if (monitors == null || monitors.Count == 0) return default;
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (var m in monitors)
            {
                if (m.IsEmpty) continue;
                l = Math.Min(l, m.Left); t = Math.Min(t, m.Top);
                r = Math.Max(r, m.Right); b = Math.Max(b, m.Bottom);
            }
            return l == int.MaxValue ? default : PixelRect.FromEdges(l, t, r, b);
        }

        /// <summary>Keeps a selection inside the desktop without changing its size where possible.</summary>
        public static PixelRect Clamp(PixelRect rect, PixelRect bounds)
        {
            if (bounds.IsEmpty) return rect;
            int w = Math.Min(rect.Width, bounds.Width);
            int h = Math.Min(rect.Height, bounds.Height);
            int x = Math.Clamp(rect.X, bounds.Left, bounds.Right - w);
            int y = Math.Clamp(rect.Y, bounds.Top, bounds.Bottom - h);
            return new PixelRect(x, y, w, h);
        }

        /// <summary>The monitor a point falls on, or the nearest one when it falls in a gap.</summary>
        public static MonitorInfo? MonitorAt(IReadOnlyList<MonitorInfo> monitors, int x, int y)
        {
            if (monitors == null || monitors.Count == 0) return null;
            foreach (var m in monitors)
                if (m.Bounds.Contains(x, y)) return m;

            MonitorInfo? best = null;
            long bestDist = long.MaxValue;
            foreach (var m in monitors)
            {
                long dx = x < m.Bounds.Left ? m.Bounds.Left - x : x >= m.Bounds.Right ? x - m.Bounds.Right + 1 : 0;
                long dy = y < m.Bounds.Top ? m.Bounds.Top - y : y >= m.Bounds.Bottom ? y - m.Bounds.Bottom + 1 : 0;
                long d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = m; }
            }
            return best;
        }

        /// <summary>
        /// The window under the cursor for click-to-capture. <paramref name="windows"/> must be
        /// front-most first (Z order), so the first hit is the one the user can actually see.
        /// </summary>
        public static CaptureTarget? WindowAt(IReadOnlyList<CaptureTarget> windows, int x, int y)
        {
            if (windows == null) return null;
            foreach (var w in windows)
                if (!w.Bounds.IsEmpty && w.Bounds.Contains(x, y)) return w;
            return null;
        }

        /// <summary>
        /// Pulls selection edges onto nearby guide edges (monitor and window borders) when they
        /// are within <paramref name="threshold"/> pixels. Each edge snaps independently, so a
        /// selection can align to a window's left edge and the monitor's bottom at once.
        /// </summary>
        public static PixelRect SnapToGuides(PixelRect rect, IReadOnlyList<PixelRect> guides, int threshold)
        {
            if (guides == null || guides.Count == 0 || threshold <= 0) return rect;

            int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
            int bestL = threshold + 1, bestT = threshold + 1, bestR = threshold + 1, bestB = threshold + 1;
            int snapL = left, snapT = top, snapR = right, snapB = bottom;

            foreach (var g in guides)
            {
                if (g.IsEmpty) continue;
                for (int i = 0; i < 2; i++)
                {
                    int gx = i == 0 ? g.Left : g.Right;
                    int dl = Math.Abs(gx - left);
                    if (dl <= threshold && dl < bestL) { bestL = dl; snapL = gx; }
                    int dr = Math.Abs(gx - right);
                    if (dr <= threshold && dr < bestR) { bestR = dr; snapR = gx; }

                    int gy = i == 0 ? g.Top : g.Bottom;
                    int dt = Math.Abs(gy - top);
                    if (dt <= threshold && dt < bestT) { bestT = dt; snapT = gy; }
                    int db = Math.Abs(gy - bottom);
                    if (db <= threshold && db < bestB) { bestB = db; snapB = gy; }
                }
            }

            // Never let snapping invert or collapse the selection.
            if (snapR <= snapL) { snapL = left; snapR = right; }
            if (snapB <= snapT) { snapT = top; snapB = bottom; }
            return PixelRect.FromEdges(snapL, snapT, snapR, snapB);
        }

        /// <summary>
        /// The source square the magnifier reads, centred on the cursor and kept inside the
        /// frozen frame so the loupe never samples outside the bitmap near an edge or corner.
        /// </summary>
        public static PixelRect MagnifierSource(int cx, int cy, int size, PixelRect bounds)
        {
            size = Math.Max(1, size);
            if (bounds.IsEmpty) return new PixelRect(cx - size / 2, cy - size / 2, size, size);
            size = Math.Min(size, Math.Min(bounds.Width, bounds.Height));
            int x = Math.Clamp(cx - size / 2, bounds.Left, bounds.Right - size);
            int y = Math.Clamp(cy - size / 2, bounds.Top, bounds.Bottom - size);
            return new PixelRect(x, y, size, size);
        }

        /// <summary>
        /// Where to place a floating readout (dimensions, loupe) so it stays on screen: beside
        /// the cursor by default, flipping to the other side near the right or bottom edge.
        /// </summary>
        public static (int X, int Y) PlaceNearCursor(int cx, int cy, int w, int h, int gap, PixelRect bounds)
        {
            int x = cx + gap;
            int y = cy + gap;
            if (!bounds.IsEmpty)
            {
                if (x + w > bounds.Right) x = cx - gap - w;
                if (y + h > bounds.Bottom) y = cy - gap - h;
                x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - w));
                y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - h));
            }
            return (x, y);
        }

        /// <summary>
        /// Grows or shrinks a selection by dragging one handle, normalising if the drag crosses
        /// the opposite edge (so pulling the left handle past the right edge keeps working).
        /// </summary>
        public static PixelRect ResizeBy(PixelRect rect, ResizeHandle handle, int dx, int dy)
        {
            int l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
            if (handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft) l += dx;
            if (handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight) r += dx;
            if (handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight) t += dy;
            if (handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight) b += dy;
            return PixelRect.FromPoints(l, t, r, b);
        }

        /// <summary>Which handle (if any) a point is over, for a selection's eight grips.</summary>
        public static ResizeHandle HandleAt(PixelRect rect, int x, int y, int grip)
        {
            if (rect.IsEmpty) return ResizeHandle.None;
            bool left = Math.Abs(x - rect.Left) <= grip;
            bool right = Math.Abs(x - rect.Right) <= grip;
            bool top = Math.Abs(y - rect.Top) <= grip;
            bool bottom = Math.Abs(y - rect.Bottom) <= grip;
            bool withinX = x >= rect.Left - grip && x <= rect.Right + grip;
            bool withinY = y >= rect.Top - grip && y <= rect.Bottom + grip;

            if (left && top) return ResizeHandle.TopLeft;
            if (right && top) return ResizeHandle.TopRight;
            if (left && bottom) return ResizeHandle.BottomLeft;
            if (right && bottom) return ResizeHandle.BottomRight;
            if (left && withinY) return ResizeHandle.Left;
            if (right && withinY) return ResizeHandle.Right;
            if (top && withinX) return ResizeHandle.Top;
            if (bottom && withinX) return ResizeHandle.Bottom;
            return ResizeHandle.None;
        }
    }

    public enum ResizeHandle
    {
        None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left
    }
}
