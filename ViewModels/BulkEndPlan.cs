using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>A matching process that End all filtered will not end, and why.</summary>
    public sealed record BulkEndExcluded(ProcessUsage Process, string Reason);

    /// <summary>
    /// What End all filtered will end, and what it refuses to.
    ///
    /// <para>
    /// Pure, so the decision behind the most destructive button in the app is tested rather
    /// than trusted. It only ever partitions its input: nothing outside the filtered rows can be
    /// planned, and every filtered row is accounted for on one side or the other. Refusals are
    /// listed with their reason rather than silently dropped, so the preview never shows a count
    /// that differs from what the user can see.
    /// </para>
    /// </summary>
    public sealed record BulkEndPlan(IReadOnlyList<ProcessUsage> ToEnd, IReadOnlyList<BulkEndExcluded> Excluded)
    {
        /// <summary>
        /// Partitions the filtered rows. Excluded: core Windows processes, MicaStats itself, and
        /// any process MicaStats is running inside — ending a terminal whose job object owns this
        /// app would end the app from under the user.
        /// </summary>
        public static BulkEndPlan Build(
            IReadOnlyList<ProcessUsage> filtered, int selfPid, IReadOnlyCollection<int> selfAncestors)
        {
            var toEnd = new List<ProcessUsage>();
            var excluded = new List<BulkEndExcluded>();
            if (filtered == null) return new BulkEndPlan(toEnd, excluded);

            var ancestors = selfAncestors as ISet<int> ?? new HashSet<int>(selfAncestors ?? Array.Empty<int>());

            foreach (var p in filtered)
            {
                if (ProcessControl.IsCriticalProcess(p.Name))
                    excluded.Add(new BulkEndExcluded(p, "core Windows process"));
                else if (p.Pid == selfPid)
                    excluded.Add(new BulkEndExcluded(p, "this is MicaStats"));
                else if (ancestors.Contains(p.Pid))
                    excluded.Add(new BulkEndExcluded(p, "MicaStats is running inside it"));
                else
                    toEnd.Add(p);
            }

            return new BulkEndPlan(toEnd, excluded);
        }

        /// <summary>
        /// Every process <paramref name="pid"/> descends from, walking parent links in one
        /// snapshot. Stops at a missing parent, at a reused parent PID (a parent younger than its
        /// child), and on a cycle — a walk that could loop forever must not run before a kill.
        /// </summary>
        public static IReadOnlyCollection<int> AncestorsOf(int pid, IReadOnlyList<ProcessUsage> snapshot)
        {
            var result = new HashSet<int>();
            if (snapshot == null || snapshot.Count == 0) return result;

            var byPid = new Dictionary<int, ProcessUsage>(snapshot.Count);
            foreach (var p in snapshot) byPid[p.Pid] = p;

            if (!byPid.TryGetValue(pid, out var current)) return result;

            while (current.ParentPid > 0 &&
                   byPid.TryGetValue(current.ParentPid, out var parent) &&
                   parent.CreateTime <= current.CreateTime &&
                   parent.Pid != pid &&
                   result.Add(parent.Pid))
            {
                current = parent;
            }

            return result;
        }
    }

    /// <summary>What happened when a planned set was ended.</summary>
    /// <param name="Ended">Gone, verified by asking the kernel whether each one exited.</param>
    /// <param name="AccessDenied">Refused for lack of privilege; retried one at a time from the list.</param>
    /// <param name="Survived">Reported ended, but the kernel says still running.</param>
    /// <param name="Failed">Any other failure, including an exception while ending it.</param>
    public sealed record BulkEndResult(int Ended, int AccessDenied, int Survived, int Failed)
    {
        /// <summary>
        /// One sentence for the footer, e.g. "Ended 35 · 2 need administrator · 0 survived".
        /// Failures are named only when there are some, so the common case stays short.
        /// </summary>
        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            string text = "Ended " + Ended.ToString(inv)
                          + " · " + AccessDenied.ToString(inv) + " need administrator"
                          + " · " + Survived.ToString(inv) + " survived";
            return Failed > 0 ? text + " · " + Failed.ToString(inv) + " failed" : text;
        }
    }
}
