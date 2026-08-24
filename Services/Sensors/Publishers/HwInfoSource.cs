using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Sensors.Publishers
{
    /// <summary>
    /// CPU die temperature as published by HWiNFO's shared memory.
    ///
    /// <para>
    /// HWiNFO requires "Shared Memory Support" to be switched on, and current free builds
    /// time-limit it to a fixed period per session. An expired session looks exactly like an
    /// absent one, and both are normal: the source reports unavailable and the panel shows a
    /// dash. Neither is a defect to work around, and the UI should say so rather than imply
    /// something is broken.
    /// </para>
    /// </summary>
    public sealed class HwInfoSource : ISensorSource
    {
        private const string MapName = @"Global\HWiNFO_SENS_SM2";
        private const uint Signature = 0x53695748;

        private const int HeaderSize = 48;
        private const int OffSignature = 0;
        private const int OffReadingSection = 36;
        private const int OffReadingStride = 40;
        private const int OffReadingCount = 44;

        // Within one reading element.
        private const int ElemType = 0;
        private const int ElemLabelOrig = 12;
        private const int ElemLabelLength = 128;
        private const int ElemValue = 288;

        private const uint ReadingTypeTemperature = 1;

        private DateTime _retryAfter = DateTime.MinValue;

        public string Name => "HWiNFO";

        public bool IsAvailable { get; private set; } = true;

        /// <summary>Hottest CPU temperature in the block, or -1 if it holds none.</summary>
        public static double DecodeCpuTemperature(byte[] block)
        {
            if (block == null || block.Length < HeaderSize) return -1;
            if (BitConverter.ToUInt32(block, OffSignature) != Signature) return -1;

            // Trust the header's own stride rather than a hard-coded element size: that is
            // what HWiNFO documents, and it survives a revision that grows the element.
            uint start = BitConverter.ToUInt32(block, OffReadingSection);
            uint stride = BitConverter.ToUInt32(block, OffReadingStride);
            uint count = BitConverter.ToUInt32(block, OffReadingCount);
            if (stride < ElemValue + 8 || count == 0 || count > 8192) return -1;

            double hottest = -1;
            for (uint i = 0; i < count; i++)
            {
                long at = start + (long)i * stride;
                if (at + stride > block.Length) break;

                if (BitConverter.ToUInt32(block, (int)(at + ElemType)) != ReadingTypeTemperature) continue;

                string label = ReadAscii(block, (int)(at + ElemLabelOrig), ElemLabelLength);
                if (label.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (label.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                double value = BitConverter.ToDouble(block, (int)(at + ElemValue));
                if (value > 0 && value < 150 && value > hottest) hottest = value;
            }
            return hottest;
        }

        public IReadOnlyList<SensorReading> Read()
        {
            if (DateTime.UtcNow < _retryAfter) return Array.Empty<SensorReading>();

            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);

                // Read the header first: only it knows how much of the block is meaningful,
                // and mapping the whole region blindly would be both wasteful and fragile.
                using (var head = mmf.CreateViewAccessor(0, HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = new byte[HeaderSize];
                    head.ReadArray(0, header, 0, HeaderSize);

                    if (BitConverter.ToUInt32(header, OffSignature) != Signature)
                        return Unavailable();

                    long start = BitConverter.ToUInt32(header, OffReadingSection);
                    long stride = BitConverter.ToUInt32(header, OffReadingStride);
                    long count = BitConverter.ToUInt32(header, OffReadingCount);
                    long total = start + stride * count;
                    if (total <= 0 || total > 32 * 1024 * 1024) return Unavailable();

                    using var view = mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.Read);
                    var block = new byte[total];
                    view.ReadArray(0, block, 0, (int)total);

                    double hottest = DecodeCpuTemperature(block);
                    if (hottest <= 0) return Unavailable();

                    IsAvailable = true;
                    return new[]
                    {
                        new SensorReading("cpu.die", "CPU die", SensorCategory.Temperature,
                                          hottest, "°C", Name, IsCpuDie: true),
                    };
                }
            }
            catch
            {
                // Not running, shared memory disabled, or the free build's session expired.
                _retryAfter = DateTime.UtcNow.AddSeconds(60);
                return Unavailable();
            }
        }

        private IReadOnlyList<SensorReading> Unavailable()
        {
            IsAvailable = false;
            return Array.Empty<SensorReading>();
        }

        private static string ReadAscii(byte[] block, int at, int max)
        {
            int end = at;
            while (end < at + max && end < block.Length && block[end] != 0) end++;
            return Encoding.ASCII.GetString(block, at, end - at);
        }
    }
}
