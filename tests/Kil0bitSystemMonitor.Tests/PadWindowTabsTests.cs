using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using ICSharpCode.AvalonEdit.Highlighting;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Two MicaPad windows over one workspace: each shows, edits and saves only its own tabs.</summary>
    public class PadWindowTabsTests
    {
        /// <summary>
        /// A window over the workspace's first window, and a second one over a window made in the
        /// model with one note (placed by <paramref name="place"/> first). Never shown; both closed at the end.
        /// </summary>
        private static void WithTwoWindows(Action<MicaPadWindow, MicaPadWindow, PadTestEnv> test, Action<PadWindowState>? place = null) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var first = new MicaPadWindow(env.Workspace, config);
            MicaPadWindow? second = null;
            try
            {
                first.LoadSession();
                var state = env.Workspace.NewWindow(first.WindowId);
                place?.Invoke(state);
                env.Workspace.NewNote(state.Id);
                second = new MicaPadWindow(env.Workspace, config, state.Id);
                second.LoadSession();
                test(first, second, env);
            }
            finally
            {
                second?.CloseForExit();
                first.CloseForExit();
            }
        });

        [Fact]
        public void Each_window_shows_only_its_own_tabs() => WithTwoWindows((first, second, env) =>
        {
            first.NewTab();

            Assert.Same(env.Workspace.TabsOf(first.WindowId), first.TabStrip.ItemsSource);
            Assert.Equal(2, first.TabStrip.Items.Count);
            Assert.Single(second.TabStrip.Items);
            Assert.Equal(3, env.Workspace.Open.Count);
            Assert.NotSame(first.Editor.Document, second.Editor.Document);
        });

        [Fact]
        public void A_new_tab_in_one_window_is_saved_from_that_windows_editor() => WithTwoWindows((first, second, env) =>
        {
            // Both windows hear every change to the open notes; only the tab's own window may build
            // its document, or the other window would take over what the tab saves.
            first.NewTab();
            first.Editor.Document.Insert(0, "typed in the first window");
            second.NewTab();
            second.Editor.Document.Insert(0, "typed in the second window");

            Assert.Equal("typed in the first window", env.Workspace.ActiveIn(first.WindowId)!.TextProvider());
            Assert.Equal("typed in the second window", env.Workspace.ActiveIn(second.WindowId)!.TextProvider());
        });

        [Fact]
        public void Zoom_and_always_on_top_belong_to_each_window() => WithTwoWindows((first, second, env) =>
        {
            first.HandleShortcut(Key.OemPlus, ModifierKeys.Control);
            PadMenuTests.Click(PadMenuTests.ItemOf(second.BuildMainMenu(), "Always on top"));

            Assert.Equal(14 * 1.1, first.Editor.FontSize, 3);
            Assert.Equal(14, second.Editor.FontSize, 3);
            Assert.False(first.Topmost);
            Assert.True(second.Topmost);
            Assert.Equal(1.1, env.Workspace.WindowStateOf(first.WindowId)!.Zoom, 3);
            Assert.Equal(1.0, env.Workspace.WindowStateOf(second.WindowId)!.Zoom, 3);
            Assert.False(env.Workspace.WindowStateOf(first.WindowId)!.AlwaysOnTop);
            Assert.True(env.Workspace.WindowStateOf(second.WindowId)!.AlwaysOnTop);
        });

        [Fact]
        public void Each_window_opens_at_its_own_size() => WithTwoWindows((first, second, env) =>
        {
            Assert.Equal(640, second.Width);
            Assert.Equal(480, second.Height);
            Assert.Equal(900, first.Width);
        }, place: state =>
        {
            state.Width = 640;
            state.Height = 480;
        });

        [Fact]
        public void Tab_keys_walk_this_windows_tabs_only() => WithTwoWindows((first, second, env) =>
        {
            first.Editor.Document.Insert(0, "one");
            first.NewTab();
            first.Editor.Document.Insert(0, "two");

            first.SelectTab(0);
            Assert.Equal("one", first.Editor.Document.Text);
            Assert.True(first.HandleShortcut(Key.Tab, ModifierKeys.Control));
            Assert.Equal("two", first.Editor.Document.Text);
            Assert.True(first.HandleShortcut(Key.Tab, ModifierKeys.Control));
            Assert.Equal("one", first.Editor.Document.Text);          // wrapped within this window
            first.SelectTab(2);                                         // past this window's tabs: ignored
            Assert.Equal("one", first.Editor.Document.Text);
        });

        [Fact]
        public void Closing_a_windows_last_tab_leaves_a_fresh_note_in_that_window() => WithTwoWindows((first, second, env) =>
        {
            var only = env.Workspace.ActiveIn(second.WindowId)!;
            var firstTabs = env.Workspace.TabsOf(first.WindowId).ToList();

            Assert.True(second.HandleShortcut(Key.W, ModifierKeys.Control));

            var fresh = Assert.Single(env.Workspace.TabsOf(second.WindowId));
            Assert.NotSame(only, fresh);
            Assert.Equal(firstTabs, env.Workspace.TabsOf(first.WindowId));
        });

        [Fact]
        public void A_document_handed_to_another_window_keeps_its_undo_and_bookmarks() => WithTwoWindows((first, second, env) =>
        {
            var note = env.Workspace.ActiveIn(first.WindowId)!;
            var document = first.Editor.Document;
            document.Insert(0, "a\nb\nc");
            first.Editor.CaretOffset = document.GetLineByNumber(2).Offset;
            first.ToggleBookmark();
            first.NewTab();                                             // the note is no longer the one shown

            var released = first.ReleaseDocument(note);
            env.Workspace.MoveToWindow(note, second.WindowId);
            Assert.True(second.AdoptDocument(note, released!));
            second.SelectTab(env.Workspace.TabsOf(second.WindowId).IndexOf(note));

            Assert.Same(document, released);
            Assert.Same(document, second.Editor.Document);
            Assert.True(second.Editor.CanUndo);
            Assert.Equal(new[] { 2 }, second.BookmarkLines);
            second.Editor.Document.Insert(0, "typed ");
            Assert.StartsWith("typed a", note.TextProvider());
            Assert.True(env.Workspace.HasPendingChanges(note));        // the second window's edits reach the workspace
        });

        [Fact]
        public void Undo_in_the_adopting_window_leaves_the_releasing_windows_caret_and_text_alone() => WithTwoWindows((first, second, env) =>
        {
            var note = env.Workspace.ActiveIn(first.WindowId)!;
            first.Editor.Document.Insert(0, "hello world, a long line of text");
            first.Editor.Document.UndoStack.ClearAll();
            first.Editor.Select(6, 5);
            first.Editor.SelectedText = "";                              // an edit made with a selection: the undo action remembers this window's text area
            first.NewTab();
            var other = env.Workspace.ActiveIn(first.WindowId)!;
            first.Editor.Document.Insert(0, "hi");
            first.Editor.CaretOffset = 2;

            var released = first.ReleaseDocument(note);
            env.Workspace.MoveToWindow(note, second.WindowId);
            Assert.True(second.AdoptDocument(note, released!));
            second.SelectTab(env.Workspace.TabsOf(second.WindowId).IndexOf(note));
            second.Editor.Undo();

            Assert.Equal("hello world, a long line of text", note.TextProvider());
            Assert.Equal("hi", other.TextProvider());
            Assert.Equal(2, first.Editor.CaretOffset);
            Assert.Equal(0, first.Editor.SelectionLength);
            first.Editor.TextArea.PerformTextInput("Z");                 // no exception, and only this window's note changes
            Assert.Equal("hiZ", other.TextProvider());
            Assert.Equal("hello world, a long line of text", note.TextProvider());
        });

        [Fact]
        public void Releasing_the_shown_note_leaves_the_editor_empty_and_unhooked() => WithTwoWindows((first, second, env) =>
        {
            var note = env.Workspace.ActiveIn(first.WindowId)!;
            first.Editor.Document.Insert(0, "kept");
            first.ShowInfo("about the note", note);

            var released = first.ReleaseDocument(note);

            Assert.NotNull(released);
            Assert.NotSame(released, first.Editor.Document);
            Assert.Equal("", first.Editor.Document.Text);
            Assert.Equal(System.Windows.Visibility.Collapsed, first.InfoBar.Visibility);
            Assert.Empty(first.Editor.TextArea.TextView.LineTransformers.OfType<ThemedHighlightingColorizer>());
            first.Editor.Document.Insert(0, "stray");
            Assert.Equal("kept", note.TextProvider());
        });

        [Fact]
        public void A_window_adopts_only_a_document_for_its_own_note_and_only_once() => WithTwoWindows((first, second, env) =>
        {
            var mine = env.Workspace.ActiveIn(second.WindowId)!;
            var theirs = env.Workspace.ActiveIn(first.WindowId)!;

            Assert.False(second.AdoptDocument(mine, new ICSharpCode.AvalonEdit.Document.TextDocument("dropped")));   // already has one
            Assert.False(second.AdoptDocument(theirs, new ICSharpCode.AvalonEdit.Document.TextDocument("not mine")));
            Assert.Equal("", mine.TextProvider());
        });
    }
}
