using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Pad;

using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts a <see cref="PadPalette"/> onto WPF: frozen brushes under the <c>Pad.*</c> keys that
    /// MicaPad's XAML reads with DynamicResource, and a dark or light title bar.
    /// </summary>
    internal static class PadThemeApplier
    {
        public static Color ToColor(PadColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

        /// <summary>A frozen brush, safe to share and cheap to render.</summary>
        public static SolidColorBrush ToBrush(PadColor color)
        {
            var brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Replaces every <c>Pad.*</c> brush in <paramref name="resources"/>. Elements that read them
        /// through DynamicResource repaint at once.
        /// </summary>
        public static void ApplyResources(ResourceDictionary resources, PadPalette palette)
        {
            foreach (var (key, color) in palette.Resources()) resources[key] = ToBrush(color);
        }

        /// <summary>
        /// A dark or light caption through <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>. A window without a
        /// handle yet is skipped; <c>SourceInitialized</c> applies it once the handle exists.
        /// </summary>
        public static void ApplyTitleBar(Window window, bool dark)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            Win32Helper.DwmSetWindowAttribute(hwnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
    }
}
