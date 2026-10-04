namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The one rule for the width of MicaPad's side pane (the AI pane or Search notes). Pure
    /// arithmetic, so it is tested without a window.
    /// </summary>
    internal static class PaneWidth
    {
        public const double Default = 360, Min = 260, Max = 900, EditorMin = 320;

        /// <summary>
        /// The pane's width for a wanted width and the width of the editor area: within Min and Max,
        /// leaving EditorMin for the editor; Min when the area is too narrow for both. NaN or not
        /// positive: Default first. An area that is not a positive number (a window not yet laid
        /// out) does not limit the width.
        /// </summary>
        public static double Fit(double wanted, double areaWidth)
        {
            double width = double.IsNaN(wanted) || wanted <= 0 ? Default : wanted;
            if (width < Min) width = Min;
            if (width > Max) width = Max;
            if (areaWidth > 0 && !double.IsInfinity(areaWidth))
            {
                double room = areaWidth - EditorMin;
                if (width > room) width = room;
                if (width < Min) width = Min;
            }
            return width;
        }
    }
}
