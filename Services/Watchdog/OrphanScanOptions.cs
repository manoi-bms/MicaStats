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
        /// Rule 2. The deepest <c>-maxdepth</c> that still counts as a bounded scan.
        ///
        /// <para>
        /// Not "any depth at all", because under Git Bash <c>/</c> mounts every drive: a depth of
        /// 6 from there is every drive five levels down, which on a real machine ran for more
        /// than 5400 seconds of CPU with nothing reading the output. A depth this shallow
        /// finishes in seconds from any root; deeper ones fall through to rules 3 to 5 like an
        /// unbounded scan, so a deliberate deep search is still protected by its age, its CPU
        /// and its live shell.
        /// </para>
        /// </summary>
        public int TrustedMaxDepth { get; init; } = 3;

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
        /// The user's settings, with an empty or all-blank list falling back to the shipped
        /// default rather than to nothing.
        ///
        /// <para>
        /// An empty binary allowlist would silently switch the feature off; an empty parent list
        /// would silently make every live parent unrecognised and put deliberate searches in
        /// range. Neither is a thing anyone means by clearing a list in a config file — and
        /// neither is <c>[""]</c>: both consumers skip blank entries, so a list of nothing but
        /// blanks is functionally the same empty list wearing a disguise. Entries that are
        /// merely blank are dropped rather than causing the whole list to be discarded, so a
        /// stray <c>""</c> next to a real entry does not throw the real one away too.
        /// </para>
        /// </summary>
        public static OrphanScanOptions FromConfig(Kil0bitSystemMonitor.Models.AppConfig config)
        {
            if (config == null) return Defaults;

            return new OrphanScanOptions
            {
                CpuSecondsThreshold = config.OrphanCpuSecondsThreshold,
                Grace = TimeSpan.FromMinutes(config.OrphanGraceMinutes),
                TrustedMaxDepth = config.OrphanTrustedMaxDepth,
                BinarySuffixes = NonBlank(config.OrphanBinaryAllowlist) is { Count: > 0 } binaries
                    ? binaries
                    : Defaults.BinarySuffixes,
                ExpectedParents = NonBlank(config.OrphanExpectedParents) is { Count: > 0 } parents
                    ? parents
                    : Defaults.ExpectedParents,
            };
        }

        /// <summary>The non-blank entries of <paramref name="values"/>, or an empty list.</summary>
        private static List<string> NonBlank(string[]? values)
        {
            var result = new List<string>();
            if (values == null) return result;

            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);

            return result;
        }
    }
}
