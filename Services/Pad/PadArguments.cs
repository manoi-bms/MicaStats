using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The <c>--pad [path]</c> switch: from the Start-menu shortcut, from Explorer's Open with,
    /// and from a pinned MicaPad taskbar button.
    /// </summary>
    public static class PadArguments
    {
        /// <summary>The switch itself.</summary>
        public const string Flag = "--pad";

        /// <summary>
        /// True when <c>--pad</c> is present. <paramref name="path"/> is the argument after it,
        /// unless that argument is missing, blank, or another switch.
        /// </summary>
        public static bool TryParse(IReadOnlyList<string> args, out string? path)
        {
            path = null;
            for (int i = 0; i < args.Count; i++)
            {
                if (!string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase)) continue;

                if (i + 1 < args.Count &&
                    !string.IsNullOrWhiteSpace(args[i + 1]) &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    path = args[i + 1];
                }
                return true;
            }
            return false;
        }
    }
}
