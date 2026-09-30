using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services;
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

        /// <summary>Where a failed menu command or shortcut is logged. Tests replace it (and put it back), so they never write the real log.</summary>
        internal static Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("pad", message);

        /// <summary>
        /// Runs a menu command or a shortcut so a failure is logged instead of thrown: MicaStats has
        /// no dispatcher exception handler, so one exception out of MicaPad would close the whole
        /// app. False when it threw.
        /// </summary>
        internal static bool Guard(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    Warn(what + " failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
                catch (Exception)
                {
                    // Logging is best effort; it must not throw either.
                }
                return false;
            }
        }

        /// <summary>A menu item; <paramref name="icon"/> is one Segoe Fluent Icons glyph shown left of the header.</summary>
        public static MenuItem Item(string header, string? gesture, Action action, bool enabled = true, string? icon = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, Icon = icon };
            item.Click += (s, e) => Guard("The menu command " + header, action);
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

        /// <summary>
        /// Lines (spec 3.2): the line operations, each one undoable edit. Move up and down go through
        /// <paramref name="moveLines"/> (true for down), the window's own path, so the bookmarks move
        /// with the lines the same way from the keys and from here.
        /// </summary>
        public static MenuItem LinesMenu(TextEditor editor, Action<bool> moveLines)
        {
            // Move and Join are checked cheaply from the block's edges; the whole-note items (sort,
            // dedupe, trim) stay enabled, since checking them would mean sorting on every open.
            string text = editor.Document.Text;
            var (blockStart, blockEnd) = TextLines.Block(text, editor.SelectionStart, editor.SelectionLength);
            bool firstLine = blockStart == 0;
            bool lastLine = blockEnd >= text.Length;
            bool oneLine = text.IndexOfAny(new[] { '\r', '\n' }, blockStart, blockEnd - blockStart) < 0;

            var lines = new MenuItem { Header = "Lines", Icon = "\uE8A4" };
            void Add(string header, string? gesture, Func<string, int, int, TextEdit?> operation, string? icon = null, bool enabled = true) =>
                lines.Items.Add(Item(header, gesture, () => RunLineOperation(editor, operation), enabled, icon));

            lines.Items.Add(Item("Duplicate", "Ctrl+D", () => Duplicate(editor), icon: "\uE8C8"));
            lines.Items.Add(Item("Move up", "Ctrl+Shift+Up", () => moveLines(false), !firstLine, "\uE74A"));
            lines.Items.Add(Item("Move down", "Ctrl+Shift+Down", () => moveLines(true), !lastLine, "\uE74B"));
            Add("Join lines", "Ctrl+J", LineOperations.Join, enabled: !(oneLine && lastLine));
            lines.Items.Add(new Separator());
            Add("Sort ascending", null, (t, s, l) => LineOperations.Sort(t, s, l, false, System.Globalization.CultureInfo.CurrentCulture), "\uE8CB");
            Add("Sort descending", null, (t, s, l) => LineOperations.Sort(t, s, l, true, System.Globalization.CultureInfo.CurrentCulture));
            Add("Remove duplicate lines", null, LineOperations.RemoveDuplicates);
            Add("Trim trailing whitespace", null, LineOperations.TrimTrailing);
            return lines;
        }

        /// <summary>What a tool says about a rectangular selection: its flat span would be garbage.</summary>
        public const string RectangleRefused = "Tools work on an ordinary selection, not a rectangle";

        /// <summary>
        /// Tools (spec 5.1): each item is one undoable edit on the selection, or at the caret for the
        /// inserts; when a tool cannot apply, the text is left alone and <paramref name="report"/>
        /// says why. The selection tools wait for a selection.
        /// </summary>
        public static MenuItem ToolsMenu(TextEditor editor, Action<string> report, Func<DateTimeOffset> now)
        {
            bool selected = editor.SelectionLength > 0;
            var tools = new MenuItem { Header = "Tools", Icon = "\uE90F" };
            MenuItem Tool(string header, Func<string, int, int, ToolOutcome> tool, bool enabled = true) =>
                Item(header, null, () => RunTool(editor, report, tool), enabled);

            tools.Items.Add(Tool("Base64 encode", (t, s, l) => TextTools.OnSelection(t, s, l, x => (TextTools.Base64Encode(x), null)), selected));
            tools.Items.Add(Tool("Base64 decode", (t, s, l) => TextTools.OnSelection(t, s, l, TextTools.Base64Decode), selected));

            var convert = new MenuItem { Header = "Convert number", IsEnabled = selected };
            foreach (var (header, target) in new[] { ("Decimal", NumberBase.Decimal), ("Hex", NumberBase.Hex), ("Binary", NumberBase.Binary), ("Octal", NumberBase.Octal) })
                convert.Items.Add(Tool(header, (t, s, l) => TextTools.OnSelection(t, s, l, x => NumberConverter.Convert(x, target))));
            tools.Items.Add(convert);

            tools.Items.Add(Tool("Insert GUID", (t, s, l) => TextTools.Insert(s, l, TextTools.FormatGuid(Guid.NewGuid()))));

            var stamp = new MenuItem { Header = "Insert timestamp" };
            stamp.Items.Add(Tool("ISO 8601", (t, s, l) => TextTools.Insert(s, l, TextTools.Iso8601(now()))));
            stamp.Items.Add(Tool("Date", (t, s, l) => TextTools.Insert(s, l, TextTools.Date(now()))));
            stamp.Items.Add(Tool("Unix seconds", (t, s, l) => TextTools.Insert(s, l, TextTools.UnixSeconds(now()))));
            tools.Items.Add(stamp);

            tools.Items.Add(Tool("Evaluate", TextTools.Evaluate, selected));
            return tools;
        }

        /// <summary>Runs a tool: its edit is one undoable change; a problem is reported and the text left alone. True when it edited.</summary>
        public static bool RunTool(TextEditor editor, Action<string> report, Func<string, int, int, ToolOutcome> tool)
        {
            if (editor.IsReadOnly) return false;
            if (editor.TextArea.Selection is RectangleSelection)
            {
                report(RectangleRefused);
                return false;
            }
            var outcome = tool(editor.Document.Text, editor.SelectionStart, editor.SelectionLength);
            if (outcome.Edit is not TextEdit edit)
            {
                report(outcome.Problem ?? TextTools.SelectFirst);
                return false;
            }
            ApplyEdit(editor, edit);
            return true;
        }

        /// <summary>
        /// Ctrl+D and Lines ▸ Duplicate: the selection, or the caret line. A rectangular selection
        /// duplicates every line it touches instead: its flat span would be garbage.
        /// </summary>
        public static void Duplicate(TextEditor editor)
        {
            if (editor.TextArea.Selection is RectangleSelection) RunLineOperation(editor, (t, s, l) => LineOperations.DuplicateLines(t, s, l));
            else RunLineOperation(editor, (t, s, l) => LineOperations.Duplicate(t, s, l));
        }

        /// <summary>Runs a line operation on the editor's text and selection; a null result changes nothing. True when it edited.</summary>
        public static bool RunLineOperation(TextEditor editor, Func<string, int, int, TextEdit?> operation)
        {
            if (editor.IsReadOnly) return false;
            if (operation(editor.Document.Text, editor.SelectionStart, editor.SelectionLength) is not TextEdit edit) return false;
            ApplyEdit(editor, edit);
            return true;
        }

        /// <summary>
        /// Applies an edit as one undoable change and selects what it says, clamped to the new text:
        /// a selection past the end would throw out of a key or menu handler and take MicaStats down.
        /// </summary>
        public static void ApplyEdit(TextEditor editor, TextEdit edit)
        {
            var document = editor.Document;
            // Only the lines that change, from the last: bookmarks on the others stay put (Trim, Sort,
            // Format prefixes), and Undo reverses line by line. One update group: one undo step.
            var pieces = TextPieces.Plan(document.GetText(edit.Offset, edit.Length), edit.Text);
            using (document.RunUpdate())
            {
                for (int i = pieces.Count - 1; i >= 0; i--)
                    document.Replace(edit.Offset + pieces[i].Offset, pieces[i].Length, pieces[i].Text);
            }
            int textLength = document.TextLength;
            int start = Math.Clamp(edit.SelectionStart, 0, textLength);
            editor.Select(start, Math.Clamp(edit.SelectionLength, 0, textLength - start));
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
