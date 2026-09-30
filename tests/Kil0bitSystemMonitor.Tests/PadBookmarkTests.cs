using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Bookmarks: toggling, jumping, following edits, and surviving a restart.</summary>
    public class PadBookmarkTests
    {
        private static void WithEnv(Action<PadTestEnv, Func<MicaPadWindow>> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var windows = new System.Collections.Generic.List<MicaPadWindow>();
            MicaPadWindow Open()
            {
                var window = new MicaPadWindow(env.Workspace, new AppConfig());
                window.LoadSession();
                windows.Add(window);
                return window;
            }
            try
            {
                test(env, Open);
            }
            finally
            {
                foreach (var w in windows) w.CloseForExit();
            }
        });

        private static void GoToLine(MicaPadWindow window, int line) =>
            window.Editor.CaretOffset = window.Editor.Document.GetLineByNumber(line).Offset;

        [Fact]
        public void Ctrl_f2_toggles_a_bookmark_on_the_caret_line() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 2);

            Assert.True(window.HandleShortcut(Key.F2, ModifierKeys.Control));
            Assert.Equal(new[] { 2 }, window.BookmarkLines);

            window.HandleShortcut(Key.F2, ModifierKeys.Control);
            Assert.Empty(window.BookmarkLines);
        });

        [Fact]
        public void F2_and_shift_f2_jump_and_wrap_around() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "1\n2\n3\n4\n5";
            foreach (int line in new[] { 2, 4 })
            {
                GoToLine(window, line);
                window.ToggleBookmark();
            }
            GoToLine(window, 1);

            window.HandleShortcut(Key.F2, ModifierKeys.None);
            Assert.Equal(2, window.Editor.TextArea.Caret.Line);
            window.HandleShortcut(Key.F2, ModifierKeys.None);
            Assert.Equal(4, window.Editor.TextArea.Caret.Line);
            window.HandleShortcut(Key.F2, ModifierKeys.None);
            Assert.Equal(2, window.Editor.TextArea.Caret.Line);        // wrapped
            window.HandleShortcut(Key.F2, ModifierKeys.Shift);
            Assert.Equal(4, window.Editor.TextArea.Caret.Line);        // wrapped backwards
        });

        [Fact]
        public void Bookmarks_move_with_the_text() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 3);
            window.ToggleBookmark();

            window.Editor.Document.Insert(0, "new 1\nnew 2\n");
            Assert.Equal(new[] { 5 }, window.BookmarkLines);

            window.Editor.Document.Remove(0, "new 1\n".Length);
            Assert.Equal(new[] { 4 }, window.BookmarkLines);
        });

        [Fact]
        public void A_deleted_bookmarked_line_does_not_duplicate_another() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 2);
            window.ToggleBookmark();
            GoToLine(window, 3);
            window.ToggleBookmark();

            var line2 = window.Editor.Document.GetLineByNumber(2);
            window.Editor.Document.Remove(line2.Offset, line2.TotalLength);   // delete "b\n"

            Assert.Equal(new[] { 2 }, window.BookmarkLines);
        });

        [Fact]
        public void Clear_removes_them_all() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb";
            window.ToggleBookmark();
            window.ClearBookmarks();
            Assert.Empty(window.BookmarkLines);
        });

        [Fact]
        public void Bookmarks_survive_a_restart_on_their_moved_line() => WithEnv((env, open) =>
        {
            var first = open();
            first.Editor.Document.Text = "a\nb\nc";
            GoToLine(first, 2);
            first.ToggleBookmark();
            first.Editor.Document.Insert(0, "top\n");       // the bookmark moves to line 3
            first.PrepareForExit();

            Assert.Equal(new[] { 3 }, env.Workspace.Session.Tabs[env.Workspace.Active!.Id].Bookmarks);

            // A second window over the same workspace rebuilds its documents from the session, as a
            // restart does. (The helper closes both at the end; a window must not be closed twice.)
            var second = open();
            Assert.Equal(new[] { 3 }, second.BookmarkLines);
        });

        [Fact]
        public void A_saved_bookmark_past_the_end_is_dropped() => WithEnv((env, open) =>
        {
            var window = open();
            var note = env.Workspace.Active!;
            window.Editor.Document.Text = "only line";
            env.Workspace.SetBookmarks(note, new[] { 1, 7 });

            var again = open();                          // rebuilds the document and loads the saved lines
            Assert.Equal(new[] { 1 }, again.BookmarkLines);
        });

        [Fact]
        public void The_view_state_keeps_bookmarks() => WithEnv((env, open) =>
        {
            open();
            var note = env.Workspace.Active!;
            env.Workspace.SetBookmarks(note, new[] { 3 });
            env.Workspace.SetTabViewState(note, 5, 10.0);
            Assert.Equal(new[] { 3 }, env.Workspace.Session.Tabs[note.Id].Bookmarks);
        });

        [Fact]
        public void Replacing_the_whole_text_keeps_bookmark_line_numbers() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 2);
            window.ToggleBookmark();

            window.ReplaceShownText("x\ny\nz\nw");
            Assert.Equal(new[] { 2 }, window.BookmarkLines);
        });
    }
}
