using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using Kil0bitSystemMonitor.Services.Pad;

using Clipboard = System.Windows.Clipboard;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's menu pieces: item helpers, the right-click edit group, and the Format (and, from
    /// Part 3, Lines) submenus. The window decides which groups a menu gets; the groups themselves
    /// live here so the window stays about windows, tabs and files.
    /// </summary>
    internal static class EditorMenus
    {
        private static PadMenuStyles? s_styles;

        /// <summary>The shared menu styles, loaded on first use (on the UI thread).</summary>
        private static PadMenuStyles Styles => s_styles ??= new PadMenuStyles();

        /// <summary>MicaPad's ContextMenu style; for tests.</summary>
        internal static System.Windows.Style MenuStyle => (System.Windows.Style)Styles["Pad.ContextMenu"];

        /// <summary>
        /// Gives a menu MicaPad's look in the given palette: the shared styles (the menu, its items,
        /// separators and submenus), the palette's Pad.* brushes (menus live in their own popup,
        /// outside the window's resources), and offsets that put the card, not the transparent
        /// margin holding its shadow, where WPF places the menu. Safe to call on every open: the
        /// palette may have changed since.
        /// </summary>
        public static void Style(ContextMenu menu, PadPalette palette)
        {
            if (!menu.Resources.MergedDictionaries.Contains(Styles)) menu.Resources.MergedDictionaries.Add(Styles);
            PadThemeApplier.ApplyResources(menu.Resources, palette);
            menu.Style = MenuStyle;
            menu.HorizontalOffset = -10;
            menu.VerticalOffset = menu.Placement == PlacementMode.Top ? 14 : -8;
        }

        /// <summary>A menu item; <paramref name="icon"/> is one Segoe Fluent Icons glyph shown left of the header.</summary>
        public static MenuItem Item(string header, string? gesture, Action action, bool enabled = true, string? icon = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, Icon = icon };
            item.Click += (s, e) => action();
            return item;
        }

        public static MenuItem Check(string header, string? gesture, bool isChecked, Action action)
        {
            var item = Item(header, gesture, action);
            item.IsCheckable = true;
            item.IsChecked = isChecked;
            return item;
        }

        /// <summary>
        /// Undo, Redo, Cut, Copy, Paste, Delete, Select all — each disabled when it cannot apply.
        /// A read-only editor (the history preview) gets Copy and Select all only.
        /// </summary>
        public static void AddEditGroup(ContextMenu menu, TextEditor editor, bool readOnly)
        {
            bool hasSelection = editor.SelectionLength > 0;
            bool hasText = editor.Document != null && editor.Document.TextLength > 0;

            if (!readOnly)
            {
                menu.Items.Add(Item("Undo", "Ctrl+Z", () => editor.Undo(), editor.CanUndo, icon: "\uE7A7"));
                menu.Items.Add(Item("Redo", "Ctrl+Y", () => editor.Redo(), editor.CanRedo, icon: "\uE7A6"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Cut", "Ctrl+X", () => editor.Cut(), hasSelection, icon: "\uE8C6"));
            }
            menu.Items.Add(Item("Copy", "Ctrl+C", () => editor.Copy(), hasSelection, icon: "\uE8C8"));
            if (!readOnly)
            {
                menu.Items.Add(Item("Paste", "Ctrl+V", () => editor.Paste(), ClipboardHasText(), icon: "\uE77F"));
                // The Del key's own command: it respects a rectangular selection.
                menu.Items.Add(Item("Delete", "Del", () => ApplicationCommands.Delete.Execute(null, editor.TextArea), hasSelection, icon: "\uE74D"));
            }
            menu.Items.Add(Item("Select all", "Ctrl+A", () => editor.SelectAll(), hasText, icon: "\uE8B3"));
        }

        /// <summary>Format ▸ for a Markdown tab (spec 2.4): each item is one undoable edit; no new shortcuts.</summary>
        public static MenuItem FormatMenu(TextEditor editor)
        {
            var format = new MenuItem { Header = "Format", Icon = "\uE8D2" };
            void Add(string header, Func<string, int, int, TextEdit> edit, string? icon = null) =>
                format.Items.Add(Item(header, null, () =>
                    ApplyEdit(editor, edit(editor.Document.Text, editor.SelectionStart, editor.SelectionLength)), icon: icon));

            Add("Bold", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "**"), "\uE8DD");
            Add("Italic", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "*"), "\uE8DB");
            Add("Strikethrough", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "~~"), "\uEDE0");
            Add("Code", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "`"), "\uE943");
            Add("Link", MarkdownFormatter.Link, "\uE71B");
            format.Items.Add(new Separator());
            Add("Heading 1", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading1));
            Add("Heading 2", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading2));
            Add("Heading 3", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading3));
            format.Items.Add(new Separator());
            Add("Bullet list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Bullet), "\uE8FD");
            Add("Numbered list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Numbered));
            Add("Task", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Task));
            Add("Quote", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Quote));
            Add("Code block", MarkdownFormatter.CodeBlock, "\uE943");
            return format;
        }

        /// <summary>Lines (spec 3.2): the line operations, each one undoable edit.</summary>
        public static MenuItem LinesMenu(TextEditor editor)
        {
            var lines = new MenuItem { Header = "Lines", Icon = "" };
            void Add(string header, string? gesture, Func<string, int, int, TextEdit?> operation, string? icon = null) =>
                lines.Items.Add(Item(header, gesture, () => Run(editor, operation), icon: icon));

            Add("Duplicate", "Ctrl+D", (t, s, l) => LineOperations.Duplicate(t, s, l), "");
            Add("Move up", "Ctrl+Shift+Up", LineOperations.MoveUp, "");
            Add("Move down", "Ctrl+Shift+Down", LineOperations.MoveDown, "");
            Add("Join lines", "Ctrl+J", LineOperations.Join);
            lines.Items.Add(new Separator());
            Add("Sort ascending", null, (t, s, l) => LineOperations.Sort(t, s, l, false, System.Globalization.CultureInfo.CurrentCulture), "");
            Add("Sort descending", null, (t, s, l) => LineOperations.Sort(t, s, l, true, System.Globalization.CultureInfo.CurrentCulture));
            Add("Remove duplicate lines", null, LineOperations.RemoveDuplicates);
            Add("Trim trailing whitespace", null, LineOperations.TrimTrailing);
            return lines;
        }

        /// <summary>Runs a line operation on the editor's text and selection; a null result changes nothing.</summary>
        public static void Run(TextEditor editor, Func<string, int, int, TextEdit?> operation)
        {
            if (editor.IsReadOnly) return;
            if (operation(editor.Document.Text, editor.SelectionStart, editor.SelectionLength) is TextEdit edit)
                ApplyEdit(editor, edit);
        }

        /// <summary>Applies an edit as one undoable change and selects what it says.</summary>
        public static void ApplyEdit(TextEditor editor, TextEdit edit)
        {
            editor.Document.Replace(edit.Offset, edit.Length, edit.Text);
            editor.Select(edit.SelectionStart, edit.SelectionLength);
        }

        /// <summary>True when Paste has something to paste. A busy clipboard counts as yes: Paste itself then tries.</summary>
        private static bool ClipboardHasText()
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return true;
            }
        }
    }
}
