using System;
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
        public void Deleting_a_closed_note_uses_the_recycle_bin()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "bye");
            Ws.Close(note);

            Assert.True(Ws.DeleteClosed(note.Id));

            Assert.Equal(new[] { _env.Store.NoteDir(note.Id) }, _env.Bin.Recycled);
            Assert.Empty(Ws.ClosedNotes());
        }
    }
}
