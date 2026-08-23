using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>An annotation's extent in image pixels.</summary>
    public readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
        public double CenterX => (Left + Right) / 2;
        public double CenterY => (Top + Bottom) / 2;

        public static Box FromPoints(double x1, double y1, double x2, double y2) =>
            new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

        public bool Contains(double x, double y, double pad = 0) =>
            x >= Left - pad && x <= Right + pad && y >= Top - pad && y <= Bottom + pad;

        public Box Inflate(double d) => new(Left - d, Top - d, Right + d, Bottom + d);
    }

    /// <summary>
    /// Hit-testing and transforms for annotations — the maths behind selecting, moving and
    /// resizing a mark after it has been drawn.
    ///
    /// <para>
    /// Kept pure and separate from the canvas so the fiddly parts are unit-tested: which mark
    /// wins when two overlap, whether a click near a freehand squiggle counts as a hit (its
    /// bounding box is mostly empty space, so strokes are measured against their actual
    /// segments), and that dragging a handle past the opposite edge flips rather than inverts.
    /// </para>
    ///
    /// <para>
    /// Annotations are immutable records, so every transform returns a NEW annotation. The
    /// editor commits one replacement per gesture, which keeps undo at one step per edit.
    /// </para>
    /// </summary>
    public static class AnnotationGeometry
    {
        /// <summary>Extra pixels of slack around a mark when hit-testing, before stroke width.</summary>
        public const double DefaultTolerance = 4;

        /// <summary>
        /// Text is measured without a font: width is approximated from the character count and
        /// font size. Selection only needs a plausible grab area, and this keeps the whole module
        /// free of WPF so it stays testable — the renderer remains the authority on exact glyphs.
        /// </summary>
        private const double AverageGlyphWidthRatio = 0.55;
        private const double LineHeightRatio = 1.4;

        public static Box BoundsOf(Annotation annotation)
        {
            switch (annotation)
            {
                case ShapeAnnotation s:
                    return Box.FromPoints(s.Start.X, s.Start.Y, s.End.X, s.End.Y).Inflate(s.Thickness / 2);

                case RedactAnnotation r:
                    return Box.FromPoints(r.Start.X, r.Start.Y, r.End.X, r.End.Y);

                case StepAnnotation p:
                    return new Box(p.Center.X - p.Radius, p.Center.Y - p.Radius,
                                   p.Center.X + p.Radius, p.Center.Y + p.Radius);

                case TextAnnotation t:
                {
                    double w = Math.Max(1, t.Text?.Length ?? 0) * t.FontSize * AverageGlyphWidthRatio;
                    double h = t.FontSize * LineHeightRatio;
                    return new Box(t.Origin.X, t.Origin.Y, t.Origin.X + w, t.Origin.Y + h);
                }

                case StrokeAnnotation k when k.Points is { Count: > 0 }:
                {
                    double l = double.MaxValue, tp = double.MaxValue, rr = double.MinValue, b = double.MinValue;
                    foreach (var p in k.Points)
                    {
                        l = Math.Min(l, p.X); tp = Math.Min(tp, p.Y);
                        rr = Math.Max(rr, p.X); b = Math.Max(b, p.Y);
                    }
                    return new Box(l, tp, rr, b).Inflate(StrokeWidth(k) / 2);
                }

                default:
                    return default;
            }
        }

        /// <summary>
        /// A mark's geometric rectangle — the corners as drawn, with no allowance for stroke
        /// width.
        ///
        /// <para>
        /// Distinct from <see cref="BoundsOf"/>, which pads by half the stroke so a click on a
        /// thick outline still counts as a hit. Resizing must use THIS one: feeding the padded
        /// bounds back into Start/End would grow the shape by its line width on every drag, so a
        /// rectangle would creep outwards each time it was nudged.
        /// </para>
        /// </summary>
        public static Box GeometryBox(Annotation annotation) => annotation switch
        {
            ShapeAnnotation s => Box.FromPoints(s.Start.X, s.Start.Y, s.End.X, s.End.Y),
            RedactAnnotation r => Box.FromPoints(r.Start.X, r.Start.Y, r.End.X, r.End.Y),
            _ => BoundsOf(annotation),
        };

        /// <summary>The width a stroke actually paints with, which the renderer widens for a highlighter.</summary>
        public static double StrokeWidth(StrokeAnnotation s) =>
            s.Highlighter ? Math.Max(10, s.Thickness * 5) : s.Thickness;

        public static bool HitTest(Annotation annotation, double x, double y, double tolerance = DefaultTolerance)
        {
            // A freehand stroke's bounding box is mostly empty, so measure the real segments.
            if (annotation is StrokeAnnotation stroke)
                return HitStroke(stroke, x, y, tolerance);

            // Everything else is grabbable anywhere inside its extent. Screenshot marks are small
            // and rarely nested, so a forgiving target beats making the user find an outline.
            return BoundsOf(annotation).Contains(x, y, tolerance);
        }

        private static bool HitStroke(StrokeAnnotation stroke, double x, double y, double tolerance)
        {
            if (stroke.Points == null || stroke.Points.Count == 0) return false;
            double reach = tolerance + StrokeWidth(stroke) / 2;

            if (stroke.Points.Count == 1)
            {
                var only = stroke.Points[0];
                return Math.Sqrt(Sq(x - only.X) + Sq(y - only.Y)) <= reach;
            }

            for (int i = 1; i < stroke.Points.Count; i++)
            {
                if (DistanceToSegment(x, y, stroke.Points[i - 1], stroke.Points[i]) <= reach) return true;
            }
            return false;
        }

        /// <summary>Shortest distance from a point to a line segment.</summary>
        public static double DistanceToSegment(double px, double py, ImgPoint a, ImgPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq <= double.Epsilon) return Math.Sqrt(Sq(px - a.X) + Sq(py - a.Y));

            // Projection of the point onto the segment, clamped to its ends.
            double t = Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / lenSq, 0, 1);
            double cx = a.X + t * dx, cy = a.Y + t * dy;
            return Math.Sqrt(Sq(px - cx) + Sq(py - cy));
        }

        /// <summary>
        /// The mark under a point, front-most first. Later annotations are painted on top, so the
        /// list is walked backwards — the one the user can see is the one they get.
        /// </summary>
        public static Annotation? TopMost(IReadOnlyList<Annotation> items, double x, double y,
                                          double tolerance = DefaultTolerance)
        {
            if (items == null) return null;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (HitTest(items[i], x, y, tolerance)) return items[i];
            }
            return null;
        }

        // ----- Transforms --------------------------------------------------------------------

        public static Annotation Translate(Annotation annotation, double dx, double dy) => annotation switch
        {
            ShapeAnnotation s => s with { Start = Shift(s.Start, dx, dy), End = Shift(s.End, dx, dy) },
            RedactAnnotation r => r with { Start = Shift(r.Start, dx, dy), End = Shift(r.End, dx, dy) },
            StepAnnotation p => p with { Center = Shift(p.Center, dx, dy) },
            TextAnnotation t => t with { Origin = Shift(t.Origin, dx, dy) },
            StrokeAnnotation k => k with { Points = k.Points.Select(p => Shift(p, dx, dy)).ToList() },
            _ => annotation,
        };

        /// <summary>Marks whose size can be dragged. Strokes, text and step badges only move.</summary>
        public static bool CanResize(Annotation annotation) =>
            annotation is ShapeAnnotation or RedactAnnotation;

        /// <summary>
        /// The grab handles for a mark, as (handle, position) pairs in image pixels.
        ///
        /// <para>
        /// A line or arrow gets two handles at its actual endpoints rather than a box of eight:
        /// dragging an arrow's tip is the gesture people expect. Those two reuse the TopLeft and
        /// BottomRight identifiers to mean "first point" and "second point".
        /// </para>
        /// </summary>
        public static IReadOnlyList<(ResizeHandle Handle, ImgPoint At)> HandlesFor(Annotation annotation)
        {
            if (annotation is ShapeAnnotation line &&
                (line.Shape == CaptureTool.Line || line.Shape == CaptureTool.Arrow))
            {
                return new[]
                {
                    (ResizeHandle.TopLeft, line.Start),
                    (ResizeHandle.BottomRight, line.End),
                };
            }

            if (!CanResize(annotation)) return Array.Empty<(ResizeHandle, ImgPoint)>();

            // Handles sit on the drawn corners, not on the stroke-padded bounds, so grabbing one
            // and letting go leaves the shape exactly where it was.
            var b = GeometryBox(annotation);
            return new[]
            {
                (ResizeHandle.TopLeft, new ImgPoint(b.Left, b.Top)),
                (ResizeHandle.Top, new ImgPoint(b.CenterX, b.Top)),
                (ResizeHandle.TopRight, new ImgPoint(b.Right, b.Top)),
                (ResizeHandle.Right, new ImgPoint(b.Right, b.CenterY)),
                (ResizeHandle.BottomRight, new ImgPoint(b.Right, b.Bottom)),
                (ResizeHandle.Bottom, new ImgPoint(b.CenterX, b.Bottom)),
                (ResizeHandle.BottomLeft, new ImgPoint(b.Left, b.Bottom)),
                (ResizeHandle.Left, new ImgPoint(b.Left, b.CenterY)),
            };
        }

        /// <summary>The handle under a point, or None. <paramref name="grip"/> is in image pixels.</summary>
        public static ResizeHandle HandleAt(Annotation annotation, double x, double y, double grip)
        {
            foreach (var pair in HandlesFor(annotation))
            {
                if (Math.Abs(x - pair.At.X) <= grip && Math.Abs(y - pair.At.Y) <= grip) return pair.Handle;
            }
            return ResizeHandle.None;
        }

        /// <summary>
        /// Applies a handle drag. Endpoint handles on a line or arrow move that endpoint;
        /// box handles move the corresponding edges, and dragging one past its opposite simply
        /// flips the mark rather than collapsing it.
        /// </summary>
        public static Annotation Resize(Annotation annotation, ResizeHandle handle, double dx, double dy)
        {
            if (handle == ResizeHandle.None) return annotation;

            if (annotation is ShapeAnnotation line &&
                (line.Shape == CaptureTool.Line || line.Shape == CaptureTool.Arrow))
            {
                return handle == ResizeHandle.TopLeft
                    ? line with { Start = Shift(line.Start, dx, dy) }
                    : line with { End = Shift(line.End, dx, dy) };
            }

            if (!CanResize(annotation)) return annotation;

            var b = GeometryBox(annotation);
            double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;

            if (handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft) left += dx;
            if (handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight) right += dx;
            if (handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight) top += dy;
            if (handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight) bottom += dy;

            var box = Box.FromPoints(left, top, right, bottom);
            var start = new ImgPoint(box.Left, box.Top);
            var end = new ImgPoint(box.Right, box.Bottom);

            return annotation switch
            {
                ShapeAnnotation s => s with { Start = start, End = end },
                RedactAnnotation r => r with { Start = start, End = end },
                _ => annotation,
            };
        }

        private static ImgPoint Shift(ImgPoint p, double dx, double dy) => new(p.X + dx, p.Y + dy);

        private static double Sq(double v) => v * v;
    }
}
