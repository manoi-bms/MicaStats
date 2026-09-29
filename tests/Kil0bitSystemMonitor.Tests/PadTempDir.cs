using System;
using System.IO;

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
}
