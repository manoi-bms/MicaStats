using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// Answers whether a process still has its parent, from a snapshot rather than from a
    /// handle.
    ///
    /// <para>
    /// Pure, and separate from <see cref="OrphanScan"/>, because it carries the one piece of
    /// reasoning that the spec's <c>parentExists</c> flag quietly assumes somebody got right:
    /// a PID is not an identity. Windows reuses process IDs freely, so a dead parent number can
    /// belong to an unrelated process minutes later.
    /// </para>
    /// </summary>
    public static class ParentState
    {
        /// <summary>One process in the snapshot, as much of it as parent resolution needs.</summary>
        public readonly record struct SnapshotEntry(int Pid, DateTime StartTime, string ImagePath);

        /// <summary>
        /// Whether <paramref name="parentPid"/> is a live parent of a process started at
        /// <paramref name="childStartTime"/>.
        ///
        /// <para>
        /// A candidate that started <em>after</em> its supposed child is rejected. Nothing can
        /// be its own child's junior: that entry is a recycled PID, the real parent is gone, and
        /// the child is an orphan. Testing only for presence would find the newcomer, report a
        /// live parent, and leave the orphan running forever — exactly the case the watchdog
        /// exists to end.
        /// </para>
        /// </summary>
        public static bool Resolve(
            int parentPid,
            DateTime childStartTime,
            IReadOnlyDictionary<int, SnapshotEntry> snapshot,
            out string parentImagePath)
        {
            parentImagePath = "";

            // PID 0 is the idle process and is never a real parent.
            if (parentPid <= 0 || snapshot == null) return false;
            if (!snapshot.TryGetValue(parentPid, out SnapshotEntry parent)) return false;
            if (parent.StartTime > childStartTime) return false;

            parentImagePath = parent.ImagePath ?? "";
            return true;
        }
    }
}
