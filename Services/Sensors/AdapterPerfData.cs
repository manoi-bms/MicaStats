using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.Sensors
{
    /// <summary>
    /// D3DKMT adapter performance data: GPU temperature, fan and power for any vendor,
    /// without a driver and without elevation.
    ///
    /// <para>
    /// The query type and the struct layout here were established by measurement, not taken
    /// from a header. Sweeping query types 0–89 against buffer sizes 8–160 on three adapters
    /// (an integrated Radeon 890M, a discrete RTX PRO 1000, and a render-only adapter) found
    /// type 62 answering with a 64-byte block whose offset 56 holds a plausible deci-Celsius
    /// on both real GPUs — 49.0°C and 50.8°C, against an ACPI zone reading 71°C at the same
    /// moment.
    /// </para>
    ///
    /// <para>
    /// The published layout in d3dkmthk.h does not match what this driver stack accepts, and
    /// the kernel validates <c>PrivateDriverDataSize</c>, so a struct that merely looks right
    /// fails with STATUS_INVALID_PARAMETER exactly like a wrong query type. That is why the
    /// previous implementation — type 35 with a ~96-byte struct — never returned a single
    /// reading on any machine, and fell through to an nvidia-smi subprocess instead.
    /// </para>
    /// </summary>
    public static class AdapterPerfData
    {
        public const int QueryTypeRegistryInfo = 8;
        public const int QueryTypePerfData = 62;
        public const int QueryTypePerfDataCaps = 63;

        [DllImport("gdi32.dll")] public static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPresentMoveRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    /// <summary>
    /// Explicit offsets, because the size is what the kernel validates and the documented
    /// layout does not match it. <see cref="PhysicalAdapterIndex"/> is an INPUT field naming
    /// which physical adapter of a linked set to report; leaving it unset is one of the two
    /// ways the old code failed.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct D3DKMT_ADAPTER_PERFDATA
    {
        [FieldOffset(0)] public uint PhysicalAdapterIndex;
        [FieldOffset(8)] public ulong MemoryFrequency;
        [FieldOffset(16)] public ulong MaxMemoryFrequency;
        [FieldOffset(24)] public ulong MaxMemoryFrequencyOC;
        [FieldOffset(32)] public ulong MemoryBandwidthUtilized;
        [FieldOffset(48)] public uint FanRPM;
        [FieldOffset(52)] public uint Power;                 // tenths of a percent of max
        [FieldOffset(56)] public uint Temperature;           // deci-Celsius
        [FieldOffset(60)] public byte PowerLimitThrottle;
        [FieldOffset(61)] public byte TemperatureLimitThrottle;
    }

    /// <summary>
    /// 40 bytes. Offset 28 measured as a plausible ceiling — 89.0°C on the discrete GPU. The
    /// integrated adapter does not implement this query at all, which is why callers must
    /// treat a failure here as ordinary.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct D3DKMT_ADAPTER_PERFDATACAPS
    {
        [FieldOffset(0)] public uint PhysicalAdapterIndex;
        [FieldOffset(8)] public ulong MaxMemoryBandwidth;
        [FieldOffset(16)] public ulong MaxMemoryFrequency;
        [FieldOffset(24)] public uint MaxFanRPM;
        [FieldOffset(28)] public uint TemperatureMax;
        [FieldOffset(32)] public uint TemperatureWarning;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }
}
