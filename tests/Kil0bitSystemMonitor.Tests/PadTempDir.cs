using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A throwaway folder under %TEMP%, deleted on dispose. Keeps every MicaPad test away from %APPDATA%.</summary>
    internal sealed class PadTempDir : IDisposable
    {
        public PadTempDir()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "micapad-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>The folder.</summary>
        public string Root { get; }

        /// <summary>A path inside the folder.</summary>
        public string PathOf(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Denies this Windows account listing a folder until disposed, so enumerating its files fails
    /// with <see cref="UnauthorizedAccessException"/> while the folder still exists and takes new
    /// files. Dispose puts the folder's access back.
    /// </summary>
    internal sealed class DeniedListing : IDisposable
    {
        private readonly DirectoryInfo _folder;
        private readonly FileSystemAccessRule _rule;
        private bool _removed;

        public DeniedListing(string folder)
        {
            _folder = new DirectoryInfo(folder);
            _rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var security = _folder.GetAccessControl();
            security.AddAccessRule(_rule);
            _folder.SetAccessControl(security);
        }

        public void Dispose()
        {
            if (_removed) return;
            _removed = true;
            var security = _folder.GetAccessControl();
            security.RemoveAccessRule(_rule);
            _folder.SetAccessControl(security);
        }
    }
}
