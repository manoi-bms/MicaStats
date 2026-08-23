using System;
using System.Drawing;
using Microsoft.Win32;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>Whether the Windows taskbar is currently painted light or dark.</summary>
    public enum TaskbarAppearance
    {
        Dark,
        Light
    }

    /// <summary>
    /// Every colour the taskbar overlay draws with, for one taskbar appearance.
    ///
    /// <para>
    /// The overlay paints directly onto the taskbar — <c>ShowBackground</c> is off by default,
    /// so there is no plate of its own behind the text. Every colour therefore has to work
    /// against whatever the taskbar is, and the shipped values were all from the white family:
    /// near-white readings, a light grey label, and translucent white pods. On a light taskbar
    /// that is white on near-white, which is the bug this exists to fix.
    /// </para>
    ///
    /// <para>
    /// Colours are <see cref="System.Drawing.Color"/> because the overlay renders through GDI+,
    /// not WPF.
    /// </para>
    /// </summary>
    public sealed record OverlayPalette(
        Color Value,
        Color Label,
        Color Graph,
        Color GraphAlt,
        Color Track,
        Color PodFill,
        Color PodEdge,
        Color Guide,
        Color HoverFill,
        Color HoverEdge,
        Color DefaultAccent,
        Color DefaultLabelAccent)
    {
        /// <summary>
        /// The shipped look: light readings on the dark Windows 11 taskbar. These are the exact
        /// values the overlay used before the palette existed, so nothing moves for the
        /// overwhelming majority of users who run a dark taskbar.
        /// </summary>
        public static OverlayPalette Dark { get; } = new(
            Value: Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF),
            Label: Color.FromArgb(0xB0, 0xA6, 0xAC, 0xB4),
            Graph: Color.FromArgb(0xFF, 0x3F, 0xD2, 0xE4),
            GraphAlt: Color.FromArgb(0xFF, 0xFF, 0x51, 0x47),
            Track: Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF),
            PodFill: Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
            PodEdge: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            Guide: Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF),
            HoverFill: Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF),
            HoverEdge: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            DefaultAccent: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            DefaultLabelAccent: Color.FromArgb(0xFF, 0x00, 0xCC, 0xFF));

        /// <summary>
        /// The same design inverted for a light taskbar: dark ink, and accent hues darkened
        /// until they carry against a near-white backdrop. Cyan at #3FD2E4 measures about
        /// 1.5:1 on white — effectively invisible — so the graph and label hues are deepened
        /// rather than reused.
        /// </summary>
        public static OverlayPalette Light { get; } = new(
            Value: Color.FromArgb(0xFF, 0x14, 0x18, 0x1C),
            Label: Color.FromArgb(0xC8, 0x36, 0x3E, 0x46),
            Graph: Color.FromArgb(0xFF, 0x0E, 0x6C, 0x78),
            GraphAlt: Color.FromArgb(0xFF, 0xC1, 0x2B, 0x22),
            Track: Color.FromArgb(0x2E, 0x00, 0x00, 0x00),
            PodFill: Color.FromArgb(0x12, 0x00, 0x00, 0x00),
            PodEdge: Color.FromArgb(0x22, 0x00, 0x00, 0x00),
            Guide: Color.FromArgb(0x44, 0x00, 0x00, 0x00),
            HoverFill: Color.FromArgb(0x1C, 0x00, 0x00, 0x00),
            HoverEdge: Color.FromArgb(0x26, 0x00, 0x00, 0x00),
            DefaultAccent: Color.FromArgb(0xFF, 0x14, 0x18, 0x1C),
            DefaultLabelAccent: Color.FromArgb(0xFF, 0x0E, 0x6C, 0x78));

        public static OverlayPalette For(TaskbarAppearance appearance) =>
            appearance == TaskbarAppearance.Light ? Light : Dark;

        /// <summary>
        /// Representative taskbar backdrops, used to check that the palette is actually
        /// readable. Windows composites the taskbar over the wallpaper, so these are the base
        /// tints rather than exact pixels — good enough to catch a colour that cannot work.
        /// </summary>
        public static Color DarkBackdrop { get; } = Color.FromArgb(0xFF, 0x20, 0x20, 0x20);

        public static Color LightBackdrop { get; } = Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        /// <summary>The backdrop this palette was designed against.</summary>
        public Color Backdrop => ReferenceEquals(this, Light) ? LightBackdrop : DarkBackdrop;
    }

    /// <summary>
    /// Contrast arithmetic, so the palette above is justified by measurement rather than by
    /// eye. Kept public because the tests assert the shipped colours clear the thresholds.
    /// </summary>
    public static class Contrast
    {
        /// <summary>Flattens a translucent colour onto an opaque backdrop.</summary>
        public static Color Composite(Color foreground, Color backdrop)
        {
            double a = foreground.A / 255d;
            return Color.FromArgb(255,
                (int)Math.Round(foreground.R * a + backdrop.R * (1 - a)),
                (int)Math.Round(foreground.G * a + backdrop.G * (1 - a)),
                (int)Math.Round(foreground.B * a + backdrop.B * (1 - a)));
        }

        /// <summary>WCAG relative luminance.</summary>
        public static double Luminance(Color c) =>
            0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        private static double Channel(byte value)
        {
            double v = value / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// WCAG contrast ratio between 1 and 21. Translucent foregrounds are flattened onto the
        /// backdrop first, which is what actually reaches the eye.
        /// </summary>
        public static double Ratio(Color foreground, Color backdrop)
        {
            double a = Luminance(Composite(foreground, backdrop));
            double b = Luminance(backdrop);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
    }

    /// <summary>
    /// Reads which way the Windows taskbar is painted.
    ///
    /// <para>
    /// <b>The taskbar follows <c>SystemUsesLightTheme</c>, not <c>AppsUseLightTheme</c>.</b>
    /// The two are independent settings, and the development machine has them disagreeing —
    /// a dark taskbar with light applications — so reading the app key, which is the more
    /// commonly cited of the pair, gets the answer exactly backwards on that configuration.
    /// </para>
    ///
    /// <para>
    /// A missing value means dark: that is the Windows 11 default, and it is also the safe
    /// assumption, because the dark palette on an unexpectedly light taskbar is merely low
    /// contrast whereas the reverse can be invisible.
    /// </para>
    /// </summary>
    public static class TaskbarTheme
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private const string TaskbarValue = "SystemUsesLightTheme";

        /// <summary>The appearance as of the last read. Refreshed on a system setting change.</summary>
        public static TaskbarAppearance Current { get; private set; } = Read();

        /// <summary>The palette matching <see cref="Current"/>.</summary>
        public static OverlayPalette Palette => OverlayPalette.For(Current);

        /// <summary>
        /// Re-reads the setting. Returns true when it changed, so the caller can rebuild its
        /// brush cache and repaint only when there is something to repaint.
        /// </summary>
        public static bool Refresh()
        {
            var latest = Read();
            if (latest == Current) return false;

            Current = latest;
            Services.DiagnosticsLog.Log("theme", "Taskbar is now " + latest.ToString().ToLowerInvariant());
            return true;
        }

        /// <summary>Reads the setting directly, without touching the cached value.</summary>
        public static TaskbarAppearance Read()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue(TaskbarValue) is int light && light != 0)
                    return TaskbarAppearance.Light;
            }
            catch
            {
                // A locked-down or roaming profile can refuse this. Dark is the safe default.
            }
            return TaskbarAppearance.Dark;
        }
    }
}
