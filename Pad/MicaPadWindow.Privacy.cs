using System.Windows;
using System.Windows.Controls.Primitives;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// StackChan presence guard: a cover over the whole window while someone stands behind the
    /// owner. The notes stay open underneath; the cover lifts when they leave or on a click.
    /// </summary>
    public partial class MicaPadWindow
    {
        /// <summary>Whether the guard is up right now, so a window opened meanwhile starts covered.</summary>
        internal static bool GuardActive { get; set; }

        internal void SetPrivacyShield(bool on)
        {
            PrivacyShield.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            // Popups and menus are separate windows the cover cannot reach: close them all.
            foreach (var popup in new Popup[] { RenamePopup, OpenNotesPopup, ClosedPopup, TaskDatesPopup })
                popup.IsOpen = false;
            if (Editor.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        }

        private void ApplyGuardOnOpen()
        {
            if (GuardActive) SetPrivacyShield(true);
        }

        private void OnPrivacyShieldClick(object sender, RoutedEventArgs e) => SetPrivacyShield(false);
    }
}
