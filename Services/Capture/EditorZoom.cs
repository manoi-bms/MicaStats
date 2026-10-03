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

        /// <summary>One wheel notch, as Windows reports it (WHEEL_DELTA).</summary>
        private const double WheelNotch = 120.0;

        private static double Scale(double dpiScale)
            => double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;

        /// <summary>Keeps a zoom inside the range whose percent is <see cref="MinPercent"/> to <see cref="MaxPercent"/>.</summary>
        public static double Clamp(double zoom, double dpiScale)
        {
            double scale = Scale(dpiScale);
            return Math.Clamp(zoom, MinPercent / 100 / scale, MaxPercent / 100 / scale);
        }

        /// <summary>The zoom after <paramref name="steps"/> steps in (positive) or out (negative).</summary>
        public static double Stepped(double zoom, int steps, double dpiScale)
            => Clamp(zoom * Math.Pow(Step, steps), dpiScale);

        /// <summary>
        /// The zoom after a wheel message. One notch (a delta of 120) is one step; a precision
        /// touchpad sends many small deltas, and each scales by its share of a step, so the zoom
        /// follows the fingers smoothly and three deltas of 40 land where one notch would.
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
            // Written as "not greater than zero" so a NaN is refused too.
            if (!(imageWidth > 0) || !(imageHeight > 0) || !(viewportWidth > 0) || !(viewportHeight > 0)) return zoom;

            double fit = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
            return Clamp(Math.Min(ActualSize(dpiScale), fit), dpiScale);
        }

        /// <summary>The zoom as the percent shown to the user: screen pixels per image pixel, rounded.</summary>
        public static int Percent(double zoom, double dpiScale)
            => (int)Math.Round(zoom * Scale(dpiScale) * 100, MidpointRounding.AwayFromZero);
    }
}
