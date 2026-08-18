using Microsoft.Win32;
using System;
using System.IO;

namespace Kil0bitSystemMonitor.Services
{
    public static class StartupService
    {
        private const string AppName = "MicaStats";

        /// <summary>The upstream app's Run value, cleaned up so a migrated machine does not autostart a ghost.</summary>
        private const string LegacyAppName = "Kil0bitSystemMonitor";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void SetStartup(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true)!)
                {
                    if (enable)
                    {
                        string appPath = ProcessPath ?? Environment.ProcessPath ?? "";
                        if (!string.IsNullOrEmpty(appPath))
                        {
                            key.SetValue(AppName, $"\"{appPath}\" --startup");
                        }
                        key.DeleteValue(LegacyAppName, false);
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception)
            {
                // Silently fail
            }
        }

        private static string? ProcessPath => System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
    }
}