using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Controls;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The capture editor's zoom bar and its wiring to the zoom helpers. The window is built and
    /// laid out in code, never shown; the wheel goes through the window's own seam with the
    /// modifier keys passed in, so nothing here reads or sends real input.
    /// </summary>
    public class CaptureEditorZoomTests
    {
        /// <summary>The window's resize borders: what its client area is narrower than the window by.</summary>
        private const double FrameWidth = 16;

        private static void WithEditor(int imageWidth, int imageHeight, Action<CaptureEditorWindow> test, bool atMinimumWidth = false) => UiThread.Run(() =>
        {
            var window = new CaptureEditorWindow(CanvasZoomTests.Image(imageWidth, imageHeight), CaptureSettings.Defaults);
            try
            {
                // An unshown window lays nothing out, and zooming needs the scroller's viewport:
                // the window's content is laid out on its own, at the size the window would give it.
                double width = (atMinimumWidth ? window.MinWidth : window.Width) - FrameWidth;
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(width, 741));
                content.Arrange(new Rect(0, 0, width, 741));
                content.UpdateLayout();
                Assert.True(window.Scroller.ViewportWidth > 600 && window.Scroller.ViewportHeight > 400, "The editor is laid out");
                test(window);
            }
            finally
            {
                window.Close();
            }
        });

        private static double DpiScale(CaptureEditorWindow window) => VisualTreeHelper.GetDpi(window.Canvas).DpiScaleX;

        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static string Label(double zoom, double dpiScale)
            => EditorZoom.Percent(zoom, dpiScale).ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>Shows the image at one DIP per pixel, scrolled away from the corner.</summary>
        private static void ScrollInto(CaptureEditorWindow window, double x, double y)
        {
            window.Canvas.SetZoom(1.0);
            window.UpdateLayout();
            window.Scroller.ScrollToHorizontalOffset(x);
            window.Scroller.ScrollToVerticalOffset(y);
            window.UpdateLayout();
            Assert.Equal(x, window.Scroller.HorizontalOffset, 3);
            Assert.Equal(y, window.Scroller.VerticalOffset, 3);
        }

        [Fact]
        public void The_zoom_bar_follows_reset_crop_behind_a_separator() => WithEditor(200, 100, window =>
        {
            var bar = Assert.IsType<StackPanel>(window.ZoomOutButton.Parent);
            var items = bar.Children.Cast<UIElement>().ToList();
            int reset = items.FindIndex(item => item is Button button && Equals(button.Content, "Reset crop"));
            Assert.True(reset >= 0, "The Reset crop button is in the same bar");

            var separator = Assert.IsType<Rectangle>(items[reset + 1]);
            Assert.Equal(1.0, separator.Width);
            Assert.Equal(
                new UIElement[] { window.ZoomOutButton, window.ZoomActualButton, window.ZoomInButton, window.ZoomFitButton },
                items.Skip(reset + 2));

            string minus = ((char)0x2212).ToString();
            Assert.Equal("Zoom out (Ctrl+" + minus + " or Ctrl+wheel)", window.ZoomOutButton.ToolTip);
            Assert.Equal("Actual size (Ctrl+0)", window.ZoomActualButton.ToolTip);
            Assert.Equal("Zoom in (Ctrl++ or Ctrl+wheel)", window.ZoomInButton.ToolTip);
            Assert.Equal("Fit to window", window.ZoomFitButton.ToolTip);
            Assert.Equal("Fit", window.ZoomFitButton.Content);

            var style = window.FindResource("ActionButton");
            Assert.All(items.Skip(reset + 2), item => Assert.Same(style, ((Button)item).Style));
        });

        [Fact]
        public void The_action_bar_still_fits_at_the_minimum_window_width() => WithEditor(200, 100, window =>
        {
            var left = Assert.IsType<StackPanel>(window.ZoomOutButton.Parent);
            var bar = Assert.IsType<Grid>(left.Parent);
            var right = Assert.IsType<StackPanel>(bar.Children[2]);

            // With the zoom bar added, the two groups of buttons must not push Copy out of the window.
            Assert.True(left.DesiredSize.Width + right.DesiredSize.Width <= bar.ActualWidth,
                string.Format(CultureInfo.InvariantCulture, "{0} + {1} does not fit in {2}",
                    left.DesiredSize.Width, right.DesiredSize.Width, bar.ActualWidth));
        }, atMinimumWidth: true);

        [Fact]
        public void The_percent_button_shows_the_zoom_in_screen_pixels_from_the_start() => WithEditor(200, 100, window =>
        {
            Assert.Same(window.ZoomText, window.ZoomActualButton.Content);
            Assert.Equal(Label(window.Canvas.Zoom, DpiScale(window)), window.ZoomText.Text);
        });

        [Fact]
        public void The_percent_button_sets_actual_size() => WithEditor(200, 100, window =>
        {
            double dpi = DpiScale(window);
            window.Canvas.SetZoom(0.3);

            Click(window.ZoomActualButton);

            Assert.Equal(1 / dpi, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);
        });

        [Fact]
        public void Plus_and_minus_step_the_zoom_and_the_label_follows() => WithEditor(200, 100, window =>
        {
            double dpi = DpiScale(window);
            Click(window.ZoomActualButton);

            Click(window.ZoomInButton);
            Assert.Equal(1.25 / dpi, window.Canvas.Zoom, 9);
            Assert.Equal("125%", window.ZoomText.Text);

            Click(window.ZoomOutButton);
            Click(window.ZoomOutButton);
            Assert.Equal(0.8 / dpi, window.Canvas.Zoom, 9);
            Assert.Equal("80%", window.ZoomText.Text);
        });

        [Fact]
        public void The_buttons_stop_at_800_and_1_percent() => WithEditor(200, 100, window =>
        {
            Click(window.ZoomActualButton);

            for (int i = 0; i < 12; i++) Click(window.ZoomInButton);
            Assert.Equal("800%", window.ZoomText.Text);

            for (int i = 0; i < 34; i++) Click(window.ZoomOutButton);
            Assert.Equal("1%", window.ZoomText.Text);
        });

        [Fact]
        public void Fit_scales_a_large_capture_into_the_view() => WithEditor(2000, 1500, window =>
        {
            double dpi = DpiScale(window);
            ScrollInto(window, 300, 200);
            double expected = EditorZoom.Fit(window.Canvas.Zoom, 2000, 1500,
                window.Scroller.ViewportWidth - 40, window.Scroller.ViewportHeight - 40, dpi);
            Assert.InRange(expected, 0.1, 0.99);

            Click(window.ZoomFitButton);

            Assert.Equal(expected, window.Canvas.Zoom, 9);
            Assert.Equal(Label(expected, dpi), window.ZoomText.Text);
            Assert.Equal(0, window.Scroller.ScrollableWidth);
            Assert.Equal(0, window.Scroller.ScrollableHeight);
        });

        [Fact]
        public void Fit_shows_a_small_capture_at_actual_size_not_enlarged() => WithEditor(200, 100, window =>
        {
            double dpi = DpiScale(window);
            Click(window.ZoomActualButton);
            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Assert.Equal(1.5625 / dpi, window.Canvas.Zoom, 9);

            Click(window.ZoomFitButton);

            Assert.Equal(1 / dpi, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);
        });

        [Fact]
        public void The_buttons_zoom_around_the_middle_of_the_view() => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            var middle = CanvasZoom.ViewportCentre(window.Scroller);
            var image = CanvasZoomTests.ImageAt(window.Scroller, window.Canvas, middle);

            Click(window.ZoomInButton);

            Assert.Equal(1.25, window.Canvas.Zoom, 9);
            CanvasZoomTests.AssertWithinOneDip(middle, CanvasZoomTests.ShownAt(window.Scroller, window.Canvas, image));

            Click(window.ZoomOutButton);

            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            CanvasZoomTests.AssertWithinOneDip(middle, CanvasZoomTests.ShownAt(window.Scroller, window.Canvas, image));
        });

        [Fact]
        public void The_wheel_zooms_around_the_pointer_only_with_ctrl() => WithEditor(2000, 1500, window =>
        {
            double dpi = DpiScale(window);
            ScrollInto(window, 300, 200);
            var pointer = new Point(700, 120);
            var image = CanvasZoomTests.ImageAt(window.Scroller, window.Canvas, pointer);

            // Without Ctrl the wheel is left to the scroller.
            Assert.False(window.WheelZoom(ModifierKeys.None, 120, pointer));
            Assert.False(window.WheelZoom(ModifierKeys.Shift, -120, pointer));
            Assert.Equal(1.0, window.Canvas.Zoom);

            Assert.True(window.WheelZoom(ModifierKeys.Control, 120, pointer));
            Assert.Equal(1.25, window.Canvas.Zoom, 9);
            Assert.Equal(Label(1.25, dpi), window.ZoomText.Text);
            CanvasZoomTests.AssertWithinOneDip(pointer, CanvasZoomTests.ShownAt(window.Scroller, window.Canvas, image));

            // A precision touchpad sends small deltas: a third of a notch is a third of a step.
            Assert.True(window.WheelZoom(ModifierKeys.Control, -40, pointer));
            Assert.Equal(EditorZoom.Wheel(1.25, -40, dpi), window.Canvas.Zoom, 9);
            Assert.InRange(window.Canvas.Zoom, 1.15, 1.17);
            CanvasZoomTests.AssertWithinOneDip(pointer, CanvasZoomTests.ShownAt(window.Scroller, window.Canvas, image));
        });

        [Fact]
        public void The_wheel_does_nothing_while_text_is_being_typed() => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            window.TextEntry.Visibility = Visibility.Visible;
            try
            {
                Assert.False(window.WheelZoom(ModifierKeys.Control, 120, new Point(700, 120)));

                Assert.Equal(1.0, window.Canvas.Zoom);
                Assert.Equal(300, window.Scroller.HorizontalOffset, 3);
                Assert.Equal(200, window.Scroller.VerticalOffset, 3);
            }
            finally
            {
                window.TextEntry.Visibility = Visibility.Collapsed;
            }
        });

        [Fact]
        public void The_status_text_no_longer_shows_the_zoom() => WithEditor(200, 100, window =>
        {
            string dot = ((char)0x00B7).ToString();
            window.Canvas.ClearAll();                       // any document change refreshes the status
            Assert.Equal("200 x 100  " + dot + "  0 marks", window.StatusText.Text);

            Click(window.ZoomInButton);

            Assert.Equal("200 x 100  " + dot + "  0 marks", window.StatusText.Text);
            Assert.DoesNotContain("%", window.StatusText.Text, StringComparison.Ordinal);
        });
    }
}
