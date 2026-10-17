using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Localization;

using Clipboard = System.Windows.Clipboard;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's menu pieces: item helpers, the right-click edit group, and the Format (and, from
    /// Part 3, Lines) submenus. The window decides which groups a menu gets; the groups themselves
    /// live here so the window stays about windows, tabs and files.
    /// </summary>
    internal static class EditorMenus
    {
        internal sealed record MenuCaptionText(string Title, string Detail, string Glyph);
        internal readonly record struct MenuPopupBounds(double MenuMaxWidth, double MenuMaxHeight, double ViewportMaxWidth, double ViewportMaxHeight);

        internal const string MenuViewportWidthKey = "Pad.MenuViewportWidth";
        internal const string MenuViewportHeightKey = "Pad.MenuViewportHeight";

        private const double MenuScreenInset = 24;
        private const double MenuCardHorizontalSpace = 20;
        private const double MenuCardVerticalSpace = 22;
        private const double DefaultMenuMaxWidth = 380;

        private sealed class MenuSizingState
        {
            public bool Initialized;
            public double RequestedMaxWidth = double.PositiveInfinity;
            public double RequestedMaxHeight = double.PositiveInfinity;
            public double AppliedMaxWidth = double.NaN;
            public double AppliedMaxHeight = double.NaN;
        }

        private static readonly ConditionalWeakTable<ContextMenu, MenuSizingState> MenuSizing = new();

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
            menu.Opened -= OnMenuOpened;
            menu.Opened += OnMenuOpened;
            ApplyMenuBounds(menu, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height, 1, 1);
        }

        private static void OnMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;
            try
            {
                var (width, height, dpiX, dpiY) = PlacementWorkArea(menu);
                ApplyMenuBounds(menu, width, height, dpiX, dpiY);
            }
            catch (Exception)
            {
                ApplyMenuBounds(menu, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height, 1, 1);
            }
        }

        private static (double Width, double Height, double DpiX, double DpiY) PlacementWorkArea(ContextMenu menu)
        {
            FrameworkElement? target = menu.PlacementTarget as FrameworkElement;
            System.Drawing.Point point;
            if (menu.Placement is PlacementMode.Mouse or PlacementMode.MousePoint || target == null || !target.IsVisible)
            {
                point = System.Windows.Forms.Cursor.Position;
            }
            else
            {
                Point screen = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                point = new System.Drawing.Point((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            }

            var area = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
            DpiScale dpi = target != null && PresentationSource.FromVisual(target)?.CompositionTarget != null
                ? VisualTreeHelper.GetDpi(target)
                : new DpiScale(1, 1);
            return (area.Width, area.Height, dpi.DpiScaleX, dpi.DpiScaleY);
        }

        private static void ApplyMenuBounds(ContextMenu menu, double workWidthPixels, double workHeightPixels, double dpiX, double dpiY)
        {
            MenuSizingState state = MenuSizing.GetOrCreateValue(menu);
            if (!state.Initialized)
            {
                state.RequestedMaxWidth = menu.MaxWidth;
                state.RequestedMaxHeight = menu.MaxHeight;
                state.Initialized = true;
            }
            else
            {
                if (!Same(menu.MaxWidth, state.AppliedMaxWidth)) state.RequestedMaxWidth = menu.MaxWidth;
                if (!Same(menu.MaxHeight, state.AppliedMaxHeight)) state.RequestedMaxHeight = menu.MaxHeight;
            }

            MenuPopupBounds bounds = FitMenuToWorkArea(workWidthPixels, workHeightPixels, dpiX, dpiY,
                state.RequestedMaxWidth, state.RequestedMaxHeight);
            menu.MaxWidth = bounds.MenuMaxWidth;
            menu.MaxHeight = bounds.MenuMaxHeight;
            state.AppliedMaxWidth = bounds.MenuMaxWidth;
            state.AppliedMaxHeight = bounds.MenuMaxHeight;
            menu.Resources[MenuViewportWidthKey] = bounds.ViewportMaxWidth;
            menu.Resources[MenuViewportHeightKey] = bounds.ViewportMaxHeight;
        }

        private static bool Same(double left, double right) => left.Equals(right) || Math.Abs(left - right) < 0.01;

        /// <summary>
        /// Fits a popup card and its scrollable viewport into a monitor work area. Pixel inputs make
        /// the DPI conversion explicit and keep this calculation deterministic for tests.
        /// </summary>
        internal static MenuPopupBounds FitMenuToWorkArea(double workWidthPixels, double workHeightPixels,
            double dpiX, double dpiY, double requestedMaxWidth = double.PositiveInfinity,
            double requestedMaxHeight = double.PositiveInfinity)
        {
            double scaleX = double.IsFinite(dpiX) && dpiX > 0 ? dpiX : 1;
            double scaleY = double.IsFinite(dpiY) && dpiY > 0 ? dpiY : 1;
            double workWidth = Math.Max(1, workWidthPixels / scaleX);
            double workHeight = Math.Max(1, workHeightPixels / scaleY);
            double availableWidth = Math.Max(1, workWidth - MenuScreenInset);
            double availableHeight = Math.Max(1, workHeight - MenuScreenInset);
            double widthCap = ValidCap(requestedMaxWidth) ? requestedMaxWidth : DefaultMenuMaxWidth;
            double heightCap = ValidCap(requestedMaxHeight) ? requestedMaxHeight : availableHeight;
            double menuWidth = Math.Max(1, Math.Min(availableWidth, Math.Min(DefaultMenuMaxWidth, widthCap)));
            double menuHeight = Math.Max(1, Math.Min(availableHeight, heightCap));
            return new MenuPopupBounds(
                menuWidth,
                menuHeight,
                Math.Max(1, menuWidth - MenuCardHorizontalSpace),
                Math.Max(1, menuHeight - MenuCardVerticalSpace));
        }

        private static bool ValidCap(double value) => double.IsFinite(value) && value > 0;

        /// <summary>A compact, noninteractive explanation of the context this menu will act on.</summary>
        internal static Separator AddCaption(ContextMenu menu, string title, string detail, string glyph)
        {
            var caption = new Separator
            {
                Style = (System.Windows.Style)Styles["Pad.MenuCaption"],
                DataContext = new MenuCaptionText(title, detail, glyph),
            };
            menu.Items.Add(caption);
            return caption;
        }

        /// <summary>A noninteractive heading that makes a long context menu easier to scan.</summary>
        internal static Separator AddSection(ContextMenu menu, string title)
        {
            var section = new Separator
            {
                Style = (System.Windows.Style)Styles["Pad.MenuSection"],
                DataContext = UiText.Tr(title),
            };
            menu.Items.Add(section);
            return section;
        }

        /// <summary>Adds concise mouse and assistive-technology help without changing an action's header.</summary>
        internal static MenuItem Describe(MenuItem item, string help)
        {
            string localized = UiText.Tr(help);
            item.ToolTip = localized;
            ToolTipService.SetShowOnDisabled(item, true);
            AutomationProperties.SetHelpText(item, localized);
            return item;
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
        public static MenuItem Item(string header, string? gesture, Action action, bool enabled = true, string? icon = null, bool localize = true)
        {
            var item = new MenuItem { Header = localize ? UiText.Tr(header) : header, InputGestureText = gesture ?? "", IsEnabled = enabled, Icon = icon };
            item.Click += (s, e) => Guard("The menu command " + header, action);
            return item;
        }

        public static MenuItem Check(string header, string? gesture, bool isChecked, Action action, string? icon = null)
        {
            var item = Item(header, gesture, action, icon: icon);
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

        /// <summary>Format ▸ for a Markdown tab (spec 2.4): each item is one undoable edit. Format table lines up the table the caret is in (spec 3) and is off elsewhere.</summary>
        public static MenuItem FormatMenu(TextEditor editor)
        {
            var format = new MenuItem { Header = UiText.Tr("Format"), Icon = "\uE8D2" };
            void Add(string header, Func<string, int, int, TextEdit> edit, string? icon = null, string? gesture = null) =>
                format.Items.Add(Item(header, gesture, () =>
                    ApplyEdit(editor, edit(editor.Document.Text, editor.SelectionStart, editor.SelectionLength)), icon: icon));

            Add("Bold", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "**"), "\uE8DD", "Ctrl+B");
            Add("Italic", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "*"), "\uE8DB", "Ctrl+I");
            Add("Strikethrough", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "~~"), "\uEDE0");
            Add("Code", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "`"), "\uE943");
            Add("Link", MarkdownFormatter.Link, "\uE71B", "Ctrl+K");
            format.Items.Add(new Separator());
            Add("Heading 1", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading1), "\uE8E9");
            Add("Heading 2", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading2), "\uE8E9");
            Add("Heading 3", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading3), "\uE8E9");
            format.Items.Add(new Separator());
            Add("Bullet list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Bullet), "\uE8FD");
            Add("Numbered list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Numbered), "\uEA37");
            Add("Task", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Task), "\uE73A");
            Add("Quote", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Quote), "\uE90A");
            Add("Code block", MarkdownFormatter.CodeBlock, "\uE943", "Ctrl+Shift+K");
            format.Items.Add(new Separator());
            bool inTable = TableFormatter.TableAt(editor.Document.Text, editor.CaretOffset) != null;
            format.Items.Add(Item("Format table", null, () =>
            {
                if (TableFormatter.Format(editor.Document.Text, editor.CaretOffset) is { } edit) ApplyEdit(editor, edit);
            }, inTable, icon: "\uE8A9"));
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

            var lines = new MenuItem { Header = UiText.Tr("Lines"), Icon = "\uEA37" };
            void Add(string header, string? gesture, Func<string, int, int, TextEdit?> operation, string? icon = null, bool enabled = true) =>
                lines.Items.Add(Item(header, gesture, () => RunLineOperation(editor, operation), enabled, icon));

            lines.Items.Add(Item("Duplicate", "Ctrl+D", () => Duplicate(editor), icon: "\uE8C8"));
            lines.Items.Add(Item("Move up", "Ctrl+Shift+Up", () => moveLines(false), !firstLine, "\uE74A"));
            lines.Items.Add(Item("Move down", "Ctrl+Shift+Down", () => moveLines(true), !lastLine, "\uE74B"));
            Add("Join lines", "Ctrl+J", LineOperations.Join, "\uE71B", enabled: !(oneLine && lastLine));
            lines.Items.Add(new Separator());
            Add("Sort ascending", null, (t, s, l) => LineOperations.Sort(t, s, l, false, System.Globalization.CultureInfo.CurrentCulture), "\uE8CB");
            Add("Sort descending", null, (t, s, l) => LineOperations.Sort(t, s, l, true, System.Globalization.CultureInfo.CurrentCulture), "\uE8CB");
            Add("Remove duplicate lines", null, LineOperations.RemoveDuplicates, "\uE738");
            Add("Trim trailing whitespace", null, LineOperations.TrimTrailing, "\uE78A");
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
            var tools = new MenuItem { Header = UiText.Tr("Tools"), Icon = "\uE90F" };
            MenuItem Tool(string header, Func<string, int, int, ToolOutcome> tool, bool enabled = true, string icon = "\uE943") =>
                Item(header, null, () => RunTool(editor, report, tool), enabled, icon);

            tools.Items.Add(Tool("Base64 encode", (t, s, l) => TextTools.OnSelection(t, s, l, x => (TextTools.Base64Encode(x), null)), selected));
            tools.Items.Add(Tool("Base64 decode", (t, s, l) => TextTools.OnSelection(t, s, l, TextTools.Base64Decode), selected));

            var convert = new MenuItem { Header = UiText.Tr("Convert number"), Icon = "\uE8EF", IsEnabled = selected };
            foreach (var (header, target) in new[] { ("Decimal", NumberBase.Decimal), ("Hex", NumberBase.Hex), ("Binary", NumberBase.Binary), ("Octal", NumberBase.Octal) })
                convert.Items.Add(Tool(header, (t, s, l) => TextTools.OnSelection(t, s, l, x => NumberConverter.Convert(x, target))));
            tools.Items.Add(convert);

            tools.Items.Add(Tool("Insert GUID", (t, s, l) => TextTools.Insert(s, l, TextTools.FormatGuid(Guid.NewGuid())), icon: "\uE943"));

            var stamp = new MenuItem { Header = UiText.Tr("Insert timestamp"), Icon = "\uE787" };
            stamp.Items.Add(Tool("ISO 8601", (t, s, l) => TextTools.Insert(s, l, TextTools.Iso8601(now())), icon: "\uE787"));
            stamp.Items.Add(Tool("Date", (t, s, l) => TextTools.Insert(s, l, TextTools.Date(now())), icon: "\uE787"));
            stamp.Items.Add(Tool("Unix seconds", (t, s, l) => TextTools.Insert(s, l, TextTools.UnixSeconds(now())), icon: "\uE917"));
            tools.Items.Add(stamp);

            tools.Items.Add(Tool("Evaluate", TextTools.Evaluate, selected, "\uE8EF"));
            return tools;
        }

        /// <summary>The one AI entry while AI is off in MicaPad, in the AI menu and on a diagram's error box: it opens Settings → MicaPad.</summary>
        public const string SetUpAiText = "Set up AI…";

        /// <summary>The AI menu's entry for a block that fails to render.</summary>
        public const string FixDiagramText = "Fix diagram";

        /// <summary>
        /// AI (MicaPad AI spec 3.1): one item per action, with Ask AI… after a separator; a rewrite
        /// waits for a selection. While AI is off the menu holds one item, Set up AI…, so nothing
        /// can be sent from it. <paramref name="run"/> starts an action; <paramref name="setUp"/> opens the settings.
        /// With <paramref name="fixDiagram"/> (the caret is in a diagram or math block that shows an
        /// error; part 2, spec 2.2), Fix diagram follows Draw as diagram and runs it.
        /// <paramref name="diagrams"/> is false in a note that is not shown as Markdown: Draw as
        /// diagram is left out there, with Fix diagram. A fenced block is not drawn in such a note,
        /// and Insert below would write one into a source file.
        /// </summary>
        public static MenuItem AiMenu(TextEditor editor, bool enabled, Action<PadAiAction> run, Action setUp, Action? fixDiagram = null,
                                      bool diagrams = true)
        {
            var ai = new MenuItem { Header = UiText.Tr("AI"), Icon = ((char)0xE99A).ToString() };
            if (!enabled)
            {
                ai.Items.Add(Item(SetUpAiText, null, setUp, icon: "\uE713"));
                return ai;
            }

            bool selected = editor.SelectionLength > 0;
            foreach (PadAiAction action in PadAiAction.Menu)
            {
                PadAiAction chosen = action;
                if (ReferenceEquals(action, PadAiAction.Ask))
                {
                    // The instruction is typed in the pane, hence the ellipsis.
                    ai.Items.Add(new Separator());
                    ai.Items.Add(Item(action.Name + "…", "Ctrl+Shift+A", () => run(chosen), icon: AiActionIcon(action)));
                }
                else if (diagrams || !ReferenceEquals(action, PadAiAction.Diagram))
                {
                    ai.Items.Add(Item(action.Name, null, () => run(chosen), selected || !action.NeedsSelection, AiActionIcon(action)));
                    // It takes the failing block's source, whatever is selected: no selection to wait for.
                    if (fixDiagram != null && ReferenceEquals(action, PadAiAction.Diagram))
                        ai.Items.Add(Item(FixDiagramText, null, fixDiagram, icon: "\uE90F"));
                }
            }
            return ai;
        }

        private static string AiActionIcon(PadAiAction action) => action.Id switch
        {
            "improve" => "\uE70F",
            "fix" => "\uE73E",
            "shorten" => "\uE78A",
            "to-english" or "to-thai" => "\uE775",
            "summarize" => "\uE8FD",
            "explain" => "\uE946",
            "diagram" => "\uE8A9",
            "ask" => "\uE90A",
            _ => "\uE99A",
        };

        /// <summary>Runs a tool: its edit is one undoable change; a problem is reported and the text left alone. True when it edited.</summary>
        public static bool RunTool(TextEditor editor, Action<string> report, Func<string, int, int, ToolOutcome> tool)
        {
            if (editor.IsReadOnly) return false;
            if (editor.TextArea.Selection is RectangleSelection)
            {
                report(UiText.Tr(RectangleRefused));
                return false;
            }
            var outcome = tool(editor.Document.Text, editor.SelectionStart, editor.SelectionLength);
            if (outcome.Edit is not TextEdit edit)
            {
                report(UiText.Tr(outcome.Problem ?? TextTools.SelectFirst));
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
            int start = Math.Clamp(edit.SelectionStart, 0, document.TextLength);
            editor.Select(start, Math.Clamp(edit.SelectionLength, 0, document.TextLength - start));
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
