using System;
using System.IO;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What identifies one version of a file on disk: its write time and length.</summary>
    public readonly record struct SourceStamp(DateTime LastWriteTimeUtc, long Length)
    {
        /// <summary>The file's current stamp, or null when it does not exist or cannot be read.</summary>
        public static SourceStamp? Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new SourceStamp(info.LastWriteTimeUtc, info.Length) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>What the window does after comparing a file-backed note with its file.</summary>
    public enum DiskChangeAction
    {
        /// <summary>Nothing changed.</summary>
        None,
        /// <summary>The file changed and the note holds no edits of its own: take the new text.</summary>
        ReloadSilently,
        /// <summary>Both changed: the user chooses Reload from disk or Keep mine.</summary>
        AskReloadOrKeep,
        /// <summary>The file is gone: the user chooses Save As or Keep as note.</summary>
        AskSaveAsOrKeepAsNote,
    }

    /// <summary>The spec's disk-change table (§3), as one pure function.</summary>
    public static class DiskChangePolicy
    {
        /// <summary>
        /// Decides from the stamp recorded at the last load or save, the stamp now, and whether the
        /// note has edits not yet written to its file.
        /// </summary>
        public static DiskChangeAction Decide(SourceStamp? recorded, SourceStamp? current, bool hasUnsavedEdits)
        {
            if (current == null) return DiskChangeAction.AskSaveAsOrKeepAsNote;
            if (recorded == null || recorded.Value == current.Value) return DiskChangeAction.None;
            return hasUnsavedEdits ? DiskChangeAction.AskReloadOrKeep : DiskChangeAction.ReloadSilently;
        }
    }
}
