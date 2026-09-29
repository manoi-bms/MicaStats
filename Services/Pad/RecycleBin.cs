using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Where deleted notes go. An interface so tests never touch the real Recycle Bin.</summary>
    public interface IRecycleBin
    {
        /// <summary>
        /// Moves a file or folder to the Recycle Bin. False when it could not — and then nothing
        /// was deleted.
        /// </summary>
        bool TryRecycle(string path);
    }

    /// <summary>
    /// The Windows Recycle Bin, through <c>SHFileOperation</c>.
    ///
    /// <para>
    /// The spec's promise is that no note is ever deleted without a way back. On a drive with no
    /// Recycle Bin (a network share, a redirected AppData) the shell would silently delete
    /// permanently instead, so that case is detected first with <c>SHQueryRecycleBin</c> and
    /// refused.
    /// </para>
    ///
    /// <para>
    /// Limitations: a drive whose Recycle Bin is switched off in its properties, or a folder
    /// larger than the bin's maximum size, is still deleted permanently by the shell, because
    /// neither setting is visible to <c>SHQueryRecycleBin</c>. MicaPad notes are small, and
    /// switching the bin off is the user's own choice for every program.
    /// </para>
    /// </summary>
    public sealed class RecycleBin : IRecycleBin
    {
        /// <summary>The one instance.</summary>
        public static readonly RecycleBin Instance = new();

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        private RecycleBin()
        {
        }

        /// <inheritdoc />
        public bool TryRecycle(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;

            var query = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(root, ref query) != 0) return false;

            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // The list is double-NUL terminated: one here, one added by the marshaller.
                pFrom = full + "\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };

            int result = SHFileOperation(ref operation);
            return result == 0 && operation.fAnyOperationsAborted == 0 && !Directory.Exists(full) && !File.Exists(full);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
    }
}
