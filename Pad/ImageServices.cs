using System;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What the image previews of an editor need from the window and the app (spec 6.3). Tests pass fakes.</summary>
    internal sealed class ImageServices
    {
        /// <summary>Loads images; the app shares one between windows.</summary>
        public required ImageSources Sources { get; init; }

        /// <summary>Draws SVG images (the diagram page); null: an SVG image cannot be read.</summary>
        public IDiagramRenderer? Renderer { get; init; }

        /// <summary>True while Settings → MicaPad → Draw diagrams is on (R6).</summary>
        public required Func<bool> Enabled { get; init; }

        /// <summary>True while Settings → MicaPad → Load images from the web is on.</summary>
        public required Func<bool> WebImages { get; init; }

        /// <summary>The shown tab's file folder, where relative paths start; null for a note.</summary>
        public required Func<string?> BaseFolder { get; init; }

        /// <summary>How long typing must pause before an image is loaded (the diagrams' 600 ms). Tests lengthen it.</summary>
        public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(600);

        /// <summary>Loaded images by source (64, memory only), shared by the window's tabs (R13).</summary>
        public DiagramCache Cache { get; init; } = new();

        /// <summary>Opens a link a failure offers (the WebView2 download page).</summary>
        public Action<Uri> OpenLink { get; init; } = _ => { };

        /// <summary>Logs a warning; it names an exception type, never an image's path or address.</summary>
        public Action<string> Warn { get; init; } = _ => { };
    }
}
