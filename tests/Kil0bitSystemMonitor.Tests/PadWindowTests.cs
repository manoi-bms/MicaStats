using System;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The MicaPad window, built for real on the shared UI thread but never shown. These catch
    /// XAML that does not load and wiring that does not connect, which no pure test can.
    /// </summary>
    public class PadWindowTests
    {
        private static void WithWindow(Action<MicaPadWindow, PadTestEnv, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var window = new MicaPadWindow(env.Workspace, config);
            try
            {
                window.LoadSession();
                test(window, env, config);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        [Fact]
        public void The_window_starts_with_one_empty_note() => WithWindow((window, env, config) =>
        {
            Assert.Single(env.Workspace.Open);
            Assert.Equal("", window.Editor.Document.Text);
            Assert.Equal("MicaPad — Untitled 1", window.Title);
        });

        [Fact]
        public void Typing_reaches_the_workspace_and_each_tab_keeps_its_own_undo() => WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.Editor.Document.Insert(0, "hello");
            Assert.Equal("hello", first.TextProvider());
            Assert.True(env.Workspace.HasPendingChanges(first));

            Assert.True(window.HandleShortcut(Key.N, ModifierKeys.Control));
            Assert.Equal(2, env.Workspace.Open.Count);
            window.Editor.Document.Insert(0, "second");

            window.SelectTab(0);
            Assert.Equal("hello", window.Editor.Document.Text);
            Assert.True(window.Editor.Document.UndoStack.CanUndo);
            Assert.Same(first, env.Workspace.Active);
        });

        [Fact]
        public void Closing_the_last_tab_leaves_a_fresh_empty_note() => WithWindow((window, env, config) =>
        {
            var only = env.Workspace.Open[0];

            Assert.True(window.HandleShortcut(Key.W, ModifierKeys.Control));

            var fresh = Assert.Single(env.Workspace.Open);
            Assert.NotSame(only, fresh);
            Assert.Equal("", window.Editor.Document.Text);
        });

        [Fact]
        public void Zoom_scales_the_editor_font_and_ctrl_0_resets_it() => WithWindow((window, env, config) =>
        {
            window.HandleShortcut(Key.OemPlus, ModifierKeys.Control);
            Assert.Equal(14 * 1.1, window.Editor.FontSize, 3);

            window.HandleShortcut(Key.D0, ModifierKeys.Control);
            Assert.Equal(14, window.Editor.FontSize, 3);
        });

        [Fact]
        public void Alt_z_toggles_word_wrap_through_the_config() => WithWindow((window, env, config) =>
        {
            Assert.True(window.Editor.WordWrap);

            Assert.True(window.HandleShortcut(Key.Z, ModifierKeys.Alt));

            Assert.False(config.PadWordWrap);
            Assert.False(window.Editor.WordWrap);
        });

        [Fact]
        public void An_unknown_key_is_left_to_the_editor() => WithWindow((window, env, config) =>
        {
            Assert.False(window.HandleShortcut(Key.Q, ModifierKeys.Control));
        });

        [Fact]
        public void The_micapad_icon_is_a_seven_size_ico()
        {
            byte[] ico = File.ReadAllBytes(Path.Combine(RepoRoot(), "Assets", "micapad.ico"));

            Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
            Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
            Assert.Equal(7, BitConverter.ToUInt16(ico, 4));
        }

        /// <summary>Walks up from the test binaries to the folder holding the app project.</summary>
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Kil0bitSystemMonitor.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
