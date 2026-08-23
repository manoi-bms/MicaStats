using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>
    /// Reads the battery: live charge and draw from WMI on every request, wear figures from a
    /// cached <c>powercfg</c> report.
    ///
    /// <para>
    /// The split exists because the two have very different costs. The live sample is a cheap
    /// WMI query against <c>root\WMI</c>; the wear figures require spawning <c>powercfg</c>,
    /// which takes the better part of a second, so it runs at most once every six hours on a
    /// background thread and everything else reads the cached result.
    /// </para>
    ///
    /// <para>
    /// Everything here works unelevated — verified on the development laptop, where
    /// <c>BatteryFullChargedCapacity</c>, <c>BatteryStatus</c> and the powercfg report all
    /// returned real values from a non-elevated shell.
    /// </para>
    /// </summary>
    public sealed class BatteryMonitor : IDisposable
    {
        /// <summary>How long a wear reading stays fresh. Capacity moves over months, not minutes.</summary>
        private static readonly TimeSpan HealthTtl = TimeSpan.FromHours(6);

        /// <summary>Guard on the powercfg spawn, so a slow machine cannot queue several.</summary>
        private readonly SemaphoreSlim _healthGate = new(1, 1);

        private BatteryHealth _health = BatteryHealth.Empty;
        private DateTime _healthAtUtc = DateTime.MinValue;
        private bool _disposed;

        /// <summary>The last wear figures read, or <see cref="BatteryHealth.Empty"/>.</summary>
        public BatteryHealth Health => _health;

        /// <summary>True once a wear reading has been attempted, whatever the outcome.</summary>
        public bool HealthResolved => _healthAtUtc != DateTime.MinValue;

        /// <summary>
        /// True when this machine has a battery at all. Cheap enough to call freely; a desktop
        /// answers false and the diagnostics window then hides the whole tab.
        /// </summary>
        public static bool HasBattery()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT BatteryStatus FROM Win32_Battery");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    mo.Dispose();
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ----- Cached live sample -------------------------------------------------------------
        // The telemetry tick runs at 1 Hz and a WMI query is not free, so the overlay reads
        // through this cache. Charge moves over minutes; sampling it every second would spend
        // real CPU to redraw the same number, which is the trade this app has repeatedly
        // refused elsewhere.

        private static readonly object CacheGate = new();
        private static BatteryReading _cached = BatteryReading.None;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        /// <summary>Most recent wear reading as a percentage, or -1. Set by <see cref="GetHealthAsync"/>.</summary>
        public static double LastKnownHealthPercent { get; private set; } = -1d;

        /// <summary>
        /// A live sample, reusing the previous one when it is younger than
        /// <paramref name="minIntervalMs"/>. Safe to call on the telemetry tick.
        /// </summary>
        public static BatteryReading ReadCached(int minIntervalMs = 5000)
        {
            lock (CacheGate)
            {
                if (_cachedAtUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - _cachedAtUtc).TotalMilliseconds < minIntervalMs)
                    return _cached;

                _cached = Read();
                _cachedAtUtc = DateTime.UtcNow;
                return _cached;
            }
        }

        /// <summary>Takes a live sample, or <see cref="BatteryReading.None"/> when there is no battery.</summary>
        public static BatteryReading Read()
        {
            try
            {
                // root\WMI BatteryStatus carries what Win32_Battery does not: the actual charge
                // and discharge rate in milliwatts, which is what an honest time-remaining needs.
                using var searcher = new ManagementObjectSearcher(@"root\WMI",
                    "SELECT Charging, Discharging, ChargeRate, DischargeRate, RemainingCapacity, Voltage, PowerOnline FROM BatteryStatus");

                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        bool charging = ToBool(mo["Charging"]);
                        bool discharging = ToBool(mo["Discharging"]);
                        int chargeRate = ToInt(mo["ChargeRate"]);
                        int dischargeRate = ToInt(mo["DischargeRate"]);
                        int remaining = ToInt(mo["RemainingCapacity"]);
                        int voltage = ToInt(mo["Voltage"]);
                        bool online = ToBool(mo["PowerOnline"]);

                        return new BatteryReading(
                            Present: true,
                            OnAcPower: online,
                            Charging: charging,
                            Discharging: discharging,
                            Percent: ReadPercent(),
                            RemainingMwh: remaining,
                            // Only one of the two is ever non-zero; publish whichever is active.
                            RateMw: charging ? chargeRate : dischargeRate,
                            VoltageMv: voltage);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("battery", "Live read failed", ex);
            }
            return BatteryReading.None;
        }

        /// <summary>Charge percentage, which only Win32_Battery reports directly.</summary>
        private static int ReadPercent()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT EstimatedChargeRemaining FROM Win32_Battery");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo) return Math.Clamp(ToInt(mo["EstimatedChargeRemaining"]), 0, 100);
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Windows' own time-remaining estimate, or null when it is the "unknown" sentinel.
        /// Read purely so the diagnostics window can show honestly whether the OS figure
        /// exists — the app's own estimate never depends on it.
        /// </summary>
        public static TimeSpan? ReadOsEstimate()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT EstimatedRunTime FROM Win32_Battery");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        long minutes = ToLong(mo["EstimatedRunTime"]);
                        if (BatteryEstimate.IsPlausibleOsEstimate(minutes))
                            return TimeSpan.FromMinutes(minutes);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Refreshes the wear figures if the cache has expired. Safe to call often; returns the
        /// cached value immediately when it is still fresh.
        /// </summary>
        public async Task<BatteryHealth> GetHealthAsync(bool force = false)
        {
            if (_disposed) return _health;
            if (!force && HealthResolved && DateTime.UtcNow - _healthAtUtc < HealthTtl) return _health;

            await _healthGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!force && HealthResolved && DateTime.UtcNow - _healthAtUtc < HealthTtl) return _health;

                var health = await Task.Run(ReadHealthViaPowercfg).ConfigureAwait(false);
                _health = health;
                _healthAtUtc = DateTime.UtcNow;
                LastKnownHealthPercent = health.Any ? health.HealthPercent : -1d;

                if (health.Any)
                {
                    // Logged every refresh so the file becomes the degradation history that
                    // Windows itself keeps nowhere the user can reach.
                    DiagnosticsLog.Log("battery", string.Format(CultureInfo.InvariantCulture,
                        "Health {0:F1}% ({1} of {2} mWh), {3} cycles",
                        health.HealthPercent, health.FullChargeCapacityMwh,
                        health.DesignCapacityMwh, health.CycleCount));
                }
                return health;
            }
            finally { _healthGate.Release(); }
        }

        /// <summary>
        /// Spawns <c>powercfg /batteryreport /XML</c> into a temporary file and parses it.
        /// Blocking; always called from a background task.
        /// </summary>
        private static BatteryHealth ReadHealthViaPowercfg()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "micastats-battery-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".xml");

            try
            {
                var psi = new ProcessStartInfo("powercfg.exe")
                {
                    // /XML makes the output a parseable document rather than the styled HTML
                    // page powercfg produces by default.
                    Arguments = "/batteryreport /XML /output \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return BatteryHealth.Empty;

                // powercfg is fast, but a hung child must not pin a thread for the session.
                if (!process.WaitForExit(20_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    DiagnosticsLog.Warn("battery", "powercfg did not finish within 20 s");
                    return BatteryHealth.Empty;
                }

                if (!File.Exists(path)) return BatteryHealth.Empty;
                return BatteryReportParser.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("battery", "powercfg report failed", ex);
                return BatteryHealth.Empty;
            }
            finally
            {
                // The report names the pack's serial number, so it does not stay on disk.
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static bool ToBool(object? value)
        {
            try { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static int ToInt(object? value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static long ToLong(object? value)
        {
            try { return value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _healthGate.Dispose();
        }
    }
}
