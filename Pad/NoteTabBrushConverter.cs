using System;
using System.Globalization;
using System.Windows.Data;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>Paints a note's saved hue using the current Pad palette and tab interaction state.</summary>
    public sealed class NoteTabBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            int hue = values.Length > 0 && values[0] is int value ? value : 210;
            var palette = values.Length > 1 && values[1] is PadPalette colors ? colors : PadPalette.Dark;
            bool active = values.Length > 2 && values[2] is true;
            bool hover = values.Length > 3 && values[3] is true;
            return PadThemeApplier.ToBrush(parameter as string == "Accent"
                ? NoteTabColors.Accent(hue, palette)
                : NoteTabColors.Background(hue, palette, active, hover));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
