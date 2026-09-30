using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    public sealed partial class PadWorkspace
    {
        /// <summary>Each window's tab list, kept in step with <see cref="Open"/> by <see cref="SyncTabs"/>.</summary>
        private readonly Dictionary<string, WindowTabs> _tabs = new();

        /// <summary>Each window's active tab.</summary>
        private readonly Dictionary<string, OpenNote> _active = new();

        /// <summary>Window ids, most recently active first. Ids of windows since closed are skipped when read.</summary>
        private readonly List<string> _activation = new();

        /// <summary>The window each note skipped at <see cref="Restore"/> belongs to, so it returns there.</summary>
        private readonly Dictionary<string, string> _unreadableWindow = new();

        /// <summary>Every MicaPad window, in session order (spec 5.3); the first is the one older MicaPads see.</summary>
        public IReadOnlyList<PadWindowState> Windows => Session.Windows!;

        /// <summary>Window ids, the most recently active first; windows never activated follow in session order.</summary>
        public IReadOnlyList<string> ActivationOrder
        {
            get
            {
                var order = _activation.Where(id => WindowStateOf(id) != null).ToList();
                order.AddRange(Windows.Select(w => w.Id).Where(id => !order.Contains(id)));
                return order;
            }
        }

        /// <summary>Where the hotkey, the overlay and a note opened without a window go: the most recently active window.</summary>
        public string MostRecentWindowId => ActivationOrder[0];

        /// <summary>The active tab of the most recently active window, or null when it has none.</summary>
        public OpenNote? Active => ActiveIn(MostRecentWindowId);

        /// <summary>A window's saved state, or null when there is no such window.</summary>
        public PadWindowState? WindowStateOf(string windowId) => Windows.FirstOrDefault(w => w.Id == windowId);

        /// <summary>A window's tabs, in tab order; a tab strip binds to it.</summary>
        public WindowTabs TabsOf(string windowId)
        {
            if (!_tabs.TryGetValue(windowId, out var tabs))
            {
                tabs = new WindowTabs();
                tabs.Sync(NotesIn(windowId));
                _tabs[windowId] = tabs;
            }
            return tabs;
        }

        /// <summary>A window's active tab, or null.</summary>
        public OpenNote? ActiveIn(string windowId) => _active.TryGetValue(windowId, out var note) ? note : null;

        /// <summary>A window came to the front: it is now the most recently active, also after a restart.</summary>
        public void ActivateWindow(string windowId)
        {
            if (WindowStateOf(windowId) is not { } state) return;
            _activation.Remove(windowId);
            _activation.Insert(0, windowId);
            state.LastActiveUtc = _clock();
        }

        /// <summary>
        /// A new window (spec 5.3), open and most recently active, cascaded from
        /// <paramref name="fromWindowId"/> (or the most recently active window). It has no tabs yet:
        /// the caller adds a note or moves one in.
        /// </summary>
        public PadWindowState NewWindow(string? fromWindowId = null)
        {
            var state = SessionWindows.Cascade(WindowStateOf(fromWindowId ?? MostRecentWindowId));
            Session.Windows!.Add(state);
            ActivateWindow(state.Id);
            return state;
        }

        /// <summary>
        /// Moves an open tab to the end of another window's tabs, where it becomes the active tab;
        /// its old window's active tab falls to a neighbour. The note itself, its text and its
        /// unsaved edits are untouched, and nothing is closed.
        /// </summary>
        public void MoveToWindow(OpenNote note, string windowId)
        {
            if (!_byId.ContainsKey(note.Id) || note.WindowId == windowId || WindowStateOf(windowId) == null) return;
            string from = note.WindowId;
            int index = TabsOf(from).IndexOf(note);
            bool wasActive = ReferenceEquals(ActiveIn(from), note);

            MoveToEndOf(note, windowId);

            if (wasActive)
            {
                _active.Remove(from);
                var rest = TabsOf(from);
                if (rest.Count > 0) SetActive(rest[Math.Min(index, rest.Count - 1)]);
                else if (WindowStateOf(from) is { } state) state.ActiveNoteId = null;
            }
            SetActive(note);
        }

        /// <summary>
        /// Closes a window (spec 5.3): its tabs move, in order, to the end of <paramref name="into"/>,
        /// or else of the most recently active other window, whose active tab stays; no note is
        /// closed. Returns the window that took them, or null (nothing done) when this is the only
        /// window or the target is not another window.
        /// </summary>
        public string? CloseWindow(string windowId, string? into = null)
        {
            var state = WindowStateOf(windowId);
            if (state == null || Windows.Count < 2) return null;
            string? target = into ?? ActivationOrder.FirstOrDefault(id => id != windowId);
            if (target == null || target == windowId || WindowStateOf(target) == null) return null;

            if (ActiveIn(windowId) is { } active) active.IsActive = false;
            _active.Remove(windowId);
            foreach (var note in TabsOf(windowId).ToList()) MoveToEndOf(note, target);
            foreach (string id in _unreadableWindow.Where(p => p.Value == windowId).Select(p => p.Key).ToList())
                _unreadableWindow[id] = target;

            Session.Windows!.Remove(state);
            _activation.Remove(windowId);
            _tabs.Remove(windowId);
            if (ActiveIn(target) == null && TabsOf(target).Count > 0) SetActive(TabsOf(target)[0]);
            SaveSession();
            return target;
        }

        /// <summary>The window a file opened from outside (<c>--pad</c>, Open with) goes to; see <see cref="SessionWindows.Route"/>.</summary>
        public string RouteFile(string? path)
        {
            string? full = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    full = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    full = null;
                }
            }
            return SessionWindows.Route(Open.Select(n => (n.WindowId, n.Meta.SourcePath)).ToList(), ActivationOrder, full);
        }

        /// <summary>Gives a note to a window, after that window's last tab in <see cref="Open"/>.</summary>
        private void MoveToEndOf(OpenNote note, string windowId)
        {
            var targetTabs = TabsOf(windowId);          // read before the note joins it
            note.WindowId = windowId;
            if (targetTabs.Count > 0)
            {
                int current = Open.IndexOf(note);
                int last = Open.IndexOf(targetTabs[targetTabs.Count - 1]);
                int to = current < last ? last : last + 1;
                if (to != current) Open.Move(current, to);
            }
            SyncTabs();
        }

        /// <summary>Brings every window's tab list in step with <see cref="Open"/> and the notes' window ids.</summary>
        private void SyncTabs()
        {
            foreach (var pair in _tabs.ToList()) pair.Value.Sync(NotesIn(pair.Key));
        }

        private List<OpenNote> NotesIn(string windowId) => Open.Where(n => n.WindowId == windowId).ToList();

        /// <summary>Where a new tab of a window goes in <see cref="Open"/>: after its active tab, else after its last one.</summary>
        private int InsertIndexAfterActive(string windowId)
        {
            if (ActiveIn(windowId) is { } active) return Open.IndexOf(active) + 1;
            var tabs = TabsOf(windowId);
            return tabs.Count == 0 ? Open.Count : Open.IndexOf(tabs[tabs.Count - 1]) + 1;
        }

        /// <summary>The window a note skipped at restore belongs to; the first window when its own is gone.</summary>
        private string UnreadableWindowOf(string id) =>
            _unreadableWindow.TryGetValue(id, out var windowId) && WindowStateOf(windowId) != null ? windowId : Windows[0].Id;
    }
}
