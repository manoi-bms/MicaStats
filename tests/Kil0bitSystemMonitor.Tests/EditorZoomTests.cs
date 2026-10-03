using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The capture editor's zoom arithmetic. A zoom is device-independent units per image pixel;
    /// the percent the user sees is screen pixels per image pixel, so 100% is the screenshot at
    /// its real size on a display at any scaling.
    /// </summary>
    public class EditorZoomTests
    {
        [Fact]
        public void The_step_and_the_range_are_fixed()
        {
            Assert.Equal(1.25, EditorZoom.Step);
            Assert.Equal(1.0, EditorZoom.MinPercent);
            Assert.Equal(800.0, EditorZoom.MaxPercent);
        }

        [Theory]
        [InlineData(1.0, 1, 1.25)]
        [InlineData(1.0, -1, 0.8)]
        [InlineData(1.0, 2, 1.5625)]
        [InlineData(0.4, 1, 0.5)]
        [InlineData(0.5, -1, 0.4)]
        [InlineData(2.0, 0, 2.0)]
        public void A_step_multiplies_or_divides_by_the_step(double zoom, int steps, double expected)
        {
            Assert.Equal(expected, EditorZoom.Stepped(zoom, steps, 1.0), 9);
            Assert.Equal(expected, EditorZoom.Stepped(zoom, steps, 1.5), 9);
        }

        [Fact]
        public void A_step_up_then_down_comes_back()
        {
            double up = EditorZoom.Stepped(0.37, 1, 1.25);

            Assert.Equal(0.37, EditorZoom.Stepped(up, -1, 1.25), 9);
        }

        [Theory]
        [InlineData(0.94, 1, 1.0, 1.0)]                  // from 94% a step in stops on 100%
        [InlineData(1.18, -1, 1.0, 1.0)]                 // from 118% a step out stops on 100%
        [InlineData(1.0, 1, 1.0, 1.25)]                  // exactly at 100% a step leaves it as usual
        [InlineData(1.0, -1, 1.0, 0.8)]
        [InlineData(0.94, 2, 1.0, 1.25)]                 // the stop takes one step; the next carries on
        [InlineData(1.18, -2, 1.0, 0.8)]
        [InlineData(0.94 / 1.5, 1, 1.5, 1 / 1.5)]        // 100% on a 150% display is two thirds of a DIP per pixel
        [InlineData(1.18 / 1.5, -1, 1.5, 1 / 1.5)]
        [InlineData(0.94, 1, 1.5, 1.175)]                // 141% there: nowhere near 100%, an ordinary step
        [InlineData(0.5, 1, 1.0, 0.625)]
        public void A_step_that_would_pass_actual_size_lands_on_it(double zoom, int steps, double dpiScale, double expected)
        {
            Assert.Equal(expected, EditorZoom.Stepped(zoom, steps, dpiScale), 9);
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        [InlineData(1.75)]
        [InlineData(3.0)]
        public void Coming_back_to_actual_size_does_not_stick_there(double dpiScale)
        {
            double actual = EditorZoom.ActualSize(dpiScale);

            // Back from a step away, the zoom may differ from actual size in its last bit;
            // the next step must still leave it, not "land on" it again.
            double fromAbove = EditorZoom.Stepped(EditorZoom.Stepped(actual, 1, dpiScale), -1, dpiScale);
            double fromBelow = EditorZoom.Stepped(EditorZoom.Stepped(actual, -1, dpiScale), 1, dpiScale);

            foreach (double back in new[] { fromAbove, fromBelow })
            {
                Assert.Equal(100, EditorZoom.Percent(back, dpiScale));
                Assert.Equal(actual * 1.25, EditorZoom.Stepped(back, 1, dpiScale), 9);
                Assert.Equal(actual / 1.25, EditorZoom.Stepped(back, -1, dpiScale), 9);
            }
        }

        [Fact]
        public void The_wheel_does_not_stop_at_actual_size()
        {
            Assert.Equal(0.94 * 1.25, EditorZoom.Wheel(0.94, 120, 1.0), 9);
            Assert.Equal(1.18 / 1.25, EditorZoom.Wheel(1.18, -120, 1.0), 9);
        }

        [Fact]
        public void Any_number_of_steps_ends_at_an_end_of_the_range()
        {
            Assert.Equal(8.0, EditorZoom.Stepped(0.3, int.MaxValue, 1.0), 9);
            Assert.Equal(0.01, EditorZoom.Stepped(0.3, int.MinValue, 1.0), 9);
        }

        [Theory]
        [InlineData(120, 1.25)]              // one notch
        [InlineData(-120, 0.8)]
        [InlineData(240, 1.5625)]            // two notches in one message
        [InlineData(40, 1.0772173450)]       // a third of a notch (precision touchpad): the cube root of 1.25
        [InlineData(0, 1.0)]
        public void The_wheel_scales_by_one_step_per_notch(int delta, double factor)
        {
            Assert.Equal(factor, EditorZoom.Wheel(1.0, delta, 1.0), 9);
            Assert.Equal(0.4 * factor, EditorZoom.Wheel(0.4, delta, 1.5), 9);
        }

        [Fact]
        public void Three_small_wheel_deltas_add_up_to_one_notch()
        {
            double zoom = 0.6;
            for (int i = 0; i < 3; i++) zoom = EditorZoom.Wheel(zoom, 40, 1.0);

            Assert.Equal(0.6 * 1.25, zoom, 9);
        }

        [Theory]
        [InlineData(1.0, 0.01, 8.0)]
        [InlineData(1.5, 0.01 / 1.5, 8.0 / 1.5)]
        public void The_zoom_is_kept_between_1_and_800_percent(double dpiScale, double lowest, double highest)
        {
            Assert.Equal(lowest, EditorZoom.Clamp(0.0001, dpiScale), 9);
            Assert.Equal(highest, EditorZoom.Clamp(1000, dpiScale), 9);
            Assert.Equal(0.5, EditorZoom.Clamp(0.5, dpiScale), 9);          // inside the range: left alone

            Assert.Equal(1, EditorZoom.Percent(EditorZoom.Clamp(0.0001, dpiScale), dpiScale));
            Assert.Equal(800, EditorZoom.Percent(EditorZoom.Clamp(1000, dpiScale), dpiScale));

            // Steps and the wheel stop at the same ends.
            Assert.Equal(highest, EditorZoom.Stepped(highest, 1, dpiScale), 9);
            Assert.Equal(lowest, EditorZoom.Stepped(lowest, -1, dpiScale), 9);
            Assert.Equal(highest, EditorZoom.Wheel(highest, 120, dpiScale), 9);
            Assert.Equal(lowest, EditorZoom.Wheel(lowest, -120, dpiScale), 9);
            Assert.Equal(highest, EditorZoom.Stepped(1.0, 40, dpiScale), 9);
            Assert.Equal(lowest, EditorZoom.Wheel(1.0, -120 * 40, dpiScale), 9);
        }

        [Theory]
        [InlineData(1.0, 1.0)]
        [InlineData(1.25, 0.8)]
        [InlineData(1.5, 1 / 1.5)]
        public void Actual_size_is_one_image_pixel_per_screen_pixel(double dpiScale, double expected)
        {
            Assert.Equal(expected, EditorZoom.ActualSize(dpiScale), 9);
            Assert.Equal(100, EditorZoom.Percent(EditorZoom.ActualSize(dpiScale), dpiScale));
        }

        [Fact]
        public void Fit_never_goes_past_actual_size()
        {
            // A small capture in a large viewport: shown at its real size, never enlarged.
            Assert.Equal(1.0, EditorZoom.Fit(0.3, 100, 100, 800, 600, 1.0), 9);
            Assert.Equal(1 / 1.5, EditorZoom.Fit(0.3, 100, 100, 800, 600, 1.5), 9);
            Assert.Equal(100, EditorZoom.Percent(EditorZoom.Fit(0.3, 100, 100, 800, 600, 1.5), 1.5));
        }

        [Fact]
        public void Fit_scales_a_large_image_down_to_the_tighter_side()
        {
            Assert.Equal(0.4, EditorZoom.Fit(1.0, 2000, 1000, 800, 600, 1.0), 9);     // the width decides
            Assert.Equal(0.3, EditorZoom.Fit(1.0, 1000, 2000, 800, 600, 1.0), 9);     // the height decides
            Assert.Equal(0.4, EditorZoom.Fit(1.0, 2000, 1000, 800, 600, 1.5), 9);     // the viewport is in DIPs at any scaling
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public void A_tall_scrolling_capture_fits_inside_the_range(double dpiScale)
        {
            double fit = EditorZoom.Fit(1.0, 1000, 20000, 800, 600, dpiScale);

            Assert.Equal(600.0 / 20000, fit, 9);
            Assert.True(20000 * fit <= 600 + 1e-9);
            Assert.InRange(EditorZoom.Percent(fit, dpiScale), (int)EditorZoom.MinPercent, (int)EditorZoom.MaxPercent);
            Assert.Equal(fit, EditorZoom.Clamp(fit, dpiScale), 9);                    // no clamping was needed
        }

        [Fact]
        public void Fit_stops_at_the_lowest_zoom_for_an_image_too_large_to_fit()
        {
            Assert.Equal(0.01, EditorZoom.Fit(1.0, 1000, 200000, 800, 600, 1.0), 9);
        }

        [Theory]
        [InlineData(0, 100, 800, 600)]
        [InlineData(100, 0, 800, 600)]
        [InlineData(100, 100, 0, 600)]
        [InlineData(100, 100, 800, -40)]       // a viewport not laid out yet, less the 40 px margin
        [InlineData(double.NaN, 100, 800, 600)]
        public void Fit_leaves_the_zoom_alone_when_a_size_is_missing(double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
        {
            Assert.Equal(0.37, EditorZoom.Fit(0.37, imageWidth, imageHeight, viewportWidth, viewportHeight, 1.0));
        }

        [Theory]
        [InlineData(1.0, 1.0, 100)]
        [InlineData(1.0, 1.25, 125)]
        [InlineData(1.0, 1.5, 150)]
        [InlineData(0.03, 1.0, 3)]
        [InlineData(0.666, 1.0, 67)]
        [InlineData(0.664, 1.0, 66)]
        [InlineData(0.125, 1.0, 13)]           // a half rounds up, not to the even number
        [InlineData(8.0, 1.0, 800)]
        public void The_percent_is_screen_pixels_per_image_pixel_rounded(double zoom, double dpiScale, int expected)
        {
            Assert.Equal(expected, EditorZoom.Percent(zoom, dpiScale));
        }

        [Theory]
        [InlineData(1000, 20000, 1.0, 1.0)]                 // a scrolling capture: its width fits, so actual size
        [InlineData(1000, 20000, 1.5, 1 / 1.5)]
        [InlineData(1200, 20000, 1.0, 0.9)]                 // wider than the view: fitted to the width
        [InlineData(1920, 1080, 1.0, 600.0 / 1080)]         // an ordinary screen capture: the whole image
        [InlineData(800, 1000, 1.0, 0.6)]                   // taller than the view, but not by much: the whole image
        [InlineData(1000, 1200, 1.0, 0.5)]                  // the whole image at exactly half the width fit: still whole
        [InlineData(1000, 1201, 1.0, 1.0)]                  // any less than half: the width
        [InlineData(20000, 1000, 1.0, 0.054)]               // very wide: the width is the whole
        [InlineData(200, 100, 1.5, 1 / 1.5)]                // small: actual size, never enlarged
        public void A_capture_opens_whole_unless_that_would_be_a_sliver(double imageWidth, double imageHeight, double dpiScale, double expected)
        {
            double open = EditorZoom.Open(0.3, imageWidth, imageHeight, 1080, 600, dpiScale);

            Assert.Equal(expected, open, 9);
            Assert.True(imageWidth * open <= 1080 + 1e-9, "The width always fits the view");
        }

        [Fact]
        public void A_tall_capture_does_not_open_at_3_percent()
        {
            Assert.Equal(3, EditorZoom.Percent(EditorZoom.Fit(1.0, 1000, 20000, 1080, 600, 1.0), 1.0));    // the whole image
            Assert.Equal(100, EditorZoom.Percent(EditorZoom.Open(1.0, 1000, 20000, 1080, 600, 1.0), 1.0));
        }

        [Theory]
        [InlineData(0, 100, 800, 600)]
        [InlineData(100, 0, 800, 600)]
        [InlineData(100, 100, 0, 600)]
        [InlineData(100, 100, 800, -40)]
        [InlineData(100, double.NaN, 800, 600)]
        public void Open_leaves_the_zoom_alone_when_a_size_is_missing(double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
        {
            Assert.Equal(0.37, EditorZoom.Open(0.37, imageWidth, imageHeight, viewportWidth, viewportHeight, 1.0));
        }

        [Theory]
        [InlineData(2.0, 1.0, true)]                // 200%
        [InlineData(8.0, 1.0, true)]
        [InlineData(1.996, 1.0, true)]              // shown as 200%
        [InlineData(1.994, 1.0, false)]             // shown as 199%
        [InlineData(1.0, 1.0, false)]
        [InlineData(0.03, 1.0, false)]
        [InlineData(2.0 / 1.5, 1.5, true)]          // 200% on a 150% display
        [InlineData(1.3, 1.5, false)]               // 195% there
        [InlineData(1.0, 2.0, true)]                // one DIP per pixel on a 200% display is 200%
        [InlineData(2.0, double.NaN, true)]         // a bad scale counts as 1
        public void Pixels_are_shown_crisp_from_200_percent(double zoom, double dpiScale, bool expected)
        {
            Assert.Equal(200.0, EditorZoom.PixelatedFromPercent);
            Assert.Equal(expected, EditorZoom.Pixelated(zoom, dpiScale));
        }

        [Theory]
        [InlineData(1 / 1.5, 1.5, 1.0, 1.0)]        // 100% on a 150% display stays 100% on a 100% one
        [InlineData(1.0, 1.0, 1.5, 1 / 1.5)]
        [InlineData(0.5, 1.25, 2.0, 0.3125)]        // 62.5% stays 62.5%
        [InlineData(0.4, 1.5, 1.5, 0.4)]            // no change of scaling, no change of zoom
        [InlineData(0.5, double.NaN, 2.0, 0.25)]    // a bad scale counts as 1
        public void A_change_of_scaling_keeps_the_percent(double zoom, double oldScale, double newScale, double expected)
        {
            double rescaled = EditorZoom.Rescaled(zoom, oldScale, newScale);

            Assert.Equal(expected, rescaled, 9);
            Assert.Equal(EditorZoom.Percent(zoom, oldScale), EditorZoom.Percent(rescaled, newScale));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_bad_dpi_scale_is_treated_as_1(double bad)
        {
            Assert.Equal(1.0, EditorZoom.ActualSize(bad), 9);
            Assert.Equal(50, EditorZoom.Percent(0.5, bad));
            Assert.Equal(8.0, EditorZoom.Clamp(1000, bad), 9);
            Assert.Equal(0.01, EditorZoom.Clamp(0.0001, bad), 9);
            Assert.Equal(1.25, EditorZoom.Stepped(1.0, 1, bad), 9);
            Assert.Equal(0.8, EditorZoom.Wheel(1.0, -120, bad), 9);
            Assert.Equal(1.0, EditorZoom.Fit(0.3, 100, 100, 800, 600, bad), 9);
        }
    }
}
