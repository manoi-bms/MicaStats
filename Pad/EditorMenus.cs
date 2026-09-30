using System;
using System.Windows.Controls;
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
        public static MenuItem Item(string header, string? gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
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
                menu.Items.Add(Item("Undo", "Ctrl+Z", () => editor.Undo(), editor.CanUndo));
                menu.Items.Add(Item("Redo", "Ctrl+Y", () => editor.Redo(), editor.CanRedo));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Cut", "Ctrl+X", () => editor.Cut(), hasSelection));
            }
            menu.Items.Add(Item("Copy", "Ctrl+C", () => editor.Copy(), hasSelection));
            if (!readOnly)
            {
                menu.Items.Add(Item("Paste", "Ctrl+V", () => editor.Paste(), ClipboardHasText()));
                // The Del key's own command: it respects a rectangular selection.
                menu.Items.Add(Item("Delete", "Del", () => ApplicationCommands.Delete.Execute(null, editor.TextArea), hasSelection));
            }
            menu.Items.Add(Item("Select all", "Ctrl+A", () => editor.SelectAll(), hasText));
        }

        /// <summary>Format ▸ for a Markdown tab (spec 2.4): each item is one undoable edit; no new shortcuts.</summary>
        public static MenuItem FormatMenu(TextEditor editor)
        {
            var format = new MenuItem { Header = "Format" };
            void Add(string header, Func<string, int, int, TextEdit> edit) =>
                format.Items.Add(Item(header, null, () =>
                    ApplyEdit(editor, edit(editor.Document.Text, editor.SelectionStart, editor.SelectionLength))));

            Add("Bold", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "**"));
            Add("Italic", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "*"));
            Add("Strikethrough", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "~~"));
            Add("Code", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "`"));
            Add("Link", MarkdownFormatter.Link);
            format.Items.Add(new Separator());
            Add("Heading 1", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading1));
            Add("Heading 2", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading2));
            Add("Heading 3", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading3));
            format.Items.Add(new Separator());
            Add("Bullet list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Bullet));
            Add("Numbered list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Numbered));
            Add("Task", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Task));
            Add("Quote", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Quote));
            Add("Code block", MarkdownFormatter.CodeBlock);
            return format;
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
