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
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

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
        public void The_locked_folder_notice_shows_once() => WithWindow((window, env, config) =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "micapad-locked-notice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var started = new System.Collections.Generic.List<System.Diagnostics.ProcessStartInfo>();
            var original = MicaPadWindow.StartExplorer;
            MicaPadWindow.StartExplorer = started.Add;
            try
            {
                env.Store.LockedFolder = folder;

                window.ShowLockedFolderNotice();

                Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
                Assert.Equal("MicaPad could not decrypt the notes saved before on this Windows account, so they were moved to "
                             + folder + ". Nothing was deleted.", window.InfoText.Text);
                Assert.Equal("Show folder", window.InfoPrimary.Content);
                Assert.Equal(Visibility.Visible, window.InfoPrimary.Visibility);

                window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Equal("/select,\"" + folder + "\"", Assert.Single(started).Arguments);
                Assert.DoesNotContain("no longer at", window.InfoText.Text, StringComparison.Ordinal);
                Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);

                window.ShowLockedFolderNotice();
                Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
                Assert.Null(env.Store.LockedFolder);
            }
            finally
            {
                MicaPadWindow.StartExplorer = original;
                Directory.Delete(folder, recursive: true);
            }
        });

        [Fact]
        public void No_notice_when_the_store_opened_normally() => WithWindow((window, env, config) =>
        {
            window.ShowLockedFolderNotice();

            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
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
        public void A_closed_note_that_cannot_be_reopened_says_so() => WithWindow((window, env, config) =>
        {
            window.Editor.Document.Insert(0, "keep this");
            var note = env.Workspace.Active!;
            window.HandleShortcut(Key.W, ModifierKeys.Control);
            env.Flush();

            using (new FileStream(env.Store.CurrentPath(note.Id), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.True(window.HandleShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift));

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("That note could not be reopened right now, so it was left as it is.", window.InfoText.Text);

            window.HandleShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal("keep this", window.Editor.Document.Text);
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
        public void Opening_a_file_shows_it_and_ctrl_s_writes_it() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("note.txt");
            File.WriteAllText(path, "from disk");

            window.OpenPath(path);
            Assert.Equal("from disk", window.Editor.Document.Text);
            Assert.Equal("UTF-8", window.EncodingButton.Content);
            Assert.Equal("CRLF", window.EolButton.Content);

            window.Editor.Document.Insert(window.Editor.Document.TextLength, "!");
            Assert.True(window.HandleShortcut(Key.S, ModifierKeys.Control));

            Assert.Equal("from disk!", File.ReadAllText(path));
            Assert.False(env.Workspace.Active!.HasUnsavedEdits);
        });

        [Fact]
        public void Opening_a_file_whose_closed_note_cannot_be_read_says_so() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("draft.txt");
            File.WriteAllText(path, "v1");
            window.OpenPath(path);
            window.Editor.Document.Insert(2, " plus edits");
            var note = env.Workspace.Active!;
            window.HandleShortcut(Key.W, ModifierKeys.Control);
            env.Flush();

            using (new FileStream(env.Store.CurrentPath(note.Id), FileMode.Open, FileAccess.Read, FileShare.None))
                window.OpenPath(path);

            Assert.Equal("draft.txt has unsaved edits in a note that cannot be read right now, so it was not opened. Try again in a moment.",
                window.InfoText.Text);
            Assert.DoesNotContain(env.Workspace.Open, n => n.Meta.IsFileBacked);
        });

        [Fact]
        public void An_outside_change_to_an_edited_file_asks_and_reload_takes_it() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("shared.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            File.WriteAllText(path, "theirs, longer");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            window.CheckShownNoteOnDisk();

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("Reload from disk", window.InfoPrimary.Content);

            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("theirs, longer", window.Editor.Document.Text);
            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
            Assert.False(env.Workspace.Active!.HasUnsavedEdits);
        });

        [Fact]
        public void A_character_the_file_cannot_hold_offers_utf8() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("latin.txt");
            File.WriteAllText(path, "plain");
            window.OpenPath(path);
            env.Workspace.SetEncoding(env.Workspace.Active!, PadEncoding.Ansi, 1252);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, " \u0E2A");

            window.HandleShortcut(Key.S, ModifierKeys.Control);

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("Save as UTF-8", window.InfoPrimary.Content);
            Assert.Equal("plain", File.ReadAllText(path));

            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("plain \u0E2A", File.ReadAllText(path));
        });

        [Fact]
        public void Ctrl_s_after_an_outside_change_asks_and_overwrite_writes() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("race.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            File.WriteAllText(path, "theirs, longer");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            Assert.True(window.HandleShortcut(Key.S, ModifierKeys.Control));

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("race.txt changed on disk since it was opened.", window.InfoText.Text);
            Assert.Equal("Overwrite", window.InfoPrimary.Content);
            Assert.Equal("Reload from disk", window.InfoSecondary.Content);
            Assert.Equal("theirs, longer", File.ReadAllText(path));

            window.CheckShownNoteOnDisk();   // activation: the same question stays, Overwrite included
            Assert.Equal("Overwrite", window.InfoPrimary.Content);

            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("mine base", File.ReadAllText(path));
            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
            Assert.False(env.Workspace.Active!.HasUnsavedEdits);
        });

        [Fact]
        public void Ctrl_s_on_a_stand_in_copy_says_the_file_was_not_read_rather_than_changed() => WithWindow((window, env, config) =>
        {
            const string Question = "offline.txt could not be read when this tab was restored, so saving could replace text you have not seen.";
            string path = env.FileOf("offline.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            env.Workspace.Active!.Meta.SourceStamp = SourceStamp.Unverified;   // as after a restore that could not read the file

            Assert.True(window.HandleShortcut(Key.S, ModifierKeys.Control));

            Assert.Equal(Question, window.InfoText.Text);
            Assert.Equal("Overwrite", window.InfoPrimary.Content);
            Assert.Equal("Reload from disk", window.InfoSecondary.Content);
            Assert.Equal("base", File.ReadAllText(path));

            window.CheckShownNoteOnDisk();   // activation: the same question stays
            Assert.Equal(Question, window.InfoText.Text);
            Assert.Equal("Overwrite", window.InfoPrimary.Content);
        });

        [Fact]
        public void The_activation_check_on_a_stand_in_copy_says_the_tab_may_not_match_the_file() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("offline.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            env.Workspace.Active!.Meta.SourceStamp = SourceStamp.Unverified;

            window.CheckShownNoteOnDisk();

            Assert.Equal("offline.txt could not be read when this tab was restored, so this tab may not match the file.",
                window.InfoText.Text);
            Assert.Equal("Reload from disk", window.InfoPrimary.Content);
            Assert.Equal("Keep mine", window.InfoSecondary.Content);
        });

        [Fact]
        public void Overwrite_asks_again_when_the_outside_version_cannot_be_kept() => WithWindow((window, env, config) =>
        {
            byte[] binary = { 0x4D, 0x5A, 0x00, 0x01 };
            string path = env.FileOf("turned-binary.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            File.WriteAllBytes(path, binary);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            window.HandleShortcut(Key.S, ModifierKeys.Control);
            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // Overwrite

            Assert.Equal("turned-binary.txt changed on disk, and that version cannot be kept in History: it does not look like text. Overwrite it anyway?",
                window.InfoText.Text);
            Assert.Equal("Overwrite anyway", window.InfoPrimary.Content);
            Assert.Equal("Reload from disk", window.InfoSecondary.Content);
            Assert.Equal(binary, File.ReadAllBytes(path));

            window.CheckShownNoteOnDisk();   // activation: the same question stays
            Assert.Equal("Overwrite anyway", window.InfoPrimary.Content);

            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // Overwrite anyway

            Assert.Equal("mine base", File.ReadAllText(path));
            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
        });

        [Fact]
        public void Another_message_for_the_same_note_does_not_hide_the_disk_question() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("broken.txt");
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0xFF, (byte)'b' });
            window.OpenPath(path);
            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);          // the undecodable-bytes warning
            Assert.Equal(Visibility.Collapsed, window.InfoPrimary.Visibility);

            window.Editor.Document.Insert(0, "mine ");
            File.WriteAllText(path, "theirs, longer");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            window.CheckShownNoteOnDisk();

            Assert.Equal("broken.txt changed on disk.", window.InfoText.Text);
            Assert.Equal("Reload from disk", window.InfoPrimary.Content);
        });

        [Fact]
        public void A_file_whose_folder_is_gone_too_is_unreachable_and_not_offered_keep_as_note() => WithWindow((window, env, config) =>
        {
            string folder = env.FileOf("share");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "remote.txt");
            File.WriteAllText(path, "text");
            window.OpenPath(path);
            Directory.Delete(folder, recursive: true);

            window.CheckShownNoteOnDisk();

            Assert.Equal("remote.txt cannot be reached right now.", window.InfoText.Text);
            Assert.Equal(Visibility.Collapsed, window.InfoPrimary.Visibility);
            Assert.Equal(Visibility.Collapsed, window.InfoSecondary.Visibility);
            Assert.True(env.Workspace.Active!.Meta.IsFileBacked);

            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "text");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            window.CheckShownNoteOnDisk();   // back, and changed: reloaded without edits of ours, notice gone

            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
        });

        [Fact]
        public void A_failed_reload_says_why_in_words() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("vanishing.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            File.WriteAllText(path, "theirs, longer");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            window.CheckShownNoteOnDisk();
            File.Delete(path);

            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // Reload from disk

            Assert.Equal("Could not reload vanishing.txt: the file is gone.", window.InfoText.Text);
            Assert.Equal("mine base", window.Editor.Document.Text);
        });

        [Fact]
        public void Find_counts_matches_f3_walks_them_and_replace_all_is_one_undo_step() => WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "cat concat cat";

            Assert.True(window.HandleShortcut(Key.H, ModifierKeys.Control));
            Assert.True(window.FindBar.IsOpen);
            window.FindBar.FindBox.Text = "cat";
            window.FindBar.Recompute();
            Assert.Equal("3 results", window.FindBar.CountText.Text);

            window.Editor.CaretOffset = 0;
            window.HandleShortcut(Key.F3, ModifierKeys.None);
            Assert.Equal(0, window.Editor.SelectionStart);
            Assert.Equal(3, window.Editor.SelectionLength);
            Assert.Equal("1 of 3", window.FindBar.CountText.Text);
            window.HandleShortcut(Key.F3, ModifierKeys.None);
            Assert.Equal(7, window.Editor.SelectionStart);

            window.FindBar.ReplaceBox.Text = "dog";
            window.FindBar.ReplaceAllButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("dog condog dog", window.Editor.Document.Text);
            Assert.Equal("3 replaced", window.FindBar.CountText.Text);

            window.Editor.Undo();
            Assert.Equal("cat concat cat", window.Editor.Document.Text);

            window.HandleShortcut(Key.Escape, ModifierKeys.None);
            Assert.False(window.FindBar.IsOpen);
        });

        [Fact]
        public void A_search_that_times_out_says_so_on_f3_too() => WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = new string('x', 40);
            window.HandleShortcut(Key.F, ModifierKeys.Control);
            window.FindBar.RegexToggle.IsChecked = true;
            window.FindBar.FindBox.Text = "(x+x+)+y";
            window.FindBar.Recompute();
            Assert.Equal(FindReplaceEngine.TimedOutMessage, window.FindBar.CountText.Text);

            window.HandleShortcut(Key.F3, ModifierKeys.None);

            Assert.Equal(FindReplaceEngine.TimedOutMessage, window.FindBar.CountText.Text);
            Assert.Equal(0, window.Editor.SelectionLength);
        });

        [Fact]
        public void Go_to_line_moves_the_caret_and_clamps() => WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "a\nb\nc";

            window.GoToLine(2);
            Assert.Equal(2, window.Editor.TextArea.Caret.Line);

            window.GoToLine(99);
            Assert.Equal(3, window.Editor.TextArea.Caret.Line);
        });

        [Fact]
        public void History_lists_versions_and_restoring_one_can_be_undone() => WithWindow((window, env, config) =>
        {
            window.Editor.Document.Insert(0, "first draft");
            env.Clock.Advance(61);
            env.Workspace.Tick();                       // a pause snapshot of "first draft"
            window.Editor.Document.Text = "rewritten";

            Assert.True(window.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Equal(Visibility.Visible, window.HistoryPanel.Visibility);
            var row = Assert.Single(window.HistoryPanel.Rows);

            window.HistoryPanel.Versions.SelectedItem = row;
            Assert.Equal(Visibility.Visible, window.PreviewPanel.Visibility);
            Assert.Equal("first draft", window.PreviewEditor.Text);

            window.RestoreButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("first draft", window.Editor.Document.Text);
            Assert.Equal(Visibility.Collapsed, window.PreviewPanel.Visibility);

            window.HistoryPanel.Versions.SelectedItem = null;
            Assert.Equal(2, window.HistoryPanel.Rows.Count);
            Assert.Contains(window.HistoryPanel.Rows, r => env.Workspace.ReadSnapshot(r.Snapshot) == "rewritten");

            window.Editor.Undo();
            Assert.Equal("rewritten", window.Editor.Document.Text);
        });

        [Fact]
        public void Settings_has_a_micapad_section()
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "SettingsWindow.xaml"));

            Assert.Contains("Tag=\"MicaPad\"", xaml);
            Assert.Contains("x:Name=\"PadSection\"", xaml);
        }

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
