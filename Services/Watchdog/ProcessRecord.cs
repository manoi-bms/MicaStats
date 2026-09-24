using System;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// One process, flattened to exactly what the decision needs.
    ///
    /// <para>
    /// A plain record rather than a handle or a <see cref="System.Diagnostics.Process"/> is the
    /// point: it is what lets the rules be exercised against the two orphans observed on the
    /// reported machine without those orphans existing. Everything expensive or privileged
    /// happens before one of these is built.
    /// </para>
    /// </summary>
    /// <param name="ParentExists">
    /// Whether the parent is still running. Resolved by <see cref="ParentState"/>, which also
    /// rejects a recycled parent PID — an unrelated newcomer wearing the dead parent number.
    /// </param>
    /// <param name="ParentImagePath">
    /// The live parent's full image path, falling back to its bare image name when the path
    /// cannot be read; an empty string when the parent is gone. Also written to the log on every
    /// kill: it names the tool that leaked the child, which is the only route to fixing the
    /// cause rather than the symptom.
    /// </param>
    public sealed record ProcessRecord(
        int Pid,
        int ParentPid,
        string ImagePath,
        string CommandLine,
        double CpuSeconds,
        DateTime StartTime,
        bool ParentExists,
        string ParentImagePath);

    /// <summary>What the scan decided about one process, and why.</summary>
    /// <param name="Reason">
    /// Human-readable and branch-specific. A keep names the rule that saved the process; a kill
    /// names which half of rule 5 fired. The log is read after the fact by someone asking why a
    /// process was or was not ended, and a generic reason answers neither question.
    /// </param>
    public sealed record OrphanVerdict(int Pid, bool Kill, string Reason);
}
