using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's right-click menus: which items there are, when they are enabled, and what they do.</summary>
    public class PadMenuTests
    {
        internal static void WithWindow(Action<MicaPadWindow, PadTestEnv> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var window = new MicaPadWindow(env.Workspace, new AppConfig());
            try
            {
                window.LoadSession();
                test(window, env);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        /// <summary>Headers in order, with "-" for each separator.</summary>
        internal static string[] Headers(ContextMenu menu) =>
            menu.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();

        internal static MenuItem ItemOf(ContextMenu menu, string header) =>
            menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);

        internal static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        [Fact]
        public void The_editor_menu_lists_edit_then_find_items() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();

            Assert.Equal(new[] { "Undo", "Redo", "-", "Cut", "Copy", "Paste", "Delete", "Select all", "-", "Format", "-", "Find", "Replace", "Go to line…" },
                         Headers(window.EditorMenu));
            Assert.Equal("Ctrl+Z", ItemOf(window.EditorMenu, "Undo").InputGestureText);
            Assert.Equal("Ctrl+G", ItemOf(window.EditorMenu, "Go to line…").InputGestureText);
        });

        [Fact]
        public void A_markdown_note_has_the_format_menu_and_a_json_file_does_not() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();
            var format = ItemOf(window.EditorMenu, "Format");
            var headers = format.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();
            Assert.Equal(new[] { "Bold", "Italic", "Strikethrough", "Code", "Link", "-", "Heading 1", "Heading 2", "Heading 3", "-",
                                 "Bullet list", "Numbered list", "Task", "Quote", "Code block" }, headers);

            string path = env.FileOf("a.json");
            File.WriteAllText(path, "{}");
            window.OpenPath(path);
            window.RefreshEditorMenu();
            Assert.DoesNotContain("Format", Headers(window.EditorMenu));
        });

        [Fact]
        public void Format_from_the_menu_is_one_undo_step() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello");
            window.Editor.Select(0, 5);
            window.RefreshEditorMenu();

            var bold = ItemOf(window.EditorMenu, "Format").Items.OfType<MenuItem>().Single(m => (string)m.Header == "Bold");
            Click(bold);

            Assert.Equal("**hello**", window.Editor.Document.Text);
            Assert.Equal("hello", window.Editor.SelectedText);
            window.Editor.Undo();
            Assert.Equal("hello", window.Editor.Document.Text);
        });

        [Fact]
        public void With_no_text_and_no_selection_only_what_can_apply_is_enabled() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();
            var menu = window.EditorMenu;

            Assert.False(ItemOf(menu, "Undo").IsEnabled);
            Assert.False(ItemOf(menu, "Redo").IsEnabled);
            Assert.False(ItemOf(menu, "Cut").IsEnabled);
            Assert.False(ItemOf(menu, "Copy").IsEnabled);
            Assert.False(ItemOf(menu, "Delete").IsEnabled);
            Assert.False(ItemOf(menu, "Select all").IsEnabled);
            Assert.True(ItemOf(menu, "Find").IsEnabled);
        });

        [Fact]
        public void A_selection_enables_cut_copy_and_delete() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 5);

            window.RefreshEditorMenu();
            var menu = window.EditorMenu;

            Assert.True(ItemOf(menu, "Undo").IsEnabled);
            Assert.True(ItemOf(menu, "Cut").IsEnabled);
            Assert.True(ItemOf(menu, "Copy").IsEnabled);
            Assert.True(ItemOf(menu, "Delete").IsEnabled);
            Assert.True(ItemOf(menu, "Select all").IsEnabled);
        });

        [Fact]
        public void Delete_removes_the_selection_as_one_undo_step() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 6);
            window.RefreshEditorMenu();

            Click(ItemOf(window.EditorMenu, "Delete"));
            Assert.Equal("world", window.Editor.Document.Text);

            window.Editor.Undo();
            Assert.Equal("hello world", window.Editor.Document.Text);
        });

        [Fact]
        public void Delete_on_a_rectangle_matches_the_Del_key_and_is_one_undo_step() => WithWindow((window, env) =>
        {
            const string original = "123456\n789012\n345678";   // digits share one width, so visual columns line up on every line
            window.Editor.Document.Text = original;
            var area = window.Editor.TextArea;
            // An unshown window has no layout, and a rectangle is measured in visual columns.
            window.Measure(new System.Windows.Size(800, 600));
            window.Arrange(new System.Windows.Rect(0, 0, 800, 600));
            window.UpdateLayout();
            area.TextView.EnsureVisualLines();
            area.Selection = new ICSharpCode.AvalonEdit.Editing.RectangleSelection(
                area, new ICSharpCode.AvalonEdit.TextViewPosition(1, 2), new ICSharpCode.AvalonEdit.TextViewPosition(3, 5));
            Assert.Equal("234\n890\n456", area.Selection.GetText().Replace("\r\n", "\n"));
            window.Editor.Document.UndoStack.ClearAll();
            window.RefreshEditorMenu();

            Click(ItemOf(window.EditorMenu, "Delete"));
            string viaMenu = window.Editor.Document.Text;

            window.Editor.Undo();
            Assert.Equal(original, window.Editor.Document.Text);

            // The Del key runs the same command on the same rectangle.
            area.Selection = new ICSharpCode.AvalonEdit.Editing.RectangleSelection(
                area, new ICSharpCode.AvalonEdit.TextViewPosition(1, 2), new ICSharpCode.AvalonEdit.TextViewPosition(3, 5));
            System.Windows.Input.ApplicationCommands.Delete.Execute(null, area);
            Assert.Equal(viaMenu, window.Editor.Document.Text);
            Assert.Equal("156\n712\n378", viaMenu);
        });

        [Fact]
        public void Select_all_and_find_do_what_they_say() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "abc");
            window.RefreshEditorMenu();

            Click(ItemOf(window.EditorMenu, "Select all"));
            Assert.Equal(3, window.Editor.SelectionLength);

            Click(ItemOf(window.EditorMenu, "Find"));
            Assert.True(window.FindBar.IsOpen);
        });

        [Fact]
        public void A_right_click_inside_the_selection_keeps_it() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 5);

            window.PlaceCaretForMenu(3);

            Assert.Equal(0, window.Editor.SelectionStart);
            Assert.Equal(5, window.Editor.SelectionLength);
        });

        [Fact]
        public void A_right_click_outside_the_selection_moves_the_caret() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 5);

            window.PlaceCaretForMenu(8);

            Assert.Equal(0, window.Editor.SelectionLength);
            Assert.Equal(8, window.Editor.CaretOffset);

            window.PlaceCaretForMenu(500);   // past the end: clamped
            Assert.Equal(11, window.Editor.CaretOffset);
        });

        [Fact]
        public void The_preview_menu_only_copies() => WithWindow((window, env) =>
        {
            window.PreviewEditor.Text = "old version";
            window.RefreshPreviewMenu();

            Assert.Equal(new[] { "Copy", "Select all" }, Headers(window.PreviewMenu));
            Assert.False(ItemOf(window.PreviewMenu, "Copy").IsEnabled);
            Assert.True(ItemOf(window.PreviewMenu, "Select all").IsEnabled);
        });

        [Fact]
        public void The_editor_menu_follows_the_theme() => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var window = new MicaPadWindow(env.Workspace, new AppConfig { PadTheme = "Light" });
            try
            {
                window.LoadSession();
                window.RefreshEditorMenu();
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window.EditorMenu));
            }
            finally
            {
                window.CloseForExit();
            }
        });

        [Fact]
        public void A_note_tab_menu_has_rename_close_and_close_others() => WithWindow((window, env) =>
        {
            var note = env.Workspace.Open[0];

            var menu = window.BuildTabMenu(note, null);

            Assert.Equal(new[] { "Rename…", "Close", "Close other tabs" }, Headers(menu));
            Assert.False(ItemOf(menu, "Close other tabs").IsEnabled);   // it is the only tab
        });

        [Fact]
        public void A_file_tab_menu_adds_copy_path_and_show_in_folder() => WithWindow((window, env) =>
        {
            string path = env.FileOf("notes.txt");
            File.WriteAllText(path, "text");
            window.OpenPath(path);
            var note = env.Workspace.Active!;

            var menu = window.BuildTabMenu(note, null);

            Assert.Equal(new[] { "Rename…", "Close", "Close other tabs", "-", "Copy file path", "Show in folder" }, Headers(menu));
            Assert.True(ItemOf(menu, "Close other tabs").IsEnabled);
        });

        [Fact]
        public void Close_from_the_tab_menu_closes_that_tab_only() => WithWindow((window, env) =>
        {
            var first = env.Workspace.Open[0];
            window.Editor.Document.Insert(0, "first");
            window.NewTab();
            window.Editor.Document.Insert(0, "second");
            var second = env.Workspace.Active!;

            Click(ItemOf(window.BuildTabMenu(first, null), "Close"));

            Assert.Same(second, Assert.Single(env.Workspace.Open));
            Assert.Equal("second", window.Editor.Document.Text);
        });

        [Fact]
        public void Close_other_tabs_keeps_every_note_recoverable() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "scratch one");
            var scratch = env.Workspace.Active!;

            string path = env.FileOf("edited.txt");
            File.WriteAllText(path, "on disk");
            window.OpenPath(path);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, " plus my edit");
            var file = env.Workspace.Active!;

            window.NewTab();
            window.Editor.Document.Insert(0, "keep me");
            var keep = env.Workspace.Active!;

            Click(ItemOf(window.BuildTabMenu(keep, null), "Close other tabs"));
            env.Flush();

            Assert.Same(keep, Assert.Single(env.Workspace.Open));
            Assert.Equal("keep me", window.Editor.Document.Text);
            var closedIds = env.Workspace.ClosedNotes().Select(m => m.Id).ToList();
            Assert.Contains(scratch.Id, closedIds);
            Assert.Contains(file.Id, closedIds);
            Assert.Equal("on disk", File.ReadAllText(path));   // closing never writes the real file

            var back = env.Workspace.Reopen(file.Id);
            Assert.NotNull(back);
            Assert.Equal("on disk plus my edit", back!.TextProvider());
        });

        [Fact]
        public void Rename_from_the_tab_menu_opens_the_rename_box() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "Shopping list");
            var note = env.Workspace.Active!;

            Click(ItemOf(window.BuildTabMenu(note, null), "Rename…"));

            Assert.Same(note, window.RenamingNote);
            Assert.Equal(note.Title, window.RenameBox.Text);
        });

        [Fact]
        public void Show_in_folder_on_a_missing_file_says_so() => WithWindow((window, env) =>
        {
            string path = env.FileOf("gone.txt");

            window.ShowInFolder(path);

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("That file is no longer at " + path + ".", window.InfoText.Text);
        });

        [Fact]
        public void A_tab_menu_notice_does_not_replace_a_file_gone_question() => WithWindow((window, env) =>
        {
            string path = env.FileOf("vanishing.txt");
            File.WriteAllText(path, "hello");
            window.OpenPath(path);
            File.Delete(path);
            window.CheckShownNoteOnDisk();

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("Save As…", window.InfoPrimary.Content);
            string question = window.InfoText.Text;

            window.ShowInFolder(path);

            Assert.Equal(question, window.InfoText.Text);
            Assert.Equal(Visibility.Visible, window.InfoPrimary.Visibility);
        });
    }
}
