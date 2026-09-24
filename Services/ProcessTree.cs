using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Names each process's parent, from one snapshot and nothing else.
    ///
    /// <para>
    /// Pure, and applied once per sample rather than per row, so the process window can show a
    /// parent for every process without opening any of them.
    /// </para>
    ///
    /// <para>
    /// A PID is not an identity. Windows reuses process IDs, so a parent entry that started
    /// <em>after</em> its supposed child is an unrelated newcomer wearing the dead parent's
    /// number, and is reported as <see cref="Gone"/>. Naming it would tell the user an
    /// unrelated program launched the process in front of them.
    /// </para>
    /// </summary>
    public static class ProcessTree
    {
        /// <summary>Shown when a process had a parent that has since exited.</summary>
        public const string Gone = "(gone)";

        /// <summary>
        /// The same rows, in the same order, each carrying its parent's name. Every other field
        /// is preserved.
        /// </summary>
        public static IReadOnlyList<ProcessUsage> WithParentNames(IReadOnlyList<ProcessUsage> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0) return System.Array.Empty<ProcessUsage>();

            var byPid = new Dictionary<int, ProcessUsage>(snapshot.Count);
            foreach (var p in snapshot) byPid[p.Pid] = p;

            var result = new ProcessUsage[snapshot.Count];
            for (int i = 0; i < snapshot.Count; i++)
            {
                var p = snapshot[i];
                result[i] = p with { ParentName = NameOfParent(p, byPid) };
            }
            return result;
        }

        private static string NameOfParent(ProcessUsage child, Dictionary<int, ProcessUsage> byPid)
        {
            // PID 0 is the idle process: a process whose parent is 0 never had one.
            if (child.ParentPid <= 0) return "";
            if (!byPid.TryGetValue(child.ParentPid, out var parent)) return Gone;

            // A parent cannot be younger than its child; that entry is a reused PID.
            if (parent.CreateTime > child.CreateTime) return Gone;

            return parent.Name;
        }
    }
}
