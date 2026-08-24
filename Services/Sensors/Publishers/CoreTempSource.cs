using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;

namespace Kil0bitSystemMonitor.Services.Sensors.Publishers
{
    /// <summary>
    /// CPU die temperature as published by Core Temp's shared memory block.
    ///
    /// <para>
    /// The die sensors — AMD Tctl, Intel DTS — are ring-0 territory. Every tool that shows
    /// them loads a kernel driver, and MicaStats deliberately does not. So this reads what
    /// Core Temp has already read, which costs nothing and requires no elevation, but does
    /// require Core Temp to be running.
    /// </para>
    ///
    /// <para>
    /// Decoding is separated from acquisition on purpose: a decoder that can only be
    /// exercised on a machine which happens to run the tool is a decoder that never gets
    /// tested. <see cref="DecodeHottest"/> is a pure function over the block.
    /// </para>
    /// </summary>
    public sealed class CoreTempSource : ISensorSource
    {
        private const string MapName = "CoreTempMappingObject";

        // CORE_TEMP_SHARED_DATA layout:
        // uint uiLoad[256]; uint uiTjMax[128]; uint uiCoreCnt; uint uiCpuCnt; float fTemp[256];
        // float fVID; float fCPUSpeed; float fFSBSpeed; float fMultiplier; char sCPUName[100];
        // byte ucFahrenheit; byte ucDeltaToTjMax;
        private const int OffTjMax = 256 * 4;                          // 1024
        private const int OffCoreCnt = OffTjMax + 128 * 4;             // 1536
        private const int OffCpuCnt = OffCoreCnt + 4;                  // 1540
        private const int OffTemp = OffCpuCnt + 4;                     // 1544
        private const int OffFlags = OffTemp + 256 * 4 + 4 * 4 + 100;  // 2684
        private const int MapLength = OffFlags + 2;                    // 2686

        private DateTime _retryAfter = DateTime.MinValue;

        public string Name => "Core Temp";

        public bool IsAvailable { get; private set; } = true;

        /// <summary>
        /// Hottest core in the block, or -1 when the block is not usable. Reports the hottest
        /// core rather than an average: an average hides the one core that is throttling.
        /// </summary>
        public static double DecodeHottest(byte[] block)
        {
            if (block == null || block.Length < MapLength) return -1;

            uint coreCnt = BitConverter.ToUInt32(block, OffCoreCnt);
            uint cpuCnt = BitConverter.ToUInt32(block, OffCpuCnt);
            if (coreCnt == 0 || coreCnt > 128 || cpuCnt == 0 || cpuCnt > 4) return -1;

            bool fahrenheit = block[OffFlags] != 0;
            bool deltaToTjMax = block[OffFlags + 1] != 0;

            int count = (int)Math.Min(coreCnt * cpuCnt, 256);
            float max = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                float value = BitConverter.ToSingle(block, OffTemp + i * 4);
                if (deltaToTjMax)
                {
                    // Stored as distance below TjMax for this value's own package.
                    uint tj = BitConverter.ToUInt32(block, OffTjMax + (int)(i / coreCnt) * 4);
                    value = tj - value;
                }
                if (value > max) max = value;
            }

            if (max <= float.MinValue) return -1;
            if (fahrenheit) max = (max - 32f) * 5f / 9f;
            return max > 0f && max < 150f ? max : -1;
        }

        public IReadOnlyList<SensorReading> Read()
        {
            if (DateTime.UtcNow < _retryAfter) return Array.Empty<SensorReading>();

            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, MapLength, MemoryMappedFileAccess.Read);

                var block = new byte[MapLength];
                view.ReadArray(0, block, 0, MapLength);

                double hottest = DecodeHottest(block);
                if (hottest <= 0) { IsAvailable = false; return Array.Empty<SensorReading>(); }

                IsAvailable = true;
                return new[]
                {
                    new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature,
                                      hottest, "°C", Name, IsCpuDie: true),
                };
            }
            catch
            {
                // Core Temp is not running. Normal, and not worth probing every tick.
                _retryAfter = DateTime.UtcNow.AddSeconds(30);
                IsAvailable = false;
                return Array.Empty<SensorReading>();
            }
        }
    }
}
