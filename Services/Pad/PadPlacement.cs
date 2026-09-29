namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Whether a saved window position can be restored. A monitor unplugged since the last
    /// session would otherwise leave MicaPad open somewhere nobody can see or drag it.
    /// </summary>
    public static class PadPlacement
    {
        /// <summary>
        /// True when at least 100 px of the window's width and its top 40 px (the title bar) lie
        /// inside the given screen area.
        /// </summary>
        public static bool IsReachable(double left, double top, double width, double height,
                                       double screenLeft, double screenTop, double screenWidth, double screenHeight)
        {
            if (!double.IsFinite(left) || !double.IsFinite(top) || width <= 0 || height <= 0) return false;

            double right = screenLeft + screenWidth;
            double bottom = screenTop + screenHeight;
            return left + 100 <= right && left + width - 100 >= screenLeft && top >= screenTop && top + 40 <= bottom;
        }
    }
}
