using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class OpenNotesPickerTests
    {
        [Fact]
        public void Filters_title_and_path_without_reading_note_text() => UiThread.Run(() =>
        {
            var title = Note("Quarterly planning", 1, window: "one");
            var path = Note("Minutes", 2, @"C:\work\roadmap\decisions.md", "one");
            title.TextProvider = () => throw new InvalidOperationException("The picker must stay metadata-only.");
            path.TextProvider = () => throw new InvalidOperationException("The picker must stay metadata-only.");
            var picker = new OpenNotesPicker();

            picker.Begin(new[] { title, path }, title, "one");
            picker.SearchInput.Text = "quarter PLAN";
            Assert.Same(title, Assert.IsType<OpenNoteRow>(Assert.Single(picker.NotesList.Items)).Note);

            picker.SearchInput.Text = "roadmap decisions";
            Assert.Same(path, Assert.IsType<OpenNoteRow>(Assert.Single(picker.NotesList.Items)).Note);
            Assert.Equal("1 of 2 notes", picker.ResultCount.Text);
        });

        [Fact]
        public void Rows_distinguish_duplicate_files_scratch_notes_and_window_state() => UiThread.Run(() =>
        {
            var first = Note("readme.md", 1, @"C:\alpha\readme.md", "one");
            var second = Note("readme.md", 2, @"D:\beta\readme.md", "two");
            var scratch = Note("", 7, window: "one");
            second.HasUnsavedEdits = true;
            var picker = new OpenNotesPicker();

            picker.Begin(new[] { first, second, scratch }, first, "one");
            var rows = picker.NotesList.Items.Cast<OpenNoteRow>().ToArray();

            Assert.Equal(@"C:\alpha\readme.md", rows[0].Detail);
            Assert.True(rows[0].IsCurrent);
            Assert.Equal(@"D:\beta\readme.md · Other window", rows[1].Detail);
            Assert.True(rows[1].Unsaved);
            Assert.Equal("Untitled 7", rows[2].Title);
            Assert.Equal("Note 7", rows[2].Detail);
            Assert.Contains("current", rows[0].AutomationName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unsaved", rows[1].AutomationName, StringComparison.OrdinalIgnoreCase);
        });

        [Fact]
        public void Refresh_preserves_query_and_selection_then_falls_back_to_current() => UiThread.Run(() =>
        {
            var first = Note("Project alpha", 1, window: "one");
            var second = Note("Project beta", 2, window: "one");
            var third = Note("Other", 3, window: "one");
            var picker = new OpenNotesPicker();
            picker.Begin(new[] { first, second, third }, first, "one");
            picker.SearchInput.Text = "project";
            picker.NotesList.SelectedIndex = 1;

            picker.RefreshNotes(new[] { third, second, first }, first, "one");
            Assert.Equal("project", picker.SearchInput.Text);
            Assert.Same(second, Assert.IsType<OpenNoteRow>(picker.NotesList.SelectedItem).Note);

            picker.RefreshNotes(new[] { third, first }, first, "one");
            Assert.Same(first, Assert.IsType<OpenNoteRow>(picker.NotesList.SelectedItem).Note);
        });

        [Fact]
        public void Keyboard_confirms_cancels_and_leaves_editing_keys_alone() => UiThread.Run(() =>
        {
            var first = Note("One", 1, window: "one");
            var second = Note("Two", 2, window: "one");
            var picker = new OpenNotesPicker();
            OpenNote? chosen = null;
            int dismissed = 0;
            int toggled = 0;
            picker.NoteChosen += note => chosen = note;
            picker.DismissRequested += () => dismissed++;
            picker.ToggleRequested += () => toggled++;
            picker.Begin(new[] { first, second }, first, "one");

            Assert.True(picker.HandleKey(Key.Down, ModifierKeys.None));
            Assert.True(picker.HandleKey(Key.Enter, ModifierKeys.None));
            Assert.Same(second, chosen);
            Assert.True(picker.HandleKey(Key.Escape, ModifierKeys.None));
            Assert.Equal(1, dismissed);
            Assert.False(picker.HandleKey(Key.Home, ModifierKeys.None));
            Assert.False(picker.HandleKey(Key.End, ModifierKeys.None));
            Assert.True(picker.HandleKey(Key.P, ModifierKeys.Control));
            Assert.Equal(1, toggled);
        });

        [Fact]
        public void Empty_and_no_match_states_are_explicit_and_clear_releases_rows() => UiThread.Run(() =>
        {
            var note = Note("One", 1, window: "one");
            var picker = new OpenNotesPicker();

            picker.Begin(Array.Empty<OpenNote>(), null, "one");
            Assert.Equal(Visibility.Visible, picker.EmptyText.Visibility);
            Assert.Equal("No notes are open.", picker.EmptyText.Text);

            picker.Begin(new[] { note }, note, "one");
            picker.SearchInput.Text = "missing";
            Assert.Equal("No open notes match this search.", picker.EmptyText.Text);
            Assert.Empty(picker.NotesList.Items);

            picker.Clear();
            Assert.Equal("", picker.SearchInput.Text);
            Assert.Empty(picker.NotesList.Items);
            Assert.Equal("0 notes", picker.ResultCount.Text);
        });

        private static OpenNote Note(string title, int number, string? path = null, string window = "one")
        {
            var meta = NoteStore.NewMeta(DateTime.UtcNow, number, path);
            meta.Title = title;
            var note = new OpenNote(meta, "body") { WindowId = window };
            return note;
        }
    }
}
