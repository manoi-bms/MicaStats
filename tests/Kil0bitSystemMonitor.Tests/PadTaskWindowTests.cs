using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PadTaskWindowTests
    {
        private static readonly DateTimeOffset Started = new(2026, 10, 6, 7, 30, 0, TimeSpan.FromHours(7));

        private static OpenNote Active(PadTestEnv env) => Assert.Single(env.Workspace.Open, note => note.IsActive);

        private static TaskDateRecord Task(OpenNote note, string body) =>
            Assert.Single(note.Meta.TaskDates, task => MarkdownTasks.Identity(task.Text) == body);

        private static void AssertSameDates(TaskDateRecord expected, TaskDateRecord actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Created, actual.Created);
            Assert.Equal(expected.Finished, actual.Finished);
        }

        [Fact]
        public void Showing_task_dates_does_not_dirty_or_rewrite_a_markdown_source_file() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            const string markdown = "- [ ] Send report";
            PadLanguageWindowTests.OpenFile(window, env, "tasks.md", markdown);
            var note = Active(env);

            Assert.Single(note.Meta.TaskDates);
            Assert.False(note.HasUnsavedEdits);
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            env.Flush();

            Assert.Equal(markdown, File.ReadAllText(env.FileOf("tasks.md")));
            Assert.Equal(markdown, note.TextProvider());
            Assert.False(note.HasUnsavedEdits);
            Assert.Single(env.Store.LoadMeta(note.Id)!.TaskDates);
        });

        [Fact]
        public void Legacy_dates_migrate_on_window_load_and_autosave_separately() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var note = env.Workspace.NewNote();
            note.Meta.TaskDatesVersion = 0;
            PadTestEnv.Type(env.Workspace, note, "- [x] Send report (created: 2026-10-06 07:30; finished: 2026-10-06 08:30)");
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            env.Flush();
            var restored = env.NewWorkspace();
            var window = new Kil0bitSystemMonitor.Pad.MicaPadWindow(restored, new Kil0bitSystemMonitor.Models.AppConfig());
            window.Now = () => Started.AddDays(2);
            try
            {
                window.LoadSession();
                var migrated = Assert.Single(restored.Open);
                Assert.Equal("- [x] Send report", window.Editor.Document.Text);
                Assert.Equal(Started, Assert.Single(migrated.Meta.TaskDates).Created);
                Assert.Equal(Started.AddHours(1), Assert.Single(migrated.Meta.TaskDates).Finished);
                Assert.False(window.Editor.Document.UndoStack.CanUndo);
                Assert.True(restored.FlushAll(TimeSpan.FromSeconds(5)));
                env.Flush();
                Assert.Equal("- [x] Send report", env.DiskText(migrated));
                Assert.Equal(1, env.Store.LoadMeta(migrated.Id)!.TaskDatesVersion);
            }
            finally { window.CloseForExit(); }
        });

        [Fact]
        public void Restoring_a_legacy_history_version_keeps_generated_dates_out_of_editable_text() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var note = env.Workspace.NewNote();
            note.Meta.TaskDatesVersion = 0;
            PadTestEnv.Type(env.Workspace, note, "- [ ] Send report (created: 2026-10-06 07:30)");
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            env.Flush();
            var version = env.Store.WriteSnapshot(note.Id,
                "- [ ] Send report (created: 2026-10-06 07:30)\r\nolder body",
                Started.DateTime.AddHours(-1));
            var restored = env.NewWorkspace();
            var window = new MicaPadWindow(restored, new Kil0bitSystemMonitor.Models.AppConfig());
            window.Now = () => Started.AddDays(2);
            try
            {
                window.LoadSession();
                var migrated = Assert.Single(restored.Open);
                TaskDateRecord original = Assert.Single(migrated.Meta.TaskDates);
                Assert.True(restored.FlushAll(TimeSpan.FromSeconds(5)));
                env.Flush();

                Assert.Equal("- [ ] Send report\r\nolder body", restored.ReadSnapshot(version));
                Assert.True(window.HandleShortcut(System.Windows.Input.Key.H,
                    System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));
                window.HistoryPanel.Versions.SelectedItem = Assert.Single(window.HistoryPanel.Rows, row => row.Snapshot.FilePath == version.FilePath);
                Assert.Equal("- [ ] Send report\r\nolder body", window.PreviewEditor.Text);
                window.RestoreButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Assert.Equal("- [ ] Send report\r\nolder body", window.Editor.Document.Text);
                Assert.Equal(original.Id, Assert.Single(migrated.Meta.TaskDates).Id);
                Assert.Equal(Started, Assert.Single(migrated.Meta.TaskDates).Created);
                window.Editor.Undo();
                Assert.Equal("- [ ] Send report", window.Editor.Document.Text);
                Assert.Equal(original, Assert.Single(migrated.Meta.TaskDates));
            }
            finally { window.CloseForExit(); }
        });

        [Fact]
        public void Real_character_input_keeps_dates_out_of_the_editable_markdown() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            foreach (char c in "- [] Send report") window.Editor.TextArea.PerformTextInput(c.ToString());

            Assert.Equal("- [] Send report", window.Editor.Document.Text);
            Assert.Equal(window.Editor.Document.TextLength, window.Editor.CaretOffset);
            TaskDateRecord dates = Assert.Single(Active(env).Meta.TaskDates);
            Assert.Equal(Started, dates.Created);
            Assert.Null(dates.Finished);
        });

        [Fact]
        public void Dates_autosave_in_metadata_and_survive_restart_separately_from_text() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [] Send report");
            window.Editor.CaretOffset = 8;
            window.Now = () => Started.AddHours(1);

            Assert.True(window.ToggleCurrentTask());
            Assert.Equal("- [x] Send report", window.Editor.Document.Text);
            TaskDateRecord completed = Assert.Single(Active(env).Meta.TaskDates);
            Assert.Equal(Started, completed.Created);
            Assert.Equal(Started.AddHours(1), completed.Finished);

            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            env.Flush();
            OpenNote restored = Assert.Single(env.NewWorkspace().Restore());
            Assert.Equal("- [x] Send report", restored.TextProvider());
            Assert.Equal(completed, Assert.Single(restored.Meta.TaskDates));
        });

        [Fact]
        public void The_real_document_end_accepts_input_and_selection_without_a_hidden_editable_suffix() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.TextArea.PerformTextInput("- [ ] Send report");

            window.Editor.Select(window.Editor.Document.TextLength, 0);
            window.Editor.TextArea.PerformTextInput(" today");
            Assert.Equal("- [ ] Send report today", window.Editor.Document.Text);
            Assert.Equal(window.Editor.Document.TextLength, window.Editor.CaretOffset);

            int body = window.Editor.Document.Text.IndexOf("Send report", StringComparison.Ordinal);
            window.Editor.Select(body, window.Editor.Document.TextLength - body);
            Assert.Equal("Send report today", window.Editor.SelectedText);
            Assert.DoesNotContain("created:", window.Editor.Document.Text);
            Assert.Single(Active(env).Meta.TaskDates);
        });

        [Fact]
        public void Undo_and_redo_restore_checkbox_metadata_with_the_original_times() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [] Send report");
            TaskDateRecord pending = Assert.Single(Active(env).Meta.TaskDates);
            window.Editor.CaretOffset = 8;
            window.Now = () => Started.AddHours(1);

            Assert.True(window.ToggleCurrentTask());
            TaskDateRecord completed = Assert.Single(Active(env).Meta.TaskDates);
            Assert.Equal(Started.AddHours(1), completed.Finished);

            window.Now = () => Started.AddHours(2);
            window.Editor.Undo();
            Assert.Equal("- [] Send report", window.Editor.Document.Text);
            Assert.Equal(pending, Assert.Single(Active(env).Meta.TaskDates));

            window.Editor.Redo();
            Assert.Equal("- [x] Send report", window.Editor.Document.Text);
            Assert.Equal(completed, Assert.Single(Active(env).Meta.TaskDates));
        });

        [Fact]
        public void Enter_creates_a_plain_fresh_task_and_an_empty_task_exits_the_list() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [] Send report");
            window.Editor.CaretOffset = window.Editor.Document.TextLength;

            Assert.True(window.ContinueCurrentTask());
            Assert.Equal("- [] Send report\r\n- [ ] ", window.Editor.Document.Text);
            Assert.Equal(2, Active(env).Meta.TaskDates.Count);
            Assert.Equal(2, window.Editor.TextArea.Caret.Line);
            Assert.Equal(7, window.Editor.TextArea.Caret.Column);

            Assert.True(window.ContinueCurrentTask());
            Assert.Equal("- [] Send report\r\n", window.Editor.Document.Text);
            Assert.Single(Active(env).Meta.TaskDates);
            window.Editor.Undo();
            Assert.Equal("- [] Send report\r\n- [ ] ", window.Editor.Document.Text);
            Assert.Equal(2, Active(env).Meta.TaskDates.Count);
        });

        [Fact]
        public void The_menu_exposes_complete_and_reopen_without_a_dialog() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [ ] Send report");
            window.Editor.CaretOffset = 8;
            window.RefreshEditorMenu();

            var complete = Assert.Single(window.EditorMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Complete task"));
            Assert.Equal("Ctrl+Enter", complete.InputGestureText);
            complete.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("- [x] Send report", window.Editor.Document.Text);
            Assert.NotNull(Assert.Single(Active(env).Meta.TaskDates).Finished);

            window.RefreshEditorMenu();
            Assert.Contains(window.EditorMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Reopen task"));
        });

        [Fact]
        public void Code_examples_and_other_languages_never_create_task_dates() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "```markdown\r\n- [] example\r\n```\r\n");
            Assert.Empty(Active(env).Meta.TaskDates);

            PadLanguageWindowTests.OpenFile(window, env, "example.cs", "- [] literal");
            Assert.Equal("- [] literal", window.Editor.Document.Text);
            Assert.Empty(Active(env).Meta.TaskDates);
            Assert.False(window.ToggleCurrentTask());
        });

        [Fact]
        public void Converting_a_task_to_prose_removes_its_record_and_undo_restores_it() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [ ] Send report");
            TaskDateRecord tracked = Assert.Single(Active(env).Meta.TaskDates);

            window.Editor.Document.Remove(0, 6);
            Assert.Equal("Send report", window.Editor.Document.Text);
            Assert.Empty(Active(env).Meta.TaskDates);

            window.Editor.Undo();
            Assert.Equal("- [ ] Send report", window.Editor.Document.Text);
            Assert.Equal(tracked, Assert.Single(Active(env).Meta.TaskDates));
        });

        [Fact]
        public void The_code_block_command_removes_task_metadata_and_undo_restores_it() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [ ] example");
            TaskDateRecord tracked = Assert.Single(Active(env).Meta.TaskDates);
            window.Editor.Select(0, window.Editor.Document.TextLength);
            window.RefreshEditorMenu();
            var format = Assert.Single(window.EditorMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Format"));
            var code = Assert.Single(format.Items.OfType<MenuItem>(), item => Equals(item.Header, "Code block"));

            code.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("```\r\n- [ ] example\r\n```", window.Editor.Document.Text);
            Assert.Empty(Active(env).Meta.TaskDates);

            window.Editor.Undo();
            Assert.Equal("- [ ] example", window.Editor.Document.Text);
            Assert.Equal(tracked, Assert.Single(Active(env).Meta.TaskDates));
            window.Editor.Redo();
            Assert.Empty(Active(env).Meta.TaskDates);
        });

        [Fact]
        public void Date_like_text_typed_by_the_user_cannot_change_the_stored_created_date() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [ ] Send report");
            TaskDateRecord original = Assert.Single(Active(env).Meta.TaskDates);
            window.Now = () => Started.AddDays(1);

            const string typed = " (created: 1999-01-01 00:00; finished: 1999-01-02 00:00)";
            window.Editor.Document.Insert(window.Editor.Document.TextLength, typed);

            Assert.EndsWith(typed, window.Editor.Document.Text);
            TaskDateRecord after = Assert.Single(Active(env).Meta.TaskDates);
            Assert.Equal(original.Id, after.Id);
            Assert.Equal(Started, after.Created);
            Assert.Null(after.Finished);
        });

        [Fact]
        public void Moving_a_task_across_another_task_and_prose_keeps_both_date_identities() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            OpenNote note = Active(env);
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [ ] Alpha");
            TaskDateRecord alpha = Task(note, "Alpha");

            window.Now = () => Started.AddHours(1);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, "\r\n- [x] Beta");
            TaskDateRecord beta = Task(note, "Beta");
            Assert.Equal(Started, alpha.Created);
            Assert.Null(alpha.Finished);
            Assert.Equal(Started.AddHours(1), beta.Created);
            Assert.Equal(Started.AddHours(1), beta.Finished);

            window.Now = () => Started.AddHours(2);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, "\r\nplanning notes");
            TaskDateRecord[] before = note.Meta.TaskDates.ToArray();

            window.Editor.Select(0, 0);
            window.MoveLines(down: true);
            Assert.Equal("- [x] Beta\r\n- [ ] Alpha\r\nplanning notes", window.Editor.Document.Text);
            AssertSameDates(alpha, Task(note, "Alpha"));
            AssertSameDates(beta, Task(note, "Beta"));

            window.Editor.Select(window.Editor.Document.Text.IndexOf("- [ ] Alpha", StringComparison.Ordinal), 0);
            window.MoveLines(down: true);
            Assert.Equal("- [x] Beta\r\nplanning notes\r\n- [ ] Alpha", window.Editor.Document.Text);
            AssertSameDates(alpha, Task(note, "Alpha"));
            AssertSameDates(beta, Task(note, "Beta"));
            TaskDateRecord[] moved = note.Meta.TaskDates.ToArray();

            window.Editor.Undo();
            Assert.Equal("- [x] Beta\r\n- [ ] Alpha\r\nplanning notes", window.Editor.Document.Text);
            AssertSameDates(alpha, Task(note, "Alpha"));
            AssertSameDates(beta, Task(note, "Beta"));
            window.Editor.Undo();
            Assert.Equal("- [ ] Alpha\r\n- [x] Beta\r\nplanning notes", window.Editor.Document.Text);
            Assert.Equal(before, note.Meta.TaskDates);

            window.Editor.Redo();
            window.Editor.Redo();
            Assert.Equal("- [x] Beta\r\nplanning notes\r\n- [ ] Alpha", window.Editor.Document.Text);
            Assert.Equal(moved, note.Meta.TaskDates);
        });

        [Fact]
        public void Moving_identical_bodies_with_different_checkbox_states_keeps_the_right_dates() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            OpenNote note = Active(env);
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [ ] same");
            TaskDateRecord pending = Assert.Single(note.Meta.TaskDates);

            window.Now = () => Started.AddHours(3);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, "\r\n- [x] same");
            TaskDateRecord completed = Assert.Single(note.Meta.TaskDates, task => task.Text == "- [x] same");
            TaskDateRecord[] before = note.Meta.TaskDates.ToArray();
            Assert.Equal(Started, pending.Created);
            Assert.Null(pending.Finished);
            Assert.Equal(Started.AddHours(3), completed.Created);
            Assert.Equal(Started.AddHours(3), completed.Finished);

            window.Editor.Select(0, 0);
            window.MoveLines(down: true);

            Assert.Equal("- [x] same\r\n- [ ] same", window.Editor.Document.Text);
            AssertSameDates(completed, Assert.Single(note.Meta.TaskDates, task => task.Text == "- [x] same"));
            AssertSameDates(pending, Assert.Single(note.Meta.TaskDates, task => task.Text == "- [ ] same"));
            TaskDateRecord[] moved = note.Meta.TaskDates.ToArray();

            window.Editor.Undo();
            Assert.Equal("- [ ] same\r\n- [x] same", window.Editor.Document.Text);
            Assert.Equal(before, note.Meta.TaskDates);

            window.Editor.Redo();
            Assert.Equal("- [x] same\r\n- [ ] same", window.Editor.Document.Text);
            Assert.Equal(moved, note.Meta.TaskDates);
        });

        [Fact]
        public void Sorting_lines_keeps_task_identities_and_undo_redo_restore_their_metadata() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            OpenNote note = Active(env);
            window.Now = () => Started;
            window.Editor.Document.Insert(0, "- [x] Zebra");
            TaskDateRecord zebra = Task(note, "Zebra");

            window.Now = () => Started.AddHours(1);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, "\r\nplain middle");
            window.Now = () => Started.AddHours(2);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, "\r\n- [ ] Alpha");
            TaskDateRecord alpha = Task(note, "Alpha");
            TaskDateRecord[] before = note.Meta.TaskDates.ToArray();
            Assert.Equal(Started, zebra.Created);
            Assert.Equal(Started, zebra.Finished);
            Assert.Equal(Started.AddHours(2), alpha.Created);
            Assert.Null(alpha.Finished);

            TextEdit? sort = LineOperations.Sort(window.Editor.Document.Text, 0, 0, descending: false, CultureInfo.InvariantCulture);
            Assert.NotNull(sort);
            EditorMenus.ApplyEdit(window.Editor, sort!.Value);

            Assert.Equal("- [ ] Alpha\r\n- [x] Zebra\r\nplain middle", window.Editor.Document.Text);
            AssertSameDates(alpha, Task(note, "Alpha"));
            AssertSameDates(zebra, Task(note, "Zebra"));
            TaskDateRecord[] sorted = note.Meta.TaskDates.ToArray();

            window.Editor.Undo();
            Assert.Equal("- [x] Zebra\r\nplain middle\r\n- [ ] Alpha", window.Editor.Document.Text);
            Assert.Equal(before, note.Meta.TaskDates);

            window.Editor.Redo();
            Assert.Equal("- [ ] Alpha\r\n- [x] Zebra\r\nplain middle", window.Editor.Document.Text);
            Assert.Equal(sorted, note.Meta.TaskDates);
        });

        [Fact]
        public void Compact_checkboxes_are_tasks_and_completed_style_covers_the_plain_body()
        {
            const string pendingLine = "- [] Send report";
            MdLine pending = MarkdownLineTokenizer.Tokenize(pendingLine, MdFence.None);
            Assert.Equal(MdBlock.Task, pending.Block);
            Assert.DoesNotContain(pending.Spans, span => span.Style == MdStyle.TaskDone);

            const string completedLine = "- [x] Send report";
            MdLine completed = MarkdownLineTokenizer.Tokenize(completedLine, MdFence.None);
            var done = Assert.Single(completed.Spans, span => span.Style == MdStyle.TaskDone);
            Assert.Equal("Send report", completedLine.Substring(done.Start, done.Length));
        }
    }
}
