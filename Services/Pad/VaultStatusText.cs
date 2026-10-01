namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Which of the buttons on Settings' credentials card apply: Change PIN, Lock now, Reset vault.</summary>
    public readonly record struct VaultButtons(bool ChangePin, bool LockNow, bool Reset);

    /// <summary>The one line Settings shows about the credential vault. Never names a value or a PIN.</summary>
    public static class VaultStatusText
    {
        public static string Describe(bool loaded, bool exists, int count, bool unlocked)
        {
            if (!loaded) return "The vault could not be read right now.";
            if (!exists) return "No PIN set yet. Select text in MicaPad and choose Store as credential.";
            string counted = count == 0 ? "No credentials stored." : count == 1 ? "1 credential stored." : count + " credentials stored.";
            return counted + (unlocked ? " Unlocked." : " Locked.");
        }

        /// <summary>Change PIN and Reset vault need a vault that was read; Lock now an unlocked one.</summary>
        public static VaultButtons Buttons(bool loaded, bool exists, bool unlocked)
        {
            bool vault = loaded && exists;
            return new VaultButtons(ChangePin: vault, LockNow: vault && unlocked, Reset: vault);
        }
    }
}
