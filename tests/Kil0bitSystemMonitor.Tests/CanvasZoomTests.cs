using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Controls;
using Xunit;

using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Zooming the capture editor's canvas around a point: the image pixel under the pointer (or
    /// under the middle of the view) stays where it is. The scroller and canvas are measured and
    /// arranged in code; no window is shown.
    /// </summary>
    public class CanvasZoomTests
    {
        private const int ImageWidth = 2000, ImageHeight = 1500;
        private const double Padding = 16;

        private static readonly Lazy<BitmapSource> s_image = new(() => Image(ImageWidth, ImageHeight));

        /// <summary>A plain generated image, frozen so every test can share it.</summary>
        internal static BitmapSource Image(int width, int height)
        {
            var pixels = new int[width * height];
            Array.Fill(pixels, unchecked((int)0xFF336699));
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }

        private sealed record View(ScrollViewer Scroller, AnnotationCanvas Canvas);

        /// <summary>The editor's scroller and canvas, 800 x 600, laid out at a zoom and a scroll position.</summary>
        private static View Build(double zoom, double scrollX = 0, double scrollY = 0)
        {
            var canvas = new AnnotationCanvas
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(Padding),
                UseLayoutRounding = true,
                Content = canvas,
            };
            // The editor window is dark-themed; without a theme the scroll bars' brushes are missing.
            ModernWpf.ThemeManager.SetRequestedTheme(scroller, ModernWpf.ElementTheme.Dark);
            canvas.Load(s_image.Value);
            canvas.SetZoom(zoom);
            scroller.Measure(new Size(800, 600));
            scroller.Arrange(new Rect(0, 0, 800, 600));
            scroller.UpdateLayout();

            scroller.ScrollToHorizontalOffset(scrollX);
            scroller.ScrollToVerticalOffset(scrollY);
            scroller.UpdateLayout();
            Assert.Equal(scrollX, scroller.HorizontalOffset, 3);
            Assert.Equal(scrollY, scroller.VerticalOffset, 3);
            return new View(scroller, canvas);
        }

        /// <summary>The image pixel shown at a point of the scroller.</summary>
        internal static Point ImageAt(ScrollViewer scroller, AnnotationCanvas canvas, Point inScroller)
        {
            var onCanvas = scroller.TranslatePoint(inScroller, canvas);
            return new Point(onCanvas.X / canvas.Zoom, onCanvas.Y / canvas.Zoom);
        }

        /// <summary>Where an image pixel is shown, in the scroller's coordinates.</summary>
        internal static Point ShownAt(ScrollViewer scroller, AnnotationCanvas canvas, Point image)
            => canvas.TranslatePoint(new Point(image.X * canvas.Zoom, image.Y * canvas.Zoom), scroller);

        private static Point ImageAt(View view, Point inScroller) => ImageAt(view.Scroller, view.Canvas, inScroller);

        private static Point ShownAt(View view, Point image) => ShownAt(view.Scroller, view.Canvas, image);

        internal static void AssertWithinOneDip(Point expected, Point actual)
        {
            Assert.InRange(actual.X - expected.X, -1.0, 1.0);
            Assert.InRange(actual.Y - expected.Y, -1.0, 1.0);
        }

        private static ScrollContentPresenter PresenterOf(View view)
        {
            DependencyObject? at = view.Canvas;
            while (at != null && at is not ScrollContentPresenter) at = VisualTreeHelper.GetParent(at);
            return Assert.IsType<ScrollContentPresenter>(at);
        }

        [Fact]
        public void Zooming_in_and_back_out_at_an_off_centre_point_keeps_the_image_under_it_when_not_scrolled() => UiThread.Run(() =>
        {
            var view = Build(0.5);
            var anchor = new Point(600, 150);
            var image = ImageAt(view, anchor);
            Assert.Equal((600 - Padding) / 0.5, image.X, 3);
            Assert.Equal((150 - Padding) / 0.5, image.Y, 3);

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, 1.0, anchor);

            Assert.Equal(1.0, view.Canvas.Zoom);
            AssertWithinOneDip(anchor, ShownAt(view, image));
            // The view had to scroll to keep it there: twice the size puts that pixel twice as far in.
            AssertWithinOneDip(new Point(600 - Padding, 150 - Padding),
                new Point(view.Scroller.HorizontalOffset, view.Scroller.VerticalOffset));

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, 0.5, anchor);

            Assert.Equal(0.5, view.Canvas.Zoom);
            AssertWithinOneDip(anchor, ShownAt(view, image));
            AssertWithinOneDip(new Point(0, 0), new Point(view.Scroller.HorizontalOffset, view.Scroller.VerticalOffset));
        });

        [Theory]
        [InlineData(1.25)]
        [InlineData(2.0)]
        [InlineData(8.0)]
        [InlineData(0.8)]
        [InlineData(0.6)]
        public void Zooming_at_an_off_centre_point_keeps_the_image_under_it_when_scrolled(double newZoom) => UiThread.Run(() =>
        {
            var view = Build(1.0, scrollX: 400, scrollY: 300);
            var anchor = new Point(200, 450);
            var image = ImageAt(view, anchor);
            Assert.Equal(400 + 200 - Padding, image.X, 3);
            Assert.Equal(300 + 450 - Padding, image.Y, 3);

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, newZoom, anchor);

            Assert.Equal(newZoom, view.Canvas.Zoom);
            AssertWithinOneDip(anchor, ShownAt(view, image));
        });

        [Fact]
        public void A_run_of_wheel_steps_in_and_out_keeps_the_same_image_point_under_the_pointer() => UiThread.Run(() =>
        {
            var view = Build(1.0, scrollX: 400, scrollY: 300);
            var anchor = new Point(520, 130);
            var image = ImageAt(view, anchor);

            double zoom = 1.0;
            foreach (double factor in new[] { 1.25, 1.25, 1.25, 0.8, 0.8, 0.8, 0.8, 1.25 })
            {
                zoom *= factor;
                CanvasZoom.ZoomAt(view.Scroller, view.Canvas, zoom, anchor);
                Assert.Equal(zoom, view.Canvas.Zoom, 9);
                AssertWithinOneDip(anchor, ShownAt(view, image));
            }
        });

        [Fact]
        public void Zooming_out_until_the_image_is_smaller_than_the_view_leaves_it_centred_and_unscrolled() => UiThread.Run(() =>
        {
            var view = Build(1.0, scrollX: 400, scrollY: 300);

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, 0.2, new Point(200, 450));

            Assert.Equal(0.2, view.Canvas.Zoom);
            Assert.Equal(0, view.Scroller.HorizontalOffset);
            Assert.Equal(0, view.Scroller.VerticalOffset);
            Assert.Equal(0, view.Scroller.ScrollableWidth);
            Assert.Equal(0, view.Scroller.ScrollableHeight);

            // Centred as before: the middle of the image is the middle of the view.
            var presenter = PresenterOf(view);
            var middle = presenter.TranslatePoint(new Point(presenter.ActualWidth / 2, presenter.ActualHeight / 2), view.Scroller);
            AssertWithinOneDip(middle, ShownAt(view, new Point(ImageWidth / 2.0, ImageHeight / 2.0)));
        });

        [Fact]
        public void Zooming_in_from_a_centred_image_keeps_the_point_under_the_pointer() => UiThread.Run(() =>
        {
            var view = Build(0.2);                       // 400 x 300: centred, nothing to scroll
            var anchor = new Point(450, 330);
            var image = ImageAt(view, anchor);
            Assert.InRange(image.X, 0, ImageWidth);
            Assert.InRange(image.Y, 0, ImageHeight);

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, 1.0, anchor);

            Assert.Equal(1.0, view.Canvas.Zoom);
            AssertWithinOneDip(anchor, ShownAt(view, image));
        });

        [Fact]
        public void The_viewport_centre_is_the_middle_of_the_visible_part() => UiThread.Run(() =>
        {
            foreach (double zoom in new[] { 1.0, 0.2 })   // with scroll bars showing, and without
            {
                var view = Build(zoom);
                var presenter = PresenterOf(view);
                var middle = presenter.TranslatePoint(new Point(presenter.ActualWidth / 2, presenter.ActualHeight / 2), view.Scroller);

                AssertWithinOneDip(middle, CanvasZoom.ViewportCentre(view.Scroller));
            }
        });

        [Fact]
        public void The_canvas_holds_any_zoom_inside_its_own_safety_net() => UiThread.Run(() =>
        {
            var view = Build(1.0);

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, 1000, new Point(100, 100));
            Assert.Equal(16, view.Canvas.Zoom);

            CanvasZoom.ZoomAt(view.Scroller, view.Canvas, 0.00001, new Point(100, 100));
            Assert.Equal(0.005, view.Canvas.Zoom);

            // 1% on a 150% display is below the old 5% floor, and 800% on a 100% display is the old ceiling.
            view.Canvas.SetZoom(0.01 / 1.5);
            Assert.Equal(0.01 / 1.5, view.Canvas.Zoom);
            view.Canvas.SetZoom(8);
            Assert.Equal(8, view.Canvas.Zoom);

            view.Canvas.SetZoom(double.NaN);                // not a size anything can be laid out at
            Assert.Equal(8, view.Canvas.Zoom);
        });

        [Fact]
        public void A_canvas_that_is_not_laid_out_yet_just_takes_the_zoom() => UiThread.Run(() =>
        {
            var canvas = new AnnotationCanvas();
            var scroller = new ScrollViewer { Content = canvas };   // never measured: no template, no presenter
            canvas.Load(Image(40, 30));

            CanvasZoom.ZoomAt(scroller, canvas, 2.0, new Point(10, 10));

            Assert.Equal(2.0, canvas.Zoom);
            var centre = CanvasZoom.ViewportCentre(scroller);       // nothing to throw on either
            Assert.False(double.IsNaN(centre.X) || double.IsNaN(centre.Y));
        });
    }
}
