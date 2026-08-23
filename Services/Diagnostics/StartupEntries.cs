using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>
    /// Reads and edits the "approved" flag Windows keeps for each startup entry — the same
    /// switch Task Manager's Startup apps list operates.
    ///
    /// <para>
    /// The value is twelve bytes. The first carries the flag; bytes four to eleven are a
    /// FILETIME recording when it was switched off, and are zero while it is on.
    /// </para>
    ///
    /// <para>
    /// <b>The flag is the low bit, not a fixed byte.</b> Per-user entries use 0x02 for enabled
    /// and 0x03 for disabled, but machine-wide entries on the same machine use 0x06 and 0x07 —
    /// both confirmed by reading the live registry. Code that hardcodes 0x02/0x03 therefore
    /// rewrites a machine entry's flag byte to an unrelated value. Everything here preserves
    /// the existing base byte and only moves bit 0.
    /// </para>
    /// </summary>
    public static class StartupApproval
    {
        /// <summary>Default flag byte for a per-user entry, used when none exists yet.</summary>
        public const byte DefaultUserFlag = 0x02;

        /// <summary>True when the twelve-byte value means "runs at sign-in".</summary>
        public static bool IsEnabled(byte[]? value)
        {
            // Absent value means the entry was never touched, and an untouched entry runs.
            if (value == null || value.Length == 0) return true;
            return (value[0] & 0x01) == 0;
        }

        /// <summary>
        /// Produces the value to write. <paramref name="existing"/> supplies the base byte so a
        /// machine-wide entry keeps its 0x06 family rather than being rewritten as 0x02.
        /// </summary>
        public static byte[] Encode(bool enabled, byte[]? existing, DateTime disabledAtUtc)
        {
            var value = new byte[12];

            byte baseFlag = existing is { Length: > 0 } ? existing[0] : DefaultUserFlag;
            value[0] = enabled
                ? (byte)(baseFlag & ~0x01)
                : (byte)(baseFlag | 0x01);

            if (!enabled)
            {
                // Windows stamps the moment it was switched off. Written little-endian, the
                // layout the shell reads it back as.
                long filetime = disabledAtUtc.ToFileTimeUtc();
                for (int i = 0; i < 8; i++)
                    value[4 + i] = (byte)((filetime >> (8 * i)) & 0xFF);
            }
            return value;
        }

        /// <summary>Recovers the FILETIME written by <see cref="Encode"/>, or null when enabled.</summary>
        public static DateTime? DisabledAtUtc(byte[]? value)
        {
            if (value == null || value.Length < 12 || IsEnabled(value)) return null;

            long filetime = 0;
            for (int i = 7; i >= 0; i--) filetime = (filetime << 8) | value[4 + i];
            if (filetime <= 0) return null;

            try { return DateTime.FromFileTimeUtc(filetime); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Enumerates what launches at sign-in, and switches per-user entries on and off.
    ///
    /// <para>
    /// <c>Win32_StartupCommand</c> alone is not enough: it lists entries but says nothing about
    /// whether each one is currently enabled, so a list built from it alone shows programs the
    /// user already switched off as if they still ran. The approval keys supply that state.
    /// </para>
    /// </summary>
    public static class StartupEntries
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
        private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        /// <summary>Marks the two folder-based locations, which key their approval by file name.</summary>
        private const string FolderLocationPrefix = "Startup folder";

        /// <summary>Everything registered to launch at sign-in, per-user entries first.</summary>
        public static List<StartupEntry> Read()
        {
            var entries = new List<StartupEntry>(32);

            ReadRunKey(Registry.CurrentUser, RunKey, ApprovedRun, StartupScope.CurrentUser,
                       "Registry (this user)", entries);
            ReadRunKey(Registry.LocalMachine, RunKey, ApprovedRun, StartupScope.Machine,
                       "Registry (all users)", entries);
            ReadRunKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                       ApprovedRun32, StartupScope.Machine, "Registry (all users, 32-bit)", entries);

            ReadStartupFolder(Environment.SpecialFolder.Startup, StartupScope.CurrentUser,
                              FolderLocationPrefix + " (this user)", entries);
            ReadStartupFolder(Environment.SpecialFolder.CommonStartup, StartupScope.Machine,
                              FolderLocationPrefix + " (all users)", entries);

            entries.Sort((a, b) =>
            {
                int scope = a.Scope.CompareTo(b.Scope);
                return scope != 0 ? scope : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return entries;
        }

        private static void ReadRunKey(RegistryKey root, string path, string approvedPath,
                                       StartupScope scope, string location, List<StartupEntry> into)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) return;

                using var approved = root.OpenSubKey(approvedPath);

                foreach (string name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    string command = key.GetValue(name)?.ToString() ?? "";
                    bool enabled = StartupApproval.IsEnabled(approved?.GetValue(name) as byte[]);
                    into.Add(new StartupEntry(name, command, location, scope, enabled));
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("boot", "Could not read " + path + ": " + ex.Message);
            }
        }

        private static void ReadStartupFolder(Environment.SpecialFolder folder, StartupScope scope,
                                              string location, List<StartupEntry> into)
        {
            try
            {
                string dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                RegistryKey root = scope == StartupScope.CurrentUser
                    ? Registry.CurrentUser : Registry.LocalMachine;
                using var approved = root.OpenSubKey(ApprovedFolder);

                foreach (string file in Directory.GetFiles(dir))
                {
                    string fileName = Path.GetFileName(file);
                    // desktop.ini is folder metadata, not a program.
                    if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    bool enabled = StartupApproval.IsEnabled(approved?.GetValue(fileName) as byte[]);
                    into.Add(new StartupEntry(
                        Path.GetFileNameWithoutExtension(file), file, location, scope, enabled));
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("boot", "Could not read startup folder: " + ex.Message);
            }
        }

        /// <summary>
        /// Switches a per-user entry on or off, exactly as Task Manager does.
        ///
        /// <para>
        /// Machine-wide entries are refused rather than attempted: writing under HKLM needs
        /// administrator rights this app does not hold, and a half-applied change would leave
        /// the user believing something was switched off when it was not.
        /// </para>
        /// </summary>
        /// <returns>Null on success, or a sentence explaining why it was not applied.</returns>
        public static string? SetEnabled(StartupEntry entry, bool enabled)
        {
            if (entry == null) return "No entry was selected.";
            if (entry.Scope != StartupScope.CurrentUser)
                return "This entry is set for all users, so changing it needs administrator rights. " +
                       "Use Task Manager's Startup apps page to switch it off.";

            bool isFolderEntry = entry.Location.StartsWith(FolderLocationPrefix, StringComparison.OrdinalIgnoreCase);
            string approvedPath = isFolderEntry ? ApprovedFolder : ApprovedRun;

            // The approval key stores folder entries under the file name including its
            // extension, and registry entries under the value name.
            string valueName = isFolderEntry ? Path.GetFileName(entry.Command) : entry.Name;
            if (string.IsNullOrEmpty(valueName)) return "This entry has no name to record.";

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(approvedPath, writable: true);
                if (key == null) return "The startup settings could not be opened.";

                var existing = key.GetValue(valueName) as byte[];
                key.SetValue(valueName, StartupApproval.Encode(enabled, existing, DateTime.UtcNow),
                             RegistryValueKind.Binary);

                DiagnosticsLog.Log("boot",
                    (enabled ? "Enabled" : "Disabled") + " startup entry " + entry.Name);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows refused the change. Administrator rights are needed for this entry.";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("boot", "Startup toggle failed for " + entry.Name, ex);
                return "The change could not be saved: " + ex.Message;
            }
        }

        /// <summary>Count of entries that will actually run, for the summary line.</summary>
        public static int CountEnabled(IReadOnlyList<StartupEntry> entries)
        {
            if (entries == null) return 0;
            int n = 0;
            foreach (var e in entries) if (e.Enabled) n++;
            return n;
        }

        /// <summary>"18 of 25 enabled" for the tab header.</summary>
        public static string Summarise(IReadOnlyList<StartupEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "None found";
            return string.Format(CultureInfo.InvariantCulture, "{0} of {1} enabled",
                CountEnabled(entries), entries.Count);
        }
    }
}
