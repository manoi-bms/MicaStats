using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A recycle bin that deletes for real but records what it was given, or refuses.</summary>
    internal sealed class FakeRecycleBin : IRecycleBin
    {
        public bool Succeed { get; set; } = true;

        public List<string> Recycled { get; } = new();

        public bool TryRecycle(string path)
        {
            if (!Succeed) return false;
            Recycled.Add(path);
            Directory.Delete(path, recursive: true);
            return true;
        }
    }

    /// <summary>History pruning and the purge of long-closed notes.</summary>
    public class PadMaintenanceTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 9, 29, 5, 0, 0, DateTimeKind.Utc);
        private readonly PadTempDir _dir = new();
        private readonly NoteStore _store;

        public PadMaintenanceTests()
        {
            _store = new NoteStore(_dir.Root, warn: _ => { });
        }

        public void Dispose() => _dir.Dispose();

        private NoteMeta Note(string text, DateTime? closedAtUtc = null)
        {
            var meta = NoteStore.NewMeta(T0, 1, null);
            meta.ClosedAtUtc = closedAtUtc;
            Assert.True(_store.SaveNote(meta, text, 1));
            return meta;
        }

        [Fact]
        public void Pruning_deletes_what_the_policy_selects()
        {
            var meta = Note("x");
            var now = new DateTime(2026, 9, 29, 12, 0, 0);
            DateTime day = now.AddDays(-2).Date;
            _store.WriteSnapshot(meta.Id, "a", day.AddHours(10).AddMinutes(5));
            _store.WriteSnapshot(meta.Id, "b", day.AddHours(10).AddMinutes(40));
            _store.WriteSnapshot(meta.Id, "c", now.AddMinutes(-5));

            Assert.Equal(1, _store.PruneHistory(meta.Id, now, 90));
            Assert.Equal(new[] { "c", "b" }, _store.ListSnapshots(meta.Id).Select(s => _store.ReadSnapshot(s)));
        }

        [Fact]
        public void Notes_closed_longer_than_the_limit_go_to_the_recycle_bin()
        {
            DateTime now = T0.AddDays(200);
            var oldClosed = Note("old", now.AddDays(-100));
            var recentClosed = Note("recent", now.AddDays(-10));
            var open = Note("open");
            var bin = new FakeRecycleBin();

            Assert.Equal(1, _store.PurgeClosedNotes(now, 90, bin));

            Assert.Equal(new[] { _store.NoteDir(oldClosed.Id) }, bin.Recycled);
            Assert.True(Directory.Exists(_store.NoteDir(recentClosed.Id)));
            Assert.True(Directory.Exists(_store.NoteDir(open.Id)));
        }

        [Fact]
        public void A_note_the_recycle_bin_refuses_is_left_in_place()
        {
            var meta = Note("keep me", T0);
            var bin = new FakeRecycleBin { Succeed = false };

            Assert.False(_store.DeleteNote(meta.Id, bin));
            Assert.True(Directory.Exists(_store.NoteDir(meta.Id)));
            Assert.Equal("keep me", _store.LoadText(meta.Id));
        }

        [Fact]
        public void PruneAll_prunes_history_and_purges_closed_notes()
        {
            DateTime utcNow = T0.AddDays(200);
            var open = Note("open");
            DateTime localNow = utcNow.ToLocalTime();
            _store.WriteSnapshot(open.Id, "ancient", localNow.AddDays(-150));
            _store.WriteSnapshot(open.Id, "recent", localNow.AddMinutes(-1));
            var closed = Note("closed", utcNow.AddDays(-120));
            var bin = new FakeRecycleBin();

            _store.PruneAll(utcNow, 90, bin);

            Assert.Single(_store.ListSnapshots(open.Id));
            Assert.False(Directory.Exists(_store.NoteDir(closed.Id)));
        }
    }
}
