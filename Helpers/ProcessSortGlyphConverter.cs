using System;
using System.Globalization;
using System.Windows.Data;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services.Localization;

namespace Kil0bitSystemMonitor.Helpers;

/// <summary>Displays the direction for the active stable sort key in either UI language.</summary>
public sealed class ProcessSortGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if ((values.Length != 3 && values.Length != 4) || values[0] is not string key ||
            values[1] is not ProcessSortColumn active || values[2] is not bool descending ||
            TaskManagerViewModel.ColumnFor(key) != active)
            return "";

        return parameter as string == "Description"
            ? UiText.Tr(descending ? "Sorted descending" : "Sorted ascending")
            : (descending ? "▼" : "▲");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
