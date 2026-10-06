using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class TaskDateEditingTests
    {
        private static readonly DateTimeOffset Initial = new(2026, 10, 6, 9, 5, 37, TimeSpan.FromHours(7));
        private static readonly DateTimeOffset CorrectedStart = new(2026, 10, 5, 8, 4, 23, TimeSpan.FromMinutes(330));
        private static readonly DateTimeOffset CorrectedFinish = new(2026, 10, 7, 18, 42, 11, TimeSpan.FromMinutes(330));

        private static NoteMeta Meta() => new() { TaskDatesVersion = 1 };

        private static void ShowOffscreen(MicaPadWindow window)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10_000;
            window.Top = -10_000;
            window.Show();
            PadLanguageWindowTests.Pump();
        }

        [Fact]
        public void Invalid_missing_and_disabled_edits_fail_without_changing_metadata_or_undo() => UiThread.Run(() =>
        {
            bool enabled = true;
            var document = new TextDocument("- [ ] task");
            NoteMeta meta = Meta();
            int changed = 0;
            using var controller = new TaskDateController(document, meta, () => enabled, () => Initial, () => changed++);
            TaskDateRecord original = Assert.Single(meta.TaskDates);
            int initialChanges = changed;

            Assert.False(controller.TrySetDates("missing-task", CorrectedStart, null));
            Assert.False(controller.TrySetDates(original.Id, CorrectedFinish, CorrectedStart));
            enabled = false;
            Assert.False(controller.TrySetDates(original.Id, CorrectedStart, CorrectedFinish));

            Assert.Equal(original, Assert.Single(meta.TaskDates));
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(initialChanges, changed);
            Assert.False(document.UndoStack.CanUndo);
        });

        [Fact]
        public void Saving_the_same_values_is_a_successful_no_op_without_undo() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [x] task");
            NoteMeta meta = Meta();
            int changed = 0;
            using var controller = new TaskDateController(document, meta, () => true, () => Initial, () => changed++);
            TaskDateRecord original = Assert.Single(meta.TaskDates);
            int initialChanges = changed;

            Assert.True(controller.TrySetDates(original.Id, original.Created, original.Finished));

            Assert.Equal(original, Assert.Single(meta.TaskDates));
            Assert.Equal(original, controller.DatesOfLine(1));
            Assert.Equal(initialChanges, changed);
            Assert.False(document.UndoStack.CanUndo);
        });

        [Fact]
        public void Metadata_only_correction_is_one_undo_step_and_dates_of_line_follow_undo_redo() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [ ] task");
            NoteMeta meta = Meta();
            int changed = 0;
            using var controller = new TaskDateController(document, meta, () => true, () => Initial, () => changed++);
            TaskDateRecord original = Assert.Single(meta.TaskDates);
            int initialChanges = changed;

            Assert.True(controller.TrySetDates(original.Id, CorrectedStart, null));

            TaskDateRecord corrected = Assert.Single(meta.TaskDates);
            Assert.Equal(original.Id, corrected.Id);
            Assert.Equal(CorrectedStart, corrected.Created);
            Assert.Null(corrected.Finished);
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(corrected, controller.DatesOfLine(1));
            Assert.Equal(initialChanges + 1, changed);
            Assert.True(document.UndoStack.CanUndo);

            document.UndoStack.Undo();
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(original, Assert.Single(meta.TaskDates));
            Assert.Equal(original, controller.DatesOfLine(1));
            Assert.Equal(initialChanges + 2, changed);
            Assert.False(document.UndoStack.CanUndo);

            document.UndoStack.Redo();
            Assert.Equal(corrected, Assert.Single(meta.TaskDates));
            Assert.Equal(corrected, controller.DatesOfLine(1));
            Assert.Equal(initialChanges + 3, changed);

            document.Insert(document.TextLength, " updated");
            document.UndoStack.Undo();
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(corrected.Created, Assert.Single(meta.TaskDates).Created);
            Assert.True(document.UndoStack.CanUndo);
        });

        [Fact]
        public void Adding_a_finish_checks_the_box_and_date_corrections_share_one_undo_step() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [ ] task");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Initial);
            TaskDateRecord original = Assert.Single(meta.TaskDates);

            Assert.True(controller.TrySetDates(original.Id, CorrectedStart, CorrectedFinish));

            TaskDateRecord completed = Assert.Single(meta.TaskDates);
            Assert.Equal("- [x] task", document.Text);
            Assert.Equal(original.Id, completed.Id);
            Assert.Equal(CorrectedStart, completed.Created);
            Assert.Equal(CorrectedFinish, completed.Finished);
            Assert.Equal(completed, controller.DatesOfLine(1));

            document.UndoStack.Undo();
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(original, Assert.Single(meta.TaskDates));
            Assert.Equal(original, controller.DatesOfLine(1));
            Assert.False(document.UndoStack.CanUndo);

            document.UndoStack.Redo();
            Assert.Equal("- [x] task", document.Text);
            Assert.Equal(completed, Assert.Single(meta.TaskDates));
            Assert.Equal(completed, controller.DatesOfLine(1));
        });

        [Fact]
        public void Clearing_the_finish_reopens_the_task_without_replacing_its_identity() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [x] task");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Initial);
            TaskDateRecord completed = Assert.Single(meta.TaskDates);

            Assert.True(controller.TrySetDates(completed.Id, CorrectedStart, null));

            TaskDateRecord reopened = Assert.Single(meta.TaskDates);
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(completed.Id, reopened.Id);
            Assert.Equal(CorrectedStart, reopened.Created);
            Assert.Null(reopened.Finished);

            document.UndoStack.Undo();
            Assert.Equal("- [x] task", document.Text);
            Assert.Equal(completed, Assert.Single(meta.TaskDates));
            document.UndoStack.Redo();
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(reopened, Assert.Single(meta.TaskDates));
        });

        [Fact]
        public void Metadata_undo_remains_safe_after_dispose_and_reattach_to_the_same_document() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [ ] task");
            NoteMeta meta = Meta();
            int firstChanges = 0;
            var first = new TaskDateController(document, meta, () => true, () => Initial, () => firstChanges++);
            TaskDateRecord original = Assert.Single(meta.TaskDates);
            Assert.True(first.TrySetDates(original.Id, CorrectedStart, null));
            TaskDateRecord corrected = Assert.Single(meta.TaskDates);
            first.Dispose();

            int secondChanges = 0;
            using var attached = new TaskDateController(document, meta, () => true, () => Initial.AddDays(3), () => secondChanges++);
            Assert.Equal(corrected, attached.DatesOfLine(1));

            document.UndoStack.Undo();
            Assert.Equal(original, Assert.Single(meta.TaskDates));
            Assert.Equal(original, attached.DatesOfLine(1));
            Assert.Equal(1, secondChanges);

            document.UndoStack.Redo();
            Assert.Equal(corrected, Assert.Single(meta.TaskDates));
            Assert.Equal(corrected, attached.DatesOfLine(1));
            Assert.Equal(2, secondChanges);
        });

        [Fact]
        public void Editor_save_preserves_unshown_seconds_and_each_original_offset() => UiThread.Run(() =>
        {
            var editor = new TaskDateEditor();
            var record = new TaskDateRecord("task-1", 0, "- [x] task", Initial, CorrectedFinish);
            DateTimeOffset? savedStart = null;
            DateTimeOffset? savedFinish = null;
            int dismissed = 0;
            editor.Dismissed += (_, _) => dismissed++;
            editor.Show(record, () => Initial.AddDays(20), (start, finish) =>
            {
                savedStart = start;
                savedFinish = finish;
                return null;
            });

            Assert.Equal("2026-10-06 09:05", editor.StartInput.Text);
            Assert.Equal("2026-10-07 18:42", editor.FinishInput.Text);
            editor.OnSave();

            Assert.Equal(Initial, savedStart);
            Assert.Equal(CorrectedFinish, savedFinish);
            Assert.Equal(1, dismissed);
            Assert.Equal(Visibility.Collapsed, editor.ErrorText.Visibility);
        });

        [Fact]
        public void Editor_validation_keeps_the_draft_open_and_cancel_never_saves() => UiThread.Run(() =>
        {
            var editor = new TaskDateEditor();
            int saves = 0;
            int dismissed = 0;
            editor.Dismissed += (_, _) => dismissed++;
            editor.Show(new TaskDateRecord("task-1", 0, "- [ ] task", Initial, null),
                () => Initial.AddDays(20), (start, finish) => { saves++; return null; });

            editor.StartInput.Text = "not a date";
            editor.OnSave();
            Assert.Equal(0, saves);
            Assert.Equal(0, dismissed);
            Assert.Equal(Visibility.Visible, editor.ErrorText.Visibility);
            Assert.Contains("yyyy-MM-dd HH:mm", editor.ErrorText.Text, StringComparison.Ordinal);

            editor.StartInput.Text = "2026-10-06 09:05";
            editor.FinishInput.Text = "2026-10-05 08:00";
            editor.OnSave();
            Assert.Equal(0, saves);
            Assert.Equal(0, dismissed);
            Assert.Contains("earlier", editor.ErrorText.Text, StringComparison.OrdinalIgnoreCase);

            editor.OnCancel();
            Assert.Equal(0, saves);
            Assert.Equal(1, dismissed);
        });

        [Fact]
        public void Window_menu_opens_the_editor_save_completes_and_cancel_keeps_dates() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            ShowOffscreen(window);
            window.Now = () => Initial;
            window.Editor.Document.Insert(0, "- [ ] task");
            OpenNote note = Assert.Single(env.Workspace.Open, item => item.IsActive);
            TaskDateRecord original = Assert.Single(note.Meta.TaskDates);
            window.Editor.CaretOffset = 8;
            window.RefreshEditorMenu();

            MenuItem edit = Assert.Single(window.EditorMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Edit task dates…"));
            edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(window.TaskDatesPopup.IsOpen);
            Assert.Equal("2026-10-06 09:05", window.TaskDatesEditor.StartInput.Text);

            window.TaskDatesEditor.FinishInput.Text = "2026-10-06 11:45";
            window.TaskDatesEditor.OnSave();

            TaskDateRecord completed = Assert.Single(note.Meta.TaskDates);
            Assert.False(window.TaskDatesPopup.IsOpen);
            Assert.Equal("- [x] task", window.Editor.Document.Text);
            Assert.Equal(original.Id, completed.Id);
            Assert.Equal(Initial, completed.Created);
            Assert.Equal(new DateTimeOffset(2026, 10, 6, 11, 45, 0, Initial.Offset), completed.Finished);

            window.ShowTaskDateEditor();
            window.TaskDatesEditor.StartInput.Text = "2026-09-01 08:00";
            window.TaskDatesEditor.OnCancel();
            Assert.False(window.TaskDatesPopup.IsOpen);
            Assert.Equal(completed, Assert.Single(note.Meta.TaskDates));
            Assert.Equal(string.Empty, window.TaskDatesEditor.StartInput.Text);
        });

        [Fact]
        public void File_task_date_correction_autosaves_without_dirtying_or_rewriting_the_file() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            const string markdown = "- [ ] file task";
            window.Now = () => Initial;
            PadLanguageWindowTests.OpenFile(window, env, "dated-tasks.md", markdown);
            OpenNote note = Assert.Single(env.Workspace.Open, item => item.IsActive);
            Assert.False(note.HasUnsavedEdits);

            window.Editor.CaretOffset = 8;
            window.ShowTaskDateEditor();
            window.TaskDatesEditor.StartInput.Text = "2026-10-05 08:04";
            window.TaskDatesEditor.OnSave();

            var corrected = new DateTimeOffset(2026, 10, 5, 8, 4, 0, Initial.Offset);
            Assert.Equal(corrected, Assert.Single(note.Meta.TaskDates).Created);
            Assert.Equal(markdown, window.Editor.Document.Text);
            Assert.False(note.HasUnsavedEdits);
            Assert.Equal(markdown, File.ReadAllText(env.FileOf("dated-tasks.md")));

            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            env.Flush();
            OpenNote restored = Assert.Single(env.NewWorkspace().Restore(), item => item.Id == note.Id);
            Assert.Equal(markdown, restored.TextProvider());
            Assert.Equal(corrected, Assert.Single(restored.Meta.TaskDates).Created);
            Assert.False(restored.HasUnsavedEdits);
            Assert.Equal(markdown, File.ReadAllText(env.FileOf("dated-tasks.md")));
        });

        [Fact]
        public void Stale_editor_save_is_rejected_and_switching_notes_clears_the_draft() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            ShowOffscreen(window);
            window.Now = () => Initial;
            window.Editor.Document.Insert(0, "- [ ] task");
            OpenNote note = Assert.Single(env.Workspace.Open, item => item.IsActive);
            window.Editor.CaretOffset = 8;
            window.ShowTaskDateEditor();
            TaskDateRecord opened = Assert.Single(note.Meta.TaskDates);

            window.Now = () => Initial.AddHours(2);
            Assert.True(window.ToggleCurrentTask());
            TaskDateRecord changed = Assert.Single(note.Meta.TaskDates);
            Assert.NotEqual(opened.Finished, changed.Finished);
            window.TaskDatesEditor.StartInput.Text = "2026-09-01 08:00";
            window.TaskDatesEditor.OnSave();

            Assert.True(window.TaskDatesPopup.IsOpen);
            Assert.Equal(Visibility.Visible, window.TaskDatesEditor.ErrorText.Visibility);
            Assert.Contains("changed", window.TaskDatesEditor.ErrorText.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(changed, Assert.Single(note.Meta.TaskDates));

            window.CloseTaskDateEditor();
            window.ShowTaskDateEditor();
            Assert.True(window.TaskDatesPopup.IsOpen);
            window.NewTab();
            Assert.False(window.TaskDatesPopup.IsOpen);
            Assert.Equal(Visibility.Collapsed, window.TaskDatesEditor.Visibility);
            Assert.Equal(string.Empty, window.TaskDatesEditor.StartInput.Text);
            window.TaskDatesEditor.OnSave();
            Assert.Equal(changed, Assert.Single(note.Meta.TaskDates));
        });
    }
}
