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

            Assert.Equal(new[] { "Undo", "Redo", "-", "Cut", "Copy", "Paste", "Delete", "Select all", "-", "Find", "Replace", "Go to line…" },
                         Headers(window.EditorMenu));
            Assert.Equal("Ctrl+Z", ItemOf(window.EditorMenu, "Undo").InputGestureText);
            Assert.Equal("Ctrl+G", ItemOf(window.EditorMenu, "Go to line…").InputGestureText);
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
    }
}
