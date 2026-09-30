using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One MicaPad color, alpha first. Pure (no WPF types), so the palette and its contrast rules
    /// are tested without a UI thread; <c>Pad/PadThemeApplier</c> turns it into a WPF brush.
    /// </summary>
    public readonly record struct PadColor(byte A, byte R, byte G, byte B)
    {
        /// <summary>Parses <c>#RRGGBB</c> (opaque) or <c>#AARRGGBB</c>; the <c>#</c> is optional.</summary>
        /// <exception cref="FormatException">The text is not one of those forms.</exception>
        public static PadColor Parse(string hex)
        {
            string s = hex ?? "";
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6) s = "FF" + s;
            if (s.Length != 8 || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
                throw new FormatException("Not a #RRGGBB or #AARRGGBB color: " + hex);
            return new PadColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        /// <summary><c>#AARRGGBB</c>, upper case.</summary>
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", A, R, G, B);

        /// <summary>This color painted over an opaque <paramref name="background"/>: what the eye sees.</summary>
        public PadColor Over(PadColor background)
        {
            double alpha = A / 255.0;
            return new PadColor(255, Mix(R, background.R, alpha), Mix(G, background.G, alpha), Mix(B, background.B, alpha));
        }

        /// <summary>WCAG 2 relative luminance of the RGB channels, ignoring alpha.</summary>
        public double Luminance => 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);

        /// <summary>
        /// WCAG 2 contrast ratio, 1 to 21, of <paramref name="foreground"/> painted over
        /// <paramref name="background"/>. The background is treated as opaque.
        /// </summary>
        public static double Contrast(PadColor foreground, PadColor background)
        {
            var solid = background with { A = 255 };
            double a = foreground.Over(solid).Luminance;
            double b = solid.Luminance;
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        private static byte Mix(byte front, byte back, double alpha) =>
            (byte)Math.Round(alpha * front + (1 - alpha) * back, MidpointRounding.AwayFromZero);

        private static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
