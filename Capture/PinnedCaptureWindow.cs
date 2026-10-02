using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Capture;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace Kil0bitSystemMonitor.Capture
{
    /// <summary>
    /// A capture pinned on top of everything — the "keep this on screen while I retype it"
    /// window. Borderless and always-on-top, dragged by its body, scaled with the wheel.
    ///
    /// <para>
    /// Several can be open at once (comparing two states side by side is the whole point), so
    /// this deliberately keeps no singleton.
    /// </para>
    /// </summary>
    public sealed class PinnedCaptureWindow : Window
    {
        private const double MinScale = 0.1, MaxScale = 4;

        /// <summary>Room kept between a fitted pin and the edges of the work area, in DIPs.</summary>
        private const double ScreenMargin = 24;

        private readonly Image _image;
        private readonly double _minScale;
        private double _scale;

        /// <param name="fit">The opening scale: below 1 when the image is larger than the screen.</param>
        private PinnedCaptureWindow(BitmapSource source, double fit)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = Brushes.Transparent;
            AllowsTransparency = true;
            Cursor = Cursors.SizeAll;
            Title = "MicaStats Pinned Capture";

            // A scaled-down pin opens centred on the screen under the pointer, where its room was measured.
            _scale = fit;
            _minScale = Math.Min(MinScale, fit);
            if (fit < 1) WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _image = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                Width = source.PixelWidth * fit,
                Height = source.PixelHeight * fit,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

            // A thin border and drop shadow separate the pin from whatever is behind it.
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD2, 0xE4)),
                BorderThickness = new Thickness(1),
                Background = Brushes.Black,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Opacity = 0.55,
                    Color = Colors.Black,
                },
                Child = _image,
                ToolTip = "Drag to move · wheel to resize · Ctrl+C copy · Esc or double-click to close",
            };

            MouseLeftButtonDown += OnLeftDown;
            MouseWheel += OnWheel;
            KeyDown += OnKey;
            MouseDoubleClick += (s, e) => Close();
        }

        /// <summary>Pins <paramref name="image"/> on screen and returns the window.</summary>
        public static PinnedCaptureWindow Pin(BitmapSource image)
        {
            var (roomWidth, roomHeight) = Room();
            var win = new PinnedCaptureWindow(image, FitScale(image.PixelWidth, image.PixelHeight, roomWidth, roomHeight));
            win.Show();
            return win;
        }

        /// <summary>
        /// The scale that fits a <paramref name="width"/> x <paramref name="height"/> image into the
        /// room, keeping its aspect ratio and never enlarging it: a 20,000 px scrolling capture
        /// pinned at full size would run far off the screen. 1 when the room is unknown.
        /// </summary>
        internal static double FitScale(double width, double height, double roomWidth, double roomHeight)
        {
            if (width <= 0 || height <= 0 || roomWidth <= 0 || roomHeight <= 0) return 1;
            return Math.Min(1, Math.Min(roomWidth / width, roomHeight / height));
        }

        /// <summary>The work area of the monitor under the pointer, in DIPs, less a margin.</summary>
        private static (double Width, double Height) Room()
        {
            try
            {
                if (Win32Helper.GetCursorPos(out var p))
                {
                    var monitor = CaptureGeometry.MonitorAt(ScreenCaptureEngine.GetMonitors(), p.X, p.Y);
                    if (monitor != null && monitor.Scale > 0 && !monitor.WorkArea.IsEmpty)
                        return (monitor.WorkArea.Width / monitor.Scale - 2 * ScreenMargin,
                                monitor.WorkArea.Height / monitor.Scale - 2 * ScreenMargin);
                }
            }
            catch { }
            var work = SystemParameters.WorkArea;
            return (work.Width - 2 * ScreenMargin, work.Height - 2 * ScreenMargin);
        }

        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            // DragMove throws if the button was already released before it runs.
            try { DragMove(); } catch { }
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_image.Source is not BitmapSource src) return;
            _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), _minScale, MaxScale);
            _image.Width = src.PixelWidth * _scale;
            _image.Height = src.PixelHeight * _scale;
            e.Handled = true;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.C &&
                _image.Source is BitmapSource src)
            {
                ScreenCaptureEngine.CopyToClipboard(src);
                e.Handled = true;
            }
        }
    }
}
