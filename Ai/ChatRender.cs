using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The one place an answer's Copy buttons and menus reach the clipboard. Tests replace
    /// <see cref="SetText"/> and <see cref="SetImage"/> so the real clipboard is never touched.
    /// </summary>
    internal static class ChatClipboard
    {
        /// <summary>Puts text on the clipboard; failures are logged by type, never thrown. Tests replace it.</summary>
        internal static Action<string> SetText { get; set; } = text =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                // The type only: the message could quote the text.
                DiagnosticsLog.Warn("ai", "Copying an answer failed (" + ex.GetType().Name + ")");
            }
        };

        /// <summary>Puts a picture on the clipboard; failures are logged by type, never thrown. Tests replace it.</summary>
        internal static Action<BitmapSource> SetImage { get; set; } = image =>
        {
            try
            {
                System.Windows.Clipboard.SetImage(image);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("ai", "Copying a diagram failed (" + ex.GetType().Name + ")");
            }
        };
    }

    /// <summary>
    /// What <see cref="ChatDocument.Build"/> needs from outside the document. The diagram members
    /// belong to one view: each answer view builds with a render of its own, so two views never
    /// share a redraw or a Source choice.
    /// </summary>
    internal sealed class ChatRender
    {
        /// <summary>The render with the real clipboard and no diagrams.</summary>
        public static ChatRender Default { get; } = new();

        /// <summary>Copies text; the default goes to <see cref="ChatClipboard.SetText"/> as it is at the time of the click.</summary>
        public Action<string> Copy { get; init; } = text => ChatClipboard.SetText(text);

        /// <summary>Where the pictures of Mermaid blocks come from; null (the default) leaves every such block as code.</summary>
        public IChatDiagrams? Diagrams { get; init; }

        /// <summary>The theme of the view the document is built for: a diagram is a bitmap drawn for one theme.</summary>
        public bool Dark { get; init; } = true;

        /// <summary>
        /// Builds the view's document again: called when a picture the document waits for has
        /// arrived or failed. A picture never reaches a document already shown; it comes with the
        /// next build. The view hands in the same delegate at every build, so it is told once
        /// however often it asked.
        /// </summary>
        public Action? Invalidate { get; init; }

        /// <summary>
        /// The sources of the diagrams whose Source toggle is on. Owned by the view: the document,
        /// and every button in it, is new at each build, so the choice cannot live on a button.
        /// </summary>
        public ISet<string> SourceShown { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }
}
