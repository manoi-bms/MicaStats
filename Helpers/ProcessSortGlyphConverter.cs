using System;
using System.Globalization;
using System.Windows.Data;
using Kil0bitSystemMonitor.ViewModels;

namespace Kil0bitSystemMonitor.Helpers;

/// <summary>Keeps header labels as sort keys while displaying the active direction separately.</summary>
public sealed class ProcessSortGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 3 || values[0] is not string header ||
            values[1] is not ProcessSortColumn active || values[2] is not bool descending ||
            TaskManagerViewModel.ColumnFor(header) != active)
            return "";

        return parameter as string == "Description"
            ? (descending ? "Sorted descending" : "Sorted ascending")
            : (descending ? "▼" : "▲");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
