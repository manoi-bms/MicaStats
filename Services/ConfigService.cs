using System;
using System.IO;
using System.Text.Json;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Loads and persists <see cref="AppConfig"/> as JSON under %APPDATA%.
    ///
    /// Writes are debounced and atomic. Both matter: a single UI action can set many properties in
    /// sequence (a factory reset touches ~40), and the config is rewritten in full on every
    /// notification. Writing directly to the live file that many times in a row is what makes a
    /// torn file likely, and a torn file used to be silently discarded — losing every setting.
    /// </summary>
    public class ConfigService : IDisposable
    {
        /// <summary>
        /// How long to wait for further changes before flushing to disk. Long enough to coalesce
        /// a burst of setters from one UI action, short enough that a crash loses nothing a user
        /// would notice.
        /// </summary>
        private const int SaveDebounceMs = 400;

        private readonly string _configPath;
        private readonly string _tempPath;
        private readonly string _corruptPath;
        private readonly object _ioLock = new();
        private System.Threading.Timer? _debounceTimer;
        private bool _pendingSave;
        private bool _disposed;

        public AppConfig Config { get; private set; }

        /// <summary>Raised after the configuration has been persisted.</summary>
        public event Action? SettingsChanged;

        public ConfigService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDir = Path.Combine(appData, "kil0bit-system-monitor");
            Directory.CreateDirectory(configDir);
            _configPath = Path.Combine(configDir, "config.json");
            _tempPath = _configPath + ".tmp";
            _corruptPath = _configPath + ".corrupt";

            Config = LoadConfig();

            // Ensure the Windows Run key matches the loaded config unconditionally.
            StartupService.SetStartup(Config.LaunchOnStartup);

            Config.PropertyChanged += (s, e) =>
            {
                ScheduleSave();
                if (e.PropertyName == nameof(AppConfig.LaunchOnStartup))
                {
                    StartupService.SetStartup(Config.LaunchOnStartup);
                }
            };
        }

        private AppConfig LoadConfig()
        {
            if (!File.Exists(_configPath))
            {
                // A leftover temp file means a previous write was interrupted before the replace.
                // It is the most recent complete serialization available, so prefer it.
                if (File.Exists(_tempPath))
                {
                    var recovered = TryDeserialize(_tempPath);
                    if (recovered != null) return recovered;
                }
                return new AppConfig();
            }

            var loaded = TryDeserialize(_configPath);
            if (loaded != null) return loaded;

            // Unparseable. Preserve it instead of overwriting — the next save would destroy the
            // only copy of the user's settings, and a corrupt file is often hand-recoverable.
            try
            {
                if (File.Exists(_corruptPath)) File.Delete(_corruptPath);
                File.Move(_configPath, _corruptPath);
            }
            catch { }

            return new AppConfig();
        }

        private static AppConfig? TryDeserialize(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<AppConfig>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests a save after the debounce interval, coalescing a burst of property changes
        /// into a single file write.
        /// </summary>
        private void ScheduleSave()
        {
            if (_disposed) return;
            lock (_ioLock)
            {
                _pendingSave = true;
                if (_debounceTimer == null)
                {
                    _debounceTimer = new System.Threading.Timer(_ => Flush(), null, SaveDebounceMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    // Restart the window; the last change in a burst decides when we write.
                    _debounceTimer.Change(SaveDebounceMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        private void Flush()
        {
            bool shouldSave;
            lock (_ioLock)
            {
                shouldSave = _pendingSave;
                _pendingSave = false;
            }
            if (shouldSave) SaveConfig();
        }

        /// <summary>
        /// Writes the configuration immediately, bypassing the debounce. Call this when the result
        /// must be on disk before continuing (window closing, application exit).
        /// </summary>
        public void SaveConfig()
        {
            bool written = false;
            try
            {
                string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });

                lock (_ioLock)
                {
                    _pendingSave = false;

                    // Write to a sibling temp file, then swap. A crash mid-write leaves the
                    // previous config intact rather than a half-written one.
                    File.WriteAllText(_tempPath, json);

                    if (File.Exists(_configPath))
                    {
                        // File.Replace is atomic on NTFS and requires the destination to exist.
                        File.Replace(_tempPath, _configPath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(_tempPath, _configPath);
                    }
                    written = true;
                }
            }
            catch
            {
                // Persistence is best-effort; a failed write must never take down the overlay.
            }

            if (written) SettingsChanged?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            System.Threading.Timer? timer;
            bool hadPending;
            lock (_ioLock)
            {
                timer = _debounceTimer;
                _debounceTimer = null;
                hadPending = _pendingSave;
            }
            timer?.Dispose();

            // Do not lose a change that was still inside the debounce window at shutdown.
            if (hadPending) SaveConfig();
        }
    }
}
