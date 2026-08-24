using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kil0bitSystemMonitor.Helpers;
using Xunit;

using Button = System.Windows.Controls.Button;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The notification cards are always dark, but they are plain windows that inherit the
    /// application's ModernWpf theme, and ModernWpf follows the system's <i>app</i> theme —
    /// a different setting from the taskbar's, and often light.
    ///
    /// <para>
    /// Under a light theme the stock button style replaced the background with a near-white
    /// brush on pointer-over. A locally set <c>Background</c> loses to that; a locally set
    /// <c>Foreground</c> does not. So the pale label on the Dismiss button landed on a pale
    /// fill and vanished while remaining clickable. These pin every state as a readable
    /// combination, measured rather than eyeballed.
    /// </para>
    /// </summary>
    public class ToastButtonTests
    {
        /// <summary>The alert card's own background, which the button states sit on.</summary>
        private static readonly MediaColor AlertCard = MediaColor.FromArgb(0xFA, 0x1C, 0x16, 0x10);

        private static readonly MediaColor Amber = MediaColor.FromRgb(0xE8, 0xA5, 0x3C);
        private static readonly MediaColor Cyan = MediaColor.FromRgb(0x3F, 0xD2, 0xE4);

        /// <summary>WCAG AA for normal text; these labels are small.</summary>
        private const double TextTarget = 4.5;

        private static DrawingColor Gdi(MediaColor c) => DrawingColor.FromArgb(c.A, c.R, c.G, c.B);

        /// <summary>Flattens a button state onto the card, then measures the label against it.</summary>
        private static double LabelContrast(MediaColor label, MediaColor fill, MediaColor card)
        {
            var opaqueCard = Contrast.Composite(Gdi(card), DrawingColor.Black);
            var overCard = Contrast.Composite(Gdi(fill), opaqueCard);
            return Contrast.Ratio(Gdi(label), overCard);
        }

        private static Button Secondary() =>
            ToastButton.Create("Dismiss", Amber, primary: false,
                               MediaColor.FromRgb(0x2A, 0x18, 0x06), () => { });

        /// <summary>
        /// The regression itself. Without an owned template the hover fill came from the light
        /// theme and measured about 1.2:1 against the label — indistinguishable from the
        /// background. This asserts the shipped hover state is nothing like that.
        /// </summary>
        [Fact]
        public void The_dismiss_button_stays_readable_while_hovered()
        {
            OnSta(() =>
            {
                var button = Secondary();
                var label = ((SolidColorBrush)button.Foreground).Color;

                MediaColor hoverFill = TriggerFill(button, UIElement.IsMouseOverProperty);
                double ratio = LabelContrast(label, hoverFill, AlertCard);

                Assert.True(ratio >= TextTarget, $"hovered label contrast {ratio:F2} is under {TextTarget}");
            });
        }

        [Fact]
        public void Every_state_of_the_secondary_button_is_readable()
        {
            OnSta(() =>
            {
                var button = Secondary();
                var label = ((SolidColorBrush)button.Foreground).Color;

                double rest = LabelContrast(label, ((SolidColorBrush)button.Background).Color, AlertCard);
                double hover = LabelContrast(label, TriggerFill(button, UIElement.IsMouseOverProperty), AlertCard);
                double press = LabelContrast(label,
                    TriggerFill(button, System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty), AlertCard);

                Assert.True(rest >= TextTarget, $"resting {rest:F2}");
                Assert.True(hover >= TextTarget, $"hovered {hover:F2}");
                Assert.True(press >= TextTarget, $"pressed {press:F2}");
            });
        }

        /// <summary>
        /// Documents what was actually wrong: a near-white hover fill, which is what the light
        /// ModernWpf theme supplied, leaves the label invisible. Guards against anyone
        /// "simplifying" the template away again.
        /// </summary>
        [Fact]
        public void A_pale_hover_fill_would_hide_the_label()
        {
            var label = MediaColor.FromArgb(0xE8, 0xED, 0xED, 0xF2);
            var paleFill = MediaColor.FromRgb(0xF3, 0xF3, 0xF3);

            double ratio = LabelContrast(label, paleFill, AlertCard);
            Assert.True(ratio < 1.5, $"expected the old behaviour to be invisible, measured {ratio:F2}");
        }

        [Fact]
        public void The_primary_button_reads_on_its_accent()
        {
            OnSta(() =>
            {
                foreach (var accent in new[] { Amber, Cyan })
                {
                    var ink = accent == Amber
                        ? MediaColor.FromRgb(0x2A, 0x18, 0x06)
                        : MediaColor.FromRgb(0x06, 0x22, 0x2A);

                    var button = ToastButton.Create("Go", accent, primary: true, ink, () => { });
                    double rest = LabelContrast(ink, ((SolidColorBrush)button.Background).Color, AlertCard);
                    double hover = LabelContrast(ink, TriggerFill(button, UIElement.IsMouseOverProperty), AlertCard);

                    Assert.True(rest >= TextTarget, $"primary resting {rest:F2}");
                    Assert.True(hover >= TextTarget, $"primary hovered {hover:F2}");
                }
            });
        }

        /// <summary>
        /// Having a template at all is the fix; inheriting the ambient one is the bug. This
        /// fails loudly if the template or either state trigger is ever dropped.
        /// </summary>
        [Fact]
        public void The_button_owns_its_template_and_both_state_triggers()
        {
            OnSta(() =>
            {
                var button = Secondary();

                Assert.NotNull(button.Template);
                Assert.Contains(button.Template.Triggers.OfType<Trigger>(),
                                t => t.Property == UIElement.IsMouseOverProperty);
                Assert.Contains(button.Template.Triggers.OfType<Trigger>(),
                                t => t.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty);
            });
        }

        [Fact]
        public void Lighten_and_darken_move_toward_white_and_black()
        {
            var mid = MediaColor.FromRgb(0x80, 0x80, 0x80);

            Assert.True(ToastButton.Lighten(mid, 0.5).R > mid.R);
            Assert.True(ToastButton.Darken(mid, 0.5).R < mid.R);

            // Extremes are clamped rather than wrapping around.
            Assert.Equal(255, ToastButton.Lighten(mid, 1.0).R);
            Assert.Equal(0, ToastButton.Darken(mid, 1.0).R);
            Assert.Equal(mid.A, ToastButton.Lighten(mid, 0.5).A);
        }


        /// <summary>
        /// Runs a test body on an STA thread. Constructing any WPF FrameworkElement initialises
        /// the input manager, which throws outright on xUnit's default MTA thread — so a helper
        /// here is cheaper than taking a dependency on an STA test package for six tests.
        /// </summary>
        private static void OnSta(Action body)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            failure?.Throw();
        }

        /// <summary>Reads the fill a state trigger applies to the template's Bg border.</summary>
        private static MediaColor TriggerFill(Button button, DependencyProperty state)
        {
            foreach (var trigger in button.Template!.Triggers.OfType<Trigger>())
            {
                if (trigger.Property != state) continue;
                foreach (var setter in trigger.Setters.OfType<Setter>())
                {
                    if (setter.Property == Border.BackgroundProperty && setter.Value is SolidColorBrush b)
                        return b.Color;
                }
            }
            Assert.Fail($"no background setter found for {state.Name}");
            return default;
        }
    }
}
