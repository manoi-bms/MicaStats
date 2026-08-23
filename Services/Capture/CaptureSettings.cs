using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What to capture.</summary>
    public enum CaptureMode
    {
        /// <summary>Pick a rectangle, window or screen interactively.</summary>
        Region,
        /// <summary>The window that was in front when the capture started.</summary>
        ActiveWindow,
        /// <summary>The monitor the pointer is on.</summary>
        Screen,
        /// <summary>Every monitor, as one image.</summary>
        AllScreens,
    }

    /// <summary>
    /// The capture options, resolved from <see cref="AppConfig"/> into typed values so the rest
    /// of the capture code never re-parses strings or worries about blank settings.
    /// </summary>
    public sealed class CaptureSettings
    {
        public string Folder { get; init; } = CaptureFileNamer.DefaultFolder;
        public string NameTemplate { get; init; } = CaptureFileNamer.DefaultTemplate;
        public CaptureFormat Format { get; init; } = CaptureFormat.Png;
        public int JpegQuality { get; init; } = 92;
        public bool IncludeCursor { get; init; }
        public bool CopyToClipboard { get; init; } = true;
        public bool AutoSave { get; init; } = true;
        public bool OpenEditor { get; init; } = true;
        public int DelaySeconds { get; init; }
        public RedactStyle RedactStyle { get; init; } = RedactStyle.Pixelate;

        public static CaptureSettings Defaults { get; } = new();

        public static CaptureSettings From(AppConfig? config)
        {
            if (config == null) return Defaults;
            return new CaptureSettings
            {
                Folder = string.IsNullOrWhiteSpace(config.CaptureFolder)
                    ? CaptureFileNamer.DefaultFolder
                    : config.CaptureFolder,
                NameTemplate = string.IsNullOrWhiteSpace(config.CaptureNameTemplate)
                    ? CaptureFileNamer.DefaultTemplate
                    : config.CaptureNameTemplate,
                Format = string.Equals(config.CaptureFormat, "Jpeg", StringComparison.OrdinalIgnoreCase)
                    ? CaptureFormat.Jpeg
                    : CaptureFormat.Png,
                JpegQuality = Math.Clamp(config.CaptureJpegQuality, 1, 100),
                IncludeCursor = config.CaptureIncludeCursor,
                CopyToClipboard = config.CaptureCopyToClipboard,
                AutoSave = config.CaptureAutoSave,
                OpenEditor = config.CaptureOpenEditor,
                DelaySeconds = Math.Clamp(config.CaptureDelaySeconds, 0, 60),
                RedactStyle = config.CaptureRedactStyle switch
                {
                    "Blur" => RedactStyle.Blur,
                    "Solid" => RedactStyle.Solid,
                    _ => RedactStyle.Pixelate,
                },
            };
        }
    }

    /// <summary>Modifier flags for <c>RegisterHotKey</c>.</summary>
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8,
        /// <summary>Suppresses the extra WM_HOTKEY sent while the key auto-repeats.</summary>
        NoRepeat = 0x4000,
    }

    /// <summary>
    /// Parses hotkey strings like <c>Ctrl+Shift+1</c> into the modifier flags and virtual-key
    /// code <c>RegisterHotKey</c> wants. Pure, so the accepted spellings are pinned by tests
    /// rather than discovered when a hotkey silently fails to register.
    /// </summary>
    public static class HotkeyParser
    {
        public static bool TryParse(string? text, out HotkeyModifiers modifiers, out uint virtualKey)
        {
            modifiers = HotkeyModifiers.None;
            virtualKey = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;

            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                bool last = i == parts.Length - 1;

                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= HotkeyModifiers.Control; continue;
                    case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                    case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                    case "win":
                    case "windows": modifiers |= HotkeyModifiers.Win; continue;
                }

                if (!last) return false;          // a non-modifier must be the final token
                if (!TryKey(p, out virtualKey)) return false;
            }

            // A bare key with no modifier would swallow that key system-wide.
            return virtualKey != 0 && modifiers != HotkeyModifiers.None;
        }

        private static bool TryKey(string key, out uint vk)
        {
            vk = 0;
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
            }

            switch (key.ToLowerInvariant())
            {
                case "printscreen":
                case "prtsc":
                case "snapshot": vk = 0x2C; return true;
                case "insert": vk = 0x2D; return true;
                case "delete": vk = 0x2E; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "space": vk = 0x20; return true;
            }

            if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') &&
                int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) &&
                n >= 1 && n <= 24)
            {
                vk = (uint)(0x70 + n - 1);   // VK_F1 = 0x70
                return true;
            }
            return false;
        }

        /// <summary>Normalised display form, so the settings UI shows a consistent spelling.</summary>
        public static string Describe(HotkeyModifiers modifiers, uint virtualKey)
        {
            var parts = new List<string>();
            if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");

            string key;
            if (virtualKey == 0x2C) key = "PrintScreen";
            else if (virtualKey == 0x2D) key = "Insert";
            else if (virtualKey == 0x2E) key = "Delete";
            else if (virtualKey == 0x24) key = "Home";
            else if (virtualKey == 0x23) key = "End";
            else if (virtualKey == 0x20) key = "Space";
            else if (virtualKey >= 0x70 && virtualKey <= 0x87)
                key = "F" + (virtualKey - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
            else key = ((char)virtualKey).ToString();

            parts.Add(key);
            return string.Join("+", parts);
        }
    }
}
