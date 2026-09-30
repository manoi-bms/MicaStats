using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Where a note's latest text stands relative to disk.</summary>
    public enum SaveState
    {
        /// <summary>Everything typed is on disk.</summary>
        Saved,
        /// <summary>A save is queued or being written.</summary>
        Saving,
        /// <summary>The last write failed; the writer is retrying.</summary>
        Failed,
    }

    /// <summary>
    /// One open tab. Observable so the tab strip and status bar can bind to it. Changed on the
    /// UI thread only: the workspace sends writer results back through its post callback.
    /// </summary>
    public sealed class OpenNote : INotifyPropertyChanged
    {
        private string _title;
        private bool _hasUnsavedEdits;
        private bool _isActive;
        private string _windowId = "";
        private SaveState _saveState = SaveState.Saved;

        internal OpenNote(NoteMeta meta, string initialText)
        {
            Meta = meta;
            _title = meta.Title;
            _hasUnsavedEdits = meta.HasUnsavedEdits;
            TextProvider = () => initialText;
        }

        /// <summary>The note's metadata; mutated on the UI thread only.</summary>
        public NoteMeta Meta { get; }

        /// <summary>The note's identifier, the name of its folder in the store.</summary>
        public string Id => Meta.Id;

        /// <summary>
        /// Reads the note's current text. Returns the text it was opened with until the window
        /// points it at an editor document. Called on the UI thread only.
        /// </summary>
        public Func<string> TextProvider { get; set; }

        /// <summary>The tab title; kept in step with <see cref="NoteMeta.Title"/>.</summary>
        public string Title
        {
            get => _title;
            internal set
            {
                Meta.Title = value;
                Set(ref _title, value);
            }
        }

        /// <summary>File-backed notes: the text differs from the file. Drives the tab's dot.</summary>
        public bool HasUnsavedEdits
        {
            get => _hasUnsavedEdits;
            internal set
            {
                Meta.HasUnsavedEdits = value;
                Set(ref _hasUnsavedEdits, value);
            }
        }

        /// <summary>The tab currently shown.</summary>
        public bool IsActive
        {
            get => _isActive;
            internal set => Set(ref _isActive, value);
        }

        /// <summary>The MicaPad window showing this tab (spec 5.3); changed by the workspace when the tab moves.</summary>
        public string WindowId
        {
            get => _windowId;
            internal set => Set(ref _windowId, value);
        }

        /// <summary>Whether the note's latest text is on disk, being written, or failed to write.</summary>
        public SaveState SaveState
        {
            get => _saveState;
            internal set => Set(ref _saveState, value);
        }

        /// <summary>When the last save finished, by the workspace clock.</summary>
        public DateTime? LastSavedUtc { get; internal set; }

        /// <summary>The store version of the newest save queued; a completion for an older one leaves the state Saving.</summary>
        internal long SaveSequence;

        internal DateTime LastEditUtc;

        /// <summary>When the pause interval started: the last snapshot, or when the note was opened.</summary>
        internal DateTime SnapshotClockUtc;

        internal bool ChangedSinceSnapshot;

        /// <summary>False only for a note that has been empty all its life; closing it deletes it.</summary>
        internal bool EverHadText;

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
