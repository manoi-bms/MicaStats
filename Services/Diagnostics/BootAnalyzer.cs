using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>
    /// Reads how long recent boots took and what held them up.
    ///
    /// <para>
    /// Windows has measured this all along and writes it to
    /// <c>Microsoft-Windows-Diagnostics-Performance/Operational</c>, where practically nobody
    /// looks. Task Manager's Startup apps page rates each entry "High", "Medium" or "Low" and
    /// shows no number at all, so there is no way to tell whether last week's change helped.
    /// This turns the log into a number and a trend.
    /// </para>
    ///
    /// <para>
    /// The log is readable without administrator rights — confirmed by reading it from a
    /// non-elevated shell on the development machine, which reported a 117,822 ms boot.
    /// </para>
    ///
    /// <para>
    /// <c>System.Diagnostics.Eventing.Reader</c> needs no package reference: the assembly is
    /// part of the WindowsDesktop shared framework this app already targets, so adding the
    /// NuGet package only puts an inert entry in the dependency manifest.
    /// </para>
    /// </summary>
    public static class BootAnalyzer
    {
        private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

        /// <summary>Boots kept for the trend. Roughly a month of daily use.</summary>
        public const int MaxBoots = 20;

        /// <summary>Delay rows kept for the most recent boot.</summary>
        public const int MaxDelays = 40;

        /// <summary>Hard ceiling on events walked, so a huge log cannot stall the window.</summary>
        private const int MaxEventsScanned = 1500;

        /// <summary>
        /// Gathers the boot history and the current startup list. Blocking — always called
        /// from a background task.
        /// </summary>
        public static BootAnalysis Gather()
        {
            List<StartupEntry> entries;
            try { entries = StartupEntries.Read(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("boot", "Startup entry enumeration failed", ex);
                entries = new List<StartupEntry>();
            }

            var boots = new List<BootRecord>(MaxBoots);
            var delays = new List<StartupDelay>(MaxDelays);
            string? problem = null;

            try
            {
                ReadEvents(boots, delays);
            }
            catch (EventLogNotFoundException)
            {
                problem = "Windows is not recording boot performance on this machine, so there is nothing to read.";
            }
            catch (UnauthorizedAccessException)
            {
                problem = "Windows refused access to the boot performance log.";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("boot", "Boot log read failed", ex);
                problem = "The boot performance log could not be read — see the diagnostics log.";
            }

            if (problem == null && boots.Count == 0)
                problem = "No boot measurements have been recorded yet. One appears a few minutes after each start.";

            delays.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            if (delays.Count > MaxDelays) delays.RemoveRange(MaxDelays, delays.Count - MaxDelays);

            if (boots.Count > 0)
            {
                DiagnosticsLog.Log("boot", string.Format(CultureInfo.InvariantCulture,
                    "Last boot {0:F1} s, {1} startup entries ({2} enabled), {3} delay records",
                    boots[0].Seconds, entries.Count, StartupEntries.CountEnabled(entries), delays.Count));
            }

            return new BootAnalysis
            {
                Boots = boots,
                Delays = delays,
                Entries = entries,
                Problem = problem,
            };
        }

        private static void ReadEvents(List<BootRecord> boots, List<StartupDelay> delays)
        {
            var query = new EventLogQuery(LogName, PathType.LogName,
                "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103)]]")
            {
                // Newest first, so the scan can stop as soon as enough history is in hand
                // instead of walking a log that may hold years of entries.
                ReverseDirection = true,
            };

            // Delay records belong to the boot that logged them. Every delay event carries the
            // same StartTime as its siblings, so the newest StartTime identifies the most
            // recent boot's group; older groups are skipped rather than blended together,
            // which would attribute last month's slow driver to this morning.
            string? newestStartTime = null;

            // Windows writes these records more than once — verified on this machine, where
            // event 100 appears twice with an identical timestamp and boot time, and every
            // delay record likewise. Without this the list reads "NVIDIA App 6.70 s" twice and
            // looks like two separate problems.
            var seenDelays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var reader = new EventLogReader(query);
            for (int scanned = 0; scanned < MaxEventsScanned; scanned++)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Warn("boot", "Stopped reading events: " + ex.Message);
                    break;
                }

                if (record == null) break;

                using (record)
                {
                    int id = record.Id;
                    DateTime at = record.TimeCreated ?? DateTime.Now;

                    if (id == BootEventParser.BootEventId)
                    {
                        if (boots.Count >= MaxBoots) continue;
                        var fields = BootEventParser.ReadFields(SafeXml(record));
                        var boot = BootEventParser.ParseBoot(fields, at);

                        // The same boot can be logged more than once; keep one row per boot.
                        if (boot != null && (boots.Count == 0 || boots[^1].BootAt != boot.BootAt))
                            boots.Add(boot);
                    }
                    else
                    {
                        if (delays.Count >= MaxDelays) continue;
                        var fields = BootEventParser.ReadFields(SafeXml(record));

                        fields.TryGetValue("StartTime", out string? startTime);
                        newestStartTime ??= startTime;

                        // Once the group changes, every remaining delay belongs to an older boot.
                        if (!string.IsNullOrEmpty(startTime) &&
                            !string.Equals(startTime, newestStartTime, StringComparison.Ordinal))
                            continue;

                        var delay = BootEventParser.ParseDelay(id, fields, at);
                        if (delay != null && seenDelays.Add(DelayKey(delay))) delays.Add(delay);
                    }
                }

                if (boots.Count >= MaxBoots && delays.Count >= MaxDelays) break;
            }
        }

        /// <summary>
        /// Identity of a delay record for de-duplication: the same component, of the same
        /// kind, blamed for the same duration is the same finding logged twice.
        /// </summary>
        public static string DelayKey(StartupDelay delay) =>
            delay.Kind + "|" + delay.Name + "|" + delay.FriendlyName + "|" +
            delay.TotalMs.ToString(CultureInfo.InvariantCulture);

        private static string? SafeXml(EventRecord record)
        {
            try { return record.ToXml(); }
            catch { return null; }
        }
    }
}
