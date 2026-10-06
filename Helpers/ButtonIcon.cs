using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using FontFamily = System.Windows.Media.FontFamily;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>Adds a Fluent icon to a labeled button without replacing its content or control style.</summary>
    public static class ButtonIcon
    {
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
            "Glyph", typeof(string), typeof(ButtonIcon), new PropertyMetadata(string.Empty, OnGlyphChanged));

        [ThreadStatic] private static DataTemplate? s_template;

        public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

        public static void SetGlyph(DependencyObject element, string value)
        {
            element.SetValue(GlyphProperty, value);
            // Mode changes may have temporarily used another content template for an alert.
            if (element is Button button && !string.IsNullOrEmpty(value)) Apply(button);
        }

        private static void OnGlyphChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            if (element is not Button button) return;
            if (!string.IsNullOrEmpty((string)args.NewValue)) Apply(button);
            else if (ReferenceEquals(button.ContentTemplate, s_template)) button.ClearValue(ContentControl.ContentTemplateProperty);
        }

        private static void Apply(Button button) => button.SetCurrentValue(ContentControl.ContentTemplateProperty,
            s_template ??= CreateTemplate());

        private static DataTemplate CreateTemplate()
        {
            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetValue(DockPanel.LastChildFillProperty, true);
            var icon = new FrameworkElementFactory(typeof(DecorativeIcon), "ActionGlyph");
            icon.SetValue(FrameworkElement.NameProperty, "ActionGlyph");
            icon.SetValue(DockPanel.DockProperty, Dock.Left);
            icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
            icon.SetValue(TextBlock.FontSizeProperty, 14d);
            icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath(GlyphProperty),
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            icon.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            row.AppendChild(icon);
            var label = new FrameworkElementFactory(typeof(ContentPresenter), "ActionLabel");
            label.SetValue(FrameworkElement.NameProperty, "ActionLabel");
            label.SetValue(ContentPresenter.ContentTemplateProperty, null);
            label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetBinding(ContentPresenter.ContentProperty, new Binding());
            row.AppendChild(label);
            return new DataTemplate { VisualTree = row };
        }
    }
}
