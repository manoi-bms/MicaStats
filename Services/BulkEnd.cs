using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.ViewModels;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Ends a planned set of processes and reports what actually happened.
    ///
    /// <para>
    /// End first, wait once, then verify. Terminating all of them before any wait keeps a batch
    /// of dozens to a couple of seconds; waiting per process would take minutes. Each reported
    /// termination is then checked by asking the kernel whether that exact process exited,
    /// because a process wedged in kernel I/O can report a successful kill and keep running.
    /// </para>
    ///
    /// <para>
    /// No tree kill. A filter matches what the user can see; ending children it did not match
    /// would end processes that were never in the preview. Blocking, so it must never run on the
    /// UI thread.
    /// </para>
    /// </summary>
    public static class BulkEnd
    {
        /// <summary>How long to let a batch of terminations land before verifying them.</summary>
        private const int SettleMs = 2000;

        private const string Area = "processes";

        /// <summary>Ends every target and counts the outcomes. Never throws.</summary>
        public static BulkEndResult Run(IReadOnlyList<ProcessUsage> targets)
        {
            if (targets == null || targets.Count == 0) return new BulkEndResult(0, 0, 0, 0);

            int ended = 0, denied = 0, failed = 0;
            var toVerify = new List<ProcessUsage>();

            foreach (var p in targets)
            {
                try
                {
                    var result = ProcessControl.TryEndTask(p.Pid, p.CreateTime, p.Name, out string message);
                    switch (result)
                    {
                        case EndTaskResult.Terminated:
                        case EndTaskResult.AlreadyExited:
                            toVerify.Add(p);
                            break;
                        case EndTaskResult.Recycled:
                            // The PID belongs to another process now; the one planned is gone.
                            ended++;
                            break;
                        case EndTaskResult.AccessDenied:
                            denied++;
                            Log(p, "ACCESS-DENIED", message);
                            break;
                        default:
                            failed++;
                            Log(p, "FAILED", message);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // One process that throws must not abandon the rest of the batch.
                    failed++;
                    DiagnosticsLog.Error(Area, "Ending pid " + p.Pid.ToString(CultureInfo.InvariantCulture) + " failed", ex);
                }
            }

            int survived = 0;
            if (toVerify.Count > 0)
            {
                System.Threading.Thread.Sleep(SettleMs);
                foreach (var p in toVerify)
                {
                    bool gone;
                    try { gone = ProcessControl.HasExited(p.Pid, p.CreateTime); }
                    catch (Exception) { gone = false; }

                    if (gone) { ended++; Log(p, "ENDED", ""); }
                    else { survived++; Log(p, "SURVIVED", "reported ended but the kernel says it is still running"); }
                }
            }

            var summary = new BulkEndResult(ended, denied, survived, failed);
            DiagnosticsLog.Log(Area, "End all filtered: " + summary.Describe());
            return summary;
        }

        private static void Log(ProcessUsage p, string outcome, string detail) =>
            DiagnosticsLog.Log(Area,
                outcome + " " + p.Name + " pid=" + p.Pid.ToString(CultureInfo.InvariantCulture)
                + (detail.Length == 0 ? "" : " :: " + detail));
    }
}
