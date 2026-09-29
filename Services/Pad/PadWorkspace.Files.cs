using System;
using System.IO;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The outcome of opening a file. <paramref name="Lossy"/>: the file held bytes its encoding
    /// could not decode, now shown as U+FFFD; saving will write those, so the window warns first.
    /// </summary>
    public sealed record OpenFileResult(OpenFileStatus Status, OpenNote? Note, bool Lossy = false);

    /// <summary>What Ctrl+S or Save As did.</summary>
    public enum SaveToFileStatus
    {
        /// <summary>The file was written and the note has no unsaved edits.</summary>
        Saved,
        /// <summary>A scratch note has no file yet: show Save As.</summary>
        NeedsSaveAs,
        /// <summary>The note's encoding cannot hold some characters; nothing was written.</summary>
        Lossy,
        /// <summary>The write failed; <see cref="SaveToFileResult.Error"/> says why. The shadow copy keeps the edits.</summary>
        Failed,
    }

    /// <summary>The result of a save to a real file, with the reason when it failed.</summary>
    public sealed record SaveToFileResult(SaveToFileStatus Status, string? Error = null);

    public sealed partial class PadWorkspace
    {
        /// <summary>
        /// Opens a file as a tab. A file already open switches to its tab; a file with a closed
        /// note reopens that note, so its history and any unsaved edits continue.
        /// </summary>
        public OpenFileResult OpenFile(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new OpenFileResult(OpenFileStatus.NotFound, null);
            }

            var open = Open.FirstOrDefault(n => SamePath(n.Meta.SourcePath, full));
            if (open != null)
            {
                SetActive(open);
                return new OpenFileResult(OpenFileStatus.AlreadyOpen, open);
            }

            var closed = ClosedNotes().FirstOrDefault(m => SamePath(m.SourcePath, full));
            if (closed != null)
            {
                var reopened = Reopen(closed.Id);
                if (reopened != null) return new OpenFileResult(OpenFileStatus.Opened, reopened);
            }

            var stamp = SourceStamp.Read(full);
            var decoded = ReadSource(full, out var status);
            if (decoded == null) return new OpenFileResult(status, null);

            var meta = NoteStore.NewMeta(_clock(), 0, full);
            meta.Encoding = decoded.Encoding;
            meta.CodePage = decoded.CodePage;
            meta.LineEnding = decoded.LineEnding;
            meta.SourceStamp = stamp;

            var note = AddOpen(meta, decoded.Text, InsertIndexAfterActive());
            SetActive(note);
            EnqueueSave(note);
            SaveSession();
            return new OpenFileResult(OpenFileStatus.Opened, note, Lossy: !decoded.Lossless);
        }

        /// <summary>
        /// Ctrl+S: writes the note to its file in the note's encoding, atomically. Checks first that
        /// the encoding can hold every character, and snapshots the text being saved.
        /// </summary>
        public SaveToFileResult SaveToSource(OpenNote note)
        {
            var meta = note.Meta;
            if (!meta.IsFileBacked) return new SaveToFileResult(SaveToFileStatus.NeedsSaveAs);

            string text = note.TextProvider();
            if (!TextFileCodec.CanEncodeLosslessly(text, meta.Encoding, meta.CodePage))
                return new SaveToFileResult(SaveToFileStatus.Lossy);

            SnapshotNow(note, SnapshotReason.SavedToFile);
            try
            {
                AtomicFile.WriteSource(meta.SourcePath!, TextFileCodec.Encode(text, meta.Encoding, meta.CodePage));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SaveToFileResult(SaveToFileStatus.Failed, ex.Message);
            }

            meta.SourceStamp = SourceStamp.Read(meta.SourcePath!);
            note.HasUnsavedEdits = false;
            EnqueueSave(note);
            return new SaveToFileResult(SaveToFileStatus.Saved);
        }

        /// <summary>
        /// Writes the note to a new path, which it then shadows. A scratch note keeps its id and
        /// history. On any failure the note is left exactly as it was.
        /// </summary>
        public SaveToFileResult SaveAs(OpenNote note, string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new SaveToFileResult(SaveToFileStatus.Failed, ex.Message);
            }

            if (Open.Any(n => !ReferenceEquals(n, note) && SamePath(n.Meta.SourcePath, full)))
                return new SaveToFileResult(SaveToFileStatus.Failed, "That file is already open in another tab.");

            var meta = note.Meta;
            string? oldPath = meta.SourcePath;
            SourceStamp? oldStamp = meta.SourceStamp;
            LineEnding oldEnding = meta.LineEnding;
            bool oldUnsaved = note.HasUnsavedEdits;
            string oldTitle = note.Title;

            if (oldPath == null) meta.LineEnding = TextFileCodec.DetectLineEnding(note.TextProvider());
            meta.SourcePath = full;
            note.HasUnsavedEdits = true;
            if (!meta.TitleIsCustom) note.Title = NoteTitle.ForFile(full);

            var result = SaveToSource(note);
            if (result.Status != SaveToFileStatus.Saved)
            {
                meta.SourcePath = oldPath;
                meta.SourceStamp = oldStamp;
                meta.LineEnding = oldEnding;
                note.HasUnsavedEdits = oldUnsaved;
                note.Title = oldTitle;
                EnqueueSave(note);   // replaces any save queued with the abandoned path
            }
            return result;
        }

        /// <summary>Chooses how Ctrl+S encodes the note. For a file-backed note this is an unsaved edit.</summary>
        public void SetEncoding(OpenNote note, PadEncoding encoding, int codePage)
        {
            note.Meta.Encoding = encoding;
            note.Meta.CodePage = encoding == PadEncoding.Ansi ? codePage : 0;
            NotifyChanged(note);
        }

        /// <summary>
        /// Snapshots the note, records the new line ending and returns the converted text. The
        /// window replaces the document with it, which reports the change.
        /// </summary>
        public string ConvertLineEndings(OpenNote note, LineEnding target)
        {
            SnapshotNow(note, SnapshotReason.BeforeReplace);
            note.Meta.LineEnding = target;
            return TextFileCodec.ConvertLineEndings(note.TextProvider(), target);
        }

        /// <summary>Compares a file-backed note with its file (the spec's section 3 table).</summary>
        public DiskChangeAction CheckDisk(OpenNote note)
        {
            var meta = note.Meta;
            if (!meta.IsFileBacked) return DiskChangeAction.None;
            return DiskChangePolicy.Decide(meta.SourceStamp, SourceStamp.Read(meta.SourcePath!), note.HasUnsavedEdits);
        }

        /// <summary>
        /// Reads the file again, after snapshotting the note's own text. Returns the file's text for
        /// the window to put in the document, or null (with the reason) when it cannot be read.
        /// </summary>
        public string? ReloadFromDisk(OpenNote note, out OpenFileStatus status)
        {
            var meta = note.Meta;
            status = OpenFileStatus.NotFound;
            if (!meta.IsFileBacked) return null;

            var stamp = SourceStamp.Read(meta.SourcePath!);
            var decoded = ReadSource(meta.SourcePath!, out status);
            if (decoded == null) return null;

            SnapshotNow(note, SnapshotReason.BeforeReplace);
            meta.Encoding = decoded.Encoding;
            meta.CodePage = decoded.CodePage;
            meta.LineEnding = decoded.LineEnding;
            meta.SourceStamp = stamp;
            note.HasUnsavedEdits = false;
            EnqueueSave(note);
            return decoded.Text;
        }

        /// <summary>Keep mine: accepts the file as it is now, so the question returns only on the next outside change.</summary>
        public void KeepMine(OpenNote note)
        {
            if (!note.Meta.IsFileBacked) return;
            note.Meta.SourceStamp = SourceStamp.Read(note.Meta.SourcePath!);
            EnqueueSave(note);
        }

        /// <summary>Keep as note: the file is gone, so the note becomes a scratch note under the file's name.</summary>
        public void DetachFromFile(OpenNote note)
        {
            var meta = note.Meta;
            if (!meta.IsFileBacked) return;

            meta.TitleIsCustom = true;   // keep the name the user recognises
            meta.SourcePath = null;
            meta.SourceStamp = null;
            note.HasUnsavedEdits = false;
            EnqueueSave(note);           // now a scratch note, so its text is written
        }

        private static bool SamePath(string? a, string b) =>
            a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
