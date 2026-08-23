using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>
    /// Persists which rules are on and what their thresholds are, as one compact line in
    /// <c>config.json</c>.
    ///
    /// <para>
    /// A flat string rather than nested JSON because <see cref="AppConfig"/> is a flat
    /// property-change model that rewrites the whole file on every edit; a nested collection
    /// would need change tracking of its own to notice a threshold being typed.
    /// </para>
    ///
    /// <para>Format: <c>id:enabled:threshold:sustain</c>, separated by semicolons.</para>
    /// </summary>
    public static class AlertRuleSettings
    {
        public static string Serialize(IReadOnlyList<AlertRule> rules)
        {
            if (rules == null || rules.Count == 0) return "";

            var sb = new StringBuilder(128);
            foreach (var r in rules)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(r.Id).Append(':')
                  .Append(r.Enabled ? '1' : '0').Append(':')
                  .Append(r.Threshold.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                  .Append(r.SustainSeconds.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Applies a saved line over the defaults. Unknown ids are ignored and missing ones keep
        /// their default, so a config written by an older or newer build still loads.
        /// </summary>
        public static List<AlertRule> Parse(string? text)
        {
            var rules = new List<AlertRule>(AlertRule.Defaults);
            if (string.IsNullOrWhiteSpace(text)) return rules;

            foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = part.Split(':');
                if (fields.Length < 2) continue;

                string id = fields[0].Trim();
                int index = rules.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;

                var rule = rules[index];
                bool enabled = fields[1].Trim() == "1";

                double threshold = rule.Threshold;
                if (fields.Length > 2 &&
                    double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    threshold = t;

                int sustain = rule.SustainSeconds;
                if (fields.Length > 3 &&
                    int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s >= 0)
                    sustain = s;

                rules[index] = rule with { Enabled = enabled, Threshold = threshold, SustainSeconds = sustain };
            }
            return rules;
        }
    }

    /// <summary>
    /// Watches the live readings against the user's rules and says something when one is
    /// breached.
    ///
    /// <para>
    /// The point of this whole class: MicaStats has always sampled temperature, disk space,
    /// memory and GPU load continuously, and has never once told anyone something was wrong.
    /// Monitoring you have to be looking at is monitoring you miss — a drive fills overnight,
    /// a cooler clogs and the CPU throttles for weeks, and all of it is visible only in a
    /// panel nobody had open at the time.
    /// </para>
    /// </summary>
    public sealed class AlertMonitor : IDisposable
    {
        private readonly AlertEvaluator _evaluator = new();
        private readonly MetricsHistory _history;
        private readonly BatteryMonitor? _battery;
        private readonly Action _onUpdated;

        private IReadOnlyList<AlertRule> _rules = AlertRule.Defaults;
        private bool _running;
        private bool _disposed;

        public AlertMonitor(MetricsHistory history, BatteryMonitor? battery)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _battery = battery;
            _onUpdated = OnMetrics;
        }

        /// <summary>Raised on the UI thread when a rule fires.</summary>
        public event Action<AlertEvent>? Raised;

        /// <summary>The rules currently in force.</summary>
        public IReadOnlyList<AlertRule> Rules => _rules;

        /// <summary>Whether a given rule is currently breached.</summary>
        public bool IsFiring(string ruleId) => _evaluator.IsFiring(ruleId);

        public void SetRules(IReadOnlyList<AlertRule> rules)
        {
            _rules = rules ?? AlertRule.Defaults;
            _evaluator.Reset();
        }

        public void Start()
        {
            if (_disposed || _running) return;
            _running = true;
            // MetricsHistory already marshals to the UI thread, so everything downstream of
            // this subscription - including the notification - is single-threaded.
            _history.Updated += _onUpdated;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _history.Updated -= _onUpdated;
            _evaluator.Reset();
        }

        private void OnMetrics()
        {
            if (!_running || _disposed) return;

            try
            {
                var metrics = _history.Latest;
                if (metrics == null) return;

                var now = DateTime.Now;
                foreach (var rule in _rules)
                {
                    if (!rule.Enabled) continue;

                    (double value, string detail) = Read(rule.Metric, metrics);
                    if (_evaluator.Feed(rule, value, now) != AlertTransition.Raised) continue;

                    var alert = new AlertEvent(rule, value, detail, now);
                    DiagnosticsLog.Warn("alert", rule.Describe() + " — " + alert.Message);
                    Raised?.Invoke(alert);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("alert", "Rule evaluation failed", ex);
            }
        }

        /// <summary>
        /// Resolves one metric to a number plus, where it helps, what it refers to.
        /// Returns NaN for anything unreadable so the evaluator holds its fire.
        /// </summary>
        private (double Value, string Detail) Read(AlertMetric metric, SystemMetrics m)
        {
            switch (metric)
            {
                case AlertMetric.CpuTemperature:
                    // -1 is the sampler's "no source could supply one". Reporting it as a
                    // number would make a missing probe look like a cold CPU.
                    return (m.CpuTemperature > 0 ? m.CpuTemperature : double.NaN, "");

                case AlertMetric.CpuUsage:
                    return (m.CpuUsage, "");

                case AlertMetric.MemoryUsed:
                    return (m.RamPercent, "");

                case AlertMetric.GpuUsage:
                    return (m.GpuUsage, "");

                case AlertMetric.DiskSpaceFree:
                    return LeastFreeDisk(m);

                case AlertMetric.BatteryHealth:
                {
                    var health = _battery?.Health;
                    if (health == null || !health.Any || health.HealthPercent < 0) return (double.NaN, "");
                    return (health.HealthPercent, "");
                }

                case AlertMetric.BatteryCharge:
                {
                    if (_battery == null) return (double.NaN, "");
                    var reading = BatteryMonitor.Read();
                    if (!reading.Present || reading.OnAcPower) return (double.NaN, "");
                    return (reading.Percent, "");
                }

                default:
                    return (double.NaN, "");
            }
        }

        /// <summary>
        /// The emptiest drive, in gigabytes free. A rule about disk space means "any of my
        /// drives", so the worst one is what the threshold is measured against.
        /// </summary>
        private static (double Value, string Detail) LeastFreeDisk(SystemMetrics m)
        {
            if (m.Disks == null || m.Disks.Count == 0) return (double.NaN, "");

            double worst = double.NaN;
            string which = "";
            foreach (var d in m.Disks)
            {
                // A drive that was not ready reports zero total; it has not run out of space,
                // it simply could not be read.
                if (d.TotalBytes == 0) continue;

                double freeGb = d.FreeBytes / 1024d / 1024d / 1024d;
                if (double.IsNaN(worst) || freeGb < worst)
                {
                    worst = freeGb;
                    which = d.Name;
                }
            }
            return (worst, which);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
