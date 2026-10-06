using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Stable, theme-aware colors for MicaPad note tabs.</summary>
    public static class NoteTabColors
    {
        /// <summary>
        /// The curated colors shown by the tab color picker. Their order also gives automatic
        /// assignment a pleasant, well-separated start: blue, orange, green, purple.
        /// </summary>
        public static readonly IReadOnlyList<(string Name, int Hue)> Choices = new (string, int)[]
        {
            ("Blue", 210),
            ("Orange", 30),
            ("Green", 120),
            ("Purple", 300),
            ("Teal", 175),
            ("Amber", 45),
            ("Rose", 345),
            ("Violet", 255),
            ("Leaf", 135),
            ("Coral", 15),
            ("Pink", 325),
            ("Indigo", 280),
        };

        public static bool IsValidHue(int? hue) => hue is >= 0 and <= 359;

        /// <summary>
        /// Picks the hue farthest from the colors already visible. Ties follow
        /// <see cref="Choices"/>, which keeps the first assignments predictable.
        /// </summary>
        public static int ChooseHue(IEnumerable<int> usedHues)
        {
            int[] used = (usedHues ?? Enumerable.Empty<int>())
                .Where(hue => IsValidHue(hue))
                .Select(Normalize)
                .Distinct()
                .ToArray();
            if (used.Length == 0) return Choices[0].Hue;

            int bestHue = Choices[0].Hue;
            int bestDistance = -1;
            foreach (var choice in Choices)
            {
                int distance = used.Min(hue => CircularDistance(choice.Hue, hue));
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    bestHue = choice.Hue;
                }
            }

            // Curated colors win ties, so the opening sequence remains memorable. Searching the
            // complete circle afterwards keeps automatic colors distinct beyond the picker set.
            for (int hue = 0; hue < 360; hue++)
            {
                int distance = used.Min(existing => CircularDistance(hue, existing));
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    bestHue = hue;
                }
            }
            return bestHue;
        }

        /// <summary>A deterministic fallback for metadata written before tab colors existed.</summary>
        public static int HueForId(string? id)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char value in id ?? string.Empty)
                {
                    hash ^= value;
                    hash *= 16777619;
                }
                return (int)(hash % 360);
            }
        }

        /// <summary>A readable colored marker/title accent for every tint of this tab.</summary>
        public static PadColor Accent(int hue, PadPalette palette)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));

            double start = palette.IsDark ? 0.68 : 0.34;
            double direction = palette.IsDark ? 1 : -1;
            for (int step = 0; step <= 50; step++)
            {
                double lightness = Math.Clamp(start + direction * step * 0.02, 0, 1);
                PadColor candidate = Hsl(Normalize(hue), 0.72, lightness);
                if (TabBackgrounds(hue, palette).All(background => PadColor.Contrast(candidate, background) >= 3.0))
                    return candidate;
            }

            return palette.IsDark
                ? new PadColor(255, 255, 255, 255)
                : new PadColor(255, 0, 0, 0);
        }

        /// <summary>A restrained opaque tint over the theme's normal tab-strip chrome.</summary>
        public static PadColor Background(int hue, PadPalette palette, bool active, bool hover)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            double amount = active ? 0.18 : hover ? 0.12 : 0.07;
            PadColor tint = Hsl(Normalize(hue), 0.68, palette.IsDark ? 0.60 : 0.43) with
            {
                A = (byte)Math.Round(amount * 255, MidpointRounding.AwayFromZero),
            };
            return tint.Over(palette.Chrome);
        }

        private static IEnumerable<PadColor> TabBackgrounds(int hue, PadPalette palette)
        {
            yield return Background(hue, palette, active: false, hover: false);
            yield return Background(hue, palette, active: false, hover: true);
            yield return Background(hue, palette, active: true, hover: false);
            yield return palette.Popup;
        }

        private static int Normalize(int hue) => ((hue % 360) + 360) % 360;

        private static int CircularDistance(int left, int right)
        {
            int distance = Math.Abs(Normalize(left) - Normalize(right));
            return Math.Min(distance, 360 - distance);
        }

        private static PadColor Hsl(int hue, double saturation, double lightness)
        {
            double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            double section = hue / 60.0;
            double second = chroma * (1 - Math.Abs(section % 2 - 1));
            (double red, double green, double blue) = section switch
            {
                < 1 => (chroma, second, 0.0),
                < 2 => (second, chroma, 0.0),
                < 3 => (0.0, chroma, second),
                < 4 => (0.0, second, chroma),
                < 5 => (second, 0.0, chroma),
                _ => (chroma, 0.0, second),
            };
            double match = lightness - chroma / 2;
            return new PadColor(255, Channel(red + match), Channel(green + match), Channel(blue + match));
        }

        private static byte Channel(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }
}
