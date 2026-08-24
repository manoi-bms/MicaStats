using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Parses the elevated one-shot request <c>--kill &lt;pid&gt; &lt;createTime&gt;</c>.
    ///
    /// <para>
    /// This is the entire input surface of the only code path that runs with administrator
    /// rights, so it refuses anything it does not completely understand: exactly three
    /// arguments, the switch matched case-sensitively, a positive pid inside <see cref="int"/>,
    /// and a non-negative FILETIME. A trailing argument is a rejection rather than something to
    /// ignore — an argument this code does not recognise means the caller is not the caller it
    /// expects.
    /// </para>
    ///
    /// <para>
    /// Note this grants no capability: anyone able to run this executable can already run
    /// <c>taskkill /f</c>. What it has to guarantee is that the elevated path does exactly one
    /// thing.
    /// </para>
    /// </summary>
    public static class KillArguments
    {
        /// <summary>The switch that selects the one-shot termination path.</summary>
        public const string Switch = "--kill";

        /// <summary>
        /// Reads a well-formed request, or returns false leaving both outputs at zero.
        /// </summary>
        public static bool TryParse(string[] args, out int pid, out long createTime)
        {
            pid = 0;
            createTime = 0;

            if (args is not { Length: 3 }) return false;
            if (!string.Equals(args[0], Switch, StringComparison.Ordinal)) return false;

            // NumberStyles.None is deliberate: it rejects leading signs, surrounding whitespace
            // and thousands separators, so " +8420 " does not quietly become 8420.
            if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPid))
                return false;
            if (parsedPid <= 0) return false;   // 0 is the idle process; negatives are nonsense

            if (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long parsedTime))
                return false;
            if (parsedTime < 0) return false;

            pid = parsedPid;
            createTime = parsedTime;
            return true;
        }
    }
}
