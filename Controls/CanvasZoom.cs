using System.Windows.Controls;

using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Controls
{
    /// <summary>
    /// Zooms an <see cref="AnnotationCanvas"/> inside its <see cref="ScrollViewer"/> around a
    /// point, so what the user is looking at does not jump away when the scale changes.
    /// </summary>
    internal static class CanvasZoom
    {
        /// <summary>
        /// Sets the canvas's zoom and scrolls so the image pixel that was under
        /// <paramref name="anchorInScroller"/> (a point in the scroller's own coordinates: the
        /// pointer, or the middle of the view) is under it again.
        ///
        /// <para>
        /// The image pixel is found before the zoom changes; after it, the scroller is laid out
        /// so the canvas has its new size and place, and the view is scrolled by however far
        /// that pixel ended up from the anchor. The scroller keeps the offsets inside what can
        /// be scrolled, so near an edge the pixel moves rather than the image leaving the view,
        /// and an image smaller than the view stays centred with nothing to scroll.
        /// </para>
        /// </summary>
        public static void ZoomAt(ScrollViewer scroller, AnnotationCanvas canvas, double newZoom, Point anchorInScroller)
        {
            // Before its first layout the canvas is not inside the scroller's visual tree, and
            // there is no place on screen to keep: the zoom is all there is to set.
            bool placed = canvas.Zoom > 0 && canvas.IsDescendantOf(scroller);
            Point image = default;
            if (placed)
            {
                var onCanvas = scroller.TranslatePoint(anchorInScroller, canvas);
                image = new Point(onCanvas.X / canvas.Zoom, onCanvas.Y / canvas.Zoom);
            }

            canvas.SetZoom(newZoom);
            if (!placed) return;

            scroller.UpdateLayout();
            var now = canvas.TranslatePoint(new Point(image.X * canvas.Zoom, image.Y * canvas.Zoom), scroller);
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + now.X - anchorInScroller.X);
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + now.Y - anchorInScroller.Y);
            scroller.UpdateLayout();      // the scroll is queued until layout: apply it now, so the next zoom starts from it
        }

        /// <summary>The middle of the scroller's viewport, in the scroller's own coordinates.</summary>
        public static Point ViewportCentre(ScrollViewer scroller)
            => new(scroller.Padding.Left + scroller.ViewportWidth / 2, scroller.Padding.Top + scroller.ViewportHeight / 2);
    }
}
