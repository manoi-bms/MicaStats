using System;
using System.Collections.Generic;
using System.IO;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A clock tests move by hand.</summary>
    internal sealed class FakeClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 29, 5, 0, 0, DateTimeKind.Utc);

        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    /// <summary>
    /// A workspace over a temp-folder store, a fast-retry writer, a fake clock and a fake recycle
    /// bin. Every callback that would reach DiagnosticsLog is a no-op.
    /// </summary>
    internal sealed class PadTestEnv : IDisposable
    {
        private readonly PadTempDir _dir = new();
        private readonly List<PadWorkspace> _workspaces = new();

        public PadTestEnv(Action<Action>? post = null)
        {
            Store = new NoteStore(Path.Combine(_dir.Root, "pad"), warn: _ => { });
            Writer = new AutosaveWriter(_ => TimeSpan.FromMilliseconds(10));
            Workspace = NewWorkspace(post);
        }

        public FakeClock Clock { get; } = new();

        public FakeRecycleBin Bin { get; } = new();

        public NoteStore Store { get; }

        public AutosaveWriter Writer { get; }

        public PadWorkspace Workspace { get; }

        /// <summary>A path for a user file, outside the store.</summary>
        public string FileOf(string name) => _dir.PathOf(name);

        /// <summary>Another workspace over the same store and writer, as after a restart.</summary>
        public PadWorkspace NewWorkspace(Action<Action>? post = null)
        {
            var workspace = new PadWorkspace(Store, new PadWorkspaceOptions
            {
                Writer = Writer,
                UtcClock = () => Clock.UtcNow,
                RecycleBin = Bin,
                Post = post ?? (action => action()),
                Warn = _ => { },
                Error = (_, _) => { },
                AnsiCodePage = 874,
            });
            _workspaces.Add(workspace);
            return workspace;
        }

        /// <summary>Waits for every queued write.</summary>
        public void Flush() => Assert.True(Writer.FlushAll(TimeSpan.FromSeconds(5)));

        /// <summary>Replaces the note's text as the window's document would, and reports the change.</summary>
        public static void Type(PadWorkspace workspace, OpenNote note, string text)
        {
            note.TextProvider = () => text;
            workspace.NotifyChanged(note);
        }

        /// <summary>The note's <c>current.txt</c>, or null when it has none.</summary>
        public string? DiskText(OpenNote note)
        {
            string path = Store.CurrentPath(note.Id);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public void Dispose()
        {
            Writer.FlushAll(TimeSpan.FromSeconds(5));
            foreach (var workspace in _workspaces) workspace.Dispose();
            Writer.Dispose();
            _dir.Dispose();
        }
    }
}
