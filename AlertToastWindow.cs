using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Diagnostics;

using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The "something is wrong" card: a small amber notice in the corner of the working area.
    ///
    /// <para>
    /// Built on the same pattern as <see cref="UpdateToastWindow"/> and for the same reason —
    /// a monitoring utility that blocks the screen is an annoyance. It never takes focus and
    /// fades itself away; the reading stays in the panel and the log either way.
    /// </para>
    ///
    /// <para>
    /// Stacks rather than replaces. Two rules can breach at once (a hot CPU while a disk fills)
    /// and a notice that silently overwrote the previous one would hide the first problem.
    /// </para>
    /// </summary>
    public sealed class AlertToastWindow : Window
    {
        /// <summary>Every notice currently on screen, newest last.</summary>
        private static readonly List<AlertToastWindow> Open = new();

        /// <summary>Beyond this the corner becomes a wall of cards; the log has the rest.</summary>
        private const int MaxOnScreen = 3;

        private const double CardMargin = 18;
        private const double CardGap = 8;

        /// <summary>Amber rather than the app's cyan: this is a warning, not information.</summary>
        private static readonly Color Amber = Color.FromRgb(0xE8, 0xA5, 0x3C);

        private readonly DispatcherTimer _dismiss;

        /// <summary>Raised when the user asks to see the detail.</summary>
        public event Action? OpenRequested;

        private AlertToastWindow(AlertEvent alert)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;           // must never steal focus from what the user is doing
            Title = "MicaStats Alert";

            Content = BuildCard(alert);

            Loaded += (s, e) => { RestackAll(); PlayEntrance(); };

            // Longer than the update notice: this one reports a condition worth reading, and
            // the user may be away from the keyboard when it appears.
            _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _dismiss.Tick += (s, e) => Close();
            _dismiss.Start();

            MouseEnter += (s, e) => _dismiss.Stop();
            MouseLeave += (s, e) => _dismiss.Start();
            Closed += (s, e) => { _dismiss.Stop(); Open.Remove(this); RestackAll(); };
        }

        /// <summary>Shows a notice for one breached rule.</summary>
        public static AlertToastWindow ShowFor(AlertEvent alert, Action onOpen)
        {
            // Drop the oldest rather than the newest: the most recent problem is the one the
            // user has not seen yet.
            while (Open.Count >= MaxOnScreen)
            {
                var oldest = Open[0];
                try { oldest.Close(); } catch { }
                Open.Remove(oldest);
            }

            var toast = new AlertToastWindow(alert);
            toast.OpenRequested += onOpen;
            Open.Add(toast);
            toast.Show();
            return toast;
        }

        /// <summary>Closes every notice, e.g. when alerts are switched off.</summary>
        public static void CloseAll()
        {
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                try { Open[i].Close(); } catch { }
            }
            Open.Clear();
        }

        private UIElement BuildCard(AlertEvent alert)
        {
            var stack = new StackPanel { Margin = new Thickness(16, 13, 16, 13) };

            var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            heading.Children.Add(new TextBlock
            {
                Text = UiGlyphs.Alert,
                FontFamily = new FontFamily(UiGlyphs.FontStack),
                FontSize = 11,
                Foreground = new SolidColorBrush(Amber),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            heading.Children.Add(new TextBlock
            {
                Text = "MicaStats alert",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
                Foreground = new SolidColorBrush(Amber),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(heading);

            stack.Children.Add(new TextBlock
            {
                Text = alert.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xFF, 0xFF)),
            });

            stack.Children.Add(new TextBlock
            {
                Text = alert.Message,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xE9, 0xED, 0xF2)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                Margin = new Thickness(0, 3, 0, 10),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(MakeButton("Show me", primary: true, () =>
            {
                OpenRequested?.Invoke();
                Close();
            }));
            buttons.Children.Add(MakeButton("Dismiss", primary: false, Close));
            stack.Children.Add(buttons);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x1C, 0x16, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x77, Amber.R, Amber.G, Amber.B)),
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

        private static Button MakeButton(string text, bool primary, Action onClick)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(primary ? 14 : 10, 5, primary ? 14 : 10, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = primary
                    ? new SolidColorBrush(Color.FromRgb(0x2A, 0x18, 0x06))
                    : new SolidColorBrush(Color.FromArgb(0xCF, 0xED, 0xED, 0xF2)),
                Background = primary
                    ? new SolidColorBrush(Amber)
                    : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0),
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            };
            button.Click += (s, e) => onClick();
            return button;
        }

        /// <summary>
        /// Lays the open notices up from the bottom-right corner. Re-run whenever one appears
        /// or closes, so a gap never opens in the middle of the stack.
        /// </summary>
        private static void RestackAll()
        {
            var work = SystemParameters.WorkArea;
            double bottom = work.Bottom - CardMargin;

            for (int i = Open.Count - 1; i >= 0; i--)
            {
                var toast = Open[i];
                if (!toast.IsLoaded) continue;

                toast.Left = work.Right - toast.ActualWidth - CardMargin;
                toast.Top = bottom - toast.ActualHeight;
                bottom -= toast.ActualHeight + CardGap;
            }
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
