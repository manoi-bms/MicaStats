using System;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// The capture editor's zoom arithmetic.
    ///
    /// <para>
    /// Two scales are in play. The canvas's <c>Zoom</c> is device-independent units (DIP) per
    /// image pixel, because that is what WPF lays out in. The percent the user sees is screen
    /// pixels per image pixel: <c>zoom x dpiScale x 100</c>. A screenshot is made of screen
    /// pixels, so 100% shows it exactly as it was captured, on a display at any scaling; at a
    /// zoom of 1 on a 150% display every image pixel would cover 1.5 screen pixels and blur.
    /// </para>
    ///
    /// <para>
    /// <c>dpiScale</c> is screen pixels per DIP (1.5 on a 150% display). A value that is not a
    /// positive number is treated as 1.
    /// </para>
    /// </summary>
    internal static class EditorZoom
    {
        /// <summary>One zoom step: a key press, a button click, or one notch of the wheel.</summary>
        public const double Step = 1.25;

        /// <summary>The smallest zoom shown, in percent. Low enough for a 20,000 px scrolling capture to fit.</summary>
        public const double MinPercent = 1;

        /// <summary>The largest zoom shown, in percent.</summary>
        public const double MaxPercent = 800;

        /// <summary>From this percent up, the image's pixels are drawn as sharp squares.</summary>
        public const double PixelatedFromPercent = 200;

        /// <summary>One wheel notch, as Windows reports it (WHEEL_DELTA).</summary>
        private const double WheelNotch = 120.0;

        /// <summary>
        /// How much of the width-fitted size the whole-image fit must keep for a capture to open
        /// whole. Below it the capture is a tall strip, and opens fitted to its width instead.
        /// </summary>
        private const double WholeAtLeast = 0.5;

        /// <summary>How close to actual size counts as being on it, as a share of it: rounding left over from earlier steps.</summary>
        private const double OnActualSize = 1e-9;

        private static double Scale(double dpiScale)
            => double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;

        /// <summary>Keeps a zoom inside the range whose percent is <see cref="MinPercent"/> to <see cref="MaxPercent"/>.</summary>
        public static double Clamp(double zoom, double dpiScale)
        {
            double scale = Scale(dpiScale);
            return Math.Clamp(zoom, MinPercent / 100 / scale, MaxPercent / 100 / scale);
        }

        /// <summary>
        /// The zoom after <paramref name="steps"/> steps in (positive) or out (negative).
        ///
        /// <para>
        /// A step that would pass actual size stops on it. 100% is the one zoom at which a
        /// capture is exactly as sharp as the screen it was taken from, and steps of 1.25 from an
        /// arbitrary fit (94% to 118%) would otherwise never reach it. From actual size itself a
        /// step leaves as usual.
        /// </para>
        /// </summary>
        public static double Stepped(double zoom, int steps, double dpiScale)
        {
            if (double.IsNaN(zoom)) return zoom;

            double actual = ActualSize(dpiScale);
            double factor = steps > 0 ? Step : 1 / Step;

            for (long left = Math.Abs((long)steps); left > 0; left--)
            {
                double next = zoom * factor;
                bool onActual = Math.Abs(zoom - actual) <= actual * OnActualSize;
                if (!onActual && (zoom < actual) != (next < actual)) next = actual;

                next = Clamp(next, dpiScale);
                if (next == zoom) break;        // at an end of the range: more steps change nothing
                zoom = next;
            }
            return Clamp(zoom, dpiScale);
        }

        /// <summary>
        /// The zoom after a wheel message. One notch (a delta of 120) is one step; a precision
        /// touchpad sends many small deltas, and each scales by its share of a step, so the zoom
        /// follows the fingers smoothly and three deltas of 40 land where one notch would. The
        /// wheel does not stop at actual size: a stream of small deltas would stick there.
        /// </summary>
        public static double Wheel(double zoom, int delta, double dpiScale)
            => Clamp(zoom * Math.Pow(Step, delta / WheelNotch), dpiScale);

        /// <summary>The zoom at which one image pixel is one screen pixel: 100%.</summary>
        public static double ActualSize(double dpiScale) => 1 / Scale(dpiScale);

        /// <summary>
        /// The zoom that shows the whole image inside the viewport (in DIP), never past actual
        /// size: enlarging a screenshot only blurs it. When a size is missing (an empty crop, a
        /// viewport that is not laid out yet), <paramref name="zoom"/> comes back unchanged.
        /// </summary>
        public static double Fit(double zoom, double imageWidth, double imageHeight, double viewportWidth, double viewportHeight, double dpiScale)
        {
            if (Missing(imageWidth, imageHeight, viewportWidth, viewportHeight)) return zoom;

            double fit = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
            return Clamp(Math.Min(ActualSize(dpiScale), fit), dpiScale);
        }

        /// <summary>
        /// The zoom a capture opens at: the whole image as <see cref="Fit"/> gives it, unless that
        /// would be a sliver. A 1000 x 20,000 scrolling capture fitted whole is a strip 29 DIP
        /// wide at 3%, where nothing can be read; it opens fitted to the width instead (never
        /// past actual size), to be read from the top by scrolling. When a size is missing,
        /// <paramref name="zoom"/> comes back unchanged.
        /// </summary>
        public static double Open(double zoom, double imageWidth, double imageHeight, double viewportWidth, double viewportHeight, double dpiScale)
        {
            if (Missing(imageWidth, imageHeight, viewportWidth, viewportHeight)) return zoom;

            double whole = Fit(zoom, imageWidth, imageHeight, viewportWidth, viewportHeight, dpiScale);
            double width = Clamp(Math.Min(ActualSize(dpiScale), viewportWidth / imageWidth), dpiScale);
            return whole < width * WholeAtLeast ? width : whole;
        }

        /// <summary>
        /// The zoom that shows the same percent after the display's scaling changed, as when the
        /// window is dragged to another monitor. The zoom is in DIP, so left alone its percent
        /// would follow the scaling: 100% on a 150% display would become a blurred 67% on a 100%
        /// one.
        /// </summary>
        public static double Rescaled(double zoom, double oldDpiScale, double newDpiScale)
            => Clamp(zoom * Scale(oldDpiScale) / Scale(newDpiScale), newDpiScale);

        /// <summary>The zoom as the percent shown to the user: screen pixels per image pixel, rounded.</summary>
        public static int Percent(double zoom, double dpiScale)
            => (int)Math.Round(zoom * Scale(dpiScale) * 100, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Whether the image's pixels are drawn as sharp squares rather than smoothed: from
        /// <see cref="PixelatedFromPercent"/> as shown. Magnified that far the user is looking at
        /// single pixels, and smoothing blurs exactly what they came to see.
        /// </summary>
        public static bool Pixelated(double zoom, double dpiScale) => Percent(zoom, dpiScale) >= PixelatedFromPercent;

        // Written as "not greater than zero" so a NaN is refused too.
        private static bool Missing(double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
            => !(imageWidth > 0) || !(imageHeight > 0) || !(viewportWidth > 0) || !(viewportHeight > 0);
    }
}
