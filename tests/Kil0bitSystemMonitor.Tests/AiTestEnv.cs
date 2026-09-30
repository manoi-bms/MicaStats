using System;
using System.Collections.Generic;
using System.IO;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A throwaway folder, a clock moved by hand and a warning collector for the AI and history
    /// tests. Nothing here reaches %APPDATA% or DiagnosticsLog.
    /// </summary>
    internal sealed class AiTestEnv : IDisposable
    {
        private readonly List<string> _warnings = new();

        public AiTestEnv()
        {
            Root = Path.Combine(Path.GetTempPath(), "micastats-ai-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Warn = message =>
            {
                lock (_warnings) _warnings.Add(message);
            };
        }

        /// <summary>The folder, created by the constructor and deleted by <see cref="Dispose"/>.</summary>
        public string Root { get; }

        /// <summary>A path inside the folder; nothing is created.</summary>
        public string PathOf(string name) => Path.Combine(Root, name);

        /// <summary>The clock handed to stores and recorders; starts at 2026-09-30 10:00:00 UTC.</summary>
        public FakeClock Clock { get; } = new() { UtcNow = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc) };

        /// <summary>A warn callback that only records, safe from any thread.</summary>
        public Action<string> Warn { get; }

        /// <summary>Every message passed to <see cref="Warn"/> so far.</summary>
        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
