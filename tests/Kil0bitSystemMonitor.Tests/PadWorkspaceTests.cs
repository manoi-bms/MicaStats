using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The rules that make MicaPad safe: when text reaches disk, what closing means, what comes back.</summary>
    public class PadWorkspaceTests : IDisposable
    {
        private readonly PadTestEnv _env = new();

        private PadWorkspace Ws => _env.Workspace;

        public void Dispose() => _env.Dispose();

        [Fact]
        public void A_new_note_is_saved_one_second_after_typing_stops()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "hello");

            _env.Clock.Advance(0.5);
            Ws.Tick();
            _env.Flush();
            Assert.Equal("", _env.DiskText(note));

            _env.Clock.Advance(0.5);
            Ws.Tick();
            _env.Flush();
            Assert.Equal("hello", _env.DiskText(note));
            Assert.Equal(SaveState.Saved, note.SaveState);
        }

        [Fact]
        public void Continuous_typing_reaches_disk_within_five_seconds()
        {
            var note = Ws.NewNote();
            _env.Flush();
            string text = "";
            for (int i = 0; i < 10; i++)
            {
                text += "x";
                PadTestEnv.Type(Ws, note, text);
                Ws.Tick();
                _env.Clock.Advance(0.5);
            }

            PadTestEnv.Type(Ws, note, text + "y");   // five seconds after the first keystroke
            Ws.Tick();
            _env.Flush();

            Assert.Equal(text + "y", _env.DiskText(note));
        }

        [Fact]
        public void The_title_follows_the_first_line()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "  \nShopping list\nmilk");

            _env.Clock.Advance(1);
            Ws.Tick();

            Assert.Equal("Shopping list", note.Title);
        }

        [Fact]
        public void Renaming_pins_the_title_and_clearing_it_restores_the_automatic_one()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "Groceries");

            Ws.Rename(note, "Weekend");
            _env.Clock.Advance(1);
            Ws.Tick();
            Assert.Equal("Weekend", note.Title);

            Ws.Rename(note, "  ");
            Assert.Equal("Groceries", note.Title);
        }

        [Fact]
        public void Restore_brings_back_the_open_notes_their_text_and_the_active_tab()
        {
            var first = Ws.NewNote();
            PadTestEnv.Type(Ws, first, "one");
            var second = Ws.NewNote();
            PadTestEnv.Type(Ws, second, "two");
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            var notes = restored.Restore();

            Assert.Equal(new[] { "one", "two" }, notes.Select(n => n.TextProvider()));
            Assert.Equal(new[] { "one", "two" }, notes.Select(n => n.Title));
            Assert.Equal(second.Id, restored.Active!.Id);
        }

        [Fact]
        public void Restore_is_idempotent()
        {
            Ws.NewNote();
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            restored.Restore();
            restored.Restore();

            Assert.Single(restored.Open);
        }

        [Fact]
        public void Closing_a_note_moves_it_to_closed_notes_and_reopening_brings_it_back()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "keep this");

            Ws.Close(note);

            Assert.Empty(Ws.Open);
            Assert.Equal(note.Id, Assert.Single(Ws.ClosedNotes()).Id);

            var back = Ws.ReopenLastClosed();

            Assert.NotNull(back);
            Assert.Equal("keep this", back!.TextProvider());
            Assert.Empty(Ws.ClosedNotes());
            Assert.Same(back, Ws.Active);
        }

        [Fact]
        public void Closing_a_note_that_never_had_text_deletes_it()
        {
            var note = Ws.NewNote();

            Ws.Close(note);
            _env.Flush();

            Assert.False(Directory.Exists(_env.Store.NoteDir(note.Id)));
            Assert.Empty(Ws.ClosedNotes());
        }

        [Fact]
        public void Closing_the_active_tab_activates_its_neighbour()
        {
            var a = Ws.NewNote();
            var b = Ws.NewNote();
            var c = Ws.NewNote();
            Ws.SetActive(b);

            Ws.Close(b);

            Assert.Equal(new[] { a, c }, Ws.Open);
            Assert.Same(c, Ws.Active);
            Assert.True(c.IsActive);
        }

        [Fact]
        public void A_pause_after_a_minute_of_changes_takes_a_snapshot()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "draft");

            _env.Clock.Advance(59);
            Ws.Tick();
            _env.Flush();
            Assert.Empty(_env.Store.ListSnapshots(note.Id));

            _env.Clock.Advance(1);
            Ws.Tick();
            _env.Flush();
            var snapshot = Assert.Single(_env.Store.ListSnapshots(note.Id));
            Assert.Equal("draft", _env.Store.ReadSnapshot(snapshot));
        }

        [Fact]
        public void Unchanged_text_is_never_snapshotted_twice()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "same");
            _env.Clock.Advance(60);
            Ws.Tick();

            PadTestEnv.Type(Ws, note, "same");
            _env.Clock.Advance(61);
            Ws.Tick();
            _env.Flush();

            Assert.Single(_env.Store.ListSnapshots(note.Id));
        }

        [Fact]
        public void Closing_takes_a_snapshot()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "last words");

            Ws.Close(note);
            _env.Flush();

            Assert.Single(_env.Store.ListSnapshots(note.Id));
        }

        [Fact]
        public void FlushAll_writes_pending_changes_and_the_session()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "unsaved");

            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            Assert.Equal("unsaved", _env.DiskText(note));
            Assert.Equal(new[] { note.Id }, _env.Store.LoadSession().OpenNoteIds);
        }

        [Fact]
        public void FlushAll_writes_directly_when_the_writer_is_stuck()
        {
            var note = Ws.NewNote();
            _env.Flush();
            using var release = new ManualResetEventSlim();
            _env.Writer.Enqueue("stuck", () => release.Wait());
            PadTestEnv.Type(Ws, note, "rescued");

            Assert.False(Ws.FlushAll(TimeSpan.FromMilliseconds(200)));
            Assert.Equal("rescued", _env.DiskText(note));

            release.Set();
            _env.Flush();
            Assert.Equal("rescued", _env.DiskText(note));
        }

        [Fact]
        public void A_save_still_queued_at_session_end_is_written_directly()
        {
            var note = Ws.NewNote();
            _env.Flush();
            using var release = new ManualResetEventSlim();
            _env.Writer.Enqueue("stuck", () => release.Wait());
            PadTestEnv.Type(Ws, note, "draft");
            _env.Clock.Advance(61);
            Ws.Tick();                                 // queues the save and the pause snapshot

            Assert.False(Ws.FlushAll(TimeSpan.FromMilliseconds(200)));
            Assert.Equal("draft", _env.DiskText(note));

            release.Set();
            _env.Flush();
            Assert.Equal("draft", _env.DiskText(note));
        }

        [Fact]
        public void A_note_closed_while_the_writer_is_stuck_is_written_at_session_end()
        {
            var note = Ws.NewNote();
            _env.Flush();
            using var release = new ManualResetEventSlim();
            _env.Writer.Enqueue("stuck", () => release.Wait());
            PadTestEnv.Type(Ws, note, "bye");
            Ws.Close(note);

            Assert.False(Ws.FlushAll(TimeSpan.FromMilliseconds(200)));
            Assert.Equal("bye", _env.DiskText(note));
            Assert.NotNull(_env.Store.LoadMeta(note.Id)!.ClosedAtUtc);

            release.Set();
            _env.Flush();
        }

        [Fact]
        public void Reopening_while_the_writer_is_stuck_keeps_the_newest_text()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "old");
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));
            using var release = new ManualResetEventSlim();
            _env.Writer.Enqueue("stuck", () => release.Wait());
            PadTestEnv.Type(Ws, note, "newest");
            Ws.Close(note);

            var back = Ws.ReopenLastClosed();

            Assert.Equal("newest", back!.TextProvider());
            release.Set();
            _env.Flush();
            Assert.Equal("newest", _env.DiskText(back));
            Assert.Null(_env.Store.LoadMeta(back.Id)!.ClosedAtUtc);
        }

        [Fact]
        public void A_note_whose_text_cannot_be_read_is_skipped_at_restore_kept_in_the_session_and_left_untouched()
        {
            var readable = Ws.NewNote();
            PadTestEnv.Type(Ws, readable, "fine");
            var locked = Ws.NewNote();
            PadTestEnv.Type(Ws, locked, "newest text");
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));
            _env.Store.WriteSnapshot(locked.Id, "older text", new DateTime(2026, 9, 29, 10, 0, 0));
            string path = _env.Store.CurrentPath(locked.Id);
            byte[] before = File.ReadAllBytes(path);

            var restored = _env.NewWorkspace();
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(new[] { readable.Id }, restored.Restore().Select(n => n.Id));
                Assert.True(restored.FlushAll(TimeSpan.FromSeconds(5)));   // everything an exit would write
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(new[] { readable.Id, locked.Id }, _env.Store.LoadSession().OpenNoteIds);

            var later = _env.NewWorkspace();
            Assert.Contains(later.Restore(), n => n.Id == locked.Id && n.TextProvider() == "newest text");
        }

        [Fact]
        public void Reopening_a_closed_note_whose_text_cannot_be_read_returns_null_and_writes_nothing()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "keep this");
            Ws.Close(note);
            _env.Flush();
            string path = _env.Store.CurrentPath(note.Id);
            byte[] before = File.ReadAllBytes(path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Null(Ws.Reopen(note.Id));
                Assert.Empty(Ws.Open);
                Assert.Equal(note.Id, Assert.Single(Ws.ClosedNotes()).Id);
                _env.Flush();
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.NotNull(_env.Store.LoadMeta(note.Id)!.ClosedAtUtc);

            var back = Ws.Reopen(note.Id);
            Assert.Equal("keep this", back!.TextProvider());
        }

        [Fact]
        public void A_write_that_keeps_failing_is_logged_once_rather_than_on_every_retry()
        {
            var errors = new ConcurrentQueue<string>();
            int failures = 0;
            _env.Writer.Completed += (key, error) =>
            {
                if (error != null && key.Contains("#snapshot#")) Interlocked.Increment(ref failures);
            };
            using var ws = new PadWorkspace(_env.Store, new PadWorkspaceOptions
            {
                Writer = _env.Writer,
                UtcClock = () => _env.Clock.UtcNow,
                RecycleBin = _env.Bin,
                Warn = _ => { },
                Error = (message, _) => errors.Enqueue(message),
                AnsiCodePage = 874,
            });
            var note = ws.NewNote();
            PadTestEnv.Type(ws, note, "text");
            _env.Flush();
            string blocker = _env.Store.HistoryDir(note.Id);
            File.WriteAllText(blocker, "a file where the history folder should be");

            Assert.True(ws.SnapshotNow(note, SnapshotReason.BeforeReplace));
            Assert.False(_env.Writer.FlushAll(TimeSpan.FromMilliseconds(300)));   // retried every 10 ms meanwhile

            Assert.True(Volatile.Read(ref failures) >= 2);
            Assert.Contains("version of note " + note.Id, Assert.Single(errors));

            File.Delete(blocker);
            _env.Flush();
            Assert.Single(_env.Store.ListSnapshots(note.Id));
        }

        [Fact]
        public void Deleting_a_closed_note_uses_the_recycle_bin()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "bye");
            Ws.Close(note);

            Assert.True(Ws.DeleteClosed(note.Id));

            Assert.Equal(new[] { _env.Store.NoteDir(note.Id) }, _env.Bin.Recycled);
            Assert.Empty(Ws.ClosedNotes());
        }

        // ---- scrubs left at exit (final review, items 3 and 4) -------------------------------

        private const string Reference = "{{secret:K7Q2M9XD}}";

        [Fact]
        public void A_pending_scrub_names_its_ids_never_its_value()
        {
            var scrub = new PendingScrub("note1", "K7Q2M9XD", "hunter2", Reference);

            Assert.Contains("note1", scrub.ToString(), StringComparison.Ordinal);
            Assert.Contains("K7Q2M9XD", scrub.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", scrub.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", $"{scrub}", StringComparison.Ordinal);
        }

        [Fact]
        public void Scrubs_left_at_exit_run_and_go_once_clean()
        {
            var note = Ws.NewNote();
            var version = _env.Store.WriteSnapshot(note.Id, "pw=hunter2", new DateTime(2026, 10, 1, 9, 0, 0));
            Ws.AddPendingScrub(new PendingScrub(note.Id, "K7Q2M9XD", "hunter2", Reference));
            Assert.Equal(1, Ws.PendingScrubCount);

            Assert.Equal(0, Ws.RunPendingScrubs());

            Assert.Equal(0, Ws.PendingScrubCount);
            Assert.Equal("pw=" + Reference, _env.Store.ReadSnapshot(version));
        }

        [Fact]
        public void A_scrub_left_at_exit_that_fails_is_kept_and_logged_by_id_only()
        {
            var warnings = new ConcurrentQueue<string>();
            using var ws = new PadWorkspace(_env.Store, new PadWorkspaceOptions
            {
                Writer = _env.Writer,
                UtcClock = () => _env.Clock.UtcNow,
                RecycleBin = _env.Bin,
                Warn = warnings.Enqueue,
                Error = (message, _) => warnings.Enqueue(message),
                AnsiCodePage = 874,
            });
            var note = ws.NewNote();
            var version = _env.Store.WriteSnapshot(note.Id, "pw=hunter2", new DateTime(2026, 10, 1, 9, 0, 0));
            ws.AddPendingScrub(new PendingScrub(note.Id, "K7Q2M9XD", "hunter2", Reference));

            using (new DeniedListing(_env.Store.HistoryDir(note.Id)))
            {
                Assert.Equal(1, ws.RunPendingScrubs());
            }

            Assert.Equal(1, ws.PendingScrubCount);
            Assert.Contains(warnings, w => w.Contains(note.Id, StringComparison.Ordinal) && w.Contains("K7Q2M9XD", StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, w => w.Contains("hunter2", StringComparison.Ordinal));

            Assert.Equal(0, ws.RunPendingScrubs());   // a second flush (session end, then exit) tries again
            Assert.Equal("pw=" + Reference, _env.Store.ReadSnapshot(version));
        }
    }
}
