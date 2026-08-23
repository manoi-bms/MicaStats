using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>Encoder for saved captures.</summary>
    public enum CaptureFormat { Png, Jpeg }

    /// <summary>
    /// Builds file names for saved captures from a user template.
    ///
    /// <para>
    /// Every date part is formatted with <see cref="CultureInfo.InvariantCulture"/>. This is not
    /// cosmetic: on a machine with a non-Gregorian locale calendar — Thai, for one — the default
    /// culture stamps Buddhist-era years, so a capture taken in 2026 would be filed under 2569
    /// and sort nowhere near its neighbours.
    /// </para>
    /// </summary>
    public static class CaptureFileNamer
    {
        public const string DefaultTemplate = "MicaStats_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";

        public static string Extension(CaptureFormat format) => format switch
        {
            CaptureFormat.Jpeg => ".jpg",
            _ => ".png",
        };

        /// <summary>
        /// Expands the template's tokens. Unknown tokens are left alone rather than silently
        /// dropped, so a typo is visible in the file name instead of vanishing.
        /// </summary>
        public static string Format(string? template, DateTime when, string mode = "", int width = 0, int height = 0)
        {
            if (string.IsNullOrWhiteSpace(template)) template = DefaultTemplate;
            var inv = CultureInfo.InvariantCulture;

            string s = template
                .Replace("{yyyy}", when.ToString("yyyy", inv))
                .Replace("{MM}", when.ToString("MM", inv))
                .Replace("{dd}", when.ToString("dd", inv))
                .Replace("{HH}", when.ToString("HH", inv))
                .Replace("{mm}", when.ToString("mm", inv))
                .Replace("{ss}", when.ToString("ss", inv))
                .Replace("{date}", when.ToString("yyyy-MM-dd", inv))
                .Replace("{time}", when.ToString("HH-mm-ss", inv))
                .Replace("{mode}", mode ?? "")
                .Replace("{w}", width.ToString(inv))
                .Replace("{h}", height.ToString(inv))
                .Replace("{app}", "MicaStats");

            return Sanitize(s);
        }

        /// <summary>
        /// Strips characters Windows forbids in a file name and trims trailing dots and spaces,
        /// which Explorer silently rejects. Never returns an empty name.
        /// </summary>
        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "capture";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);

            string cleaned = sb.ToString().TrimEnd('.', ' ').Trim();
            return cleaned.Length == 0 ? "capture" : cleaned;
        }

        /// <summary>
        /// Returns a full path that does not collide with an existing file, appending " (2)",
        /// " (3)" and so on. <paramref name="exists"/> is injected so the rule is testable
        /// without touching the disk.
        /// </summary>
        public static string UniquePath(string directory, string baseName, CaptureFormat format, Func<string, bool> exists)
        {
            string ext = Extension(format);
            string first = Path.Combine(directory, baseName + ext);
            if (exists == null || !exists(first)) return first;

            for (int n = 2; n < 10000; n++)
            {
                string candidate = Path.Combine(directory, $"{baseName} ({n}){ext}");
                if (!exists(candidate)) return candidate;
            }
            // Astronomically unlikely; fall back to something certainly unique.
            return Path.Combine(directory, $"{baseName} ({Guid.NewGuid():N}){ext}");
        }

        /// <summary>The default folder for saved captures: Pictures\MicaStats.</summary>
        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MicaStats");
    }
}
