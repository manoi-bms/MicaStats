using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Selecting, moving and resizing a mark after it is drawn. These pin the behaviours a user
    /// notices immediately: clicking a shape selects it, clicking empty space does not, the
    /// front-most of two overlapping marks wins, a freehand squiggle is grabbed by its line
    /// rather than its mostly-empty bounding box, and a handle dragged past the far edge flips
    /// the shape instead of collapsing it.
    /// </summary>
    public class AnnotationGeometryTests
    {
        private static ShapeAnnotation Rect(double x1, double y1, double x2, double y2) =>
            new(CaptureTool.Rectangle, new ImgPoint(x1, y1), new ImgPoint(x2, y2)) { Thickness = 3 };

        [Fact]
        public void Clicking_inside_a_shape_hits_it()
        {
            var r = Rect(100, 100, 300, 200);
            Assert.True(AnnotationGeometry.HitTest(r, 200, 150));
            Assert.True(AnnotationGeometry.HitTest(r, 100, 100));
        }

        [Fact]
        public void Clicking_away_from_a_shape_misses_it()
        {
            var r = Rect(100, 100, 300, 200);
            Assert.False(AnnotationGeometry.HitTest(r, 400, 150));
            Assert.False(AnnotationGeometry.HitTest(r, 200, 400));
        }

        [Fact]
        public void The_front_most_of_two_overlapping_marks_is_selected()
        {
            var behind = Rect(0, 0, 400, 400);
            var infront = Rect(100, 100, 200, 200);
            var items = new List<Annotation> { behind, infront };   // painted in order

            Assert.Same(infront, AnnotationGeometry.TopMost(items, 150, 150));
            Assert.Same(behind, AnnotationGeometry.TopMost(items, 350, 350));
            Assert.Null(AnnotationGeometry.TopMost(items, 900, 900));
        }

        [Fact]
        public void A_freehand_stroke_is_grabbed_by_its_line_not_its_bounding_box()
        {
            // A diagonal stroke: the box corners are far from any ink.
            var stroke = new StrokeAnnotation(new List<ImgPoint>
            {
                new(0, 0), new(100, 100),
            }, Highlighter: false) { Thickness = 3 };

            Assert.True(AnnotationGeometry.HitTest(stroke, 50, 50));    // on the line
            Assert.False(AnnotationGeometry.HitTest(stroke, 5, 95));    // inside the box, no ink
        }

        [Fact]
        public void A_highlighter_is_easier_to_hit_because_it_paints_wider()
        {
            var pen = new StrokeAnnotation(new List<ImgPoint> { new(0, 0), new(100, 0) }, false) { Thickness = 2 };
            var highlighter = new StrokeAnnotation(new List<ImgPoint> { new(0, 0), new(100, 0) }, true) { Thickness = 2 };

            // The pen reaches 4px of slack + half of its 2px stroke; the highlighter paints at
            // 10px, so it reaches 4 + 5. A point 7px off the line falls between the two.
            Assert.False(AnnotationGeometry.HitTest(pen, 50, 7));
            Assert.True(AnnotationGeometry.HitTest(highlighter, 50, 7));
        }

        [Fact]
        public void A_single_point_stroke_is_still_selectable()
        {
            var dot = new StrokeAnnotation(new List<ImgPoint> { new(50, 50) }, false) { Thickness = 4 };
            Assert.True(AnnotationGeometry.HitTest(dot, 51, 51));
            Assert.False(AnnotationGeometry.HitTest(dot, 90, 90));
        }

        [Fact]
        public void Step_badges_are_hit_within_their_radius()
        {
            var step = new StepAnnotation(new ImgPoint(100, 100), 1, 16);
            Assert.True(AnnotationGeometry.HitTest(step, 105, 105));
            Assert.False(AnnotationGeometry.HitTest(step, 160, 100));
        }

        [Fact]
        public void Text_has_a_grab_area_derived_from_its_length()
        {
            var text = new TextAnnotation(new ImgPoint(10, 10), "hello world", 20);
            var box = AnnotationGeometry.BoundsOf(text);

            Assert.True(box.Width > 50);
            Assert.True(AnnotationGeometry.HitTest(text, 20, 20));
            Assert.False(AnnotationGeometry.HitTest(text, 600, 20));
        }

        [Fact]
        public void Moving_a_shape_shifts_both_corners()
        {
            var moved = (ShapeAnnotation)AnnotationGeometry.Translate(Rect(100, 100, 200, 200), 25, -10);
            Assert.Equal(125, moved.Start.X);
            Assert.Equal(90, moved.Start.Y);
            Assert.Equal(225, moved.End.X);
            Assert.Equal(190, moved.End.Y);
        }

        [Fact]
        public void Moving_a_stroke_shifts_every_point()
        {
            var stroke = new StrokeAnnotation(new List<ImgPoint> { new(0, 0), new(10, 10) }, false);
            var moved = (StrokeAnnotation)AnnotationGeometry.Translate(stroke, 5, 5);

            Assert.Equal(new ImgPoint(5, 5), moved.Points[0]);
            Assert.Equal(new ImgPoint(15, 15), moved.Points[1]);
            // The original is untouched: annotations are immutable, which is what makes undo work.
            Assert.Equal(new ImgPoint(0, 0), stroke.Points[0]);
        }

        [Fact]
        public void Moving_a_redaction_keeps_its_style_and_strength()
        {
            var redaction = new RedactAnnotation(new ImgPoint(0, 0), new ImgPoint(50, 20), RedactStyle.Blur)
            { Strength = 17 };
            var moved = (RedactAnnotation)AnnotationGeometry.Translate(redaction, 10, 10);

            Assert.Equal(RedactStyle.Blur, moved.Style);
            Assert.Equal(17, moved.Strength);
            Assert.Equal(new ImgPoint(10, 10), moved.Start);
        }

        [Fact]
        public void Rect_like_marks_expose_eight_handles_and_shapes_that_cannot_resize_expose_none()
        {
            Assert.Equal(8, AnnotationGeometry.HandlesFor(Rect(0, 0, 10, 10)).Count);
            Assert.Equal(8, AnnotationGeometry.HandlesFor(
                new RedactAnnotation(new ImgPoint(0, 0), new ImgPoint(10, 10), RedactStyle.Pixelate)).Count);

            Assert.Empty(AnnotationGeometry.HandlesFor(new StepAnnotation(new ImgPoint(0, 0), 1, 10)));
            Assert.Empty(AnnotationGeometry.HandlesFor(new TextAnnotation(new ImgPoint(0, 0), "x", 12)));
        }

        [Fact]
        public void An_arrow_exposes_its_two_endpoints_rather_than_a_box()
        {
            var arrow = new ShapeAnnotation(CaptureTool.Arrow, new ImgPoint(10, 90), new ImgPoint(80, 20));
            var handles = AnnotationGeometry.HandlesFor(arrow);

            Assert.Equal(2, handles.Count);
            Assert.Contains(handles, h => h.At.Equals(new ImgPoint(10, 90)));
            Assert.Contains(handles, h => h.At.Equals(new ImgPoint(80, 20)));
        }

        [Fact]
        public void Dragging_an_arrow_endpoint_moves_only_that_end()
        {
            var arrow = new ShapeAnnotation(CaptureTool.Arrow, new ImgPoint(0, 0), new ImgPoint(100, 0));
            var pulled = (ShapeAnnotation)AnnotationGeometry.Resize(arrow, ResizeHandle.BottomRight, 20, 5);

            Assert.Equal(new ImgPoint(0, 0), pulled.Start);
            Assert.Equal(new ImgPoint(120, 5), pulled.End);
        }

        [Fact]
        public void Dragging_a_corner_resizes_the_shape()
        {
            var resized = (ShapeAnnotation)AnnotationGeometry.Resize(
                Rect(100, 100, 200, 200), ResizeHandle.BottomRight, 50, 30);
            var box = AnnotationGeometry.GeometryBox(resized);

            Assert.Equal(250, box.Right, 1);
            Assert.Equal(230, box.Bottom, 1);
            Assert.Equal(100, box.Left, 1);   // the anchored corner does not move
            Assert.Equal(100, box.Top, 1);
        }

        [Fact]
        public void Repeated_resizing_does_not_make_a_shape_creep_outwards()
        {
            // Regression: resizing once used the stroke-padded bounds and wrote them back as the
            // shape's corners, so every drag silently grew the shape by its line width.
            Annotation shape = Rect(100, 100, 200, 200);
            for (int i = 0; i < 5; i++)
                shape = AnnotationGeometry.Resize(shape, ResizeHandle.BottomRight, 10, 10);

            var box = AnnotationGeometry.GeometryBox(shape);
            Assert.Equal(100, box.Left, 3);
            Assert.Equal(100, box.Top, 3);
            Assert.Equal(250, box.Right, 3);    // exactly 200 + 5 drags x 10
            Assert.Equal(250, box.Bottom, 3);
        }

        [Fact]
        public void Grabbing_a_handle_and_releasing_leaves_the_shape_untouched()
        {
            var original = Rect(100, 100, 200, 200);
            var afterNoOpDrag = AnnotationGeometry.Resize(original, ResizeHandle.BottomRight, 0, 0);

            var a = AnnotationGeometry.GeometryBox(original);
            var b = AnnotationGeometry.GeometryBox(afterNoOpDrag);
            Assert.Equal(a.Left, b.Left, 3);
            Assert.Equal(a.Top, b.Top, 3);
            Assert.Equal(a.Right, b.Right, 3);
            Assert.Equal(a.Bottom, b.Bottom, 3);
        }

        [Fact]
        public void Handles_sit_on_the_drawn_corners_not_the_padded_bounds()
        {
            var r = Rect(100, 100, 200, 200);   // thickness 3, so bounds pad by 1.5
            var handles = AnnotationGeometry.HandlesFor(r);

            Assert.Contains(handles, h => h.Handle == ResizeHandle.TopLeft &&
                                          h.At.Equals(new ImgPoint(100, 100)));
            Assert.Contains(handles, h => h.Handle == ResizeHandle.BottomRight &&
                                          h.At.Equals(new ImgPoint(200, 200)));
        }

        [Fact]
        public void Dragging_an_edge_past_the_opposite_one_flips_instead_of_collapsing()
        {
            var flipped = AnnotationGeometry.Resize(Rect(100, 100, 200, 200), ResizeHandle.Left, 300, 0);
            var box = AnnotationGeometry.BoundsOf(flipped);

            Assert.True(box.Width > 0);
            Assert.True(box.Left < box.Right);
        }

        [Fact]
        public void Handle_hit_testing_finds_the_corner_under_the_pointer()
        {
            var r = Rect(100, 100, 200, 200);
            var box = AnnotationGeometry.GeometryBox(r);

            Assert.Equal(ResizeHandle.TopLeft,
                AnnotationGeometry.HandleAt(r, box.Left, box.Top, 6));
            Assert.Equal(ResizeHandle.BottomRight,
                AnnotationGeometry.HandleAt(r, box.Right, box.Bottom, 6));
            Assert.Equal(ResizeHandle.None,
                AnnotationGeometry.HandleAt(r, box.CenterX, box.CenterY, 6));
        }

        [Fact]
        public void Distance_to_a_segment_clamps_at_its_ends()
        {
            var a = new ImgPoint(0, 0);
            var b = new ImgPoint(100, 0);

            Assert.Equal(0, AnnotationGeometry.DistanceToSegment(50, 0, a, b), 3);
            Assert.Equal(10, AnnotationGeometry.DistanceToSegment(50, 10, a, b), 3);
            // Beyond the end, distance is measured to the endpoint, not the infinite line.
            Assert.Equal(50, AnnotationGeometry.DistanceToSegment(150, 0, a, b), 3);
        }
    }

    /// <summary>Editing a committed mark must be one undo step and must keep paint order.</summary>
    public class AnnotationReplaceTests
    {
        private static readonly PixelRect Full = new(0, 0, 800, 600);

        private static ShapeAnnotation Rect(double x) =>
            new(CaptureTool.Rectangle, new ImgPoint(x, 0), new ImgPoint(x + 50, 50));

        [Fact]
        public void Replace_swaps_in_place_and_is_a_single_undo_step()
        {
            var doc = new AnnotationDocument(Full);
            var first = Rect(0);
            var second = Rect(100);
            doc.Add(first);
            doc.Add(second);

            var moved = AnnotationGeometry.Translate(first, 10, 10);
            Assert.True(doc.Replace(first, moved));

            Assert.Equal(2, doc.Items.Count);
            Assert.Same(moved, doc.Items[0]);       // still behind the second mark
            Assert.Same(second, doc.Items[1]);

            doc.Undo();
            Assert.Same(first, doc.Items[0]);
        }

        [Fact]
        public void Replacing_a_mark_that_is_not_in_the_document_does_nothing()
        {
            var doc = new AnnotationDocument(Full);
            doc.Add(Rect(0));
            Assert.False(doc.Replace(Rect(999), Rect(998)));
            Assert.Single(doc.Items);
        }

        [Fact]
        public void A_drag_that_changes_nothing_is_not_recorded()
        {
            var doc = new AnnotationDocument(Full);
            var mark = Rect(0);
            doc.Add(mark);
            bool couldUndoBefore = doc.CanUndo;

            Assert.False(doc.Replace(mark, mark));
            Assert.Equal(couldUndoBefore, doc.CanUndo);
        }
    }
}
