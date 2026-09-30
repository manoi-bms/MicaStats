using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>More than one MicaPad window, in the workspace: whose tabs are whose, moving, closing, routing, restarting.</summary>
    public class PadWorkspaceWindowsTests : IDisposable
    {
        private readonly PadTestEnv _env = new();

        private PadWorkspace Ws => _env.Workspace;

        private string First => Ws.Windows[0].Id;

        public void Dispose() => _env.Dispose();

        [Fact]
        public void A_workspace_starts_with_one_window_that_new_notes_go_to()
        {
            var note = Ws.NewNote();

            var window = Assert.Single(Ws.Windows);
            Assert.Equal(window.Id, note.WindowId);
            Assert.Equal(new[] { note }, Ws.TabsOf(window.Id));
            Assert.Same(note, Ws.ActiveIn(window.Id));
            Assert.Same(note, Ws.Active);
        }

        [Fact]
        public void Each_window_has_its_own_tabs_and_active_tab()
        {
            var a = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var b = Ws.NewNote(second.Id);
            var c = Ws.NewNote(second.Id);

            Assert.Equal(new[] { a }, Ws.TabsOf(First));
            Assert.Equal(new[] { b, c }, Ws.TabsOf(second.Id));
            Assert.Same(a, Ws.ActiveIn(First));
            Assert.Same(c, Ws.ActiveIn(second.Id));
            Assert.True(a.IsActive);                    // one active tab per window
            Assert.True(c.IsActive);
            Assert.False(b.IsActive);
            Assert.Equal(3, Ws.Open.Count);             // still one list of open notes
            Assert.Equal(second.Id, Ws.MostRecentWindowId);
        }

        [Fact]
        public void Move_tab_reorders_within_its_window_only()
        {
            var a = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            var b = Ws.NewNote(First);                  // after a: Open is a, b, x
            var y = Ws.NewNote(second.Id);              // after x: a, b, x, y
            var c = Ws.NewNote(First);                  // after b: a, b, c, x, y

            Ws.MoveTab(c, 0);

            Assert.Equal(new[] { c, a, b }, Ws.TabsOf(First));
            Assert.Equal(new[] { x, y }, Ws.TabsOf(second.Id));
            Ws.MoveTab(c, 99);                          // clamped to this window's last tab
            Assert.Equal(new[] { a, b, c }, Ws.TabsOf(First));
        }

        [Fact]
        public void Moving_a_note_to_another_window_keeps_it_open_and_makes_it_active_there()
        {
            var a = Ws.NewNote();
            var b = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, b, "moving");

            Ws.MoveToWindow(b, second.Id);

            Assert.Equal(second.Id, b.WindowId);
            Assert.Equal(new[] { a }, Ws.TabsOf(First));
            Assert.Equal(new[] { x, b }, Ws.TabsOf(second.Id));
            Assert.Same(a, Ws.ActiveIn(First));         // the neighbour took over
            Assert.True(a.IsActive);
            Assert.Same(b, Ws.ActiveIn(second.Id));
            Assert.False(x.IsActive);
            Assert.Equal(3, Ws.Open.Count);
            Assert.Equal("moving", b.TextProvider());
            Assert.Empty(Ws.ClosedNotes());
        }

        [Fact]
        public void Closing_a_window_moves_its_tabs_to_the_most_recently_active_other_window()
        {
            var a = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            var y = Ws.NewNote(second.Id);
            var third = Ws.NewWindow(First);
            Ws.NewNote(third.Id);
            Ws.ActivateWindow(First);
            Ws.ActivateWindow(second.Id);               // most recent first: second, first, third

            string? into = Ws.CloseWindow(second.Id);

            Assert.Equal(First, into);
            Assert.Equal(new[] { First, third.Id }, Ws.Windows.Select(w => w.Id));
            Assert.Equal(new[] { a, x, y }, Ws.TabsOf(First));
            Assert.Same(a, Ws.ActiveIn(First));         // the tabs arrive at the end; the active tab stays
            Assert.False(y.IsActive);
            Assert.Equal(4, Ws.Open.Count);
            Assert.Empty(Ws.ClosedNotes());             // no note is closed by closing a window
            Assert.Equal(new[] { First, third.Id }, Ws.ActivationOrder);
        }

        [Fact]
        public void Closing_a_window_keeps_unsaved_file_edits_in_the_other_window()
        {
            string path = _env.FileOf("draft.txt");
            File.WriteAllText(path, "on disk");
            Ws.NewNote();
            var second = Ws.NewWindow(First);
            var file = Ws.OpenFile(path, second.Id).Note!;
            PadTestEnv.Type(Ws, file, "on disk plus my edit");
            Assert.True(file.HasUnsavedEdits);

            Ws.CloseWindow(second.Id);
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            Assert.Equal(First, file.WindowId);
            Assert.True(file.HasUnsavedEdits);
            Assert.Equal("on disk plus my edit", file.TextProvider());
            Assert.Equal("on disk plus my edit", _env.DiskText(file));      // MicaPad's copy has the edit
            Assert.Equal("on disk", File.ReadAllText(path));                // the file itself is untouched

            var restored = _env.NewWorkspace();
            restored.Restore();
            Assert.Contains(restored.TabsOf(restored.Windows[0].Id), n => n.Id == file.Id && n.TextProvider() == "on disk plus my edit");
        }

        [Fact]
        public void The_only_window_is_not_closed_this_way()
        {
            var a = Ws.NewNote();

            Assert.Null(Ws.CloseWindow(First));
            Assert.Single(Ws.Windows);
            Assert.Equal(new[] { a }, Ws.TabsOf(First));
        }

        [Fact]
        public void Windows_come_back_after_a_restart_with_their_tabs_active_tab_and_place()
        {
            var a = Ws.NewNote();
            PadTestEnv.Type(Ws, a, "a");
            var b = Ws.NewNote();
            PadTestEnv.Type(Ws, b, "b");
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, x, "x");
            Ws.SetActive(a);
            second.Left = 300;
            second.Top = 200;
            second.Width = 640;
            second.Height = 480;
            second.Zoom = 1.5;
            second.AlwaysOnTop = true;
            Ws.ActivateWindow(First);
            _env.Clock.Advance(1);
            Ws.ActivateWindow(second.Id);               // the second was used last
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            restored.Restore();

            Assert.Equal(new[] { First, second.Id }, restored.Windows.Select(w => w.Id));
            Assert.Equal(new[] { a.Id, b.Id }, restored.TabsOf(First).Select(n => n.Id));
            Assert.Equal(new[] { x.Id }, restored.TabsOf(second.Id).Select(n => n.Id));
            Assert.Equal(a.Id, restored.ActiveIn(First)!.Id);
            var place = restored.WindowStateOf(second.Id)!;
            Assert.Equal(300, place.Left);
            Assert.Equal(200, place.Top);
            Assert.Equal(640, place.Width);
            Assert.Equal(480, place.Height);
            Assert.Equal(1.5, place.Zoom);
            Assert.True(place.AlwaysOnTop);
            Assert.Equal(second.Id, restored.MostRecentWindowId);
        }

        [Fact]
        public void A_note_that_cannot_be_read_at_restore_returns_to_its_own_window()
        {
            var a = Ws.NewNote();
            PadTestEnv.Type(Ws, a, "a");
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, x, "x");
            var y = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, y, "y");
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            using (new FileStream(_env.Store.CurrentPath(y.Id), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                restored.Restore();
                Assert.Equal(new[] { x.Id }, restored.TabsOf(second.Id).Select(n => n.Id));
                Assert.True(restored.FlushAll(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal(new[] { x.Id, y.Id }, _env.Store.LoadSession().Windows![1].NoteIds);
        }

        [Fact]
        public void Opening_a_file_goes_to_the_given_window_and_stays_there()
        {
            string path = _env.FileOf("notes.txt");
            File.WriteAllText(path, "text");
            Ws.NewNote();
            var second = Ws.NewWindow(First);

            var opened = Ws.OpenFile(path, second.Id);
            var again = Ws.OpenFile(path, First);

            Assert.Equal(second.Id, opened.Note!.WindowId);
            Assert.Equal(OpenFileStatus.AlreadyOpen, again.Status);
            Assert.Same(opened.Note, again.Note);
            Assert.Equal(second.Id, again.Note!.WindowId);   // the window layer brings that window forward
        }

        [Fact]
        public void A_file_is_routed_to_the_window_showing_it()
        {
            string path = _env.FileOf("app.log");
            File.WriteAllText(path, "log");
            Ws.NewNote();
            var second = Ws.NewWindow(First);
            Ws.OpenFile(path, second.Id);
            Ws.ActivateWindow(First);

            Assert.Equal(second.Id, Ws.RouteFile(path.ToUpperInvariant()));
            Assert.Equal(First, Ws.RouteFile(_env.FileOf("other.txt")));
            Assert.Equal(First, Ws.RouteFile(null));
        }

        [Fact]
        public void Reopening_a_closed_note_goes_to_the_window_that_asked()
        {
            var a = Ws.NewNote();
            PadTestEnv.Type(Ws, a, "keep");
            var second = Ws.NewWindow(First);
            Ws.NewNote(second.Id);
            Ws.Close(a);

            var back = Ws.Reopen(a.Id, second.Id)!;

            Assert.Equal(second.Id, back.WindowId);
            Assert.Same(back, Ws.ActiveIn(second.Id));
            Assert.Empty(Ws.TabsOf(First));
        }

        [Fact]
        public void A_window_tab_list_follows_with_moves_not_rebuilds()
        {
            var meta = NoteStore.NewMeta(DateTime.UtcNow, 1, null);
            var a = new OpenNote(meta, "");
            var b = new OpenNote(NoteStore.NewMeta(DateTime.UtcNow, 2, null), "");
            var c = new OpenNote(NoteStore.NewMeta(DateTime.UtcNow, 3, null), "");
            var tabs = new WindowTabs();
            tabs.Sync(new[] { a, b, c });
            var actions = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)tabs).CollectionChanged += (s, e) => actions.Add(e.Action);

            tabs.Sync(new[] { c, a });

            Assert.Equal(new[] { c, a }, tabs);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Move }, actions);
        }
    }
}
