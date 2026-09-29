using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Gives one window its own taskbar identity. MicaPad runs inside the MicaStats process, so
    /// without this its button would stack under MicaStats and could not be pinned on its own.
    /// The relaunch properties make a pinned MicaPad button start <c>MicaStats.exe --pad</c>.
    /// Must be applied before the window is first shown (in SourceInitialized).
    /// </summary>
    internal static class TaskbarIdentity
    {
        private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const uint PidRelaunchCommand = 2;
        private const uint PidRelaunchIconResource = 3;
        private const uint PidRelaunchDisplayName = 4;
        private const uint PidAppId = 5;
        private const ushort VT_LPWSTR = 31;

        /// <summary>Sets the window's AppUserModelID and relaunch command. Failure is harmless and silent.</summary>
        public static void Apply(IntPtr hwnd, string appId, string relaunchCommand, string displayName, string iconResource)
        {
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore store) != 0) return;
            try
            {
                Set(store, PidAppId, appId);
                Set(store, PidRelaunchCommand, relaunchCommand);
                Set(store, PidRelaunchDisplayName, displayName);
                Set(store, PidRelaunchIconResource, iconResource);
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        private static void Set(IPropertyStore store, uint propertyId, string value)
        {
            var key = new PropertyKey { FormatId = AppUserModelFormat, PropertyId = propertyId };
            var variant = new PropVariant { VarType = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(value) };
            try
            {
                store.SetValue(ref key, ref variant);
            }
            finally
            {
                PropVariantClear(ref variant);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        /// <summary>A PROPVARIANT holding a string: 24 bytes on x64, so the native side never reads past it.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort VarType;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant variant);
    }
}
