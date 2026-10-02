using System;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// Where the scrolling capture's status card goes (scrolling capture spec 3). Outside the
    /// picked area, because every grab is clipped to that area and a card inside it would be
    /// joined into the image; inside the monitor's work area, so the taskbar never hides it.
    /// </summary>
    public static class ScrollStatusPlacement
    {
        /// <summary>Pixels between the card and the area.</summary>
        public const int Gap = 8;

        /// <summary>
        /// The card's rectangle: centred on the area, above it when that fits in the work area,
        /// otherwise below it, otherwise in the work area's top-left corner. Horizontally it is
        /// kept on the monitor, so an area at a screen edge does not push the card off it.
        /// </summary>
        public static PixelRect Place(PixelRect area, PixelRect monitorWork, int cardWidth, int cardHeight)
        {
            int x = area.X + (area.Width - cardWidth) / 2;
            x = Math.Max(monitorWork.Left, Math.Min(x, monitorWork.Right - cardWidth));

            int above = area.Top - Gap - cardHeight;
            if (above >= monitorWork.Top) return new PixelRect(x, above, cardWidth, cardHeight);

            int below = area.Bottom + Gap;
            if (below + cardHeight <= monitorWork.Bottom) return new PixelRect(x, below, cardWidth, cardHeight);

            return new PixelRect(monitorWork.X, monitorWork.Y, cardWidth, cardHeight);
        }
    }
}
