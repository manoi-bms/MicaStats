using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Update
{
    /// <summary>
    /// The background half of updating: runs the daily check, remembers what is waiting, and
    /// tells the rest of the app about it.
    ///
    /// <para>
    /// Deliberately quiet. The check is throttled to once a day, a version the user dismissed is
    /// never raised again, and a failure is logged rather than shown — nobody wants a monitoring
    /// utility interrupting them because GitHub was briefly unreachable.
    /// </para>
    /// </summary>
    public static class UpdateNotifier
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        /// <summary>Delay before the startup check, so it never competes with launch.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(25);

        /// <summary>The release waiting to be installed, or null. Read by the overlay menu.</summary>
        public static ReleaseInfo? Pending { get; private set; }

        /// <summary>The pending version as text (e.g. "v1.5.0"), or null.</summary>
        public static string? PendingVersion => Pending?.TagName;

        /// <summary>Raised on the UI thread when a newer release is found.</summary>
        public static event Action<ReleaseInfo>? UpdateFound;

        /// <summary>
        /// Starts the automatic check if it is enabled and due. Safe to call at startup; it
        /// returns immediately and does its work in the background.
        /// </summary>
        public static void ScheduleStartupCheck(AppConfig config, Dispatcher dispatcher)
        {
            if (config == null || dispatcher == null || !config.AutoCheckUpdates) return;
            if (!IsDue(config)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(StartupDelay).ConfigureAwait(false);
                    await RunCheckAsync(config, dispatcher, announce: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("update", "Scheduled check failed", ex);
                }
            });
        }

        /// <summary>True when no check has run inside the interval.</summary>
        public static bool IsDue(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.LastUpdateCheckUtc)) return true;

            return !DateTimeOffset.TryParse(config.LastUpdateCheckUtc,
                       CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                   || DateTimeOffset.UtcNow - last >= CheckInterval;
        }

        /// <summary>
        /// Performs a check and records the time. When <paramref name="announce"/> is set and a
        /// new version is found, the notification is raised on the UI thread.
        /// </summary>
        public static async Task<UpdateCheckResult> RunCheckAsync(AppConfig config, Dispatcher dispatcher,
                                                                  bool announce)
        {
            var result = await UpdateService.CheckAsync().ConfigureAwait(false);

            // Record the attempt regardless of outcome, so a machine that is offline for a week
            // does not retry on every single launch.
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                config.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            }));

            if (!result.UpdateAvailable || result.Release == null) return result;

            Pending = result.Release;

            if (announce && !IsSkipped(config, result.Release.TagName))
            {
                var release = result.Release;
                _ = dispatcher.BeginInvoke(new Action(() => UpdateFound?.Invoke(release)));
            }
            return result;
        }

        public static bool IsSkipped(AppConfig config, string tag) =>
            !string.IsNullOrWhiteSpace(config.SkippedUpdateVersion) &&
            string.Equals(config.SkippedUpdateVersion, tag, StringComparison.OrdinalIgnoreCase);

        /// <summary>Stops this version from being announced again.</summary>
        public static void Skip(AppConfig config, string tag)
        {
            config.SkippedUpdateVersion = tag;
            DiagnosticsLog.Log("update", "User dismissed " + tag);
        }

        /// <summary>Clears the pending release, e.g. after the installer has been launched.</summary>
        public static void Clear() => Pending = null;
    }
}
