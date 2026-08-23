using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>One physical battery pack's wear figures, as reported by the firmware.</summary>
    public sealed record BatteryPack(
        string Name,
        string Chemistry,
        int DesignCapacityMwh,
        int FullChargeCapacityMwh,
        int CycleCount)
    {
        /// <summary>False when the firmware withholds capacity, which some packs do.</summary>
        public bool CapacityKnown => DesignCapacityMwh > 0 && FullChargeCapacityMwh > 0;

        /// <summary>
        /// Remaining capacity as a share of the pack's design capacity, or -1 when unknown.
        /// This is the number macOS shows and Windows does not.
        /// </summary>
        public double HealthPercent =>
            CapacityKnown ? FullChargeCapacityMwh * 100d / DesignCapacityMwh : -1d;

        /// <summary>Capacity lost to age, or -1 when unknown.</summary>
        public double WearPercent => CapacityKnown ? Math.Max(0d, 100d - HealthPercent) : -1d;
    }

    /// <summary>Wear figures for every pack in the machine, plus the aggregate.</summary>
    public sealed class BatteryHealth
    {
        public BatteryHealth(IReadOnlyList<BatteryPack> packs) =>
            Packs = packs ?? Array.Empty<BatteryPack>();

        public IReadOnlyList<BatteryPack> Packs { get; }

        public bool Any => Packs.Count > 0;

        public int DesignCapacityMwh
        {
            get { int t = 0; foreach (var p in Packs) t += Math.Max(0, p.DesignCapacityMwh); return t; }
        }

        public int FullChargeCapacityMwh
        {
            get { int t = 0; foreach (var p in Packs) t += Math.Max(0, p.FullChargeCapacityMwh); return t; }
        }

        /// <summary>Highest cycle count across packs, or -1 when no pack reports one.</summary>
        public int CycleCount
        {
            get
            {
                int best = -1;
                foreach (var p in Packs) if (p.CycleCount > best) best = p.CycleCount;
                return best;
            }
        }

        /// <summary>Aggregate health, or -1 when no pack reported usable capacity.</summary>
        public double HealthPercent =>
            DesignCapacityMwh > 0 ? FullChargeCapacityMwh * 100d / DesignCapacityMwh : -1d;

        public static BatteryHealth Empty { get; } = new BatteryHealth(Array.Empty<BatteryPack>());
    }

    /// <summary>
    /// Parses the XML that <c>powercfg /batteryreport /XML</c> produces.
    ///
    /// <para>
    /// This is the only route to design capacity on a typical machine. <c>Win32_Battery</c>
    /// exposes <c>DesignCapacity</c> and <c>FullChargeCapacity</c> properties but leaves both
    /// null on every laptop tested, and <c>root\WMI BatteryStaticData</c> returns an empty
    /// <c>DesignedCapacity</c> — so the health percentage cannot be computed from WMI alone.
    /// </para>
    ///
    /// <para>
    /// The report also contains the pack's serial number. It is deliberately not read: a
    /// serial identifies the specific machine, and this data is written to a log file and a
    /// shareable report.
    /// </para>
    ///
    /// <para>Pure over the XML text so the shape can be pinned by tests without a battery.</para>
    /// </summary>
    public static class BatteryReportParser
    {
        public static BatteryHealth Parse(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return BatteryHealth.Empty;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return BatteryHealth.Empty; }

            var packs = new List<BatteryPack>();

            // The report is namespaced, and the namespace URI has changed between Windows
            // releases. Match on local name so a namespace bump does not silently empty this.
            foreach (var element in doc.Descendants())
            {
                if (element.Name.LocalName != "Battery") continue;

                string name = Child(element, "Id");
                string chemistry = Child(element, "Chemistry");
                int design = ChildInt(element, "DesignCapacity");
                int full = ChildInt(element, "FullChargeCapacity");
                int cycles = ChildInt(element, "CycleCount");

                // A pack with no capacity at all is a placeholder entry, not a battery.
                if (design <= 0 && full <= 0) continue;

                packs.Add(new BatteryPack(name, chemistry, design, full, cycles));
            }

            return packs.Count == 0 ? BatteryHealth.Empty : new BatteryHealth(packs);
        }

        private static string Child(XElement parent, string localName)
        {
            foreach (var e in parent.Elements())
                if (e.Name.LocalName == localName) return (e.Value ?? "").Trim();
            return "";
        }

        private static int ChildInt(XElement parent, string localName)
        {
            string text = Child(parent, localName);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }
    }

    /// <summary>A live battery sample.</summary>
    public sealed record BatteryReading(
        bool Present,
        bool OnAcPower,
        bool Charging,
        bool Discharging,
        int Percent,
        int RemainingMwh,
        int RateMw,
        int VoltageMv)
    {
        public static BatteryReading None { get; } =
            new BatteryReading(false, true, false, false, 0, 0, 0, 0);

        /// <summary>Charge or discharge power in watts, or 0 when idle or unreported.</summary>
        public double Watts => RateMw / 1000d;
    }

    /// <summary>
    /// Time-remaining arithmetic, kept separate from any WMI access so it can be tested.
    ///
    /// <para>
    /// Windows has its own estimate in <c>Win32_Battery.EstimatedRunTime</c>, and on the
    /// development machine it returns 71,582,788 minutes — the documented "unknown" sentinel,
    /// roughly 136 years. That is why this computes its own from the measured discharge rate
    /// instead of forwarding what the OS says.
    /// </para>
    /// </summary>
    public static class BatteryEstimate
    {
        /// <summary>Above this many minutes the OS figure is a sentinel, not an estimate.</summary>
        public const int ImplausibleMinutes = 60 * 24 * 7;   // a week on battery is not real

        /// <summary>True when a <c>Win32_Battery.EstimatedRunTime</c> value is usable.</summary>
        public static bool IsPlausibleOsEstimate(long minutes) =>
            minutes > 0 && minutes < ImplausibleMinutes;

        /// <summary>
        /// How long the remaining charge lasts at the current draw, or null when that cannot
        /// be computed. Capacity is in mWh and the rate in mW, so the quotient is hours.
        /// </summary>
        public static TimeSpan? TimeToEmpty(int remainingMwh, int dischargeRateMw)
        {
            if (remainingMwh <= 0 || dischargeRateMw <= 0) return null;
            return Clamp(remainingMwh / (double)dischargeRateMw);
        }

        /// <summary>How long until full at the current charge rate, or null.</summary>
        public static TimeSpan? TimeToFull(int remainingMwh, int fullChargeMwh, int chargeRateMw)
        {
            if (chargeRateMw <= 0 || fullChargeMwh <= 0) return null;
            int deficit = fullChargeMwh - remainingMwh;
            if (deficit <= 0) return TimeSpan.Zero;
            return Clamp(deficit / (double)chargeRateMw);
        }

        private static TimeSpan? Clamp(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0) return null;
            // A rate sampled over one tick can be momentarily tiny; refuse to render a number
            // that would read as days rather than showing an obviously wrong estimate.
            if (hours > 48) return null;
            return TimeSpan.FromHours(hours);
        }

        /// <summary>"3 h 12 min", "48 min", or a dash when unknown.</summary>
        public static string Format(TimeSpan? span)
        {
            if (span == null) return "—";
            var t = span.Value;
            if (t.TotalMinutes < 1) return "under a minute";
            int hours = (int)t.TotalHours;
            int minutes = t.Minutes;
            return hours > 0
                ? hours.ToString(CultureInfo.InvariantCulture) + " h " +
                  minutes.ToString(CultureInfo.InvariantCulture) + " min"
                : minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        /// <summary>
        /// The plain-language verdict macOS shows and Windows does not. The 80% line is the
        /// common industry definition of a worn-out pack; below 65% replacement is overdue.
        /// </summary>
        public static string HealthVerdict(double healthPercent) => healthPercent switch
        {
            < 0 => "Unknown",
            >= 80 => "Normal",
            >= 65 => "Service recommended",
            _ => "Replace soon",
        };
    }
}
