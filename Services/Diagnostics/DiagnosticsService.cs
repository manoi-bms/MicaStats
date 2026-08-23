using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.HardwareInfo;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>One saved report on disk, for the list in the Slowdowns tab.</summary>
    public sealed record SavedReport(string Path, string Name, DateTime At, long Bytes)
    {
        public string SizeText => Bytes >= 1024 ? (Bytes / 1024) + " KB" : Bytes + " B";

        public string WhenText => At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Assembles what the diagnostics window shows and writes the combined report.
    ///
    /// <para>
    /// Reuses <see cref="SpecGroup"/> and <see cref="SpecRow"/> from the hardware inspector so
    /// the two windows render identically and the same row template serves both.
    /// </para>
    /// </summary>
    public static class DiagnosticsService
    {
        /// <summary>Reports currently on disk, newest first.</summary>
        public static List<SavedReport> ListReports()
        {
            var result = new List<SavedReport>();
            try
            {
                var dir = new DirectoryInfo(SlowdownRecorder.ReportDir);
                if (!dir.Exists) return result;

                foreach (var file in dir.GetFiles("*.txt"))
                {
                    result.Add(new SavedReport(file.FullName, file.Name,
                        file.LastWriteTime, file.Length));
                }
                result.Sort((a, b) => b.At.CompareTo(a.At));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("diagnostics", "Report listing failed: " + ex.Message);
            }
            return result;
        }

        /// <summary>The Battery tab's read-only rows.</summary>
        public static List<SpecGroup> BuildBattery(BatteryHealth? health, BatteryReading? reading,
                                                   TimeSpan? osEstimate)
        {
            var groups = new List<SpecGroup>();

            var wear = new SpecGroup("Health", UiGlyphs.Health);
            if (health != null && health.Any && health.HealthPercent >= 0)
            {
                wear.AddAlways("Health",
                    health.HealthPercent.ToString("F1", CultureInfo.InvariantCulture) + "%  (" +
                    BatteryEstimate.HealthVerdict(health.HealthPercent) + ")");
                wear.AddAlways("Full charge capacity", Mwh(health.FullChargeCapacityMwh));
                wear.AddAlways("Design capacity", Mwh(health.DesignCapacityMwh));
                wear.AddAlways("Capacity lost to age",
                    Math.Max(0d, 100d - health.HealthPercent).ToString("F1", CultureInfo.InvariantCulture) + "%");
                if (health.CycleCount >= 0)
                    wear.AddAlways("Charge cycles", health.CycleCount.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                wear.AddAlways("Health", "");   // renders as an honest dash
                wear.AddAlways("Reason", "The firmware did not report a design capacity.");
            }
            groups.Add(wear);

            var now = new SpecGroup("Right now", UiGlyphs.Battery);
            if (reading is { Present: true })
            {
                now.AddAlways("Charge", reading.Percent.ToString(CultureInfo.InvariantCulture) + "%");
                now.AddAlways("Power source", reading.OnAcPower ? "Plugged in" : "On battery");

                string state = reading.Charging ? "Charging"
                    : reading.Discharging ? "Discharging"
                    : reading.OnAcPower ? "Not charging" : "Idle";
                now.AddAlways("State", state);

                if (reading.RateMw > 0)
                    now.AddAlways(reading.Charging ? "Charging at" : "Drawing",
                        reading.Watts.ToString("F1", CultureInfo.InvariantCulture) + " W");

                if (reading.RemainingMwh > 0) now.AddAlways("Charge left", Mwh(reading.RemainingMwh));
                if (reading.VoltageMv > 0)
                    now.AddAlways("Voltage",
                        (reading.VoltageMv / 1000d).ToString("F2", CultureInfo.InvariantCulture) + " V");
            }
            else
            {
                now.AddAlways("Battery", "");
            }
            groups.Add(now);

            var time = new SpecGroup("Time remaining", UiGlyphs.Clock);
            if (reading is { Present: true, Discharging: true })
            {
                time.AddAlways("Measured here",
                    BatteryEstimate.Format(BatteryEstimate.TimeToEmpty(reading.RemainingMwh, reading.RateMw)));
            }
            else if (reading is { Present: true, Charging: true })
            {
                time.AddAlways("Until full", BatteryEstimate.Format(BatteryEstimate.TimeToFull(
                    reading.RemainingMwh,
                    health != null && health.FullChargeCapacityMwh > 0 ? health.FullChargeCapacityMwh : 0,
                    reading.RateMw)));
            }
            else if (reading is { Present: true })
            {
                time.AddAlways("Measured here", "Plugged in, so there is nothing to count down.");
            }

            // Shown so the difference is visible rather than asserted. Windows reports a
            // sentinel of roughly 136 years when it has no estimate, which is exactly why
            // MicaStats computes its own from the measured draw instead of forwarding this.
            time.AddAlways("Windows' own estimate",
                osEstimate == null ? "Not available" : BatteryEstimate.Format(osEstimate));
            groups.Add(time);

            return groups;
        }

        /// <summary>The Boot tab's summary rows. The lists are rendered separately.</summary>
        public static List<SpecGroup> BuildBootSummary(BootAnalysis? analysis)
        {
            var groups = new List<SpecGroup>();
            var summary = new SpecGroup("Last boot", UiGlyphs.Boot);

            var latest = analysis?.Latest;
            if (latest != null)
            {
                summary.AddAlways("Time to desktop", latest.SecondsText);
                summary.AddAlways("When", latest.BootAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                if (latest.MainPathMs > 0)
                    summary.AddAlways("Of which, core startup", Seconds(latest.MainPathMs));
                if (latest.PostBootMs > 0)
                    summary.AddAlways("Of which, after sign-in", Seconds(latest.PostBootMs));
                if (latest.StartupAppCount > 0)
                    summary.AddAlways("Apps Windows counted",
                        latest.StartupAppCount.ToString(CultureInfo.InvariantCulture));
                if (latest.IsDegradation)
                    summary.AddAlways("Windows' verdict", "Slower than this machine's normal boot");
            }
            else
            {
                summary.AddAlways("Time to desktop", "");
                if (!string.IsNullOrWhiteSpace(analysis?.Problem))
                    summary.AddAlways("Why", analysis!.Problem!);
            }
            groups.Add(summary);

            if (analysis != null && analysis.Boots.Count >= 2)
            {
                var trend = new SpecGroup("Trend", UiGlyphs.History);
                trend.AddAlways("Boots recorded", analysis.Boots.Count.ToString(CultureInfo.InvariantCulture));
                trend.AddAlways("Average", analysis.AverageSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");

                double delta = analysis.TrendSeconds;
                string direction = delta > 0.5 ? "slower than usual"
                    : delta < -0.5 ? "faster than usual"
                    : "about the same as usual";
                trend.AddAlways("Last boot was",
                    Math.Abs(delta).ToString("F1", CultureInfo.InvariantCulture) + " s " + direction);
                groups.Add(trend);
            }

            if (analysis != null && analysis.Entries.Count > 0)
            {
                var startup = new SpecGroup("Startup entries", UiGlyphs.Processes);
                startup.AddAlways("Registered", analysis.Entries.Count.ToString(CultureInfo.InvariantCulture));
                startup.AddAlways("Will actually run",
                    StartupEntries.CountEnabled(analysis.Entries).ToString(CultureInfo.InvariantCulture));
                groups.Add(startup);
            }

            return groups;
        }

        /// <summary>
        /// Writes one text file covering everything the window shows, for pasting into a
        /// support thread. Pure over its inputs so the shape can be pinned by tests.
        /// </summary>
        public static string BuildReport(BootAnalysis? boot, BatteryHealth? health,
                                         BatteryReading? battery, IReadOnlyList<AlertRule>? rules,
                                         IReadOnlyList<SavedReport>? reports, string appVersion)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.AppendLine("==============================================================");
            sb.AppendLine(" MicaStats Diagnostics Report");
            sb.AppendLine(" Generated : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(" Version   : MicaStats " + appVersion);
            sb.AppendLine("==============================================================");

            sb.AppendLine();
            sb.AppendLine("[Boot]");
            if (boot?.Latest is { } latest)
            {
                sb.AppendLine("  Last boot took " + latest.SecondsText + " on " +
                    latest.BootAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
                if (boot.Boots.Count >= 2)
                {
                    sb.AppendLine("  Average of " + boot.Boots.Count.ToString(CultureInfo.InvariantCulture) +
                        " recorded boots: " + boot.AverageSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s.");
                }
                sb.AppendLine("  Startup entries: " + boot.Entries.Count.ToString(CultureInfo.InvariantCulture) +
                    " registered, " + StartupEntries.CountEnabled(boot.Entries).ToString(CultureInfo.InvariantCulture) +
                    " enabled.");

                if (boot.Delays.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  What held it up:");
                    int shown = 0;
                    foreach (var d in boot.Delays)
                    {
                        if (shown >= 15) break;
                        shown++;
                        sb.AppendLine("    " + d.TotalText.PadLeft(9) + "  " +
                            d.Kind.ToString().PadRight(12) + d.DisplayName);
                    }
                }
            }
            else
            {
                sb.AppendLine("  " + (boot?.Problem ?? "No boot measurements available."));
            }

            sb.AppendLine();
            sb.AppendLine("[Battery]");
            if (health is { Any: true } && health.HealthPercent >= 0)
            {
                sb.AppendLine("  Health " + health.HealthPercent.ToString("F1", CultureInfo.InvariantCulture) +
                    "% (" + BatteryEstimate.HealthVerdict(health.HealthPercent) + ")");
                sb.AppendLine("  " + Mwh(health.FullChargeCapacityMwh) + " of " +
                    Mwh(health.DesignCapacityMwh) + " design capacity, " +
                    health.CycleCount.ToString(CultureInfo.InvariantCulture) + " cycles.");
            }
            else if (battery is { Present: true })
            {
                sb.AppendLine("  Present, but the firmware reported no design capacity.");
            }
            else
            {
                sb.AppendLine("  No battery on this machine.");
            }

            if (battery is { Present: true })
            {
                sb.AppendLine("  Charge " + battery.Percent.ToString(CultureInfo.InvariantCulture) + "%, " +
                    (battery.OnAcPower ? "plugged in" : "on battery") +
                    (battery.RateMw > 0
                        ? ", " + battery.Watts.ToString("F1", CultureInfo.InvariantCulture) + " W"
                        : ""));
            }

            sb.AppendLine();
            sb.AppendLine("[Alerts]");
            if (rules == null || rules.Count == 0) sb.AppendLine("  No rules configured.");
            else foreach (var r in rules)
                sb.AppendLine("  [" + (r.Enabled ? "on " : "off") + "]  " + r.Describe());

            sb.AppendLine();
            sb.AppendLine("[Saved reports]");
            if (reports == null || reports.Count == 0) sb.AppendLine("  None yet.");
            else
            {
                int shown = 0;
                foreach (var r in reports)
                {
                    if (shown >= 20) break;
                    shown++;
                    sb.AppendLine("  " + r.WhenText + "  " + r.SizeText.PadLeft(7) + "  " + r.Name);
                }
            }

            sb.AppendLine();
            sb.AppendLine("Reports and the diagnostics log live in " + DiagnosticsLog.DataDir + ".");
            return sb.ToString();
        }

        /// <summary>Saves the combined report and returns the path written.</summary>
        public static string SaveReport(BootAnalysis? boot, BatteryHealth? health,
                                        BatteryReading? battery, IReadOnlyList<AlertRule>? rules,
                                        IReadOnlyList<SavedReport>? reports)
        {
            string version = typeof(DiagnosticsService).Assembly.GetName().Version?.ToString(3) ?? "?";
            string text = BuildReport(boot, health, battery, rules, reports, version);

            Directory.CreateDirectory(SlowdownRecorder.ReportDir);
            string path = Path.Combine(SlowdownRecorder.ReportDir,
                "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");

            File.WriteAllText(path, text, new UTF8Encoding(false));
            DiagnosticsLog.Log("diagnostics", "Saved report " + path);
            return path;
        }

        private static string Mwh(int mwh) =>
            mwh <= 0 ? "—" : mwh.ToString("N0", CultureInfo.InvariantCulture) + " mWh";

        private static string Seconds(int ms) =>
            (ms / 1000d).ToString("F1", CultureInfo.InvariantCulture) + " s";
    }
}
