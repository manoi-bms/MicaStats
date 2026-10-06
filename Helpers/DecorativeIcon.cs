using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>A decorative glyph whose adjacent label supplies the accessible name.</summary>
    public sealed class DecorativeIcon : TextBlock
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
}
