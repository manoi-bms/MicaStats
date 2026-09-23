using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// A specific process, not a slot.
    ///
    /// <para>
    /// PID plus creation time, because a PID alone is reused. Every piece of the watchdog's
    /// bookkeeping is keyed on this: alerting, logging, and the record of what could not be
    /// killed. Keyed on PID alone, a recycled number would inherit the previous tenant's
    /// history and be silently exempted from a kill it deserves.
    /// </para>
    /// </summary>
    public readonly record struct ProcessIdentity(int Pid, long CreateTime);

    /// <summary>
    /// What the watchdog has already said and already tried.
    ///
    /// <para>
    /// Pure state with no process API, so the two guarantees that matter — never nag, and never
    /// loop forever on one PID — are tested rather than hoped for.
    /// </para>
    ///
    /// <para>
    /// Safe for concurrent use: every public method takes an internal lock. The watchdog scans
    /// on a timer thread every 60 seconds, and a kill started from a click can hold the caller's
    /// thread for several seconds per finding while it terminates, waits, and re-reads — a scan
    /// landing in the middle of a kill is a routine overlap, not an edge case, and unsynchronized
    /// writes to a <see cref="Dictionary{TKey,TValue}"/> from two threads can corrupt it or spin
    /// forever inside a lookup after a resize. Locking here, rather than around each call site,
    /// means a future caller cannot forget it, and keeps the lock's scope to the bookkeeping
    /// itself rather than spanning the multi-second kill work around it.
    /// </para>
    /// </summary>
    public sealed class OrphanLedger
    {
        private readonly object _gate = new();
        private readonly Dictionary<ProcessIdentity, string> _lastLogged = new();
        private readonly HashSet<ProcessIdentity> _alerted = new();
        private readonly HashSet<ProcessIdentity> _unkillable = new();
        private readonly Dictionary<ProcessIdentity, string> _parentImage = new();

        /// <summary>
        /// The parent as the log should name it, remembering the name while the parent is alive
        /// so it can still be named after the parent exits.
        ///
        /// <para>
        /// The kill branch that matters most is the one where the parent has already gone — and
        /// by then it can no longer be asked what it was. The parent image is the one clue to
        /// which tool leaked the child, so it is captured every scan the parent is seen alive and
        /// recalled once it is not. A parent that exited before any scan saw it cannot be named
        /// at all, and the log says so rather than guessing.
        /// </para>
        /// </summary>
        /// <param name="identity">The candidate process, not its parent.</param>
        /// <param name="parentExists">Whether this scan found the parent alive.</param>
        /// <param name="parentImagePath">
        /// The parent image as read this scan: a full path when it could be read, otherwise the
        /// bare name. Ignored when the parent is gone.
        /// </param>
        /// <returns>
        /// The live parent image; <c>(gone, was …)</c> with the last one seen; or
        /// <c>(gone, never seen)</c>.
        /// </returns>
        public string DescribeParent(ProcessIdentity identity, bool parentExists, string parentImagePath)
        {
            lock (_gate)
            {
                if (parentExists)
                {
                    if (string.IsNullOrEmpty(parentImagePath))
                        return _parentImage.TryGetValue(identity, out string? known) ? known : "(unreadable)";

                    _parentImage[identity] = parentImagePath;
                    return parentImagePath;
                }

                return _parentImage.TryGetValue(identity, out string? last)
                    ? "(gone, was " + last + ")"
                    : "(gone, never seen)";
            }
        }

        /// <summary>
        /// Whether this verdict is worth a log line, recording it as said when it is.
        ///
        /// <para>
        /// A verdict that has not changed is not news. Without this a legitimately long search
        /// writes the same line sixty times an hour and buries the one line somebody needs.
        /// </para>
        /// </summary>
        public bool ShouldLog(ProcessIdentity identity, string reason)
        {
            lock (_gate)
            {
                if (_lastLogged.TryGetValue(identity, out string? previous) &&
                    string.Equals(previous, reason, StringComparison.Ordinal))
                    return false;

                _lastLogged[identity] = reason;
                return true;
            }
        }

        /// <summary>
        /// Whether to raise a notice. False once this process has been reported, and false
        /// forever for one that survived both kill attempts — nagging about a process the user
        /// cannot end is noise.
        /// </summary>
        public bool ShouldAlert(ProcessIdentity identity)
        {
            lock (_gate)
            {
                return !_alerted.Contains(identity) && !_unkillable.Contains(identity);
            }
        }

        /// <summary>Records that the user has been told about this process.</summary>
        public void MarkAlerted(ProcessIdentity identity)
        {
            lock (_gate) { _alerted.Add(identity); }
        }

        /// <summary>
        /// Records that this process survived a terminate and a tree kill. It is never tried
        /// again and never reported again.
        /// </summary>
        public void MarkUnkillable(ProcessIdentity identity)
        {
            lock (_gate) { _unkillable.Add(identity); }
        }

        /// <summary>Whether this process has already been given up on.</summary>
        public bool IsUnkillable(ProcessIdentity identity)
        {
            lock (_gate) { return _unkillable.Contains(identity); }
        }

        /// <summary>
        /// Drops bookkeeping for processes no longer present.
        ///
        /// <para>
        /// MicaStats runs for weeks. Without this the four collections accumulate an entry per
        /// search anyone has ever run.
        /// </para>
        /// </summary>
        public void Prune(IReadOnlyCollection<ProcessIdentity> alive)
        {
            if (alive == null) return;
            var live = alive as HashSet<ProcessIdentity> ?? new HashSet<ProcessIdentity>(alive);

            lock (_gate)
            {
                Remove(_lastLogged.Keys, live, key => _lastLogged.Remove(key));
                Remove(_parentImage.Keys, live, key => _parentImage.Remove(key));
                Remove(_alerted, live, key => _alerted.Remove(key));
                Remove(_unkillable, live, key => _unkillable.Remove(key));
            }
        }

        /// <summary>
        /// Drops every key not in <paramref name="live"/>. The doomed keys are collected first
        /// rather than removed during iteration, which would invalidate the enumerator.
        /// </summary>
        private static void Remove(
            IEnumerable<ProcessIdentity> keys,
            HashSet<ProcessIdentity> live,
            Func<ProcessIdentity, bool> remove)
        {
            List<ProcessIdentity>? gone = null;
            foreach (var key in keys)
            {
                if (!live.Contains(key)) (gone ??= new List<ProcessIdentity>()).Add(key);
            }
            if (gone == null) return;
            foreach (var key in gone) remove(key);
        }
    }
}
