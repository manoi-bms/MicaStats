using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Sensors.Publishers
{
    /// <summary>
    /// CPU die temperature as published by MSI Afterburner's shared memory.
    ///
    /// <para>
    /// Afterburner is free and extremely common on machines with a discrete GPU, which makes
    /// it worth supporting even though it is a graphics tool: its hardware monitor collects
    /// CPU sensors too, and publishes them under source names like "CPU1 temperature".
    /// </para>
    /// </summary>
    public sealed class AfterburnerSource : ISensorSource
    {
        private const string MapName = "MAHMSharedMemory";
        private const uint Signature = 0x4D48414D;

        private const int HeaderSize = 32;
        private const int OffSignature = 0;
        private const int OffHeaderSize = 8;
        private const int OffNumEntries = 12;
        private const int OffEntrySize = 16;

        // Within one entry.
        private const int EntrySrcName = 0;
        private const int EntrySrcNameLength = 260;
        private const int EntryData = 552;

        private DateTime _retryAfter = DateTime.MinValue;

        public string Name => "MSI Afterburner";

        public bool IsAvailable { get; private set; } = true;

        /// <summary>Hottest CPU temperature in the block, or -1 if it holds none.</summary>
        public static double DecodeCpuTemperature(byte[] block)
        {
            if (block == null || block.Length < HeaderSize) return -1;
            if (BitConverter.ToUInt32(block, OffSignature) != Signature) return -1;

            uint start = BitConverter.ToUInt32(block, OffHeaderSize);
            uint count = BitConverter.ToUInt32(block, OffNumEntries);
            uint stride = BitConverter.ToUInt32(block, OffEntrySize);
            if (stride < EntryData + 4 || count == 0 || count > 8192) return -1;

            double hottest = -1;
            for (uint i = 0; i < count; i++)
            {
                long at = start + (long)i * stride;
                if (at + stride > block.Length) break;

                // Afterburner has no reading-type field, so the source name is the only
                // discriminator: "CPU1 temperature" counts, "CPU usage" must not.
                string name = ReadAscii(block, (int)(at + EntrySrcName), EntrySrcNameLength);
                if (name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf("temp", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                float value = BitConverter.ToSingle(block, (int)(at + EntryData));
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

                using (var head = mmf.CreateViewAccessor(0, HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = new byte[HeaderSize];
                    head.ReadArray(0, header, 0, HeaderSize);

                    if (BitConverter.ToUInt32(header, OffSignature) != Signature) return Unavailable();

                    long start = BitConverter.ToUInt32(header, OffHeaderSize);
                    long count = BitConverter.ToUInt32(header, OffNumEntries);
                    long stride = BitConverter.ToUInt32(header, OffEntrySize);
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
