using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Controls;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The capture editor's zoom bar and its wiring to the zoom helpers. The window is built and
    /// laid out in code, never shown. The wheel and the keys go through the window's own seams
    /// with the modifier keys passed in, and the display's scaling is set through its seam, so
    /// nothing here reads real input or depends on the machine's display.
    /// </summary>
    public class CaptureEditorZoomTests
    {
        /// <summary>The window's resize borders: what its client area is narrower than the window by.</summary>
        private const double FrameWidth = 16;

        private static void WithEditor(BitmapSource image, Action<CaptureEditorWindow> test, bool atMinimumWidth = false, double dpiScale = 1.0) => UiThread.Run(() =>
        {
            var window = new CaptureEditorWindow(image, CaptureSettings.Defaults);
            try
            {
                window.DpiScaleOverride = dpiScale;

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

        private static void WithEditor(int imageWidth, int imageHeight, Action<CaptureEditorWindow> test, bool atMinimumWidth = false, double dpiScale = 1.0)
            => WithEditor(CanvasZoomTests.Image(imageWidth, imageHeight), test, atMinimumWidth, dpiScale);

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

        private static Point ImageAt(CaptureEditorWindow window, Point inScroller)
            => CanvasZoomTests.ImageAt(window.Scroller, window.Canvas, inScroller);

        private static Point ShownAt(CaptureEditorWindow window, Point image)
            => CanvasZoomTests.ShownAt(window.Scroller, window.Canvas, image);

        /// <summary>The bottom bar: its two groups of buttons, the grid they sit in and the border around it.</summary>
        private static (StackPanel Left, StackPanel Right, Grid Bar, Border Border) ActionBar(CaptureEditorWindow window)
        {
            var left = Assert.IsType<StackPanel>(window.ZoomOutButton.Parent);
            var bar = Assert.IsType<Grid>(left.Parent);
            var right = Assert.IsType<StackPanel>(bar.Children[2]);
            return (left, right, bar, Assert.IsType<Border>(bar.Parent));
        }

        // ----- The zoom bar ----------------------------------------------------------------------

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
        public void The_action_bar_fits_in_the_room_it_is_given_at_the_minimum_window_width() => WithEditor(200, 100, window =>
        {
            var (left, right, bar, border) = ActionBar(window);

            // The room the bar is offered, not the bar's own width: a grid whose columns need more
            // than it is given is still arranged at what it needs, and clipped.
            double room = border.ActualWidth - border.Padding.Left - border.Padding.Right;
            Assert.True(left.DesiredSize.Width + right.DesiredSize.Width <= room,
                string.Format(CultureInfo.InvariantCulture, "{0} + {1} does not fit in {2}: Copy is pushed out of the window",
                    left.DesiredSize.Width, right.DesiredSize.Width, room));
            Assert.Null(LayoutInformation.GetLayoutClip(bar));
        }, atMinimumWidth: true);

        [Fact]
        public void The_status_text_keeps_room_at_the_minimum_window_width() => WithEditor(200, 100, window =>
        {
            var (_, _, bar, _) = ActionBar(window);
            Assert.Same(window.StatusText, bar.Children.Cast<UIElement>().Single(child => Grid.GetColumn(child) == 1));

            double column = bar.ColumnDefinitions[1].ActualWidth;
            Assert.True(column >= 100,
                string.Format(CultureInfo.InvariantCulture, "The status column is {0} DIP wide", column));
        }, atMinimumWidth: true);

        [Fact]
        public void The_status_text_shows_itself_in_full_in_a_tooltip() => WithEditor(200, 100, window =>
        {
            string dash = ((char)0x2014).ToString();
            window.StatusText.Text = "Could not save " + dash + " see the diagnostics log";
            Assert.Equal("Could not save " + dash + " see the diagnostics log", window.StatusText.ToolTip);

            window.Canvas.ClearAll();                       // any document change refreshes the status
            Assert.Equal(window.StatusText.Text, window.StatusText.ToolTip);
            Assert.StartsWith("200 x 100", Assert.IsType<string>(window.StatusText.ToolTip), StringComparison.Ordinal);
        });

        [Fact]
        public void The_percent_button_shows_the_zoom_in_screen_pixels_from_the_start() => UiThread.Run(() =>
        {
            // Before anything sets the scaling: the label is the canvas's zoom at the scaling WPF reports.
            var window = new CaptureEditorWindow(CanvasZoomTests.Image(200, 100), CaptureSettings.Defaults);
            try
            {
                Assert.Same(window.ZoomText, window.ZoomActualButton.Content);
                Assert.Equal(Label(window.Canvas.Zoom, VisualTreeHelper.GetDpi(window.Canvas).DpiScaleX), window.ZoomText.Text);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void The_percent_button_sets_actual_size() => WithEditor(200, 100, window =>
        {
            window.Canvas.SetZoom(0.3);

            Click(window.ZoomActualButton);

            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);
        });

        [Fact]
        public void Plus_and_minus_step_the_zoom_and_the_label_follows() => WithEditor(200, 100, window =>
        {
            Click(window.ZoomActualButton);

            Click(window.ZoomInButton);
            Assert.Equal(1.25, window.Canvas.Zoom, 9);
            Assert.Equal("125%", window.ZoomText.Text);

            Click(window.ZoomOutButton);
            Click(window.ZoomOutButton);
            Assert.Equal(0.8, window.Canvas.Zoom, 9);
            Assert.Equal("80%", window.ZoomText.Text);
        });

        [Fact]
        public void A_step_that_would_pass_100_percent_stops_on_it() => WithEditor(200, 100, window =>
        {
            window.Canvas.SetZoom(0.94);
            Click(window.ZoomInButton);
            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);

            window.Canvas.SetZoom(1.18);
            Click(window.ZoomOutButton);
            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);

            Click(window.ZoomInButton);
            Assert.Equal("125%", window.ZoomText.Text);
        });

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        [InlineData(2.5)]
        [InlineData(3.0)]
        public void The_buttons_stop_at_800_and_1_percent_at_any_display_scaling(double dpiScale) => WithEditor(200, 100, window =>
        {
            Click(window.ZoomActualButton);

            for (int i = 0; i < 40; i++) Click(window.ZoomInButton);
            Assert.Equal(8 / dpiScale, window.Canvas.Zoom, 9);
            Assert.Equal("800%", window.ZoomText.Text);

            for (int i = 0; i < 60; i++) Click(window.ZoomOutButton);
            Assert.Equal(0.01 / dpiScale, window.Canvas.Zoom, 9);
            Assert.Equal("1%", window.ZoomText.Text);
        }, dpiScale: dpiScale);

        [Fact]
        public void Fit_scales_a_large_capture_into_the_view() => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            double expected = EditorZoom.Fit(window.Canvas.Zoom, 2000, 1500,
                window.Scroller.ViewportWidth - 40, window.Scroller.ViewportHeight - 40, 1.0);
            Assert.InRange(expected, 0.1, 0.99);

            Click(window.ZoomFitButton);

            Assert.Equal(expected, window.Canvas.Zoom, 9);
            Assert.Equal(Label(expected, 1.0), window.ZoomText.Text);
            Assert.Equal(0, window.Scroller.ScrollableWidth);
            Assert.Equal(0, window.Scroller.ScrollableHeight);
        });

        [Fact]
        public void Fit_shows_a_small_capture_at_actual_size_not_enlarged() => WithEditor(200, 100, window =>
        {
            Click(window.ZoomActualButton);
            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Assert.Equal(1.5625, window.Canvas.Zoom, 9);

            Click(window.ZoomFitButton);

            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);
        });

        [Fact]
        public void The_buttons_zoom_around_the_middle_of_the_view() => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            var middle = CanvasZoom.ViewportCentre(window.Scroller);
            var image = ImageAt(window, middle);

            Click(window.ZoomInButton);

            Assert.Equal(1.25, window.Canvas.Zoom, 9);
            CanvasZoomTests.AssertWithinOneDip(middle, ShownAt(window, image));

            Click(window.ZoomOutButton);

            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            CanvasZoomTests.AssertWithinOneDip(middle, ShownAt(window, image));
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

        // ----- The wheel -------------------------------------------------------------------------

        [Fact]
        public void The_wheel_zooms_around_the_pointer_only_with_ctrl() => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            var pointer = new Point(700, 120);
            var image = ImageAt(window, pointer);

            // Without Ctrl the wheel is left to the scroller.
            Assert.False(window.WheelZoom(ModifierKeys.None, 120, pointer));
            Assert.False(window.WheelZoom(ModifierKeys.Shift, -120, pointer));
            Assert.Equal(1.0, window.Canvas.Zoom);

            Assert.True(window.WheelZoom(ModifierKeys.Control, 120, pointer));
            Assert.Equal(1.25, window.Canvas.Zoom, 9);
            Assert.Equal("125%", window.ZoomText.Text);
            CanvasZoomTests.AssertWithinOneDip(pointer, ShownAt(window, image));

            // A precision touchpad sends small deltas: a third of a notch is a third of a step.
            Assert.True(window.WheelZoom(ModifierKeys.Control, -40, pointer));
            Assert.Equal(EditorZoom.Wheel(1.25, -40, 1.0), window.Canvas.Zoom, 9);
            Assert.InRange(window.Canvas.Zoom, 1.15, 1.17);
            CanvasZoomTests.AssertWithinOneDip(pointer, ShownAt(window, image));
        });

        [Fact]
        public void Ctrl_wheel_is_swallowed_while_text_is_being_typed() => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            var pointer = new Point(700, 120);
            window.TextEntry.Visibility = Visibility.Visible;
            try
            {
                // Taken, so the scroller does not scroll the image under the text box; and no zoom.
                Assert.True(window.WheelZoom(ModifierKeys.Control, 120, pointer));
                Assert.True(window.WheelZoom(ModifierKeys.Control | ModifierKeys.Shift, -120, pointer));

                Assert.Equal(1.0, window.Canvas.Zoom);
                Assert.Equal("100%", window.ZoomText.Text);
                Assert.Equal(300, window.Scroller.HorizontalOffset, 3);
                Assert.Equal(200, window.Scroller.VerticalOffset, 3);

                // A plain wheel is not the editor's business, typing or not.
                Assert.False(window.WheelZoom(ModifierKeys.None, 120, pointer));
            }
            finally
            {
                window.TextEntry.Visibility = Visibility.Collapsed;
            }
        });

        // ----- The keys --------------------------------------------------------------------------

        [Theory]
        [InlineData(ModifierKeys.Control, Key.OemPlus, 1.25)]
        [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.OemPlus, 1.25)]      // Ctrl and "+" on a US layout
        [InlineData(ModifierKeys.Control, Key.Add, 1.25)]                                // the numpad
        [InlineData(ModifierKeys.Control, Key.OemMinus, 0.8)]
        [InlineData(ModifierKeys.Control, Key.Subtract, 0.8)]
        public void Ctrl_plus_and_minus_step_the_zoom_around_the_middle_of_the_view(ModifierKeys modifiers, Key key, double expected) => WithEditor(2000, 1500, window =>
        {
            ScrollInto(window, 300, 200);
            var middle = CanvasZoom.ViewportCentre(window.Scroller);
            var image = ImageAt(window, middle);

            Assert.True(window.KeyZoom(modifiers, key));

            Assert.Equal(expected, window.Canvas.Zoom, 9);
            Assert.Equal(Label(expected, 1.0), window.ZoomText.Text);
            CanvasZoomTests.AssertWithinOneDip(middle, ShownAt(window, image));
        });

        [Theory]
        [InlineData(Key.D0)]
        [InlineData(Key.NumPad0)]
        public void Ctrl_0_shows_actual_size(Key key) => WithEditor(200, 100, window =>
        {
            window.Canvas.SetZoom(0.3);

            Assert.True(window.KeyZoom(ModifierKeys.Control, key));

            Assert.Equal(1.0, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);
        });

        [Theory]
        [InlineData(ModifierKeys.None, Key.OemPlus)]
        [InlineData(ModifierKeys.None, Key.Add)]
        [InlineData(ModifierKeys.None, Key.OemMinus)]
        [InlineData(ModifierKeys.None, Key.Subtract)]
        [InlineData(ModifierKeys.None, Key.D0)]
        [InlineData(ModifierKeys.None, Key.NumPad0)]
        [InlineData(ModifierKeys.Shift, Key.OemPlus)]
        [InlineData(ModifierKeys.Alt, Key.Add)]
        [InlineData(ModifierKeys.Control, Key.Z)]          // another Ctrl shortcut: not a zoom key
        [InlineData(ModifierKeys.Control, Key.D1)]
        public void A_key_that_is_not_a_zoom_shortcut_is_not_handled(ModifierKeys modifiers, Key key) => WithEditor(200, 100, window =>
        {
            window.Canvas.SetZoom(0.3);

            Assert.False(window.KeyZoom(modifiers, key));

            Assert.Equal(0.3, window.Canvas.Zoom);
        });

        // ----- The display's scaling -------------------------------------------------------------

        [Fact]
        public void On_a_150_percent_display_actual_size_is_one_image_pixel_per_screen_pixel() => WithEditor(200, 100, window =>
        {
            window.Canvas.SetZoom(0.3);

            Assert.True(window.KeyZoom(ModifierKeys.Control, Key.D0));

            Assert.Equal(1 / 1.5, window.Canvas.Zoom, 9);
            Assert.Equal("100%", window.ZoomText.Text);

            Click(window.ZoomInButton);
            Assert.Equal(1.25 / 1.5, window.Canvas.Zoom, 9);
            Assert.Equal("125%", window.ZoomText.Text);

            Assert.True(window.WheelZoom(ModifierKeys.Control, -120, new Point(300, 300)));
            Assert.Equal("100%", window.ZoomText.Text);
        }, dpiScale: 1.5);

        [Theory]
        [InlineData(200, 100)]         // small: would fit several times over
        [InlineData(1000, 500)]        // fits at one DIP per pixel, which there is 150%: too much
        [InlineData(2000, 1500)]       // larger than the view
        public void On_a_150_percent_display_fit_never_goes_past_actual_size(int imageWidth, int imageHeight) => WithEditor(imageWidth, imageHeight, window =>
        {
            double byView = Math.Min((window.Scroller.ViewportWidth - 40) / imageWidth, (window.Scroller.ViewportHeight - 40) / imageHeight);

            Click(window.ZoomFitButton);

            Assert.Equal(Math.Min(1 / 1.5, byView), window.Canvas.Zoom, 9);
            Assert.True(window.Canvas.Zoom <= 1 / 1.5 + 1e-12);
            Assert.Equal(Label(window.Canvas.Zoom, 1.5), window.ZoomText.Text);
            Assert.True(EditorZoom.Percent(window.Canvas.Zoom, 1.5) <= 100);
        }, dpiScale: 1.5);

        [Fact]
        public void Moving_to_a_display_with_another_scaling_keeps_the_percent() => WithEditor(2000, 1500, window =>
        {
            Click(window.ZoomActualButton);
            Assert.Equal(1 / 1.5, window.Canvas.Zoom, 9);

            // Dragged from the 150% display to a 100% one.
            window.DpiScaleOverride = 1.0;
            window.OnDpiScaleChanged(1.5, 1.0);

            Assert.Equal(1.0, window.Canvas.Zoom, 9);        // still one image pixel per screen pixel
            Assert.Equal("100%", window.ZoomText.Text);

            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Assert.Equal("244%", window.ZoomText.Text);

            // And back again.
            window.DpiScaleOverride = 1.5;
            window.OnDpiScaleChanged(1.0, 1.5);

            Assert.Equal(2.44140625 / 1.5, window.Canvas.Zoom, 9);
            Assert.Equal("244%", window.ZoomText.Text);
            Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(window.Canvas));
        }, dpiScale: 1.5);

        // ----- Crisp pixels ----------------------------------------------------------------------

        [Fact]
        public void From_200_percent_the_image_is_drawn_with_its_pixels_crisp() => WithEditor(200, 100, window =>
        {
            Click(window.ZoomActualButton);
            Assert.Equal(BitmapScalingMode.Unspecified, RenderOptions.GetBitmapScalingMode(window.Canvas));

            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Click(window.ZoomInButton);
            Assert.Equal("195%", window.ZoomText.Text);
            Assert.Equal(BitmapScalingMode.Unspecified, RenderOptions.GetBitmapScalingMode(window.Canvas));

            Click(window.ZoomInButton);
            Assert.Equal("244%", window.ZoomText.Text);
            Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(window.Canvas));

            Assert.True(window.WheelZoom(ModifierKeys.Control, -120, new Point(300, 300)));
            Assert.Equal("195%", window.ZoomText.Text);
            Assert.Equal(BitmapScalingMode.Unspecified, RenderOptions.GetBitmapScalingMode(window.Canvas));

            Assert.True(window.WheelZoom(ModifierKeys.Control, 12, new Point(300, 300)));     // a nudge on a touchpad
            Assert.Equal("200%", window.ZoomText.Text);
            Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(window.Canvas));

            Click(window.ZoomFitButton);
            Assert.Equal(BitmapScalingMode.Unspecified, RenderOptions.GetBitmapScalingMode(window.Canvas));
        });

        /// <summary>The colours the canvas shows, drawn off screen the way the app draws it: in software.</summary>
        private static int[] ColoursShown(AnnotationCanvas canvas)
        {
            int width = (int)Math.Round(canvas.ActualWidth), height = (int)Math.Round(canvas.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(canvas), null, new Rect(0, 0, width, height));

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new int[width * height];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels.Distinct().ToArray();
        }

        [Fact]
        public void Magnified_pixels_stay_their_own_colour_and_smaller_ones_are_smoothed() => UiThread.Run(() =>
        {
            // One-pixel checks of two colours: any smoothing between neighbours makes a third.
            const int Dark = unchecked((int)0xFF103050), Light = unchecked((int)0xFFE0F0FF);
            var pixels = new int[40 * 30];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (i % 40 + i / 40) % 2 == 0 ? Dark : Light;
            var checks = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, pixels, 40 * 4);
            checks.Freeze();

            WithEditor(checks, window =>
            {
                var pointer = new Point(300, 300);

                CanvasZoom.ZoomAt(window.Scroller, window.Canvas, 4.0, pointer);
                Assert.True(window.WheelZoom(ModifierKeys.Control, 0, pointer));    // the window catches up
                window.UpdateLayout();
                Assert.Equal("400%", window.ZoomText.Text);
                Assert.Equal(new[] { Dark, Light }, ColoursShown(window.Canvas).OrderBy(colour => (uint)colour));

                CanvasZoom.ZoomAt(window.Scroller, window.Canvas, 1.5, pointer);
                Assert.True(window.WheelZoom(ModifierKeys.Control, 0, pointer));
                window.UpdateLayout();
                Assert.Equal("150%", window.ZoomText.Text);
                Assert.True(ColoursShown(window.Canvas).Length > 2, "Below 200% the image is smoothed as before");
            });
        });

        // ----- On open ---------------------------------------------------------------------------

        [Fact]
        public void A_very_tall_capture_opens_at_a_readable_size_from_the_top() => WithEditor(CanvasZoomTests.Blank(1000, 20000), window =>
        {
            double whole = EditorZoom.Fit(1.0, 1000, 20000, window.Scroller.ViewportWidth - 40, window.Scroller.ViewportHeight - 40, 1.0);
            Assert.Equal("3%", Label(whole, 1.0));

            window.ZoomOnOpen();
            window.UpdateLayout();

            Assert.Equal(1.0, window.Canvas.Zoom, 9);           // 1000 px across fits the view: actual size
            Assert.Equal("100%", window.ZoomText.Text);
            Assert.Equal(0, window.Scroller.HorizontalOffset);
            Assert.Equal(0, window.Scroller.VerticalOffset);
            Assert.Equal(0, window.Scroller.ScrollableWidth);
            Assert.True(window.Scroller.ScrollableHeight > 19000);

            // The Fit button still shows the whole capture.
            Click(window.ZoomFitButton);

            Assert.Equal(whole, window.Canvas.Zoom, 9);
            Assert.Equal("3%", window.ZoomText.Text);
            Assert.Equal(0, window.Scroller.ScrollableHeight);
        });

        [Fact]
        public void A_tall_capture_wider_than_the_view_opens_fitted_to_the_width() => WithEditor(CanvasZoomTests.Blank(2000, 20000), window =>
        {
            double width = (window.Scroller.ViewportWidth - 40) / 2000;
            Assert.InRange(width, 0.3, 0.9);

            window.ZoomOnOpen();
            window.UpdateLayout();

            Assert.Equal(width, window.Canvas.Zoom, 9);
            Assert.Equal(0, window.Scroller.VerticalOffset);
            Assert.Equal(0, window.Scroller.ScrollableWidth);
        });

        [Fact]
        public void An_ordinary_capture_opens_fitted_whole() => WithEditor(2000, 1500, window =>
        {
            double whole = EditorZoom.Fit(1.0, 2000, 1500, window.Scroller.ViewportWidth - 40, window.Scroller.ViewportHeight - 40, 1.0);

            window.ZoomOnOpen();
            window.UpdateLayout();

            Assert.Equal(whole, window.Canvas.Zoom, 9);
            Assert.Equal(Label(whole, 1.0), window.ZoomText.Text);
            Assert.Equal(0, window.Scroller.ScrollableWidth);
            Assert.Equal(0, window.Scroller.ScrollableHeight);
        });

        // ----- Export ----------------------------------------------------------------------------

        [Fact]
        public void The_exported_image_is_the_same_at_any_zoom() => WithEditor(CanvasZoomTests.Pattern(400, 300), window =>
        {
            var document = window.Canvas.Document!;
            document.Add(new ShapeAnnotation(CaptureTool.Rectangle, new ImgPoint(120, 100), new ImgPoint(260, 200)) { ColorHex = "#FF453A", Thickness = 3 });
            document.Add(new RedactAnnotation(new ImgPoint(80, 60), new ImgPoint(180, 120), RedactStyle.Pixelate));
            document.ApplyCrop(new PixelRect(50, 40, 300, 220));
            window.Canvas.InvalidateMeasure();
            window.UpdateLayout();

            byte[]? first = null;
            foreach (double zoom in new[] { 1.0, 0.03, 7.5 })
            {
                var pointer = new Point(300, 300);
                CanvasZoom.ZoomAt(window.Scroller, window.Canvas, zoom, pointer);
                Assert.True(window.WheelZoom(ModifierKeys.Control, 0, pointer));    // the window catches up: label and drawing mode
                Assert.Equal(zoom, window.Canvas.Zoom, 9);
                Assert.Equal(zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified,
                    RenderOptions.GetBitmapScalingMode(window.Canvas));

                var image = Assert.IsAssignableFrom<BitmapSource>(window.Result);
                Assert.Equal(300, image.PixelWidth);
                Assert.Equal(220, image.PixelHeight);
                var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
                image.CopyPixels(bytes, image.PixelWidth * 4, 0);

                first ??= bytes;
                Assert.True(bytes.AsSpan().SequenceEqual(first),
                    string.Format(CultureInfo.InvariantCulture, "The export at zoom {0} differs from the one at zoom 1", zoom));
            }

            // Not a blank picture: the mark and the pattern are in it.
            Assert.True(first!.Distinct().Count() > 50);
        });
    }
}
