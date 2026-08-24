using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Kil0bitSystemMonitor.Services.Sensors.Publishers
{
    /// <summary>
    /// CPU die temperature as published by AIDA64's shared memory.
    ///
    /// <para>
    /// AIDA64 requires shared memory to be enabled in its preferences; it is off by default,
    /// so an absent mapping is the ordinary case rather than a fault.
    /// </para>
    ///
    /// <para>
    /// The block is a fragment, not a document: repeated elements with no single root, so it
    /// has to be wrapped before parsing. Values are parsed invariantly — AIDA64 writes what
    /// its locale gives it, and a decimal comma would otherwise become a parse failure on a
    /// machine like this one.
    /// </para>
    /// </summary>
    public sealed class AidaSource : ISensorSource
    {
        private const string MapName = "AIDA64_SensorValues";
        private const int MaxBlock = 256 * 1024;

        private DateTime _retryAfter = DateTime.MinValue;

        public string Name => "AIDA64";

        public bool IsAvailable { get; private set; } = true;

        /// <summary>Hottest CPU temperature in the fragment, or -1 if it holds none.</summary>
        public static double DecodeCpuTemperature(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return -1;

            try
            {
                var root = XDocument.Parse("<root>" + xml + "</root>").Root;
                if (root == null) return -1;

                double hottest = -1;
                foreach (var temp in root.Elements("temp"))
                {
                    string label = (string?)temp.Element("label") ?? "";
                    if (label.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (label.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string raw = ((string?)temp.Element("value") ?? "").Trim();
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        continue;

                    if (value > 0 && value < 150 && value > hottest) hottest = value;
                }
                return hottest;
            }
            catch (XmlException)
            {
                // A torn read mid-update looks exactly like malformed input. Skip this tick.
                return -1;
            }
        }

        public IReadOnlyList<SensorReading> Read()
        {
            if (DateTime.UtcNow < _retryAfter) return Array.Empty<SensorReading>();

            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                long length = Math.Min(view.Capacity, MaxBlock);
                var raw = new byte[length];
                view.ReadArray(0, raw, 0, (int)length);

                // The mapping is fixed-size and NUL-padded; decode only the live prefix.
                int end = Array.IndexOf(raw, (byte)0);
                if (end < 0) end = raw.Length;

                double hottest = DecodeCpuTemperature(Encoding.ASCII.GetString(raw, 0, end));
                if (hottest <= 0) return Unavailable();

                IsAvailable = true;
                return new[]
                {
                    new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature,
                                      hottest, "°C", Name, IsCpuDie: true),
                };
            }
            catch
            {
                _retryAfter = DateTime.UtcNow.AddSeconds(60);
                return Unavailable();
            }
        }

        private IReadOnlyList<SensorReading> Unavailable()
        {
            IsAvailable = false;
            return Array.Empty<SensorReading>();
        }
    }
}
