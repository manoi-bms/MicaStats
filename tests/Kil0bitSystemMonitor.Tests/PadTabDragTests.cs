using System.Linq;
using System.Windows;
using System.Windows.Input;
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
        public void A_move_with_the_button_up_ends_the_press() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            var drag = window.TabDrag;

            drag.Press(first, new Point(50, 10));
            Assert.False(drag.PointerMoved(new Point(51, 11), buttonDown: false, () => Layout));   // released outside the strip

            Assert.False(drag.IsPressed);
            Assert.False(drag.IsDragging);
            Assert.False(drag.MoveTo(new Point(170, 12), Layout));   // a later jitter, e.g. on another tab's x
            Assert.Equal(0, env.Workspace.Open.IndexOf(first));
        });

        [Fact]
        public void A_move_with_the_button_up_mid_drag_drops_and_saves() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            var drag = window.TabDrag;
            drag.Press(first, new Point(50, 10));
            Assert.True(drag.PointerMoved(new Point(170, 12), buttonDown: true, () => Layout));

            Assert.False(drag.PointerMoved(new Point(172, 12), buttonDown: false, () => Layout));

            Assert.False(drag.IsDragging);
            Assert.Equal(1, env.Workspace.Open.IndexOf(first));
            Assert.Equal(env.Workspace.Open.Select(n => n.Id), env.Workspace.Session.OpenNoteIds);
        });

        [Fact]
        public void A_press_anywhere_on_the_strip_forgets_an_old_press() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            var drag = window.TabDrag;
            drag.Press(first, new Point(50, 10));

            // A press the tab does not handle, such as one on another tab's x button, still tunnels through the strip.
            window.TabStrip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            });

            Assert.False(drag.IsPressed);
            Assert.False(drag.MoveTo(new Point(170, 12), Layout));
            Assert.Equal(0, env.Workspace.Open.IndexOf(first));
        });

        [Fact]
        public void A_second_press_on_another_tab_replaces_the_first() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            window.NewTab();
            var third = env.Workspace.Open[2];
            var drag = window.TabDrag;

            drag.Press(first, new Point(50, 10));
            drag.StripPressed();
            drag.Press(third, new Point(250, 10));
            Assert.True(drag.MoveTo(new Point(20, 12), Layout));
            drag.Release();

            Assert.Equal(0, env.Workspace.Open.IndexOf(third));
            Assert.Equal(1, env.Workspace.Open.IndexOf(first));
        });

        [Fact]
        public void The_order_is_saved_exactly_once_per_drop() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            env.Workspace.NewNote();
            env.Workspace.NewNote();
            env.Workspace.NewNote();
            var first = env.Workspace.Open[0];
            int saves = 0;
            // The window wires the drop callback to PadWorkspace.SaveSession; count its calls here.
            var drag = new TabDragController(new System.Windows.Controls.ItemsControl(), new System.Windows.Controls.ScrollViewer(),
                () => env.Workspace.Open, (note, index) => env.Workspace.MoveTab(note, index), () => saves++);

            drag.Press(first, new Point(50, 10));
            drag.MoveTo(new Point(170, 12), Layout);
            drag.MoveTo(new Point(290, 12), Layout);
            drag.Release();
            drag.Release();                                   // the capture loss that follows the button up
            Assert.Equal(1, saves);

            drag.Press(first, new Point(250, 10));
            drag.MoveTo(new Point(20, 12), Layout);
            Assert.False(drag.PointerMoved(new Point(20, 12), buttonDown: false, () => Layout));
            drag.Release();
            Assert.Equal(2, saves);

            drag.Press(first, new Point(50, 10));             // a click without a drag saves nothing
            drag.Release();
            Assert.Equal(2, saves);
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
