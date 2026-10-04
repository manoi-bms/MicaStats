using System;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The one place an answer's Copy buttons put text on the clipboard. Tests replace
    /// <see cref="SetText"/> so the real clipboard is never touched.
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
    }

    /// <summary>What <see cref="ChatDocument.Build"/> needs from outside the document.</summary>
    internal sealed class ChatRender
    {
        /// <summary>The render with the real clipboard.</summary>
        public static ChatRender Default { get; } = new();

        /// <summary>Copies text; the default goes to <see cref="ChatClipboard.SetText"/> as it is at the time of the click.</summary>
        public Action<string> Copy { get; init; } = text => ChatClipboard.SetText(text);
    }
}
