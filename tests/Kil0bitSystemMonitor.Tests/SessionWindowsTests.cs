using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>session.json with windows: older files, files older MicaPads can read, bad files, and where a file goes.</summary>
    public class SessionWindowsTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        private readonly NoteStore _store;

        public SessionWindowsTests() => _store = new NoteStore(_dir.Root, warn: _ => { });

        public void Dispose() => _dir.Dispose();

        /// <summary>A note on disk, so the session may list it.</summary>
        private string Note(string text)
        {
            var meta = NoteStore.NewMeta(new DateTime(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc), 1, null);
            Assert.True(_store.SaveNote(meta, text, _store.NextVersion()));
            return meta.Id;
        }

        [Fact]
        public void A_v1_session_becomes_one_window_from_the_old_fields()
        {
            string a = Note("a"), b = Note("b");
            // Exactly the shape v1.12 writes: no Windows.
            File.WriteAllText(_store.SessionPath,
                "{ \"WindowOpen\": true, \"Left\": 120, \"Top\": 80, \"Width\": 700, \"Height\": 500, \"Maximized\": true, " +
                "\"AlwaysOnTop\": true, \"Zoom\": 1.5, \"OpenNoteIds\": [\"" + a + "\", \"" + b + "\"], \"ActiveNoteId\": \"" + b + "\", \"Tabs\": {} }");

            var window = Assert.Single(_store.LoadSession().Windows!);

            Assert.False(string.IsNullOrEmpty(window.Id));
            Assert.True(window.Open);
            Assert.Equal(120, window.Left);
            Assert.Equal(80, window.Top);
            Assert.Equal(700, window.Width);
            Assert.Equal(500, window.Height);
            Assert.True(window.Maximized);
            Assert.True(window.AlwaysOnTop);
            Assert.Equal(1.5, window.Zoom);
            Assert.Equal(new[] { a, b }, window.NoteIds);
            Assert.Equal(b, window.ActiveNoteId);
        }

        [Fact]
        public void An_empty_windows_list_is_read_as_v1()
        {
            string a = Note("a");
            File.WriteAllText(_store.SessionPath, "{ \"OpenNoteIds\": [\"" + a + "\"], \"Windows\": [] }");

            Assert.Equal(new[] { a }, Assert.Single(_store.LoadSession().Windows!).NoteIds);
        }

        [Fact]
        public void A_v2_session_round_trips()
        {
            string a = Note("a"), b = Note("b"), c = Note("c");
            var lastActive = new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc);
            var session = new SessionState
            {
                Windows = new List<PadWindowState>
                {
                    new() { Id = "w1", Open = true, Left = 10, Top = 20, Width = 800, Height = 600, Zoom = 1.2, NoteIds = { a, b }, ActiveNoteId = b },
                    new() { Id = "w2", Open = true, Left = 300, Top = 200, Width = 640, Height = 480, Maximized = true, AlwaysOnTop = true,
                            NoteIds = { c }, ActiveNoteId = c, LastActiveUtc = lastActive },
                },
            };
            SessionWindows.WriteMirror(session);
            _store.SaveSession(session);

            var back = new NoteStore(_dir.Root, warn: _ => { }).LoadSession().Windows!;

            Assert.Equal(new[] { "w1", "w2" }, back.Select(w => w.Id));
            Assert.Equal(new[] { a, b }, back[0].NoteIds);
            Assert.Equal(1.2, back[0].Zoom);
            var w2 = back[1];
            Assert.Equal(300, w2.Left);
            Assert.Equal(200, w2.Top);
            Assert.Equal(640, w2.Width);
            Assert.Equal(480, w2.Height);
            Assert.True(w2.Maximized);
            Assert.True(w2.AlwaysOnTop);
            Assert.Equal(new[] { c }, w2.NoteIds);
            Assert.Equal(c, w2.ActiveNoteId);
            Assert.Equal(lastActive, w2.LastActiveUtc);
        }

        [Fact]
        public void A_v2_file_keeps_the_v1_fields_an_older_micapad_reads()
        {
            string a = Note("a"), b = Note("b"), c = Note("c");
            var session = new SessionState
            {
                Windows = new List<PadWindowState>
                {
                    new() { Id = "w1", Open = false, Left = 10, Top = 20, Width = 800, Height = 600, Zoom = 1.2, NoteIds = { a, b }, ActiveNoteId = b },
                    new() { Id = "w2", Open = true, Left = 300, NoteIds = { c }, ActiveNoteId = c },
                },
            };

            SessionWindows.WriteMirror(session);
            using var json = JsonDocument.Parse(NoteStore.SerializeSession(session));
            var root = json.RootElement;

            // What v1.12 reads: every tab (none vanishes after a downgrade), and the first window's place, zoom and active tab.
            Assert.Equal(new[] { a, b, c }, root.GetProperty("OpenNoteIds").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(b, root.GetProperty("ActiveNoteId").GetString());
            Assert.Equal(10, root.GetProperty("Left").GetDouble());
            Assert.Equal(800, root.GetProperty("Width").GetDouble());
            Assert.Equal(1.2, root.GetProperty("Zoom").GetDouble());
            Assert.True(root.GetProperty("WindowOpen").GetBoolean());       // some window was open: v1.12 reopens at login
        }

        [Fact]
        public void A_session_an_older_micapad_rewrote_loads_as_one_window_with_every_tab()
        {
            string a = Note("a"), b = Note("b"), c = Note("c");
            var session = new SessionState
            {
                Windows = new List<PadWindowState> { new() { Id = "w1", NoteIds = { a, b } }, new() { Id = "w2", NoteIds = { c } } },
            };
            SessionWindows.WriteMirror(session);
            session.Windows = null;                          // v1.12 does not know Windows and writes the old fields only
            _store.SaveSession(session);

            Assert.Equal(new[] { a, b, c }, Assert.Single(_store.LoadSession().Windows!).NoteIds);
        }

        [Fact]
        public void Bad_windows_are_dropped_and_a_note_in_two_windows_stays_in_the_first()
        {
            string a = Note("a"), b = Note("b");
            string gone = Guid.NewGuid().ToString("N");
            var session = new SessionState
            {
                OpenNoteIds = { a, b },
                Windows = new List<PadWindowState>
                {
                    new() { Id = "w1", NoteIds = { a, gone }, ActiveNoteId = gone },
                    new() { Id = "", NoteIds = { b } },              // no id
                    new() { Id = "w1", NoteIds = { b } },            // the same id again
                    new() { Id = "w3", NoteIds = { a, b } },         // a is w1's already
                    new() { Id = "w4", NoteIds = { gone } },         // nothing left to show
                },
            };
            _store.SaveSession(session);

            var back = _store.LoadSession().Windows!;

            Assert.Equal(new[] { "w1", "w3" }, back.Select(w => w.Id));
            Assert.Equal(new[] { a }, back[0].NoteIds);
            Assert.Null(back[0].ActiveNoteId);
            Assert.Equal(new[] { b }, back[1].NoteIds);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("{ \"Windows\": 5 }")]
        [InlineData("{ \"Windows\": [ { \"Id\": 7 } ] }")]
        public void An_unreadable_session_is_rebuilt_into_one_window(string json)
        {
            string a = Note("a");
            File.WriteAllText(_store.SessionPath, json);

            var window = Assert.Single(_store.LoadSession().Windows!);

            Assert.Equal(new[] { a }, window.NoteIds);
            Assert.False(window.Open);
        }

        [Theory]
        [InlineData(0.0, 0.5)]
        [InlineData(9.0, 4.0)]
        [InlineData(1.25, 1.25)]
        public void Zoom_is_kept_in_range(double saved, double expected)
        {
            var session = new SessionState { Windows = new List<PadWindowState> { new() { Id = "w", Zoom = saved, NoteIds = { "n" } } } };
            Assert.Equal(expected, SessionWindows.Normalize(session).Windows![0].Zoom);
        }

        [Fact]
        public void A_file_goes_to_the_window_already_showing_it_else_to_the_most_recent()
        {
            var notes = new List<(string WindowId, string? SourcePath)> { ("w1", null), ("w2", @"C:\Logs\app.log"), ("w1", @"C:\a.txt") };
            var order = new[] { "w1", "w2" };

            Assert.Equal("w2", SessionWindows.Route(notes, order, @"c:\LOGS\App.log"));     // Windows paths ignore case
            Assert.Equal("w1", SessionWindows.Route(notes, order, @"C:\other.txt"));
            Assert.Equal("w1", SessionWindows.Route(notes, order, null));
            Assert.Equal("w2", SessionWindows.Route(notes, new[] { "w2", "w1" }, null));
        }

        [Fact]
        public void After_a_restart_the_most_recently_active_window_comes_first()
        {
            var t = new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc);
            var windows = new List<PadWindowState>
            {
                new() { Id = "w1", LastActiveUtc = t },
                new() { Id = "w2", LastActiveUtc = t.AddMinutes(5) },
                new() { Id = "w3" },                                  // never activated (an older session)
                new() { Id = "w4", LastActiveUtc = t },
            };

            Assert.Equal(new[] { "w2", "w1", "w4", "w3" }, SessionWindows.ByLastActive(windows));
        }

        [Fact]
        public void Reopen_at_login_is_wanted_when_any_window_was_open()
        {
            Assert.True(SessionWindows.AnyOpen(new SessionState { Windows = new List<PadWindowState> { new() { Id = "a" }, new() { Id = "b", Open = true } } }));
            Assert.False(SessionWindows.AnyOpen(new SessionState { Windows = new List<PadWindowState> { new() { Id = "a" } } }));
            Assert.True(SessionWindows.AnyOpen(new SessionState { WindowOpen = true }));   // v1: the old flag
        }

        [Fact]
        public void A_new_window_opens_offset_from_the_one_it_came_from()
        {
            var from = new PadWindowState { Id = "w1", Left = 100, Top = 50, Width = 700, Height = 500, Zoom = 2, AlwaysOnTop = true, Maximized = true };

            var next = SessionWindows.Cascade(from);

            Assert.NotEqual("w1", next.Id);
            Assert.False(string.IsNullOrEmpty(next.Id));
            Assert.Equal(132, next.Left);
            Assert.Equal(82, next.Top);
            Assert.Equal(700, next.Width);
            Assert.Equal(500, next.Height);
            Assert.Equal(1.0, next.Zoom);
            Assert.False(next.AlwaysOnTop);
            Assert.False(next.Maximized);
            Assert.True(next.Open);
            Assert.Empty(next.NoteIds);
            Assert.Null(SessionWindows.Cascade(null).Left);         // no window to start from: Windows places it
        }
    }
}
