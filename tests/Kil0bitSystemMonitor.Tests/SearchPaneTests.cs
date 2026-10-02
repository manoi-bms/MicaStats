using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Search notes pane in a MicaPad window built on the shared UI thread (never shown):
    /// it shares the right column with History, lists the passages the service finds and opens a
    /// picked one at its passage.
    /// </summary>
    public class SearchPaneTests
    {
        private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

        private static void WithWindow(Action<MicaPadWindow, PadTestEnv, NoteSearchService> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            using var service = new NoteSearchService(env.Store, () => SearchSettings.Off);
            var originalService = MicaPadWindow.SearchService;
            MicaPadWindow.SearchService = service;
            var window = new MicaPadWindow(env.Workspace, new AppConfig());
            try
            {
                window.LoadSession();
                test(window, env, service);
            }
            finally
            {
                window.CloseForExit();
                MicaPadWindow.SearchService = originalService;
            }
        });

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static T Wait<T>(Task<T> task)
        {
            while (!task.IsCompleted) Pump();
            return task.Result;
        }

        private static void Wait(Task task)
        {
            while (!task.IsCompleted) Pump();
            task.GetAwaiter().GetResult();
        }

        /// <summary>Ctrl+Shift+H, as the History tests toggle the pane.</summary>
        private static void ToggleHistory(MicaPadWindow window) => Assert.True(window.HandleShortcut(Key.H, CtrlShift));

        /// <summary>Shows one of the window's tabs, as a click on it does.</summary>
        private static void ShowNote(MicaPadWindow window, PadTestEnv env, OpenNote note) =>
            window.SelectTab(env.Workspace.TabsOf(window.WindowId).IndexOf(note));

        [Fact]
        public void Ctrl_Shift_F_opens_the_pane_and_closes_History() => WithWindow((window, env, service) =>
        {
            ToggleHistory(window);
            Assert.Equal(Visibility.Visible, window.HistoryPanel.Visibility);

            window.ToggleSearch();

            Assert.Equal(Visibility.Visible, window.SearchPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.HistoryPanel.Visibility);

            window.ToggleSearch();
            Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
        });

        [Fact]
        public void Opening_History_closes_Search() => WithWindow((window, env, service) =>
        {
            window.ToggleSearch();
            ToggleHistory(window);
            Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
        });

        [Fact]
        public void The_keys_toggle_the_pane_and_Esc_in_the_editor_closes_it() => WithWindow((window, env, service) =>
        {
            Assert.True(window.HandleShortcut(Key.F, CtrlShift));
            Assert.Equal(Visibility.Visible, window.SearchPanel.Visibility);
            Assert.True(window.HandleShortcut(Key.F, CtrlShift));
            Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);

            window.ToggleSearch();
            Assert.True(window.HandleShortcut(Key.Escape, ModifierKeys.None));   // the focus is not in the pane
            Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
            Assert.False(window.HandleShortcut(Key.Escape, ModifierKeys.None));  // nothing left to close: Esc stays the editor's
        });

        [Fact]
        public void A_search_lists_matching_notes_with_their_line() => WithWindow((window, env, service) =>
        {
            var note = env.Workspace.Open.First();
            window.Editor.Document.Text = "# Intro\nfirst\n\n# Net\nthe vpn does not connect";   // headings make two passages
            service.Indexer.SetNote(note.Id, note.Title, window.Editor.Document.Text, DateTime.UtcNow);
            Wait(service.Indexer.WhenIdle());

            window.ToggleSearch();
            window.SearchPanel.QueryBox.Text = "vpn";
            Wait(window.SearchPanel.SearchNow());

            var row = Assert.Single(window.SearchPanel.Rows);
            Assert.Equal(note.Id, row.NoteId);
            Assert.Equal(4, row.FirstLine);
            Assert.False(row.Closed);
            Assert.Equal("Words", window.SearchPanel.StatusText.Text);
        });

        [Fact]
        public void Picking_a_closed_note_reopens_it_with_the_passage_selected() => WithWindow((window, env, service) =>
        {
            var note = env.Workspace.NewNote();
            ShowNote(window, env, note);
            window.Editor.Document.Text = "one\ntwo\n\n# Part\nneedle in here\nafter";
            env.Workspace.NotifyChanged(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            string id = note.Id;
            window.CloseTab(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            service.Indexer.IndexStored(id, "t", DateTime.UtcNow);
            Wait(service.Indexer.WhenIdle());

            window.ToggleSearch();
            window.SearchPanel.QueryBox.Text = "needle";
            Wait(window.SearchPanel.SearchNow());
            var row = Assert.Single(window.SearchPanel.Rows);
            Assert.True(row.Closed);

            window.OpenSearchResult(row);

            Assert.Contains(env.Workspace.Open, n => n.Id == id);
            Assert.Equal("# Part\nneedle in here\nafter", window.Editor.SelectedText.Replace("\r\n", "\n"));
        });

        [Fact]
        public void The_selection_follows_the_text_when_lines_moved() => WithWindow((window, env, service) =>
        {
            var note = env.Workspace.Open.First();
            window.Editor.Document.Text = "# A\na\n# B\nneedle here";
            service.Indexer.SetNote(note.Id, note.Title, window.Editor.Document.Text, DateTime.UtcNow);
            Wait(service.Indexer.WhenIdle());
            window.ToggleSearch();
            window.SearchPanel.QueryBox.Text = "needle";
            Wait(window.SearchPanel.SearchNow());
            var row = Assert.Single(window.SearchPanel.Rows);

            window.Editor.Document.Insert(0, "new top line\n");   // the passage moved down one line
            window.OpenSearchResult(row);

            Assert.Equal("# B\nneedle here", window.Editor.SelectedText.Replace("\r\n", "\n"));
        });
    }
}
