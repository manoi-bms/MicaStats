using System;
using System.IO;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class TaskDateHistoryMigrationTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc);
        private readonly PadTempDir _dir = new();
        private readonly NoteStore _store;

        public TaskDateHistoryMigrationTests() => _store = new NoteStore(_dir.Root, warn: _ => { });

        public void Dispose() => _dir.Dispose();

        private NoteMeta LegacyNote(string current = "- [ ] current")
        {
            NoteMeta meta = NoteStore.NewMeta(T0, 1, null);
            meta.TaskDatesVersion = 0;
            Assert.True(_store.SaveNote(meta, current, _store.NextVersion()));
            return meta;
        }

        private void Upgrade(NoteMeta meta)
        {
            meta.TaskDatesVersion = 1;
            Assert.True(_store.SaveNote(meta, null, _store.NextVersion()));
        }

        [Fact]
        public void Version_transition_scrubs_encrypted_history_without_changing_names_stamps_or_newlines()
        {
            NoteMeta meta = LegacyNote();
            const string before = "Title\r\n- [ ] first (created: 2026-10-06 09:00)\r\nkeep\n- [x] second (created: 2026-10-06 09:05; finished: 2026-10-06 09:20)\n";
            const string after = "Title\r\n- [ ] first\r\nkeep\n- [x] second\n";
            SnapshotInfo written = _store.WriteSnapshot(meta.Id, before, new DateTime(2026, 10, 6, 9, 30, 0));
            string path = written.FilePath;
            DateTime stamp = written.Stamp;

            Upgrade(meta);

            SnapshotInfo restored = Assert.Single(_store.ListSnapshots(meta.Id));
            Assert.Equal(path, restored.FilePath);
            Assert.Equal(stamp, restored.Stamp);
            Assert.Equal(after, _store.ReadSnapshot(restored));
            Assert.True(StoreCipher.IsEncrypted(File.ReadAllBytes(restored.FilePath)));
        }

        [Fact]
        public void Structural_markdown_and_code_examples_keep_legacy_looking_suffixes()
        {
            NoteMeta meta = LegacyNote();
            const string suffix = " (created: 2026-10-06 09:00)";
            string before = string.Join("\r\n", new[]
            {
                "---",
                "- [ ] front matter" + suffix,
                "---",
                "```md",
                "- [ ] fenced example" + suffix,
                "```",
                "$$",
                "- [ ] math example" + suffix,
                "$$",
                "- [ ] table | cell" + suffix,
                "--- | ---",
                "- [ ] ordinary" + suffix,
            });
            SnapshotInfo snapshot = _store.WriteSnapshot(meta.Id, before, new DateTime(2026, 10, 6, 9, 30, 0));

            Upgrade(meta);

            string expected = before.Replace("- [ ] ordinary" + suffix, "- [ ] ordinary", StringComparison.Ordinal);
            Assert.Equal(expected, _store.ReadSnapshot(snapshot));
        }

        [Fact]
        public void A_version_one_note_never_scrubs_history_lookalikes()
        {
            NoteMeta meta = NoteStore.NewMeta(T0, 1, null);
            Assert.True(_store.SaveNote(meta, "- [ ] current", _store.NextVersion()));
            const string lookalike = "- [ ] user text (created: 2026-10-06 09:00)";
            SnapshotInfo snapshot = _store.WriteSnapshot(meta.Id, lookalike, new DateTime(2026, 10, 6, 9, 30, 0));

            Assert.True(_store.SaveNote(meta, null, _store.NextVersion()));

            Assert.Equal(lookalike, _store.ReadSnapshot(snapshot));
        }

        [Fact]
        public void Migration_runs_once_and_does_not_scrub_snapshots_written_later()
        {
            NoteMeta meta = LegacyNote();
            SnapshotInfo old = _store.WriteSnapshot(meta.Id,
                "- [ ] old (created: 2026-10-06 09:00)", new DateTime(2026, 10, 6, 9, 0, 0));
            Upgrade(meta);
            Assert.Equal("- [ ] old", _store.ReadSnapshot(old));

            const string later = "- [ ] later (created: 2026-10-06 10:00)";
            SnapshotInfo recent = _store.WriteSnapshot(meta.Id, later, new DateTime(2026, 10, 6, 10, 0, 0));
            using (new FileStream(recent.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.True(_store.SaveNote(meta, null, _store.NextVersion()));

            Assert.Equal(later, _store.ReadSnapshot(recent));
            Assert.Equal(2, _store.ListSnapshots(meta.Id).Count);
        }

        [Fact]
        public void A_failed_history_read_keeps_the_version_transition_pending_for_retry()
        {
            NoteMeta meta = LegacyNote();
            SnapshotInfo snapshot = _store.WriteSnapshot(meta.Id,
                "- [ ] retry (created: 2026-10-06 09:00)", new DateTime(2026, 10, 6, 9, 0, 0));
            meta.TaskDatesVersion = 1;

            using (new FileStream(snapshot.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => _store.SaveNote(meta, null, _store.NextVersion()));

            Assert.Equal(0, _store.LoadMeta(meta.Id)!.TaskDatesVersion);
            Assert.True(_store.SaveNote(meta, null, _store.NextVersion()));
            Assert.Equal("- [ ] retry", _store.ReadSnapshot(snapshot));
            Assert.Equal(1, _store.LoadMeta(meta.Id)!.TaskDatesVersion);
        }
    }
}
