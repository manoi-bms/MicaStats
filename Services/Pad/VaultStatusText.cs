namespace Kil0bitSystemMonitor.Services.Pad
{
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
    }
}
