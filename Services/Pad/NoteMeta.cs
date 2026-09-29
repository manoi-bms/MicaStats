using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One note's metadata, persisted as <c>meta.json</c> beside its text. Mutated on the UI
    /// thread only; the background writer is always handed a <see cref="Clone"/>.
    /// </summary>
    public sealed class NoteMeta
    {
        /// <summary>GUID in N format; also the note's folder name.</summary>
        public string Id { get; set; } = "";

        /// <summary>The tab title.</summary>
        public string Title { get; set; } = "";

        /// <summary>True once the user renamed the tab; the title then stops following the first line.</summary>
        public bool TitleIsCustom { get; set; }

        /// <summary>The N of "Untitled N" for a scratch note.</summary>
        public int UntitledNumber { get; set; }

        /// <summary>The file this note shadows, or null for a scratch note.</summary>
        public string? SourcePath { get; set; }

        /// <summary>When the note was created.</summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>When the note was last changed.</summary>
        public DateTime ModifiedUtc { get; set; }

        /// <summary>When the tab was closed; null while it is open.</summary>
        public DateTime? ClosedAtUtc { get; set; }

        /// <summary>
        /// File-backed notes only: the text differs from the file. While true, <c>current.txt</c>
        /// is authoritative; while false, the file is, and <c>current.txt</c> may be stale.
        /// </summary>
        public bool HasUnsavedEdits { get; set; }

        /// <summary>How Ctrl+S encodes the text.</summary>
        public PadEncoding Encoding { get; set; } = PadEncoding.Utf8;

        /// <summary>The code page when <see cref="Encoding"/> is <see cref="PadEncoding.Ansi"/>; 0 otherwise.</summary>
        public int CodePage { get; set; }

        /// <summary>The line ending recorded at open, or chosen by the user.</summary>
        public LineEnding LineEnding { get; set; } = LineEnding.CrLf;

        /// <summary>The source file's write time at the last load or save.</summary>
        public DateTime? SourceWriteTimeUtc { get; set; }

        /// <summary>The source file's length at the last load or save.</summary>
        public long? SourceLength { get; set; }

        /// <summary>Hash of the newest snapshot's text, so an unchanged note is never snapshotted twice.</summary>
        public string? LastSnapshotHash { get; set; }

        /// <summary>When the newest snapshot was written.</summary>
        public DateTime? LastSnapshotUtc { get; set; }

        /// <summary>True when this note shadows a file; false for scratch notes.</summary>
        [JsonIgnore]
        public bool IsFileBacked => SourcePath != null;

        /// <summary>True when the note has been closed in MicaPad.</summary>
        [JsonIgnore]
        public bool IsClosed => ClosedAtUtc != null;

        /// <summary>The source stamp as one value; null when none is recorded.</summary>
        [JsonIgnore]
        public SourceStamp? SourceStamp
        {
            get => SourceWriteTimeUtc is DateTime time && SourceLength is long length ? new SourceStamp(time, length) : null;
            set
            {
                SourceWriteTimeUtc = value?.LastWriteTimeUtc;
                SourceLength = value?.Length;
            }
        }

        /// <summary>A copy for the background writer. Every member is a value or an immutable string.</summary>
        public NoteMeta Clone() => (NoteMeta)MemberwiseClone();
    }

    /// <summary>The window and its tabs, persisted as <c>session.json</c>.</summary>
    public sealed class SessionState
    {
        /// <summary>MicaPad was showing when this was written; drives reopen at login.</summary>
        public bool WindowOpen { get; set; }

        /// <summary>Null until the window has been placed once. Nullable rather than NaN: JSON cannot store NaN.</summary>
        public double? Left { get; set; }

        /// <summary>Null until the window has been placed once.</summary>
        public double? Top { get; set; }

        /// <summary>Window width in device-independent pixels (DIPs, 1/96 inch), as WPF measures it; default 900.</summary>
        public double Width { get; set; } = 900;

        /// <summary>Window height in device-independent pixels (DIPs, 1/96 inch), as WPF measures it; default 640.</summary>
        public double Height { get; set; } = 640;

        /// <summary>True when the window was maximized.</summary>
        public bool Maximized { get; set; }

        /// <summary>True to keep the window above all others.</summary>
        public bool AlwaysOnTop { get; set; }

        /// <summary>Editor zoom factor, 0.5 to 4.</summary>
        public double Zoom { get; set; } = 1.0;

        /// <summary>Open notes in tab order.</summary>
        public List<string> OpenNoteIds { get; set; } = new();

        /// <summary>The currently selected note tab, or null if none.</summary>
        public string? ActiveNoteId { get; set; }

        /// <summary>Caret and scroll per open note.</summary>
        public Dictionary<string, TabViewState> Tabs { get; set; } = new();
    }

    /// <summary>Where the user was in one tab.</summary>
    public sealed class TabViewState
    {
        /// <summary>
        /// Caret position in the text, in UTF-16 code units (a .NET string index, as the editor
        /// counts), not code points: a character outside the BMP, such as an emoji, counts as two.
        /// </summary>
        public int CaretOffset { get; set; }

        /// <summary>Vertical scroll offset in the editor.</summary>
        public double VerticalOffset { get; set; }
    }

    /// <summary>One version in a note's history.</summary>
    public sealed record SnapshotInfo(
        /// <summary>Full path to the snapshot file.</summary>
        string FilePath,
        /// <summary>Local time when this version was taken, parsed from the file name.</summary>
        DateTime Stamp,
        /// <summary>File size in bytes.</summary>
        long Size);
}
