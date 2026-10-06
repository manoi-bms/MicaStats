using System;
using System.IO;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class TaskDatePersistenceTests
    {
        private static readonly DateTime T0 = new(2026, 10, 6, 2, 15, 0, DateTimeKind.Utc);

        [Fact]
        public void New_notes_start_at_the_current_task_date_version()
        {
            NoteMeta meta = NoteStore.NewMeta(T0, 1, null);

            Assert.Equal(1, meta.TaskDatesVersion);
            Assert.Empty(meta.TaskDates);
        }

        [Fact]
        public void Task_dates_round_trip_in_encrypted_meta_while_markdown_stays_plain()
        {
            using var dir = new PadTempDir();
            var store = new NoteStore(dir.Root, warn: _ => { });
            NoteMeta meta = NoteStore.NewMeta(T0, 1, null);
            var dates = new TaskDateRecord(
                "task-1",
                8,
                "- [ ] prepare report",
                new DateTimeOffset(2026, 10, 6, 9, 15, 0, TimeSpan.FromHours(7)),
                new DateTimeOffset(2026, 10, 6, 10, 30, 0, TimeSpan.FromHours(7)));
            meta.TaskDatesVersion = 1;
            meta.TaskDates.Add(dates);

            Assert.True(store.SaveNote(meta, "heading\n- [ ] prepare report", store.NextVersion()));

            Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(store.MetaPath(meta.Id))));
            Assert.Equal("heading\n- [ ] prepare report", new NoteStore(dir.Root).LoadText(meta.Id));
            NoteMeta restored = Assert.IsType<NoteMeta>(new NoteStore(dir.Root).LoadMeta(meta.Id));
            Assert.Equal(1, restored.TaskDatesVersion);
            Assert.Equal(dates, Assert.Single(restored.TaskDates));
        }

        [Theory]
        [InlineData("{\"Id\":\"0123456789abcdef0123456789abcdef\"}")]
        [InlineData("{\"Id\":\"0123456789abcdef0123456789abcdef\",\"TaskDates\":null}")]
        public void Legacy_metadata_defaults_to_no_task_dates(string json)
        {
            using var dir = new PadTempDir();
            var store = new NoteStore(dir.Root, warn: _ => { });
            const string id = "0123456789abcdef0123456789abcdef";
            Directory.CreateDirectory(store.NoteDir(id));
            File.WriteAllText(store.MetaPath(id), json);
            File.WriteAllText(store.CurrentPath(id), "legacy note");

            NoteMeta restored = Assert.IsType<NoteMeta>(store.LoadMeta(id));

            Assert.Equal(0, restored.TaskDatesVersion);
            Assert.Empty(restored.TaskDates);
        }

        [Fact]
        public void Clone_owns_its_task_date_list()
        {
            NoteMeta original = NoteStore.NewMeta(T0, 1, null);
            original.TaskDates.Add(new TaskDateRecord(
                "task-1", 0, "- [ ] one", new DateTimeOffset(T0), null));

            NoteMeta copy = original.Clone();
            copy.TaskDates.Add(new TaskDateRecord(
                "task-2", 10, "- [x] two", new DateTimeOffset(T0.AddMinutes(1)), new DateTimeOffset(T0.AddMinutes(2))));

            Assert.Single(original.TaskDates);
            Assert.Equal(2, copy.TaskDates.Count);
            Assert.NotSame(original.TaskDates, copy.TaskDates);
        }

        [Fact]
        public void Metadata_only_autosave_restores_dates_without_rewriting_note_text()
        {
            using var env = new PadTestEnv();
            OpenNote note = env.Workspace.NewNote();
            const string markdown = "Plan\n- [ ] send invoice";
            PadTestEnv.Type(env.Workspace, note, markdown);
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            byte[] textBefore = File.ReadAllBytes(env.Store.CurrentPath(note.Id));
            DateTime lastEdit = note.LastEditUtc;
            bool snapshotDirty = note.ChangedSinceSnapshot;
            int textNotifications = 0;
            env.Workspace.NoteTextChanged += _ => textNotifications++;

            var dates = new TaskDateRecord(
                "task-1", 5, "- [ ] send invoice", new DateTimeOffset(env.Clock.UtcNow), null);
            note.Meta.TaskDatesVersion = 1;
            note.Meta.TaskDates.Add(dates);
            env.Workspace.NotifyTaskDatesChanged(note);
            env.Clock.Advance(1);
            env.Workspace.Tick();
            env.Flush();

            Assert.Equal(textBefore, File.ReadAllBytes(env.Store.CurrentPath(note.Id)));
            Assert.Equal(markdown, env.DiskText(note));
            Assert.Equal(lastEdit, note.LastEditUtc);
            Assert.Equal(snapshotDirty, note.ChangedSinceSnapshot);
            Assert.Equal(0, textNotifications);

            PadWorkspace restoredWorkspace = env.NewWorkspace();
            OpenNote restored = Assert.Single(restoredWorkspace.Restore());
            Assert.Equal(markdown, restored.TextProvider());
            Assert.Equal(1, restored.Meta.TaskDatesVersion);
            Assert.Equal(dates, Assert.Single(restored.Meta.TaskDates));
        }

        [Fact]
        public void A_pending_text_change_is_not_downgraded_by_a_task_date_notification()
        {
            using var env = new PadTestEnv();
            OpenNote note = env.Workspace.NewNote();
            PadTestEnv.Type(env.Workspace, note, "- [ ] before");
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));

            PadTestEnv.Type(env.Workspace, note, "- [x] after");
            note.Meta.TaskDates.Add(new TaskDateRecord(
                "task-1",
                0,
                "- [x] after",
                new DateTimeOffset(env.Clock.UtcNow),
                new DateTimeOffset(env.Clock.UtcNow)));
            env.Workspace.NotifyTaskDatesChanged(note);
            env.Clock.Advance(1);
            env.Workspace.Tick();
            env.Flush();

            Assert.Equal("- [x] after", env.DiskText(note));
            Assert.Single(env.Store.LoadMeta(note.Id)!.TaskDates);
        }
    }
}
