using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Watchdog;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The notice that something is burning a core for nobody.
    ///
    /// <para>
    /// Shares the corner with <see cref="AlertToastWindow"/> through
    /// <see cref="ToastStack"/>, so the two kinds of card stack together rather than on top of
    /// each other. What is its own is the one thing a card is for: what it says, and what its
    /// button does.
    /// </para>
    ///
    /// <para>
    /// The button ends processes, so it waits for a click and the card never takes focus. An
    /// irreversible action on a card that stole focus mid-keystroke would be pressed by
    /// accident.
    /// </para>
    /// </summary>
    public sealed class OrphanToastWindow : Window
    {
        /// <summary>
        /// Three, matching <see cref="AlertToastWindow"/>, and deliberately not one.
        ///
        /// <para>
        /// Each card carries its own findings, and those are marked alerted once it is shown, so
        /// the watchdog never raises them again. A later scan that finds a new orphan must
        /// therefore not evict an unanswered card: its End them button is the only way left to
        /// end those processes for the life of the app. Scans are a minute apart, so three leaves
        /// room for that while still capping how much of the corner the cards can take.
        /// </para>
        /// </summary>
        private const int MaxOnScreen = 3;

        /// <summary>Amber, matching the alert card: this is a warning, not information.</summary>
        private static readonly Color Amber = Color.FromRgb(0xE8, 0xA5, 0x3C);

        private readonly DispatcherTimer _dismiss;
        private readonly IReadOnlyList<OrphanFinding> _findings;

        private OrphanToastWindow(IReadOnlyList<OrphanFinding> findings,
                                  Action<IReadOnlyList<OrphanFinding>> onEndAll)
        {
            _findings = findings;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;           // must never steal focus: its button is destructive
            Title = "MicaStats runaway search";

            Content = BuildCard(onEndAll);

            Loaded += (s, e) => { ToastStack.Restack(); ToastStack.PlayEntrance(this); };

            // Longer than the alert card. This one asks for a decision, and the machine it
            // appears on is by definition busy.
            _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _dismiss.Tick += (s, e) => Close();
            _dismiss.Start();

            MouseEnter += (s, e) => _dismiss.Stop();
            MouseLeave += (s, e) => _dismiss.Start();
            Closed += (s, e) => { _dismiss.Stop(); ToastStack.Remove(this); };
        }

        /// <summary>Shows one card for a set of findings.</summary>
        public static OrphanToastWindow ShowFor(
            IReadOnlyList<OrphanFinding> findings, Action<IReadOnlyList<OrphanFinding>> onEndAll)
        {
            var toast = new OrphanToastWindow(findings, onEndAll);
            ToastStack.Add(toast, MaxOnScreen);
            toast.Show();
            return toast;
        }

        /// <summary>Closes every runaway-search notice, e.g. when the watchdog is switched off.</summary>
        public static void CloseAll() => ToastStack.CloseAll<OrphanToastWindow>();

        /// <summary>
        /// The headline: what it is, how many, and how much it has cost. The command lines are
        /// in the log — a card is read at a glance, and a full find expression is not.
        /// </summary>
        internal static string Headline(IReadOnlyList<OrphanFinding> findings)
        {
            double totalCpu = 0;
            foreach (var finding in findings) totalCpu += finding.CpuSeconds;

            string what = findings.Count == 1
                ? "An orphaned " + findings[0].Name
                : findings.Count.ToString(CultureInfo.InvariantCulture) + " orphaned searches";

            string cost = totalCpu >= 120
                ? (totalCpu / 60d).ToString("0", CultureInfo.InvariantCulture) + " minutes"
                : totalCpu.ToString("0", CultureInfo.InvariantCulture) + " seconds";

            return what + ", " + cost + " of CPU burned";
        }

        private UIElement BuildCard(Action<IReadOnlyList<OrphanFinding>> onEndAll)
        {
            var stack = new StackPanel { Margin = new Thickness(16, 13, 16, 13) };

            var heading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4),
            };
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
                Text = "Runaway search",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
                Foreground = new SolidColorBrush(Amber),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(heading);

            stack.Children.Add(new TextBlock
            {
                Text = Headline(_findings),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xFF, 0xFF)),
            });

            stack.Children.Add(new TextBlock
            {
                Text = _findings[0].Reason
                       + ". Scanning the whole drive with nothing reading the output. "
                       + "The command lines are in the MicaStats log.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xE9, 0xED, 0xF2)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                Margin = new Thickness(0, 3, 0, 10),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ToastButton.Create("End them", Amber, primary: true,
                Color.FromRgb(0x2A, 0x18, 0x06), () =>
                {
                    onEndAll(_findings);
                    Close();
                }));
            buttons.Children.Add(ToastButton.Create("Dismiss", Amber, primary: false,
                Color.FromRgb(0x2A, 0x18, 0x06), Close));
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
    }
}
