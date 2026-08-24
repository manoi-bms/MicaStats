using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// System.Windows.Forms is in scope via UseWindowsForms and defines its own Control,
// HorizontalAlignment and ButtonBase. The WinForms ButtonBase in particular has no
// IsPressedProperty, so an unaliased reference fails with a confusing "member not found".
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// The small buttons on the corner notification cards, with a control template of their own.
    ///
    /// <para>
    /// Owning the template is the whole point. The toasts are always dark cards, but they are
    /// plain windows that inherit the application's ModernWpf theme, and ModernWpf follows the
    /// system's <i>app</i> theme — which is a separate setting from the taskbar's and is
    /// frequently light. Under a light theme the stock button style swaps its background to a
    /// near-white brush on pointer-over. A locally assigned <c>Background</c> loses to that,
    /// while a locally assigned <c>Foreground</c> does not, so a secondary button's pale text
    /// landed on a pale hover fill and disappeared while still being clickable.
    /// </para>
    ///
    /// <para>
    /// Templating the button removes it from that conversation entirely: every state is drawn
    /// from colours chosen against the card, whatever Windows or ModernWpf are doing.
    /// </para>
    /// </summary>
    public static class ToastButton
    {
        /// <summary>
        /// Builds a notification button.
        /// </summary>
        /// <param name="text">The label, which should say what will happen.</param>
        /// <param name="accent">The card's accent, used to fill a primary button.</param>
        /// <param name="primary">True for the button that performs the card's action.</param>
        /// <param name="onInk">Text colour for a primary button, dark enough to read on the accent.</param>
        public static Button Create(string text, Color accent, bool primary, Color onInk, Action onClick)
        {
            // Secondary buttons sit on the card itself, so every state is a translucent white
            // wash over the dark card rather than a colour of its own.
            Color normal = primary ? accent : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
            Color hover = primary ? Lighten(accent, 0.14) : Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF);
            Color pressed = primary ? Darken(accent, 0.12) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

            // The pale label the bug was about. Kept bright, because the fills above are now
            // guaranteed to stay dark.
            Color ink = primary ? onInk : Color.FromArgb(0xE8, 0xED, 0xED, 0xF2);

            var button = new Button
            {
                Content = text,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(primary ? 14 : 10, 5, primary ? 14 : 10, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = new SolidColorBrush(ink),
                Background = new SolidColorBrush(normal),
                BorderThickness = new Thickness(0),
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Focusable = true,
                Template = BuildTemplate(normal, hover, pressed),
            };

            button.Click += (s, e) => onClick();
            return button;
        }

        private static ControlTemplate BuildTemplate(Color normal, Color hover, Color pressed)
        {
            var root = new FrameworkElementFactory(typeof(Border), "Bg");
            root.SetValue(Border.BackgroundProperty, new SolidColorBrush(normal));
            root.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            root.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
                Path = new PropertyPath(nameof(Control.Padding)),
            });

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = root };

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hover), "Bg"));
            template.Triggers.Add(over);

            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(pressed), "Bg"));
            template.Triggers.Add(down);

            // Keyboard focus needs to be visible too; the stock adorner is invisible on a dark card.
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), "Bg"));
            focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Bg"));
            template.Triggers.Add(focused);

            template.Seal();
            return template;
        }

        /// <summary>Moves a colour toward white, for the hover state of a filled button.</summary>
        public static Color Lighten(Color c, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(c.A,
                (byte)(c.R + (255 - c.R) * amount),
                (byte)(c.G + (255 - c.G) * amount),
                (byte)(c.B + (255 - c.B) * amount));
        }

        /// <summary>Moves a colour toward black, for the pressed state of a filled button.</summary>
        public static Color Darken(Color c, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(c.A,
                (byte)(c.R * (1 - amount)),
                (byte)(c.G * (1 - amount)),
                (byte)(c.B * (1 - amount)));
        }
    }
}
