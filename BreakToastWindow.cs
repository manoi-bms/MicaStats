using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Localization;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// StackChan's kind nag: after a long stretch at the desk, a quiet corner card suggests a
    /// break. Same pattern as <see cref="AlertToastWindow"/>: never takes focus, fades by itself.
    /// </summary>
    public sealed class BreakToastWindow : Window
    {
        private static readonly Color Mint = Color.FromRgb(0x34, 0xD3, 0x99);
        private readonly DispatcherTimer _dismiss;

        private BreakToastWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;
            Title = UiText.Tr("Time for a break");
            Content = BuildCard();

            Loaded += (s, e) => { ToastStack.Restack(); ToastStack.PlayEntrance(this); };
            _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            _dismiss.Tick += (s, e) => Close();
            _dismiss.Start();
            MouseEnter += (s, e) => _dismiss.Stop();
            MouseLeave += (s, e) => _dismiss.Start();
            Closed += (s, e) => { _dismiss.Stop(); ToastStack.Remove(this); };
        }

        public static void ShowNow()
        {
            ToastStack.CloseAll<BreakToastWindow>();
            var toast = Create();
            ToastStack.Add(toast, 1);
            toast.Show();
        }

        /// <summary>Builds the card without showing it; <see cref="ShowNow"/> shows it, tests do not.</summary>
        internal static BreakToastWindow Create() => new();

        private UIElement BuildCard()
        {
            var stack = new StackPanel { Margin = new Thickness(16, 13, 16, 13) };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            heading.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily(UiGlyphs.FontStack),
                FontSize = 11,
                Foreground = new SolidColorBrush(Mint),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            heading.Children.Add(new TextBlock
            {
                Text = UiText.Tr("StackChan"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
                Foreground = new SolidColorBrush(Mint),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(heading);
            stack.Children.Add(new TextBlock
            {
                Text = UiText.Tr("Time for a break"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xFF, 0xFF)),
            });
            stack.Children.Add(new TextBlock
            {
                Text = UiText.Tr("You have been at your desk for a long while. Stand up, stretch and rest your eyes for a minute."),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xE9, 0xED, 0xF2)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                Margin = new Thickness(0, 3, 0, 10),
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ToastButton.Create(UiText.Tr("Got it"), Mint, primary: true, Color.FromRgb(0x06, 0x24, 0x18), Close));
            stack.Children.Add(buttons);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x10, 0x1C, 0x17)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x77, Mint.R, Mint.G, Mint.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = stack,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.5,
                    Color = Colors.Black,
                },
            };
        }
    }
}
