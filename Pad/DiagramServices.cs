using System;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What the diagram pictures of an editor need from the window and the app. Tests pass fakes.</summary>
    internal sealed class DiagramServices
    {
        public required IDiagramRenderer Renderer { get; init; }

        /// <summary>True while Settings → MicaPad → Draw diagrams is on.</summary>
        public required Func<bool> Enabled { get; init; }

        /// <summary>The Kroki server while Draw other types with Kroki is on; null while it is off.</summary>
        public required Func<string?> KrokiServer { get; init; }

        /// <summary>How long typing must pause before a block is drawn again (spec 4: 600 ms). Tests lengthen it.</summary>
        public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(600);

        /// <summary>Opens an error's help link through the window's safe-link path.</summary>
        public Action<Uri> OpenLink { get; init; } = _ => { };

        /// <summary>Puts a PNG on the clipboard; false when the clipboard stays busy.</summary>
        public Func<byte[], bool> TrySetClipboardImage { get; init; } = _ => false;

        /// <summary>Asks where to save (default file name, filter); null when the user cancels.</summary>
        public Func<string, string, string?> AskSavePath { get; init; } = (_, _) => null;

        public Action<string> ShowStatus { get; init; } = _ => { };

        /// <summary>Logs a warning; it names an engine and an exception type, never a diagram's text.</summary>
        public Action<string> Warn { get; init; } = _ => { };
    }
}
