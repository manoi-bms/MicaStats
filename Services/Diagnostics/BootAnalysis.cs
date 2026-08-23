using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>What kind of thing held the boot up.</summary>
    public enum StartupDelayKind
    {
        Application,
        Driver,
        Service,
        Other
    }

    /// <summary>Where a startup entry is registered, which decides whether it can be switched off.</summary>
    public enum StartupScope
    {
        /// <summary>Per-user registry Run key or Startup folder — editable without elevation.</summary>
        CurrentUser,
        /// <summary>Machine-wide Run key, a service, or a scheduled task — needs administrator rights.</summary>
        Machine,
        Unknown
    }

    /// <summary>One measured boot, from event 100 of the boot performance log.</summary>
    public sealed record BootRecord(
        DateTime BootAt,
        int BootTimeMs,
        int MainPathMs,
        int PostBootMs,
        int StartupAppCount,
        bool IsDegradation)
    {
        /// <summary>Total boot as seconds, the unit people actually think in.</summary>
        public double Seconds => BootTimeMs / 1000d;

        public string SecondsText =>
            (BootTimeMs / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// One application, driver or service that Windows measured as slowing startup, from
    /// events 101 to 103.
    /// </summary>
    public sealed record StartupDelay(
        StartupDelayKind Kind,
        string Name,
        string FriendlyName,
        string Company,
        int TotalMs,
        int DegradationMs,
        DateTime At)
    {
        /// <summary>The clearest name available: the friendly one when Windows supplied it.</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(FriendlyName) ? Name : FriendlyName;

        public string TotalText =>
            (TotalMs / 1000d).ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>One program registered to launch at sign-in.</summary>
    public sealed record StartupEntry(
        string Name,
        string Command,
        string Location,
        StartupScope Scope,
        bool Enabled)
    {
        /// <summary>True when this app can be switched off without administrator rights.</summary>
        public bool CanToggle => Scope == StartupScope.CurrentUser;
    }

    /// <summary>Everything the Boot tab renders.</summary>
    public sealed class BootAnalysis
    {
        /// <summary>Most recent boot first.</summary>
        public IReadOnlyList<BootRecord> Boots { get; init; } = Array.Empty<BootRecord>();

        /// <summary>Slowest first, limited to the most recent boot.</summary>
        public IReadOnlyList<StartupDelay> Delays { get; init; } = Array.Empty<StartupDelay>();

        public IReadOnlyList<StartupEntry> Entries { get; init; } = Array.Empty<StartupEntry>();

        /// <summary>Set when the boot performance log could not be read at all.</summary>
        public string? Problem { get; init; }

        public BootRecord? Latest => Boots.Count > 0 ? Boots[0] : null;

        /// <summary>Mean boot time across the retained records, or 0 when there are none.</summary>
        public double AverageSeconds
        {
            get
            {
                if (Boots.Count == 0) return 0;
                double sum = 0;
                foreach (var b in Boots) sum += b.Seconds;
                return sum / Boots.Count;
            }
        }

        /// <summary>
        /// How much slower the latest boot was than the average of the ones before it, in
        /// seconds. Negative means it got faster. 0 when there is nothing to compare against.
        /// </summary>
        public double TrendSeconds
        {
            get
            {
                if (Boots.Count < 2) return 0;
                double sum = 0;
                for (int i = 1; i < Boots.Count; i++) sum += Boots[i].Seconds;
                return Boots[0].Seconds - sum / (Boots.Count - 1);
            }
        }
    }

    /// <summary>
    /// Turns boot performance event payloads into records.
    ///
    /// <para>
    /// Kept pure over a field dictionary — no <c>EventLogReader</c> in sight — because the
    /// events only exist after a real boot on a real machine, and the parsing is the part that
    /// can silently go wrong when Microsoft renames a field.
    /// </para>
    ///
    /// <para>
    /// Fields are read by NAME, never by position. The payload of event 101 carries pairs like
    /// <c>NameLength</c> beside <c>Name</c>, and the ordering has changed across Windows
    /// releases; a positional reader would report the length as the duration.
    /// </para>
    /// </summary>
    public static class BootEventParser
    {
        /// <summary>Event id of the "boot performance monitoring" summary.</summary>
        public const int BootEventId = 100;

        public const int AppDelayEventId = 101;
        public const int DriverDelayEventId = 102;
        public const int ServiceDelayEventId = 103;

        /// <summary>
        /// Pulls the <c>&lt;Data Name="x"&gt;value&lt;/Data&gt;</c> pairs out of a rendered
        /// event. Returns an empty dictionary rather than throwing on anything unexpected.
        /// </summary>
        public static Dictionary<string, string> ReadFields(string? eventXml)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(eventXml)) return fields;

            XDocument doc;
            try { doc = XDocument.Parse(eventXml); }
            catch { return fields; }

            foreach (var data in doc.Descendants())
            {
                if (data.Name.LocalName != "Data") continue;
                string? name = data.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                fields[name] = data.Value ?? "";
            }
            return fields;
        }

        /// <summary>Builds a boot record, or null when the payload carries no usable boot time.</summary>
        public static BootRecord? ParseBoot(IReadOnlyDictionary<string, string> fields, DateTime at)
        {
            if (fields == null) return null;

            int bootTime = Int(fields, "BootTime");
            if (bootTime <= 0) return null;

            return new BootRecord(
                BootAt: at,
                BootTimeMs: bootTime,
                MainPathMs: Int(fields, "MainPathBootTime"),
                PostBootMs: Int(fields, "BootPostBootTime"),
                StartupAppCount: Int(fields, "BootNumStartupApps"),
                IsDegradation: Int(fields, "BootIsDegradation") != 0);
        }

        /// <summary>Builds a delay record, or null when the payload has no measured duration.</summary>
        public static StartupDelay? ParseDelay(int eventId, IReadOnlyDictionary<string, string> fields, DateTime at)
        {
            if (fields == null) return null;

            int total = Int(fields, "TotalTime");
            if (total <= 0) return null;

            string name = Text(fields, "Name");
            string friendly = Text(fields, "FriendlyName");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(friendly)) return null;

            // ProductName is usually the better label; CompanyName is the fallback.
            string product = Text(fields, "ProductName");
            string company = product.Length > 0 ? product : Text(fields, "CompanyName");

            return new StartupDelay(
                Kind: KindOf(eventId),
                Name: name,
                FriendlyName: friendly,
                Company: company,
                TotalMs: total,
                DegradationMs: Int(fields, "DegradationTime"),
                At: at);
        }

        public static StartupDelayKind KindOf(int eventId) => eventId switch
        {
            AppDelayEventId => StartupDelayKind.Application,
            DriverDelayEventId => StartupDelayKind.Driver,
            ServiceDelayEventId => StartupDelayKind.Service,
            _ => StartupDelayKind.Other,
        };

        private static int Int(IReadOnlyDictionary<string, string> fields, string key) =>
            fields.TryGetValue(key, out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0
                ? v : 0;

        private static string Text(IReadOnlyDictionary<string, string> fields, string key) =>
            fields.TryGetValue(key, out var text) ? (text ?? "").Trim() : "";
    }
}
