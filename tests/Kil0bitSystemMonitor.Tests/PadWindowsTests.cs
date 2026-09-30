using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>More than one MicaPad window: opening, closing, exiting, coming back, moving tabs and routing files.</summary>
    public class PadWindowsTests
    {
        /// <summary>
        /// A window over a fresh workspace. Showing is replaced — tests never show windows — and the
        /// windows MicaPad would have shown are listed in order. Every window loaded over the
        /// workspace is closed at the end.
        /// </summary>
        internal static void WithWindows(Action<MicaPadWindow, PadTestEnv, List<MicaPadWindow>> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var shown = new List<MicaPadWindow>();
            var previous = MicaPadWindow.ShowWindow;
            MicaPadWindow.ShowWindow = shown.Add;
            try
            {
                var window = new MicaPadWindow(env.Workspace, new AppConfig());
                window.LoadSession();
                test(window, env, shown);
            }
            finally
            {
                MicaPadWindow.ShowWindow = previous;
                foreach (var open in MicaPadWindow.WindowsOf(env.Workspace).ToList()) open.CloseForExit();
            }
        });

        [Fact]
        public void Ctrl_shift_n_opens_a_second_window_with_one_new_note() => WithWindows((first, env, shown) =>
        {
            var firstTabs = env.Workspace.TabsOf(first.WindowId).ToList();

            Assert.True(first.HandleShortcut(Key.N, ModifierKeys.Control | ModifierKeys.Shift));

            var second = Assert.Single(shown);
            Assert.NotSame(first, second);
            Assert.Equal(2, env.Workspace.Windows.Count);
            Assert.Equal(2, MicaPadWindow.WindowsOf(env.Workspace).Count);
            var note = Assert.Single(env.Workspace.TabsOf(second.WindowId));
            Assert.Equal("", note.TextProvider());
            Assert.Equal(firstTabs, env.Workspace.TabsOf(first.WindowId));
            Assert.Equal(second.WindowId, env.Workspace.MostRecentWindowId);
            Assert.True(env.Workspace.WindowStateOf(second.WindowId)!.Open);
            Assert.Same(second, MicaPadWindow.CurrentOf(env.Workspace));
        });

        [Fact]
        public void The_main_menu_offers_a_new_window() => WithWindows((window, env, shown) =>
        {
            var item = PadMenuTests.ItemOf(window.BuildMainMenu(), "New window");
            Assert.Equal("Ctrl+Shift+N", item.InputGestureText);

            PadMenuTests.Click(item);

            Assert.Single(shown);
        });

        [Fact]
        public void Closing_a_window_moves_its_tabs_documents_and_undo_to_the_most_recent_window() => WithWindows((first, env, shown) =>
        {
            var second = first.NewWindow();
            second.Editor.Document.Insert(0, "draft");
            var draft = env.Workspace.ActiveIn(second.WindowId)!;
            var document = second.Editor.Document;
            var third = first.NewWindow();
            env.Workspace.ActivateWindow(first.WindowId);
            env.Workspace.ActivateWindow(second.WindowId);             // most recent first: second, first, third

            second.CloseByUser();

            Assert.DoesNotContain(second, MicaPadWindow.WindowsOf(env.Workspace));
            Assert.Equal(new[] { first.WindowId, third.WindowId }, env.Workspace.Windows.Select(w => w.Id));
            Assert.Contains(draft, env.Workspace.TabsOf(first.WindowId));
            Assert.Same(first, shown[shown.Count - 1]);                 // the window that took the tabs comes forward
            first.SelectTab(env.Workspace.TabsOf(first.WindowId).IndexOf(draft));
            Assert.Same(document, first.Editor.Document);               // the same document: its undo came along
            Assert.True(first.Editor.CanUndo);
            first.Editor.Undo();
            Assert.Equal("", first.Editor.Document.Text);
            Assert.Empty(env.Workspace.ClosedNotes());
        });

        [Fact]
        public void Closing_a_window_keeps_unsaved_file_edits() => WithWindows((first, env, shown) =>
        {
            var second = first.NewWindow();
            PadLanguageWindowTests.OpenFile(second, env, "report.txt", "on disk");
            second.Editor.Document.Insert(second.Editor.Document.TextLength, " plus mine");
            var file = env.Workspace.ActiveIn(second.WindowId)!;

            second.CloseByUser();
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));

            Assert.Equal(first.WindowId, file.WindowId);
            Assert.True(file.HasUnsavedEdits);
            Assert.Equal("on disk plus mine", file.TextProvider());
            Assert.Equal("on disk plus mine", env.DiskText(file));      // MicaPad's copy has the edit
            Assert.Equal("on disk", File.ReadAllText(env.FileOf("report.txt")));   // the file itself is untouched
        });

        [Fact]
        public void Closing_the_last_window_hides_it_and_keeps_its_tabs() => WithWindows((window, env, shown) =>
        {
            window.Editor.Document.Insert(0, "stay");
            var tabs = env.Workspace.TabsOf(window.WindowId).ToList();

            window.CloseByUser();

            Assert.Contains(window, MicaPadWindow.WindowsOf(env.Workspace));
            Assert.True(window.IsHiddenByClose);
            Assert.False(env.Workspace.WindowStateOf(window.WindowId)!.Open);
            Assert.Equal(tabs, env.Workspace.TabsOf(window.WindowId));
            Assert.Equal("stay", window.Editor.Document.Text);
        });

        [Fact]
        public void A_hidden_window_comes_back_on_the_next_show() => WithWindows((window, env, shown) =>
        {
            window.CloseByUser();

            var front = MicaPadWindow.ShowOrActivate(env.Workspace, new AppConfig(), null);

            Assert.Same(window, front);
            Assert.Equal(new[] { window }, shown);
            Assert.False(window.IsHiddenByClose);
            Assert.True(env.Workspace.WindowStateOf(window.WindowId)!.Open);
        });

        [Fact]
        public void The_hotkey_brings_the_most_recently_active_window_forward() => WithWindows((first, env, shown) =>
        {
            first.NewWindow();
            env.Workspace.ActivateWindow(first.WindowId);
            shown.Clear();

            var front = MicaPadWindow.ShowOrActivate(env.Workspace, new AppConfig(), null);

            Assert.Same(first, front);
            Assert.Equal(new[] { first }, shown);
        });

        [Fact]
        public void Preparing_for_exit_keeps_every_window_in_the_session() => WithWindows((first, env, shown) =>
        {
            first.NewWindow();

            MicaPadWindow.PrepareAllForExit(env.Workspace);                  // what Quit and session end do, for this test's windows only
            foreach (var window in MicaPadWindow.WindowsOf(env.Workspace).ToList()) window.CloseForExit();   // what shutdown does next
            env.Workspace.SaveSession();
            env.Flush();

            var session = env.Store.LoadSession();
            Assert.Equal(2, session.Windows!.Count);
            Assert.All(session.Windows, w => Assert.True(w.Open));
        });

        [Fact]
        public void The_first_show_brings_back_every_window_where_it_was() => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var before = env.Workspace;
            PadTestEnv.Type(before, before.NewNote(), "one");
            var second = before.NewWindow(before.Windows[0].Id);
            PadTestEnv.Type(before, before.NewNote(second.Id), "two");
            second.Width = 640;
            second.Height = 480;
            env.Clock.Advance(1);
            before.ActivateWindow(before.Windows[0].Id);                // the first window was used last
            Assert.True(before.FlushAll(TimeSpan.FromSeconds(5)));

            var restarted = env.NewWorkspace(post: action => dispatcher.BeginInvoke(action));
            var shown = new List<MicaPadWindow>();
            var previous = MicaPadWindow.ShowWindow;
            MicaPadWindow.ShowWindow = shown.Add;
            try
            {
                var front = MicaPadWindow.ShowOrActivate(restarted, new AppConfig(), null);

                Assert.Equal(2, MicaPadWindow.WindowsOf(restarted).Count);
                Assert.Equal(restarted.Windows[0].Id, front.WindowId);   // the most recently active one is in front
                Assert.Same(front, shown[shown.Count - 1]);
                var other = MicaPadWindow.WindowsOf(restarted).Single(w => !ReferenceEquals(w, front));
                Assert.Equal(640, other.Width);
                Assert.Equal(480, other.Height);
                Assert.Equal("two", other.Editor.Document.Text);
                Assert.Equal("one", front.Editor.Document.Text);
            }
            finally
            {
                MicaPadWindow.ShowWindow = previous;
                foreach (var window in MicaPadWindow.WindowsOf(restarted).ToList()) window.CloseForExit();
            }
        });

        [Fact]
        public void Move_to_new_window_takes_the_tab_with_its_undo() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "stays");
            first.NewTab();
            first.Editor.Document.Insert(0, "leaves");
            var leaving = env.Workspace.ActiveIn(first.WindowId)!;
            var document = first.Editor.Document;

            PadMenuTests.Click(PadMenuTests.ItemOf(first.BuildTabMenu(leaving, null), "Move to new window"));

            var second = Assert.Single(shown);
            Assert.Equal(new[] { leaving }, env.Workspace.TabsOf(second.WindowId));
            Assert.DoesNotContain(leaving, env.Workspace.TabsOf(first.WindowId));
            Assert.Same(document, second.Editor.Document);
            Assert.True(second.Editor.CanUndo);
            Assert.Equal("stays", first.Editor.Document.Text);        // the neighbour is shown here now
        });

        [Fact]
        public void The_only_tab_cannot_move_to_a_new_window() => WithWindows((window, env, shown) =>
        {
            var only = env.Workspace.ActiveIn(window.WindowId)!;

            var menu = window.BuildTabMenu(only, null);

            Assert.False(PadMenuTests.ItemOf(menu, "Move to new window").IsEnabled);
            Assert.DoesNotContain("Move to", PadMenuTests.Headers(menu));    // no other window to move to
        });

        [Fact]
        public void Move_to_lists_each_other_window_by_its_active_tab() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "Groceries");
            var second = first.NewWindow();
            second.Editor.Document.Insert(0, "Meeting notes");
            env.Workspace.FlushPending();                             // a note's title follows its first line when saved
            var note = env.Workspace.ActiveIn(first.WindowId)!;

            var moveTo = PadMenuTests.ItemOf(first.BuildTabMenu(note, null), "Move to");

            Assert.Equal(new[] { "Meeting notes" }, moveTo.Items.OfType<MenuItem>().Select(m => (string)m.Header));

            env.Workspace.Rename(env.Workspace.ActiveIn(second.WindowId)!, "error_log.txt");
            moveTo = PadMenuTests.ItemOf(first.BuildTabMenu(note, null), "Move to");
            Assert.Equal("error__log.txt", (string)moveTo.Items.OfType<MenuItem>().Single().Header);   // shows as error_log.txt, no access key
        });

        [Fact]
        public void Move_to_another_window_shows_the_tab_there() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "keep");
            first.NewTab();
            first.Editor.Document.Insert(0, "Travel");
            var travel = env.Workspace.ActiveIn(first.WindowId)!;
            var second = first.NewWindow();
            shown.Clear();

            PadMenuTests.Click(PadMenuTests.ItemOf(first.BuildTabMenu(travel, null), "Move to").Items.OfType<MenuItem>().Single());

            Assert.Same(travel, env.Workspace.ActiveIn(second.WindowId));
            Assert.Equal("Travel", second.Editor.Document.Text);
            Assert.Equal("keep", first.Editor.Document.Text);
            Assert.Equal(new[] { second }, shown);
        });

        [Fact]
        public void Moving_the_only_tab_closes_its_window_into_the_other() => WithWindows((first, env, shown) =>
        {
            var second = first.NewWindow();
            second.Editor.Document.Insert(0, "Travel");
            var travel = env.Workspace.ActiveIn(second.WindowId)!;

            PadMenuTests.Click(PadMenuTests.ItemOf(second.BuildTabMenu(travel, null), "Move to").Items.OfType<MenuItem>().Single());

            Assert.DoesNotContain(second, MicaPadWindow.WindowsOf(env.Workspace));
            Assert.Single(env.Workspace.Windows);
            Assert.Same(travel, env.Workspace.ActiveIn(first.WindowId));
            Assert.Equal("Travel", first.Editor.Document.Text);
            Assert.True(first.Editor.CanUndo);
        });

        [Fact]
        public void Opening_a_file_open_in_another_window_brings_that_window_forward_on_its_tab() => WithWindows((first, env, shown) =>
        {
            string path = env.FileOf("app.log");
            File.WriteAllText(path, "started");
            var second = first.NewWindow();
            second.OpenPath(path);
            var log = env.Workspace.ActiveIn(second.WindowId)!;
            second.NewTab();                                          // the log is no longer the tab shown there
            env.Workspace.ActivateWindow(first.WindowId);             // and the user went back to the first window
            shown.Clear();

            var window = MicaPadWindow.Open(env.Workspace, new AppConfig(), null, path.ToUpperInvariant());   // what --pad and Open with do

            Assert.Same(second, window);
            Assert.Same(second, shown[shown.Count - 1]);
            Assert.Same(log, env.Workspace.ActiveIn(second.WindowId));
            Assert.Equal("started", second.Editor.Document.Text);
            Assert.Single(env.Workspace.Open, n => string.Equals(n.Meta.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        });

        [Fact]
        public void A_file_open_nowhere_goes_to_the_most_recently_active_window() => WithWindows((first, env, shown) =>
        {
            string path = env.FileOf("new.txt");
            File.WriteAllText(path, "fresh");
            first.NewWindow();
            env.Workspace.ActivateWindow(first.WindowId);

            var window = MicaPadWindow.Open(env.Workspace, new AppConfig(), null, path);

            Assert.Same(first, window);
            Assert.Equal(first.WindowId, env.Workspace.Open.Single(n => n.Meta.SourcePath == path).WindowId);
            Assert.Equal("fresh", first.Editor.Document.Text);
        });

        [Fact]
        public void Ctrl_o_of_a_file_open_in_another_window_shows_it_there() => WithWindows((first, env, shown) =>
        {
            string path = env.FileOf("shared.txt");
            File.WriteAllText(path, "one copy");
            var second = first.NewWindow();
            second.OpenPath(path);
            second.NewTab();
            shown.Clear();

            first.OpenPath(path);                                     // Ctrl+O and the Open dialog end here

            Assert.Same(second, shown[shown.Count - 1]);
            Assert.Equal("one copy", second.Editor.Document.Text);
            Assert.DoesNotContain(env.Workspace.TabsOf(first.WindowId), n => n.Meta.SourcePath == path);
        });

        [Fact]
        public void Undo_in_the_window_a_tab_moved_to_leaves_the_source_windows_caret_and_text_alone() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "stays put");
            first.NewTab();
            first.Editor.Document.Insert(0, "leaves");
            var leaving = env.Workspace.ActiveIn(first.WindowId)!;
            PadMenuTests.Click(PadMenuTests.ItemOf(first.BuildTabMenu(leaving, null), "Move to new window"));
            var second = Assert.Single(shown);
            first.Editor.Select(2, 4);
            int caret = first.Editor.CaretOffset;

            second.Editor.Undo();                                     // the moved tab's undo runs in its new window

            Assert.Equal("", second.Editor.Document.Text);
            Assert.Equal("stays put", first.Editor.Document.Text);
            Assert.Equal(2, first.Editor.SelectionStart);
            Assert.Equal(4, first.Editor.SelectionLength);
            Assert.Equal(caret, first.Editor.CaretOffset);
            first.Editor.TextArea.PerformTextInput("X");
            Assert.Equal("stXput", first.Editor.Document.Text);      // typing in the source window still works
        });
    }
}
