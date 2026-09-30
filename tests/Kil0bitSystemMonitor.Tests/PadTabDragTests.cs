using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Xunit;

using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Reordering tabs by dragging, and keeping the order.</summary>
    public class PadTabDragTests
    {
        private static readonly (double Left, double Width)[] Layout = { (0, 100), (100, 100), (200, 100) };

        [Fact]
        public void Move_tab_reorders_and_the_order_survives_a_restart() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var a = env.Workspace.NewNote();
            var b = env.Workspace.NewNote();
            var c = env.Workspace.NewNote();
            foreach (var (note, text) in new[] { (a, "a"), (b, "b"), (c, "c") }) PadTestEnv.Type(env.Workspace, note, text);
            env.Workspace.FlushPending();              // notes with text on disk, as a real session has

            env.Workspace.MoveTab(c, 0);
            env.Workspace.SaveSession();
            env.Flush();

            Assert.Equal(new[] { c, a, b }, env.Workspace.Open.ToArray());
            var again = env.NewWorkspace();
            again.Restore();
            Assert.Equal(new[] { c.Id, a.Id, b.Id }, again.Open.Select(n => n.Id).ToArray());
        });

        [Fact]
        public void Move_tab_clamps_the_index() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var a = env.Workspace.NewNote();
            var b = env.Workspace.NewNote();
            env.Workspace.MoveTab(a, 99);
            Assert.Equal(new[] { b, a }, env.Workspace.Open.ToArray());
            env.Workspace.MoveTab(a, -3);
            Assert.Equal(new[] { a, b }, env.Workspace.Open.ToArray());
        });

        [Fact]
        public void A_drag_moves_the_tab_live_and_saves_once_on_drop() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            window.NewTab();
            var drag = window.TabDrag;

            drag.Press(first, new Point(50, 10));
            Assert.True(drag.MoveTo(new Point(170, 12), Layout));
            Assert.True(drag.IsDragging);
            Assert.Equal(1, env.Workspace.Open.IndexOf(first));        // the others made room already

            drag.MoveTo(new Point(290, 12), Layout);
            Assert.Equal(2, env.Workspace.Open.IndexOf(first));

            drag.Release();
            Assert.False(drag.IsDragging);
            Assert.Equal(env.Workspace.Open.Select(n => n.Id), env.Workspace.Session.OpenNoteIds);
        });

        [Fact]
        public void A_move_below_the_threshold_does_not_drag() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            var drag = window.TabDrag;

            drag.Press(first, new Point(50, 10));
            Assert.False(drag.MoveTo(new Point(51, 11), Layout));
            drag.Release();

            Assert.Equal(0, env.Workspace.Open.IndexOf(first));
            Assert.False(drag.IsDragging);
        });

        [Fact]
        public void A_moved_tab_keeps_its_document() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.NewTab();
            var shown = env.Workspace.Open[env.Workspace.Open.Count - 1];   // the new tab, now shown
            window.Editor.Document.Text = "kept";
            window.ToggleBookmark();                       // a bookmark is forgotten if the tab is treated as closed
            var document = window.Editor.Document;

            env.Workspace.MoveTab(shown, 0);
            window.SelectTab(1);                           // away ...
            window.SelectTab(0);                           // ... and back to the moved tab

            Assert.Same(document, window.Editor.Document);
            Assert.Equal("kept", window.Editor.Document.Text);
            Assert.Equal(new[] { 1 }, window.BookmarkLines.ToArray());
            Assert.Equal(0, env.Workspace.Open.IndexOf(shown));
        });
    }
}
