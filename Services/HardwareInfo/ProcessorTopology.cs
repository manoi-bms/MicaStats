using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.HardwareInfo
{
    /// <summary>A distinct cache shape: e.g. "L2 Unified, 1.25 MB, ×6 instances".</summary>
    public sealed record CacheGroup(int Level, string Kind, long SizeBytes, int Count);

    public sealed class ProcessorTopologyInfo
    {
        public int PhysicalCores { get; init; }
        public int LogicalProcessors { get; init; }
        public int Packages { get; init; }
        public bool SmtPresent { get; init; }

        /// <summary>
        /// Core counts keyed by Windows efficiency class. On hybrid parts the higher class is
        /// the performance core (Intel: class 1 = P, class 0 = E); a single class means a
        /// uniform design.
        /// </summary>
        public IReadOnlyDictionary<byte, int> CoresByEfficiencyClass { get; init; } =
            new Dictionary<byte, int>();

        public IReadOnlyList<CacheGroup> Caches { get; init; } = Array.Empty<CacheGroup>();

        public bool IsHybrid => CoresByEfficiencyClass.Count >= 2;

        public int PerformanceCores => IsHybrid
            ? CoresByEfficiencyClass[CoresByEfficiencyClass.Keys.Max()]
            : PhysicalCores;

        public int EfficiencyCores => IsHybrid ? PhysicalCores - PerformanceCores : 0;
    }

    /// <summary>
    /// Core/thread/cache topology from <c>GetLogicalProcessorInformationEx</c>, the
    /// vendor-neutral native source (it also carries the P/E efficiency class that CPUID
    /// only exposes per-core with affinity pinning). The buffer is variable-length records,
    /// parsed manually so the parser stays a pure, testable function over bytes.
    /// </summary>
    public static class ProcessorTopology
    {
        private const int RelationProcessorCore = 0;
        private const int RelationCache = 2;
        private const int RelationProcessorPackage = 3;
        private const int RelationAll = 0xFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType, byte[]? buffer, ref uint returnedLength);

        public static ProcessorTopologyInfo? TryReadNative()
        {
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(RelationAll, null, ref len);
                if (len == 0 || len > 16 * 1024 * 1024) return null;
                var buf = new byte[len];
                if (!GetLogicalProcessorInformationEx(RelationAll, buf, ref len)) return null;
                return Parse(buf, (int)len);
            }
            catch { return null; }
        }

        /// <summary>
        /// Each record: int Relationship, uint Size, then a relationship-specific payload at
        /// offset 8. PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20)
        /// GroupCount(2) then GROUP_AFFINITY[GroupCount] of 16 bytes each (ulong mask first).
        /// CACHE_RELATIONSHIP: Level(1) Associativity(1) LineSize(2) CacheSize(4) Type(4).
        /// </summary>
        public static ProcessorTopologyInfo Parse(byte[] b, int length)
        {
            int cores = 0, logical = 0, packages = 0;
            bool smt = false;
            var byClass = new Dictionary<byte, int>();
            var cacheShapes = new Dictionary<(int Level, string Kind, long Size), int>();

            int off = 0;
            while (off + 8 <= length)
            {
                int relation = BitConverter.ToInt32(b, off);
                int size = BitConverter.ToInt32(b, off + 4);
                if (size <= 0 || off + size > length) break;
                int body = off + 8;

                switch (relation)
                {
                    case RelationProcessorCore:
                    {
                        cores++;
                        byte flags = b[body];
                        byte efficiency = b[body + 1];
                        if ((flags & 0x1) != 0) smt = true;
                        byClass[efficiency] = byClass.TryGetValue(efficiency, out int n) ? n + 1 : 1;

                        int groupCount = BitConverter.ToUInt16(b, body + 22);
                        for (int g = 0; g < groupCount && body + 24 + g * 16 + 8 <= off + size; g++)
                        {
                            ulong mask = BitConverter.ToUInt64(b, body + 24 + g * 16);
                            logical += BitOperations.PopCount(mask);
                        }
                        break;
                    }

                    case RelationProcessorPackage:
                        packages++;
                        break;

                    case RelationCache:
                    {
                        int level = b[body];
                        long cacheSize = BitConverter.ToUInt32(b, body + 4);
                        int type = BitConverter.ToInt32(b, body + 8);
                        string kind = type switch
                        {
                            1 => "Instruction",
                            2 => "Data",
                            3 => "Trace",
                            _ => "Unified",
                        };
                        var key = (level, kind, cacheSize);
                        cacheShapes[key] = cacheShapes.TryGetValue(key, out int n) ? n + 1 : 1;
                        break;
                    }
                }

                off += size;
            }

            var caches = cacheShapes
                .Select(kv => new CacheGroup(kv.Key.Level, kv.Key.Kind, kv.Key.Size, kv.Value))
                .OrderBy(c => c.Level)
                .ThenBy(c => c.Kind)
                .ToList();

            return new ProcessorTopologyInfo
            {
                PhysicalCores = cores,
                LogicalProcessors = logical,
                Packages = packages,
                SmtPresent = smt,
                CoresByEfficiencyClass = byClass,
                Caches = caches,
            };
        }
    }
}
