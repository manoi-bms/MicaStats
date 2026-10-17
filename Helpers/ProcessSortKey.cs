using System.Windows;

namespace Kil0bitSystemMonitor.Helpers;

/// <summary>Stable sort identity for a process-list column, independent of its localized header.</summary>
public static class ProcessSortKey
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ProcessSortKey), new PropertyMetadata(""));

    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

    public static string GetKey(DependencyObject element) => (string)element.GetValue(KeyProperty);
}
