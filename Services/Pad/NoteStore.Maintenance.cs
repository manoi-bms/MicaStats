using System;
using System.IO;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    public sealed partial class NoteStore
    {
        /// <summary>
        /// Deletes the versions <see cref="HistoryPolicy.SelectToPrune"/> selects. Pruned versions
        /// are deleted outright: they are superseded copies, and the newest always survives.
        /// </summary>
        /// <returns>How many versions were deleted.</returns>
        public int PruneHistory(string id, DateTime localNow, int historyDays)
        {
            lock (LockFor(id))
            {
                var snapshots = ListSnapshots(id);
                var doomed = HistoryPolicy.SelectToPrune(snapshots.Select(s => s.Stamp).ToList(), localNow, historyDays).ToHashSet();

                int deleted = 0;
                foreach (var snapshot in snapshots)
                {
                    if (!doomed.Contains(snapshot.Stamp)) continue;
                    try
                    {
                        File.Delete(snapshot.FilePath);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _warn("Could not prune " + snapshot.FilePath + ": " + ex.Message);
                    }
                }
                return deleted;
            }
        }

        /// <summary>Moves notes closed longer than <paramref name="historyDays"/> (at least 7) to the Recycle Bin.</summary>
        /// <returns>How many notes were moved.</returns>
        public int PurgeClosedNotes(DateTime utcNow, int historyDays, IRecycleBin bin)
        {
            var limit = TimeSpan.FromDays(Math.Max(historyDays, HistoryPolicy.MinHistoryDays));
            int purged = 0;
            foreach (var meta in LoadAllMetas())
            {
                if (meta.ClosedAtUtc is not DateTime closed || utcNow - closed < limit) continue;
                if (DeleteNote(meta.Id, bin)) purged++;
            }
            return purged;
        }

        /// <summary>
        /// Moves a note's folder to the Recycle Bin. When that fails the folder stays where it is:
        /// nothing is ever deleted permanently.
        /// </summary>
        public bool DeleteNote(string id, IRecycleBin bin)
        {
            lock (LockFor(id))
            {
                string folder = NoteDir(id);
                if (!Directory.Exists(folder)) return true;

                if (bin.TryRecycle(folder))
                {
                    // A tombstone, not a removal: a save queued before the delete carries a lower
                    // version and must not recreate the folder. Saves issued afterwards still pass.
                    _written[id] = NextVersion();
                    return true;
                }

                _warn("Could not move note " + id + " to the Recycle Bin; it was left in place");
                return false;
            }
        }

        /// <summary>
        /// The daily maintenance pass: prune every note's history, then purge long-closed notes.
        /// Runs on a background thread; a failure on one note does not stop the others.
        /// </summary>
        public void PruneAll(DateTime utcNow, int historyDays, IRecycleBin bin)
        {
            DateTime localNow = utcNow.ToLocalTime();
            foreach (var meta in LoadAllMetas())
            {
                try
                {
                    PruneHistory(meta.Id, localNow, historyDays);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("Pruning note " + meta.Id + " failed: " + ex.Message);
                }
            }

            try
            {
                PurgeClosedNotes(utcNow, historyDays, bin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("Purging closed notes failed: " + ex.Message);
            }
        }
    }
}
