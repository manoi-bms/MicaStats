using System;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class TaskDateControllerTests
    {
        private static readonly DateTimeOffset Morning = new(2026, 10, 6, 9, 5, 0, TimeSpan.FromHours(7));

        private static NoteMeta Meta(int version = 1) => new() { TaskDatesVersion = version };

        private static void ReplaceWithPieces(TextDocument document, string replacement)
        {
            var pieces = TextPieces.Plan(document.Text, replacement);
            using (document.RunUpdate())
                for (int i = pieces.Count - 1; i >= 0; i--)
                    document.Replace(pieces[i].Offset, pieces[i].Length, pieces[i].Text);
        }

        [Fact]
        public void Typing_in_the_middle_of_many_tasks_does_not_sort_or_rescan_the_document() => UiThread.Run(() =>
        {
            var document = new TextDocument(string.Join("\n", Enumerable.Range(0, 2000).Select(index => "- [ ] task " + index)));
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);
            int scans = controller.StructureScans;
            int sorts = controller.RecordSorts;
            string originalId = meta.TaskDates[999].Id;

            for (int i = 0; i < 20; i++) document.Insert(document.GetLineByNumber(1000).EndOffset, "a");

            Assert.Equal(2000, meta.TaskDates.Count);
            Assert.Equal(originalId, meta.TaskDates[999].Id);
            Assert.EndsWith(new string('a', 20), meta.TaskDates[999].Text);
            Assert.Equal(scans, controller.StructureScans);
            Assert.Equal(sorts, controller.RecordSorts);
            Assert.Equal(meta.TaskDates.OrderBy(task => task.Offset), meta.TaskDates);
        });

        [Fact]
        public void Replacing_the_document_on_reload_reconciles_existing_task_dates() => UiThread.Run(() =>
        {
            DateTimeOffset clock = Morning;
            var document = new TextDocument("- [ ] one\n- [x] two");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => clock);
            TaskDateRecord[] original = meta.TaskDates.ToArray();

            clock = clock.AddDays(5);
            document.Text = "heading\n- [ ] one\n- [x] two";

            Assert.Equal(original.Select(task => task.Id), meta.TaskDates.Select(task => task.Id));
            Assert.Equal(original.Select(task => task.Created), meta.TaskDates.Select(task => task.Created));
            Assert.Equal(original.Select(task => task.Finished), meta.TaskDates.Select(task => task.Finished));
            Assert.Equal(8, meta.TaskDates[0].Offset);
        });

        [Fact]
        public void Initialize_tracks_only_tasks_outside_front_matter_fences_math_and_tables() => UiThread.Run(() =>
        {
            const string text = "---\n- [ ] metadata\n---\n- [ ] visible\n```md\n- [ ] code\n```\n$$\n- [ ] math\n$$\nTask | Owner\n--- | ---\n- [ ] cell | me";
            var document = new TextDocument(text);
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);

            Assert.Equal(text, document.Text);
            TaskDateRecord dates = Assert.Single(meta.TaskDates);
            Assert.Equal(document.GetLineByNumber(4).Offset, dates.Offset);
            Assert.Equal("- [ ] visible", dates.Text);
            Assert.Equal(Morning, dates.Created);
            Assert.Null(dates.Finished);
            Assert.False(controller.IsTaskLine(2));
            Assert.True(controller.IsTaskLine(4));
            Assert.False(controller.IsTaskLine(6));
            Assert.False(controller.IsTaskLine(9));
            Assert.False(controller.IsTaskLine(13));
            Assert.Equal(dates, controller.DatesOfLine(4));
            Assert.Null(controller.DatesOfLine(6));
        });

        [Fact]
        public void A_paste_is_one_undo_unit_and_redo_keeps_the_original_clock() => UiThread.Run(() =>
        {
            DateTimeOffset clock = Morning;
            var document = new TextDocument();
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => clock);

            document.Insert(0, "- [] one\r\n  - [ ] two");
            TaskDateRecord[] pasted = meta.TaskDates.ToArray();
            Assert.Equal(2, pasted.Length);
            Assert.All(pasted, task => Assert.Equal(Morning, task.Created));
            Assert.Equal("- [] one\r\n  - [ ] two", document.Text);

            document.UndoStack.Undo();
            Assert.Equal(string.Empty, document.Text);
            Assert.Empty(meta.TaskDates);

            clock = clock.AddDays(3);
            document.UndoStack.Redo();
            Assert.Equal("- [] one\r\n  - [ ] two", document.Text);
            Assert.Equal(pasted, meta.TaskDates);
            Assert.False(document.UndoStack.CanRedo);
        });

        [Fact]
        public void Body_edits_and_inserted_lines_move_records_without_changing_identity_or_dates() => UiThread.Run(() =>
        {
            DateTimeOffset clock = Morning;
            var document = new TextDocument("- [ ] alpha\n- [x] beta");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => clock);
            TaskDateRecord[] original = meta.TaskDates.ToArray();

            clock = clock.AddDays(10);
            document.Insert(document.Text.IndexOf("alpha", StringComparison.Ordinal) + 5, " polished");
            document.Insert(0, "heading\nnotes\n");

            Assert.Equal("heading\nnotes\n- [ ] alpha polished\n- [x] beta", document.Text);
            Assert.Equal(original.Select(task => task.Id), meta.TaskDates.Select(task => task.Id));
            Assert.All(meta.TaskDates, task => Assert.Equal(Morning, task.Created));
            Assert.Equal("- [ ] alpha polished", meta.TaskDates[0].Text);
            Assert.Equal(document.GetLineByNumber(3).Offset, meta.TaskDates[0].Offset);
            Assert.Equal("- [x] beta", meta.TaskDates[1].Text);
            Assert.Equal(document.GetLineByNumber(4).Offset, meta.TaskDates[1].Offset);
        });

        [Fact]
        public void Reopening_clears_finished_and_a_later_completion_gets_a_new_time() => UiThread.Run(() =>
        {
            DateTimeOffset clock = Morning;
            var document = new TextDocument("- [x] task");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => clock);
            TaskDateRecord first = Assert.Single(meta.TaskDates);
            Assert.Equal(Morning, first.Finished);

            document.Replace(3, 1, " ");
            TaskDateRecord reopened = Assert.Single(meta.TaskDates);
            Assert.Equal(first.Id, reopened.Id);
            Assert.Equal(first.Created, reopened.Created);
            Assert.Null(reopened.Finished);

            clock = clock.AddDays(1);
            document.Replace(3, 1, "x");
            TaskDateRecord completed = Assert.Single(meta.TaskDates);
            Assert.Equal(first.Id, completed.Id);
            Assert.Equal(Morning, completed.Created);
            Assert.Equal(Morning.AddDays(1), completed.Finished);
            Assert.Equal("- [x] task", document.Text);
        });

        [Fact]
        public void Disabled_tracking_does_not_scan_until_reenabled_and_initialized() => UiThread.Run(() =>
        {
            bool enabled = false;
            var document = new TextDocument("- [ ] existing");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => enabled, () => Morning);

            document.Insert(document.TextLength, "\n- [x] pasted");
            document.Replace(document.Text.IndexOf("existing", StringComparison.Ordinal), "existing".Length, "edited");
            Assert.Equal(0, controller.StructureScans);
            Assert.Empty(meta.TaskDates);
            Assert.Equal("- [ ] edited\n- [x] pasted", document.Text);
            Assert.False(controller.IsTaskLine(1));

            enabled = true;
            controller.Initialize();

            Assert.Equal(1, controller.StructureScans);
            Assert.Equal(2, meta.TaskDates.Count);
            Assert.Equal("- [ ] edited", meta.TaskDates[0].Text);
            Assert.Null(meta.TaskDates[0].Finished);
            Assert.Equal(Morning, meta.TaskDates[1].Finished);
            Assert.Equal("- [ ] edited\n- [x] pasted", document.Text);
        });

        [Fact]
        public void Legacy_migration_preserves_dates_removes_only_eligible_suffixes_and_runs_once() => UiThread.Run(() =>
        {
            const string suffix = " (created: 2026-10-05 08:00)";
            const string finished = " (created: 2026-10-04 07:00; finished: 2026-10-05 18:30)";
            string text = "---\n- [ ] metadata" + suffix + "\n---\n- [ ] visible" + suffix
                + "\n```\n- [ ] code" + suffix + "\n```\n$$\n- [ ] math" + suffix + "\n$$\n- [x] done" + finished;
            var document = new TextDocument(text);
            NoteMeta meta = Meta(version: 0);
            int changed = 0;

            using var controller = new TaskDateController(document, meta, () => true, () => Morning, () => changed++);

            Assert.Equal(1, meta.TaskDatesVersion);
            Assert.Equal(2, meta.TaskDates.Count);
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 0, 0, Morning.Offset), meta.TaskDates[0].Created);
            Assert.Null(meta.TaskDates[0].Finished);
            Assert.Equal(new DateTimeOffset(2026, 10, 4, 7, 0, 0, Morning.Offset), meta.TaskDates[1].Created);
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 18, 30, 0, Morning.Offset), meta.TaskDates[1].Finished);
            Assert.Contains("- [ ] metadata" + suffix, document.Text, StringComparison.Ordinal);
            Assert.Contains("- [ ] code" + suffix, document.Text, StringComparison.Ordinal);
            Assert.Contains("- [ ] math" + suffix, document.Text, StringComparison.Ordinal);
            Assert.Contains("- [ ] visible\n", document.Text, StringComparison.Ordinal);
            Assert.EndsWith("- [x] done", document.Text, StringComparison.Ordinal);
            int changesAfterMigration = changed;
            Assert.True(changesAfterMigration >= 1);
            Assert.False(document.UndoStack.CanUndo);

            TaskDateRecord[] migrated = meta.TaskDates.ToArray();
            controller.Initialize();
            Assert.Equal(changesAfterMigration, changed);
            Assert.Equal(migrated, meta.TaskDates);

            document.Insert(document.GetLineByNumber(4).EndOffset, suffix);
            Assert.Contains("- [ ] visible" + suffix, document.Text, StringComparison.Ordinal);
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 0, 0, Morning.Offset), meta.TaskDates[0].Created);
        });

        [Fact]
        public void Current_metadata_treats_a_typed_date_suffix_as_text_not_authoritative_dates() => UiThread.Run(() =>
        {
            const string fake = " (created: 1999-01-01 00:00; finished: 1999-01-02 00:00)";
            var document = new TextDocument("- [ ] task" + fake);
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);

            Assert.Equal("- [ ] task" + fake, document.Text);
            TaskDateRecord dates = Assert.Single(meta.TaskDates);
            Assert.Equal(Morning, dates.Created);
            Assert.Null(dates.Finished);
            Assert.EndsWith(fake, dates.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void Inserting_a_duplicate_before_an_existing_task_creates_fresh_dates_without_stealing_its_record() => UiThread.Run(() =>
        {
            DateTimeOffset clock = Morning;
            var document = new TextDocument("- [ ] same");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => clock);
            TaskDateRecord original = Assert.Single(meta.TaskDates);

            clock = clock.AddDays(1);
            document.Insert(0, "- [ ] same\n");

            Assert.Equal(2, meta.TaskDates.Count);
            Assert.NotEqual(original.Id, meta.TaskDates[0].Id);
            Assert.Equal(clock, meta.TaskDates[0].Created);
            Assert.Equal(original.Id, meta.TaskDates[1].Id);
            Assert.Equal(original.Created, meta.TaskDates[1].Created);
            TaskDateRecord[] pasted = meta.TaskDates.ToArray();
            document.UndoStack.Undo();
            Assert.Equal(original, Assert.Single(meta.TaskDates));
            document.UndoStack.Redo();
            Assert.Equal(pasted, meta.TaskDates);
        });

        [Fact]
        public void Deleting_one_of_duplicate_tasks_and_undoing_preserves_each_tasks_id() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [ ] same\n- [ ] same");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);
            TaskDateRecord[] original = meta.TaskDates.ToArray();
            Assert.Equal(2, original.Select(task => task.Id).Distinct().Count());

            DocumentLine firstLine = document.GetLineByNumber(1);
            document.Remove(firstLine.Offset, firstLine.TotalLength);
            Assert.Equal("- [ ] same", document.Text);
            Assert.Equal(original[1].Id, Assert.Single(meta.TaskDates).Id);

            document.UndoStack.Undo();
            Assert.Equal("- [ ] same\n- [ ] same", document.Text);
            Assert.Equal(original.Select(task => task.Id), meta.TaskDates.Select(task => task.Id));

            document.UndoStack.Redo();
            Assert.Equal(original[1].Id, Assert.Single(meta.TaskDates).Id);
        });

        [Fact]
        public void Wrapping_a_task_in_a_fence_removes_metadata_and_undo_restores_it() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [ ] task");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);
            TaskDateRecord tracked = Assert.Single(meta.TaskDates);

            ReplaceWithPieces(document, "```\n- [ ] task\n```");
            Assert.Equal("```\n- [ ] task\n```", document.Text);
            Assert.Empty(meta.TaskDates);

            document.UndoStack.Undo();
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(tracked, Assert.Single(meta.TaskDates));

            document.UndoStack.Redo();
            Assert.Empty(meta.TaskDates);
        });

        [Fact]
        public void Joining_a_task_to_prose_removes_metadata_and_undo_restores_it() => UiThread.Run(() =>
        {
            var document = new TextDocument("heading\n- [ ] task");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);
            TaskDateRecord tracked = Assert.Single(meta.TaskDates);

            document.Remove("heading".Length, 1);
            Assert.Equal("heading- [ ] task", document.Text);
            Assert.Empty(meta.TaskDates);

            document.UndoStack.Undo();
            Assert.Equal("heading\n- [ ] task", document.Text);
            Assert.Equal(tracked, Assert.Single(meta.TaskDates));
        });

        [Fact]
        public void Splitting_a_task_with_text_pieces_keeps_the_old_id_on_the_left_task() => UiThread.Run(() =>
        {
            var document = new TextDocument("- [ ] Send report today");
            NoteMeta meta = Meta();
            using var controller = new TaskDateController(document, meta, () => true, () => Morning);
            string originalId = Assert.Single(meta.TaskDates).Id;

            ReplaceWithPieces(document, "- [ ] Send report\n- [ ] today");

            Assert.Equal(2, meta.TaskDates.Count);
            Assert.Equal(originalId, meta.TaskDates[0].Id);
            Assert.NotEqual(originalId, meta.TaskDates[1].Id);
            Assert.Equal("- [ ] Send report", meta.TaskDates[0].Text);
            Assert.Equal("- [ ] today", meta.TaskDates[1].Text);
        });

        [Fact]
        public void Reattaching_a_controller_to_the_same_document_keeps_metadata_undo_safe() => UiThread.Run(() =>
        {
            DateTimeOffset clock = Morning;
            var document = new TextDocument("- [ ] task");
            NoteMeta meta = Meta();
            var first = new TaskDateController(document, meta, () => true, () => clock);
            TaskDateRecord pending = Assert.Single(meta.TaskDates);

            clock = clock.AddHours(2);
            document.Replace(3, 1, "x");
            TaskDateRecord completed = Assert.Single(meta.TaskDates);
            first.Dispose();

            using var transferred = new TaskDateController(document, meta, () => true, () => clock);
            document.UndoStack.Undo();
            Assert.Equal("- [ ] task", document.Text);
            Assert.Equal(pending, Assert.Single(meta.TaskDates));

            document.UndoStack.Redo();
            Assert.Equal("- [x] task", document.Text);
            Assert.Equal(completed, Assert.Single(meta.TaskDates));
        });

        [Fact]
        public void Dispose_stops_following_document_edits() => UiThread.Run(() =>
        {
            var document = new TextDocument();
            NoteMeta meta = Meta();
            var controller = new TaskDateController(document, meta, () => true, () => Morning);
            controller.Dispose();

            document.Insert(0, "- [ ] after disposal");

            Assert.Equal("- [ ] after disposal", document.Text);
            Assert.Empty(meta.TaskDates);
        });
    }
}
