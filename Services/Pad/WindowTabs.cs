using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One window's tabs, in tab order: the notes of <see cref="PadWorkspace.Open"/> whose
    /// <see cref="OpenNote.WindowId"/> is that window's (spec 5.3). A tab strip binds to it; the
    /// workspace keeps it in step. Read-only to everyone else, so there is one truth: <c>Open</c>
    /// and each note's window id.
    /// </summary>
    public sealed class WindowTabs : ReadOnlyObservableCollection<OpenNote>
    {
        private readonly ObservableCollection<OpenNote> _items;

        public WindowTabs() : this(new ObservableCollection<OpenNote>())
        {
        }

        private WindowTabs(ObservableCollection<OpenNote> items) : base(items) => _items = items;

        /// <summary>
        /// Makes the list equal <paramref name="wanted"/> with removals, moves and inserts only — never
        /// a reset — so a tab strip keeps its tab containers, and a tab drag its tab.
        /// </summary>
        internal void Sync(IReadOnlyList<OpenNote> wanted)
        {
            var keep = new HashSet<OpenNote>(wanted);
            for (int i = _items.Count - 1; i >= 0; i--)
                if (!keep.Contains(_items[i])) _items.RemoveAt(i);

            for (int i = 0; i < wanted.Count; i++)
            {
                int at = _items.IndexOf(wanted[i]);
                if (at == i) continue;
                if (at < 0) _items.Insert(i, wanted[i]);
                else _items.Move(at, i);        // at > i: every place before i already holds a wanted note
            }
        }
    }
}
