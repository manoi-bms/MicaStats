using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanBreakToastTests
{
    [Fact]
    public void Break_toast_suggests_a_break_and_dismisses() => UiThread.Run(() =>
    {
        var toast = BreakToastWindow.Create();
        try
        {
            var texts = Descendants(toast.Content as DependencyObject).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Time for a break", texts);
            Assert.False(toast.ShowActivated);
            Assert.True(toast.Topmost);
        }
        finally
        {
            toast.Close();
        }
    });

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
