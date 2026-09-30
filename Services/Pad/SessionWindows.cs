using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// <c>session.json</c>'s windows (spec 5.3): sessions from before windows existed, checking
    /// what a file says, the fields older MicaPads read, which window a file goes to, and where a
    /// new window opens. Pure: the store and the workspace call it.
    /// </summary>
    public static class SessionWindows
    {
        /// <summary>How far a new window opens below and to the right of the one it came from, in DIPs.</summary>
        public const double CascadeOffset = 32;

        public static string NewId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// Makes <see cref="SessionState.Windows"/> usable, in place. A session without windows
        /// (written by v1.12, rebuilt from the notes, or brand new) becomes one window made from its
        /// old top-level fields. Windows without an id, with an id seen before, or with no note left
        /// are dropped; a note listed twice stays in the first window listing it; notes
        /// <paramref name="noteExists"/> rejects are dropped; zoom is kept between 0.5 and 4. If no
        /// window is left, one is made from the old fields.
        /// </summary>
        public static SessionState Normalize(SessionState session, Func<string, bool>? noteExists = null)
        {
            noteExists ??= _ => true;
            session.OpenNoteIds ??= new List<string>();
            session.Tabs ??= new Dictionary<string, TabViewState>();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var seenNotes = new HashSet<string>(StringComparer.Ordinal);
            var windows = new List<PadWindowState>();
            foreach (var window in session.Windows ?? new List<PadWindowState>())
            {
                if (window == null || string.IsNullOrWhiteSpace(window.Id) || !ids.Add(window.Id)) continue;

                var kept = new List<string>();
                foreach (string id in window.NoteIds ?? new List<string>())
                    if (!string.IsNullOrEmpty(id) && noteExists(id) && seenNotes.Add(id)) kept.Add(id);
                if (kept.Count == 0) continue;

                window.NoteIds = kept;
                if (window.ActiveNoteId != null && !kept.Contains(window.ActiveNoteId)) window.ActiveNoteId = null;
                window.Zoom = ClampZoom(window.Zoom);
                windows.Add(window);
            }

            if (windows.Count == 0) windows.Add(FromOldFields(session, noteExists));
            session.Windows = windows;
            return session;
        }

        /// <summary>
        /// Writes the fields an older MicaPad reads (downgrade safety): the first window's place,
        /// zoom, on-top and active tab; every window's notes in <see cref="SessionState.OpenNoteIds"/>
        /// (the first window's first), so no tab disappears; <see cref="SessionState.WindowOpen"/>
        /// when any window was open.
        /// </summary>
        public static void WriteMirror(SessionState session)
        {
            var windows = session.Windows;
            if (windows == null || windows.Count == 0) return;
            var first = windows[0];
            session.WindowOpen = windows.Any(w => w.Open);
            session.Left = first.Left;
            session.Top = first.Top;
            session.Width = first.Width;
            session.Height = first.Height;
            session.Maximized = first.Maximized;
            session.AlwaysOnTop = first.AlwaysOnTop;
            session.Zoom = first.Zoom;
            session.OpenNoteIds = windows.SelectMany(w => w.NoteIds).Distinct().ToList();
            session.ActiveNoteId = first.ActiveNoteId;
        }

        /// <summary>True when MicaPad should reopen at login: any window was open (or, in a v1 session, the window was).</summary>
        public static bool AnyOpen(SessionState session) =>
            session.Windows is { Count: > 0 } windows ? windows.Any(w => w.Open) : session.WindowOpen;

        /// <summary>
        /// The window a file goes to (spec 5.3): the one already showing it (paths compared as
        /// Windows does, ignoring case), otherwise the most recently active. With no path, the most
        /// recently active.
        /// </summary>
        public static string Route(IReadOnlyList<(string WindowId, string? SourcePath)> notes, IReadOnlyList<string> mostRecentFirst, string? fullPath)
        {
            if (fullPath != null)
            {
                foreach (var (windowId, source) in notes)
                {
                    if (source != null && string.Equals(source, fullPath, StringComparison.OrdinalIgnoreCase) && mostRecentFirst.Contains(windowId))
                        return windowId;
                }
            }
            return mostRecentFirst[0];
        }

        /// <summary>Window ids, the most recently active first; windows never activated keep their session order, last.</summary>
        public static IReadOnlyList<string> ByLastActive(IReadOnlyList<PadWindowState> windows) =>
            windows.Select((window, index) => (window, index))
                   .OrderByDescending(x => x.window.LastActiveUtc ?? DateTime.MinValue)
                   .ThenBy(x => x.index)
                   .Select(x => x.window.Id)
                   .ToList();

        /// <summary>A new window: the size of <paramref name="from"/>, a little below and right of it, zoom 1, not on top.</summary>
        public static PadWindowState Cascade(PadWindowState? from) => new()
        {
            Id = NewId(),
            Open = true,
            Left = from?.Left + CascadeOffset,
            Top = from?.Top + CascadeOffset,
            Width = from?.Width ?? 900,
            Height = from?.Height ?? 640,
        };

        /// <summary>Zoom within 0.5 to 4; anything unreadable is 1.</summary>
        public static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, 0.5, 4.0) : 1.0;

        private static PadWindowState FromOldFields(SessionState session, Func<string, bool> noteExists)
        {
            var noteIds = session.OpenNoteIds.Where(id => !string.IsNullOrEmpty(id) && noteExists(id)).Distinct().ToList();
            return new PadWindowState
            {
                Id = NewId(),
                Open = session.WindowOpen,
                Left = session.Left,
                Top = session.Top,
                Width = session.Width,
                Height = session.Height,
                Maximized = session.Maximized,
                AlwaysOnTop = session.AlwaysOnTop,
                Zoom = ClampZoom(session.Zoom),
                NoteIds = noteIds,
                ActiveNoteId = session.ActiveNoteId != null && noteIds.Contains(session.ActiveNoteId) ? session.ActiveNoteId : null,
            };
        }
    }
}
