using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Update;

using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The "an update is available" notification: a small card in the corner of the working
    /// area, in the app's own dark styling.
    ///
    /// <para>
    /// Deliberately not a modal dialog. A monitoring utility that blocks the screen to announce
    /// its own update is an annoyance, so this appears quietly, never takes focus, and fades
    /// itself away after a while — the update stays available in the overlay menu and in
    /// Settings either way.
    /// </para>
    /// </summary>
    public sealed class UpdateToastWindow : Window
    {
        private static UpdateToastWindow? _open;
        private readonly DispatcherTimer _dismiss;

        /// <summary>Raised when the user chooses to install; the host opens the Updates page.</summary>
        public event Action? InstallRequested;

        /// <summary>Raised when the user asks not to be reminded about this version.</summary>
        public event Action? SkipRequested;

        private UpdateToastWindow(ReleaseInfo release)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;           // must never steal focus from what the user is doing
            Title = "MicaStats Update";

            Content = BuildCard(release);

            Loaded += (s, e) => { PositionInCorner(); PlayEntrance(); };

            _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(18) };
            _dismiss.Tick += (s, e) => Close();
            _dismiss.Start();

            // Keep it on screen while the pointer is over it.
            MouseEnter += (s, e) => _dismiss.Stop();
            MouseLeave += (s, e) => _dismiss.Start();
            Closed += (s, e) => { _dismiss.Stop(); if (ReferenceEquals(_open, this)) _open = null; };
        }

        /// <summary>Shows the notification, replacing any already on screen.</summary>
        public static UpdateToastWindow ShowFor(ReleaseInfo release, Action onInstall, Action onSkip)
        {
            try { _open?.Close(); } catch { }

            var toast = new UpdateToastWindow(release);
            _open = toast;
            toast.InstallRequested += onInstall;
            toast.SkipRequested += onSkip;
            toast.Show();
            return toast;
        }

        private UIElement BuildCard(ReleaseInfo release)
        {
            var stack = new StackPanel { Margin = new Thickness(16, 13, 16, 13) };

            stack.Children.Add(new TextBlock
            {
                Text = "Update available",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xD2, 0xE4)),
                Margin = new Thickness(0, 0, 0, 4),
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"MicaStats {release.TagName}",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xFF, 0xFF)),
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"You have {UpdateService.CurrentVersionText}. Windows will ask for permission to install.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xE9, 0xED, 0xF2)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                Margin = new Thickness(0, 3, 0, 10),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ToastButton.Create("Install", Color.FromRgb(0x3F, 0xD2, 0xE4), primary: true, Color.FromRgb(0x06, 0x22, 0x2A), () =>
            {
                InstallRequested?.Invoke();
                Close();
            }));
            buttons.Children.Add(ToastButton.Create("Later", Color.FromRgb(0x3F, 0xD2, 0xE4), primary: false, Color.FromRgb(0x06, 0x22, 0x2A), Close));
            buttons.Children.Add(ToastButton.Create("Skip this version", Color.FromRgb(0x3F, 0xD2, 0xE4), primary: false, Color.FromRgb(0x06, 0x22, 0x2A), () =>
            {
                SkipRequested?.Invoke();
                Close();
            }));
            stack.Children.Add(buttons);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x14, 0x14, 0x1C)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x3F, 0xD2, 0xE4)),
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


        /// <summary>Bottom-right of the working area, clear of the taskbar.</summary>
        private void PositionInCorner()
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 18;
            Top = work.Bottom - ActualHeight - 18;
        }

        private void PlayEntrance()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            BeginAnimation(TopProperty,
                new DoubleAnimation(Top + 16, Top, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
    }
}
