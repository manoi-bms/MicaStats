using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

public sealed record GpuAdapterInfo(string Name, long Luid, bool IsSoftware);

/// <summary>
/// Picks the GPU for presence detection. DirectML's device id is the DXGI EnumAdapters1
/// index; Windows' high-performance preference names the discrete GPU on hybrid notebooks.
/// </summary>
internal static class GpuAdapters
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapterFlag = 2;
    private const int HighPerformancePreference = 2;
    // Vtable slots: IDXGIFactory1::EnumAdapters1, IDXGIFactory6::EnumAdapterByGpuPreference, IDXGIAdapter1::GetDesc1.
    private const int EnumAdapters1Slot = 12;
    private const int EnumAdapterByGpuPreferenceSlot = 29;
    private const int GetDesc1Slot = 10;
    private static readonly Guid FactoryOneId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid FactorySixId = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid AdapterOneId = new("29038f61-3839-4626-91fd-086879011a05");

    internal static int ChooseDevice(IReadOnlyList<GpuAdapterInfo> adapters, long? preferredLuid)
    {
        if (preferredLuid is { } luid)
        {
            for (int index = 0; index < adapters.Count; index++)
                if (adapters[index].Luid == luid && !adapters[index].IsSoftware) return index;
        }
        for (int index = 0; index < adapters.Count; index++)
            if (!adapters[index].IsSoftware) return index;
        return -1;
    }

    internal static IReadOnlyList<GpuAdapterInfo> EnumerateInDirectMLOrder()
    {
        var adapters = new List<GpuAdapterInfo>();
        var factoryId = FactoryOneId;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryId, out var factory));
        try
        {
            var enumerate = Slot<EnumAdapters1Fn>(factory, EnumAdapters1Slot);
            for (uint index = 0; ; index++)
            {
                int result = enumerate(factory, index, out var adapter);
                if (result == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(result);
                try { adapters.Add(Describe(adapter)); }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return adapters;
    }

    /// <summary>The LUID Windows prefers for high performance, or null before Windows 10 1803.</summary>
    internal static long? HighPerformanceLuid()
    {
        var factoryId = FactorySixId;
        if (CreateDXGIFactory1(ref factoryId, out var factory) < 0) return null;
        try
        {
            var adapterId = AdapterOneId;
            var byPreference = Slot<EnumAdapterByGpuPreferenceFn>(factory, EnumAdapterByGpuPreferenceSlot);
            if (byPreference(factory, 0, HighPerformancePreference, ref adapterId, out var adapter) < 0) return null;
            try { return Describe(adapter).Luid; }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(factory); }
    }

    private static GpuAdapterInfo Describe(IntPtr adapter)
    {
        Marshal.ThrowExceptionForHR(Slot<GetDesc1Fn>(adapter, GetDesc1Slot)(adapter, out var desc));
        long luid = ((long)desc.LuidHigh << 32) | desc.LuidLow;
        return new GpuAdapterInfo(desc.Description.Trim(), luid, (desc.Flags & SoftwareAdapterFlag) != 0);
    }

    private static T Slot<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapterByGpuPreferenceFn(IntPtr self, uint index, int preference, ref Guid riid, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
