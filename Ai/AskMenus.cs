using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services.Ai;

// UseWindowsForms puts System.Windows.Forms in scope, which has its own ContextMenu and MenuItem.
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using Control = System.Windows.Controls.Control;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Context menus for the Ask window's text boxes (Copy and Select all; the question box also
    /// Undo, Cut and Paste). A menu opens in its own popup, outside the window's resources, so each
    /// carries the Ask.* brushes and its own requested theme, refreshed when it opens and when the
    /// window switches theme.
    /// </summary>
    internal static class AskMenus
    {
        /// <summary>Gives <paramref name="box"/> the Ask menu; <paramref name="editable"/> adds Undo, Cut and Paste.</summary>
        public static ContextMenu Install(TextBoxBase box, bool editable)
        {
            var menu = new ContextMenu { Tag = typeof(AskMenus) };
            if (editable)
            {
                Add(menu, ApplicationCommands.Undo, box);
                menu.Items.Add(new Separator());
                Add(menu, ApplicationCommands.Cut, box);
            }
            Add(menu, ApplicationCommands.Copy, box);
            if (editable) Add(menu, ApplicationCommands.Paste, box);
            Add(menu, ApplicationCommands.SelectAll, box);

            Apply(menu, AskPalette.Dark);
            box.ContextMenu = menu;
            box.ContextMenuOpening += (s, e) =>
            {
                if (Window.GetWindow(box) is AskWindow window) Apply(menu, window.Palette);
            };
            return menu;
        }

        /// <summary>Paints <paramref name="menu"/> in <paramref name="palette"/>: its brushes and its requested theme.</summary>
        public static void Apply(ContextMenu menu, AskPalette palette)
        {
            AskThemeApplier.ApplyResources(menu.Resources, palette);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            menu.SetResourceReference(Control.BackgroundProperty, "Ask.Surface");
            menu.SetResourceReference(Control.ForegroundProperty, "Ask.Ink");
            menu.SetResourceReference(Control.BorderBrushProperty, "Ask.Border");
        }

        /// <summary>Re-themes every Ask menu under <paramref name="root"/>, FlowDocuments inside answer boxes included.</summary>
        public static void Retheme(DependencyObject root, AskPalette palette)
        {
            if (root is FrameworkElement { ContextMenu: { } menu } && ReferenceEquals(menu.Tag, typeof(AskMenus)))
                Apply(menu, palette);
            if (root is RichTextBox { Document: { } document }) Retheme(document, palette);
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject element) Retheme(element, palette);
        }

        private static void Add(ContextMenu menu, RoutedUICommand command, IInputElement target) =>
            menu.Items.Add(new MenuItem { Command = command, CommandTarget = target });
    }
}
