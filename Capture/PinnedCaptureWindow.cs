using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private readonly Image _image;
        private double _scale = 1;

        private PinnedCaptureWindow(BitmapSource source)
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

            _image = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                Width = source.PixelWidth,
                Height = source.PixelHeight,
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
            var win = new PinnedCaptureWindow(image);
            win.Show();
            return win;
        }

        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            // DragMove throws if the button was already released before it runs.
            try { DragMove(); } catch { }
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_image.Source is not BitmapSource src) return;
            _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 4);
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
