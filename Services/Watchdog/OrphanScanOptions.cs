using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// The thresholds and allowlists the rules are measured against.
    ///
    /// <para>
    /// The defaults are deliberately conservative and must not be relaxed to catch more. A
    /// missed orphan costs a core until the next scan; a false positive destroys work somebody
    /// intended, silently, and they find out when the results never arrive.
    /// </para>
    /// </summary>
    public sealed class OrphanScanOptions
    {
        /// <summary>Rule 3. Below this, a scan is still plausibly doing its job.</summary>
        public double CpuSecondsThreshold { get; init; } = 120;

        /// <summary>Rule 4. Nothing younger than this is ever killed.</summary>
        public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Rule 1. Image-path suffixes, matched case-insensitively after slashes are normalised.
        /// A suffix rather than a name so <c>C:\Windows\System32\find.exe</c> cannot match.
        /// </summary>
        public IReadOnlyList<string> BinarySuffixes { get; init; } = new[] { @"\Git\usr\bin\find.exe" };

        /// <summary>
        /// Rule 5. Parent images that mean a human still has somewhere to receive the output.
        /// Compared on file name only: the same shell ships from several install locations.
        /// </summary>
        public IReadOnlyList<string> ExpectedParents { get; init; } =
            new[] { "bash.exe", "sh.exe", "pwsh.exe", "cmd.exe" };

        /// <summary>The shipped defaults, as described in the README.</summary>
        public static OrphanScanOptions Defaults { get; } = new();

        /// <summary>
        /// The user's settings, with an empty list falling back to the shipped default rather
        /// than to nothing.
        ///
        /// <para>
        /// An empty binary allowlist would silently switch the feature off; an empty parent list
        /// would silently make every live parent unrecognised and put deliberate searches in
        /// range. Neither is a thing anyone means by clearing a list in a config file.
        /// </para>
        /// </summary>
        public static OrphanScanOptions FromConfig(Kil0bitSystemMonitor.Models.AppConfig config)
        {
            if (config == null) return Defaults;

            return new OrphanScanOptions
            {
                CpuSecondsThreshold = config.OrphanCpuSecondsThreshold,
                Grace = TimeSpan.FromMinutes(config.OrphanGraceMinutes),
                BinarySuffixes = config.OrphanBinaryAllowlist is { Length: > 0 } binaries
                    ? binaries
                    : Defaults.BinarySuffixes,
                ExpectedParents = config.OrphanExpectedParents is { Length: > 0 } parents
                    ? parents
                    : Defaults.ExpectedParents,
            };
        }
    }
}
