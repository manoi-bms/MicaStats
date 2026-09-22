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
    /// </summary>
    public sealed class OrphanLedger
    {
        private readonly Dictionary<ProcessIdentity, string> _lastLogged = new();
        private readonly HashSet<ProcessIdentity> _alerted = new();
        private readonly HashSet<ProcessIdentity> _unkillable = new();

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
            if (_lastLogged.TryGetValue(identity, out string? previous) &&
                string.Equals(previous, reason, StringComparison.Ordinal))
                return false;

            _lastLogged[identity] = reason;
            return true;
        }

        /// <summary>
        /// Whether to raise a notice. False once this process has been reported, and false
        /// forever for one that survived both kill attempts — nagging about a process the user
        /// cannot end is noise.
        /// </summary>
        public bool ShouldAlert(ProcessIdentity identity) =>
            !_alerted.Contains(identity) && !_unkillable.Contains(identity);

        /// <summary>Records that the user has been told about this process.</summary>
        public void MarkAlerted(ProcessIdentity identity) => _alerted.Add(identity);

        /// <summary>
        /// Records that this process survived a terminate and a tree kill. It is never tried
        /// again and never reported again.
        /// </summary>
        public void MarkUnkillable(ProcessIdentity identity) => _unkillable.Add(identity);

        /// <summary>Whether this process has already been given up on.</summary>
        public bool IsUnkillable(ProcessIdentity identity) => _unkillable.Contains(identity);

        /// <summary>
        /// Drops bookkeeping for processes no longer present.
        ///
        /// <para>
        /// MicaStats runs for weeks. Without this the three collections accumulate an entry per
        /// search anyone has ever run.
        /// </para>
        /// </summary>
        public void Prune(IReadOnlyCollection<ProcessIdentity> alive)
        {
            if (alive == null) return;
            var live = alive as HashSet<ProcessIdentity> ?? new HashSet<ProcessIdentity>(alive);

            Remove(_lastLogged.Keys, live, key => _lastLogged.Remove(key));
            Remove(_alerted, live, key => _alerted.Remove(key));
            Remove(_unkillable, live, key => _unkillable.Remove(key));
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
