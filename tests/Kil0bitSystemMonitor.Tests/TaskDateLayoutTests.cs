using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using Size = System.Windows.Size;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Kil0bitSystemMonitor.Tests
{
    public class TaskDateLayoutTests
    {
        [Theory]
        [InlineData("Dark", 900, 640)]
        [InlineData("Light", 900, 640)]
        [InlineData("Dark", 420, 260)]
        [InlineData("Light", 420, 260)]
        public void Date_popup_fits_the_window_in_both_themes(string theme, int width, int height) => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            config.PadTheme = theme;
            window.Width = width;
            window.Height = height;
            window.Now = () => new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(7));
            window.Editor.Document.Insert(0, "# Weekly priorities\n\n- [ ] Prepare the planning notes\n- [x] Review decisions");
            window.Editor.CaretOffset = window.Editor.Document.GetLineByNumber(3).Offset + 8;
            var root = Assert.IsType<Grid>(window.Content);
            Layout(root, width, height);
            window.ShowTaskDateEditor();
            var dates = env.Workspace.ActiveIn(window.WindowId)!.Meta.TaskDates.First();

            // Detach the popup child before closing its HWND, then render only synthetic content.
            var panel = window.TaskDatesEditor;
            window.TaskDatesPopup.Child = null;
            window.TaskDatesPopup.IsOpen = false;
            root.Children.Add(panel);
            Grid.SetRow(panel, 0);
            // A real Popup is bounded by the whole window, rather than the rows above its wrapping footer.
            Grid.SetRowSpan(panel, root.RowDefinitions.Count);
            panel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;
            panel.Show(dates, () => window.Now(), (_, _) => null);
            try
            {
                Layout(root, width, height);
                Assert.InRange(panel.ActualWidth, 240, width - 24);
                Assert.InRange(panel.ActualHeight, 100, height - 48);
                Assert.True(panel.StartInput.ActualWidth >= 150);
                Assert.True(panel.FinishInput.ActualWidth >= 150);
                Assert.Equal(0, panel.EditorScroll.ScrollableWidth);
                Assert.Equal(0, panel.EditorScroll.ScrollableHeight);
                Assert.True(panel.SaveButton.ActualWidth >= 30);
                var saveBottom = panel.SaveButton.TranslatePoint(new System.Windows.Point(0, panel.SaveButton.ActualHeight), panel).Y;
                Assert.InRange(saveBottom, 0, panel.ActualHeight);
                SaveFixture(root, width, height, theme);
            }
            finally
            {
                root.Children.Remove(panel);
                window.TaskDatesPopup.Child = panel;
                window.CloseTaskDateEditor();
            }
        });

        private static void Layout(FrameworkElement root, int width, int height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            PadLanguageWindowTests.Pump();
        }

        private static void SaveFixture(Visual root, int width, int height, string theme)
        {
            string? folder = Environment.GetEnvironmentVariable("MICAPAD_DATE_SCREENSHOTS");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"task-dates-{theme.ToLowerInvariant()}-{width}.png"));
            encoder.Save(output);
        }
    }
}
