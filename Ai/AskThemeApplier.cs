using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>Puts an <see cref="AskPalette"/> onto WPF as frozen <c>Ask.*</c> brushes.</summary>
    internal static class AskThemeApplier
    {
        /// <summary>
        /// Replaces every <c>Ask.*</c> brush in <paramref name="resources"/>. Elements that read them
        /// through DynamicResource or SetResourceReference repaint at once.
        /// </summary>
        public static void ApplyResources(ResourceDictionary resources, AskPalette palette)
        {
            foreach (var (key, color) in palette.Resources()) resources[key] = PadThemeApplier.ToBrush(color);
        }
    }
}
