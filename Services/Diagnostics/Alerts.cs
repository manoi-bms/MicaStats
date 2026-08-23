using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>Which reading a rule watches. Every one of these is already sampled.</summary>
    public enum AlertMetric
    {
        CpuTemperature,
        CpuUsage,
        MemoryUsed,
        DiskSpaceFree,
        GpuUsage,
        BatteryHealth,
        BatteryCharge
    }

    /// <summary>What just happened to a rule.</summary>
    public enum AlertTransition
    {
        None,
        /// <summary>The condition has held long enough; tell the user once.</summary>
        Raised,
        /// <summary>The reading recovered, so the rule is armed again.</summary>
        Cleared
    }

    /// <summary>
    /// One threshold the user cares about.
    ///
    /// <para>
    /// <see cref="Above"/> distinguishes "too hot" from "too little left": a temperature rule
    /// fires when the reading climbs past the threshold, a free-space rule when it falls below.
    /// </para>
    /// </summary>
    public sealed record AlertRule(
        string Id,
        string Label,
        AlertMetric Metric,
        double Threshold,
        bool Above,
        int SustainSeconds,
        bool Enabled)
    {
        /// <summary>
        /// How far the reading must recover before the rule re-arms, in the metric's own units.
        /// Without this a value hovering on the threshold would alternate raise and clear on
        /// every tick.
        /// </summary>
        public double ClearMargin { get; init; } = 3d;

        /// <summary>The unit shown after the number.</summary>
        public string Unit => Metric switch
        {
            AlertMetric.CpuTemperature => "°C",
            AlertMetric.DiskSpaceFree => " GB",
            _ => "%",
        };

        public string ThresholdText =>
            Threshold.ToString("0.#", CultureInfo.InvariantCulture) + Unit;

        /// <summary>A sentence the user can check at a glance.</summary>
        public string Describe() =>
            Label + " " + (Above ? "above " : "below ") + ThresholdText +
            " for " + SustainSeconds.ToString(CultureInfo.InvariantCulture) + " s";

        /// <summary>
        /// The rules a fresh install starts with. Deliberately few and conservative: an alert
        /// system that cries wolf gets switched off, and then it protects nothing.
        /// </summary>
        public static IReadOnlyList<AlertRule> Defaults { get; } = new[]
        {
            new AlertRule("cpu-temp", "CPU temperature", AlertMetric.CpuTemperature,
                          95d, Above: true, SustainSeconds: 30, Enabled: true) { ClearMargin = 5d },

            new AlertRule("disk-free", "Free space on a drive", AlertMetric.DiskSpaceFree,
                          10d, Above: false, SustainSeconds: 60, Enabled: true) { ClearMargin = 2d },

            new AlertRule("memory", "Memory in use", AlertMetric.MemoryUsed,
                          92d, Above: true, SustainSeconds: 120, Enabled: false) { ClearMargin = 5d },

            new AlertRule("battery-health", "Battery health", AlertMetric.BatteryHealth,
                          80d, Above: false, SustainSeconds: 1, Enabled: true) { ClearMargin = 1d },
        };
    }

    /// <summary>An alert that fired, ready to be shown and logged.</summary>
    public sealed record AlertEvent(AlertRule Rule, double Value, string Detail, DateTime At)
    {
        /// <summary>The headline shown on the notification.</summary>
        public string Title => Rule.Label;

        /// <summary>The explanatory line: what the reading is, and on what.</summary>
        public string Message
        {
            get
            {
                string reading = Value.ToString("0.#", CultureInfo.InvariantCulture) + Rule.Unit;
                string body = Rule.Above
                    ? reading + ", past the " + Rule.ThresholdText + " you set"
                    : reading + " left, under the " + Rule.ThresholdText + " you set";
                return string.IsNullOrWhiteSpace(Detail) ? body : Detail + ": " + body;
            }
        }
    }

    /// <summary>
    /// Decides when a rule fires and when it re-arms.
    ///
    /// <para>
    /// Pure and clock-free — the caller supplies the time — so a test can push a day of
    /// readings through it and assert the exact tick it fires on. It holds one small piece of
    /// state per rule and nothing else.
    /// </para>
    /// </summary>
    public sealed class AlertEvaluator
    {
        private sealed class RuleState
        {
            public DateTime? Since;
            public bool Firing;
        }

        private readonly Dictionary<string, RuleState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Feeds one reading for one rule.
        /// </summary>
        /// <param name="value">
        /// The reading, or <see cref="double.NaN"/> when the sensor could not be read. An
        /// unreadable sensor never fires and never clears — treating it as zero would make a
        /// missing temperature probe look like a cold CPU, or a missing free-space figure look
        /// like a full disk.
        /// </param>
        public AlertTransition Feed(AlertRule rule, double value, DateTime now)
        {
            if (rule == null || !rule.Enabled) return AlertTransition.None;
            if (double.IsNaN(value) || double.IsInfinity(value)) return AlertTransition.None;

            if (!_states.TryGetValue(rule.Id, out var state))
            {
                state = new RuleState();
                _states[rule.Id] = state;
            }

            bool breached = rule.Above ? value >= rule.Threshold : value <= rule.Threshold;

            if (state.Firing)
            {
                // Re-arm only once the reading has recovered past the margin, not the instant
                // it touches the threshold again from the other side.
                double clearAt = rule.Above
                    ? rule.Threshold - rule.ClearMargin
                    : rule.Threshold + rule.ClearMargin;
                bool recovered = rule.Above ? value < clearAt : value > clearAt;

                if (!recovered) return AlertTransition.None;

                state.Firing = false;
                state.Since = null;
                return AlertTransition.Cleared;
            }

            if (!breached) { state.Since = null; return AlertTransition.None; }

            state.Since ??= now;
            if (now - state.Since.Value < TimeSpan.FromSeconds(Math.Max(0, rule.SustainSeconds)))
                return AlertTransition.None;

            state.Firing = true;
            return AlertTransition.Raised;
        }

        /// <summary>Whether a rule is currently in the fired state.</summary>
        public bool IsFiring(string ruleId) =>
            _states.TryGetValue(ruleId, out var state) && state.Firing;

        /// <summary>Drops all state, e.g. after the rules were edited.</summary>
        public void Reset() => _states.Clear();
    }
}
