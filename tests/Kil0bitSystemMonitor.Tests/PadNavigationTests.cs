using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Pad;
using Xunit;
using Size = System.Windows.Size;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Kil0bitSystemMonitor.Tests
{
    public class PadNavigationTests
    {
        [Fact]
        public void Finding_and_cancelling_preserve_the_note_document_caret_and_selection() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Insert(0, "Current note text");
            window.NewTab();
            window.Editor.Document.Insert(0, "Another note text");
            env.Workspace.FlushAll(TimeSpan.FromSeconds(5));
            env.Flush();
            window.SelectTab(0);
            var original = window.Editor.Document;
            window.Editor.Select(2, 7);
            int caret = window.Editor.CaretOffset;
            window.PrepareOpenNotes();
            window.NotesPicker.SearchInput.Text = "Another";

            Assert.Same(original, window.Editor.Document);
            Assert.Equal("rrent n", window.Editor.SelectedText);
            window.NotesPicker.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
            Assert.Same(original, window.Editor.Document);
            Assert.Equal(caret, window.Editor.CaretOffset);
            Assert.Equal(7, window.Editor.SelectionLength);
            Assert.Empty(window.NotesPicker.NotesList.Items);
        });

        [Fact]
        public void Confirming_a_result_uses_the_existing_document_and_keeps_undo() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.ActiveIn(window.WindowId)!;
            window.Editor.Document.Insert(0, "alpha");
            var firstDocument = window.Editor.Document;
            window.NewTab();
            window.Editor.Document.Insert(0, "beta");
            window.PrepareOpenNotes();
            window.NotesPicker.SearchInput.Text = first.Title;
            window.NotesPicker.NotesList.SelectedItem = window.NotesPicker.NotesList.Items.Cast<OpenNoteRow>().Single(row => row.Note == first);
            window.NotesPicker.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);

            Assert.Same(firstDocument, window.Editor.Document);
            Assert.Same(first, env.Workspace.ActiveIn(window.WindowId));
            Assert.Empty(window.NotesPicker.NotesList.Items);
            window.Editor.Undo();
            Assert.Equal("", firstDocument.Text);
        });

        [Fact]
        public void The_button_count_tracks_add_close_and_the_menu_documents_the_shortcut() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.Equal("1", window.OpenNotesCount.Text);
            window.NewTab();
            Assert.Equal("2", window.OpenNotesCount.Text);
            window.HandleShortcut(System.Windows.Input.Key.W, System.Windows.Input.ModifierKeys.Control);
            Assert.Equal("1", window.OpenNotesCount.Text);
            Assert.Equal("Ctrl+P", PadMenuTests.ItemOf(window.BuildMainMenu(), "Open notes").InputGestureText);
        });

        [Fact]
        public void The_picker_lists_other_windows_and_selection_presents_the_owner() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var state = env.Workspace.NewWindow(window.WindowId);
            var otherNote = env.Workspace.NewNote(state.Id);
            var other = new MicaPadWindow(env.Workspace, config, state.Id);
            var oldShow = MicaPadWindow.ShowWindow;
            MicaPadWindow? presented = null;
            MicaPadWindow.ShowWindow = shown => presented = shown;
            try
            {
                other.LoadSession();
                other.Editor.Document.Insert(0, "other window");
                var original = window.Editor.Document;
                window.PrepareOpenNotes();
                var row = window.NotesPicker.NotesList.Items.Cast<OpenNoteRow>().Single(item => item.Note == otherNote);
                Assert.Contains("Other window", row.Detail);
                window.ChooseOpenNote(otherNote);
                Assert.Same(other, presented);
                Assert.Same(original, window.Editor.Document);
                Assert.Same(otherNote, env.Workspace.ActiveIn(state.Id));
            }
            finally
            {
                MicaPadWindow.ShowWindow = oldShow;
                other.CloseForExit();
            }
        });

        [Fact]
        public void Closing_the_window_releases_the_picker_rows() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.PrepareOpenNotes();
            Assert.NotEmpty(window.NotesPicker.NotesList.Items);
            window.CloseForExit();
            Assert.Empty(window.NotesPicker.NotesList.Items);
            Assert.Equal("", window.NotesPicker.SearchInput.Text);
        });

        [Theory]
        [InlineData("Dark", 900, 640)]
        [InlineData("Light", 900, 640)]
        [InlineData("Dark", 420, 260)]
        [InlineData("Light", 420, 260)]
        public void Many_notes_remain_navigable_at_normal_and_minimum_sizes(string theme, int width, int height) => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            config.PadTheme = theme;
            window.Width = width;
            window.Height = height;
            var active = env.Workspace.ActiveIn(window.WindowId)!;
            env.Workspace.Rename(active, "Weekly priorities and follow-up tasks");
            window.Editor.Document.Insert(0, "# Weekly priorities\n\nA calm writing surface with quick access to every open note.\n\n- [ ] Prepare the planning notes\n- [ ] Follow up on decisions");
            string[] titles = { "Project roadmap", "Research and useful references", "Meeting decisions", "readme.md", "Ideas for next week", "Customer follow-up" };
            for (int i = 1; i < 24; i++)
            {
                var note = env.Workspace.NewNote(window.WindowId);
                env.Workspace.Rename(note, titles[(i - 1) % titles.Length] + (i < 7 ? "" : " " + i));
            }
            window.SelectTab(0);
            var root = Assert.IsType<Grid>(window.Content);
            Layout(root, width, height);
            window.PrepareOpenNotes();
            window.UpdateOpenNotesBounds();
            Assert.Equal("24", window.OpenNotesCount.Text);
            Assert.Equal(24, window.NotesPicker.NotesList.Items.Count);
            Assert.True(window.TabScroller.ScrollableWidth > 0);
            Assert.True(window.TabScroller.ViewportWidth >= 120, "The tab strip should retain a readable working area.");
            Assert.InRange(window.TabHeaderWidth, 100, window.TabScroller.ViewportWidth);
            Assert.True(window.NewNoteButton.ActualWidth > 20);
            Assert.True(window.OpenNotesButton.ActualWidth > 60);

            // Render the real popup child as a fixture overlay without showing any HWND or user's notes.
            var panel = window.OpenNotesPanel;
            window.OpenNotesPopup.Child = null;
            root.Children.Add(panel);
            Grid.SetRow(panel, 0);
            // A real Popup overlays the entire client area, including a footer that wraps at narrow widths.
            Grid.SetRowSpan(panel, root.RowDefinitions.Count);
            panel.HorizontalAlignment = HorizontalAlignment.Right;
            panel.VerticalAlignment = VerticalAlignment.Top;
            panel.Margin = new Thickness(12, 48, 12, 0);
            try
            {
                Layout(root, width, height);
                Assert.InRange(panel.ActualWidth, 240, width - 24);
                Assert.InRange(panel.ActualHeight, 100, height - 48);
                Assert.True(window.NotesPicker.SearchInput.ActualWidth > 200);
                Assert.True(window.NotesPicker.NotesList.ActualHeight >= 40);
                Assert.IsType<OpenNoteRow>(window.NotesPicker.NotesList.SelectedItem);
                SaveFixture(root, width, height, theme);
            }
            finally
            {
                root.Children.Remove(panel);
                window.OpenNotesPopup.Child = panel;
            }
        });

        private static void Layout(FrameworkElement root, int width, int height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            PadLanguageWindowTests.Pump();
        }

        private static void SaveFixture(Visual root, int width, int height, string theme)
        {
            string? folder = Environment.GetEnvironmentVariable("MICAPAD_TAB_SCREENSHOTS");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"open-notes-{theme.ToLowerInvariant()}-{width}.png"));
            encoder.Save(output);
        }
    }
}
