using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Kil0bitSystemMonitor.Services.Sensors.AdapterPerfData;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// Every GPU's temperature, fan, power draw and throttle state, read straight from the
    /// display kernel.
    ///
    /// <para>
    /// This replaces the nvidia-smi subprocess the app used to keep alive for the same
    /// reading, and gives AMD-only machines a GPU temperature they never had — the previous
    /// D3DKMT attempt failed on every call, so those machines silently had no source at all.
    /// </para>
    ///
    /// <para>
    /// Note that a GPU temperature is never a CPU temperature, even on an APU. The integrated
    /// Radeon 890M shares a die with the CPU cores and still moved only 2°C while 24 threads
    /// saturated the processor: the graphics block has its own diode and is thermally
    /// isolated enough that CPU load barely registers. None of these readings sets
    /// <see cref="SensorReading.IsCpuDie"/>.
    /// </para>
    /// </summary>
    public sealed class AdapterPerfSource : ISensorSource
    {
        private readonly List<(LUID Luid, string Name)> _adapters = new();
        private bool _enumerated;

        public string Name => "Display kernel";

        public bool IsAvailable { get; private set; } = true;

        /// <summary>
        /// Trims vendor boilerplate so a row fits the card. The registry names are written for
        /// Device Manager, not for a 372px-wide panel: "AMD Radeon(TM) 890M Graphics" becomes
        /// "AMD Radeon 890M", and "NVIDIA RTX PRO 1000 Blackwell Generation Laptop GPU"
        /// becomes "NVIDIA RTX PRO 1000 Blackwell".
        /// </summary>
        public static string ShortAdapterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            string s = name;
            foreach (string noise in new[] { "(TM)", "(tm)", "(R)", "(r)", " Generation", " Laptop GPU", " Graphics" })
                s = s.Replace(noise, "", StringComparison.OrdinalIgnoreCase);

            // Removing an infix leaves a double space behind.
            while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
            s = s.Trim();

            return s.Length > 0 ? s : name.Trim();
        }

        public IReadOnlyList<SensorReading> Read()
        {
            var readings = new List<SensorReading>();
            try
            {
                if (!_enumerated) { Enumerate(); _enumerated = true; }

                foreach (var (luid, full) in _adapters)
                {
                    string name = ShortAdapterName(full);
                    if (!TryReadPerf(luid, out var pd)) continue;

                    string id = "gpu." + name;
                    double celsius = pd.Temperature / 10.0;

                    // A driver that fills only some fields must not yield a confident 0°C.
                    if (celsius > 0 && celsius < 150)
                        readings.Add(new SensorReading(id + ".temp", name,
                            SensorCategory.Temperature, celsius, "°C", Name));

                    if (pd.FanRPM > 0)
                        readings.Add(new SensorReading(id + ".fan", name + " fan",
                            SensorCategory.Fan, pd.FanRPM, "RPM", Name));

                    if (pd.Power > 0)
                        readings.Add(new SensorReading(id + ".power", name + " power",
                            SensorCategory.Power, pd.Power / 10.0, "%", Name));

                    if (pd.TemperatureLimitThrottle != 0)
                        readings.Add(new SensorReading(id + ".tthrottle", name + " thermal limit",
                            SensorCategory.Throttle, 1, "", Name));

                    if (pd.PowerLimitThrottle != 0)
                        readings.Add(new SensorReading(id + ".pthrottle", name + " power limit",
                            SensorCategory.Throttle, 1, "", Name));
                }

                IsAvailable = readings.Count > 0;
            }
            catch
            {
                // gdi32 absent, or the display stack is mid-reset after a driver update.
                IsAvailable = false;
            }
            return readings;
        }

        private void Enumerate()
        {
            // Called with a null buffer first purely to learn the count.
            var e = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;

            int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * size);
                    string name = ReadAdapterName(info.hAdapter);

                    // Render-only adapters answer the enumeration but have no name and no
                    // perf data; skipping them here keeps the per-tick loop quiet.
                    if (!string.IsNullOrWhiteSpace(name)) _adapters.Add((info.AdapterLuid, name));
                }
            }
            finally { Marshal.FreeHGlobal(e.pAdapters); }
        }

        private static string ReadAdapterName(uint hAdapter)
        {
            int size = Marshal.SizeOf<D3DKMT_ADAPTERREGISTRYINFO>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = hAdapter,
                    Type = QueryTypeRegistryInfo,
                    pPrivateDriverData = buf,
                    PrivateDriverDataSize = (uint)size,
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return "";
                return Marshal.PtrToStructure<D3DKMT_ADAPTERREGISTRYINFO>(buf).AdapterString ?? "";
            }
            catch { return ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static bool TryReadPerf(LUID luid, out D3DKMT_ADAPTER_PERFDATA pd)
        {
            pd = default;
            var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return false;

            int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                // Zero the block, then set the input field. Both matter: a partially filled
                // reply must not expose heap noise, and PhysicalAdapterIndex selects which
                // physical adapter answers.
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 }, buf, false);

                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter,
                    Type = QueryTypePerfData,
                    pPrivateDriverData = buf,
                    PrivateDriverDataSize = (uint)size,
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
                pd = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buf);
                return true;
            }
            catch { return false; }
            finally
            {
                Marshal.FreeHGlobal(buf);
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
    }
}
