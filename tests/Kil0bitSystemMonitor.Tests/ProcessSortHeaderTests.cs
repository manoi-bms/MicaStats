using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Xunit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests;

public class ProcessSortHeaderTests
{
    [Fact]
    public Task Active_header_shows_initial_sort_and_follows_column_clicks() => UiThread.RunAsync(async () =>
    {
        using var sampler = new ProcessSampler();
        var window = new TaskManagerWindow(sampler) { ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var headers = Descendants<GridViewColumnHeader>(window)
                .Where(header => header.Column?.Header is string)
                .ToDictionary(header => (string)header.Column.Header);

            Assert.Equal(9, headers.Count);
            AssertDirection(headers, "CPU", "▼");
            foreach (string column in new[] { "Name", "PID", "Parent", "CPU", "Memory", "Disk", "Uptime", "Threads", "Handles" })
            {
                headers[column].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                string initialDirection = column == "Name" ? "▲" : "▼";
                AssertDirection(headers, column, initialDirection);

                headers[column].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                AssertDirection(headers, column, initialDirection == "▲" ? "▼" : "▲");
            }

            // Preview the real headers with no process data in the image.
            ((TaskManagerViewModel)window.DataContext).SearchText = "__header_preview_no_matching_process__";
            window.Width = window.MinWidth;
            window.UpdateLayout();
            string directory = Path.Combine(PadWindowTests.RepoRoot(), "artifacts", "process-ui");
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), 145, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(directory, "sort-headers-minimum.png"));
            encoder.Save(image);
        }
        finally { window.Close(); }
    });

    private static void AssertDirection(IReadOnlyDictionary<string, GridViewColumnHeader> headers, string active, string direction)
    {
        foreach (var header in headers)
        {
            var glyph = Assert.Single(Descendants<TextBlock>(header.Value), text => text.Name == "ProcessSortGlyph");
            Assert.Equal(header.Key == active ? direction : "", glyph.Text);
            Assert.Equal(header.Key == active ? (direction == "▼" ? "Sorted descending" : "Sorted ascending") : "",
                System.Windows.Automation.AutomationProperties.GetName(glyph));
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
