using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Kil0bitSystemMonitor.Services.Update
{
    /// <summary>One downloadable file attached to a release.</summary>
    public sealed record ReleaseAsset(string Name, string Url, long Size);

    /// <summary>A published release, as reported by the GitHub releases API.</summary>
    public sealed record ReleaseInfo(
        string TagName,
        string Name,
        string Notes,
        bool PreRelease,
        IReadOnlyList<ReleaseAsset> Assets)
    {
        public Version? Version => ReleaseParser.ParseVersion(TagName);
    }

    /// <summary>The outcome of an update check.</summary>
    public sealed record UpdateCheckResult(
        bool UpdateAvailable,
        Version? Current,
        ReleaseInfo? Release,
        string? Error)
    {
        public static UpdateCheckResult Failed(string error) => new(false, null, null, error);
    }

    /// <summary>
    /// Parsing and comparison for release metadata — everything the updater decides before it
    /// touches the network or the disk.
    ///
    /// <para>
    /// Pure and separately tested because the failure modes are quiet rather than loud: a tag
    /// that parses wrong offers a "newer" version that is actually older, an asset picked by the
    /// wrong rule downloads the checksum file instead of the installer, and a checksum parsed
    /// loosely would wave through a file that does not match.
    /// </para>
    /// </summary>
    public static class ReleaseParser
    {
        /// <summary>Suffix identifying the installer asset, matching this project's release convention.</summary>
        public const string InstallerSuffix = "-Setup.exe";

        /// <summary>
        /// Reads a tag such as <c>v1.4.1</c> into a version. A pre-release suffix
        /// (<c>v1.5.0-beta.2</c>) is ignored for ordering; use <see cref="IsPreReleaseTag"/> to
        /// decide whether to offer it at all.
        /// </summary>
        public static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;

            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);

            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (s.Length == 0) return null;

            // Version.TryParse needs at least major.minor.
            if (!s.Contains('.')) s += ".0";

            return Version.TryParse(s, out var version) ? version : null;
        }

        public static bool IsPreReleaseTag(string? tag) =>
            !string.IsNullOrWhiteSpace(tag) && tag.Contains('-');

        /// <summary>
        /// True when <paramref name="candidate"/> should be offered over <paramref name="current"/>.
        /// Only the first three components are compared: the build number is set by the SDK and
        /// is not part of how releases are numbered here.
        /// </summary>
        public static bool IsNewer(Version? current, Version? candidate)
        {
            if (candidate == null) return false;
            if (current == null) return true;

            return Normalize(candidate) > Normalize(current);
        }

        private static Version Normalize(Version v) =>
            new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

        /// <summary>Parses the JSON body of a GitHub "latest release" response.</summary>
        public static ReleaseInfo? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                string tag = Text(root, "tag_name");
                if (tag.Length == 0) return null;

                var assets = new List<ReleaseAsset>();
                if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        string name = Text(a, "name");
                        string url = Text(a, "browser_download_url");
                        if (name.Length == 0 || url.Length == 0) continue;

                        long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long v) ? v : 0;
                        assets.Add(new ReleaseAsset(name, url, size));
                    }
                }

                bool pre = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;

                return new ReleaseInfo(tag, Text(root, "name"), Text(root, "body"), pre, assets);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        /// <summary>The installer asset, or null when the release does not carry one.</summary>
        public static ReleaseAsset? FindInstaller(IReadOnlyList<ReleaseAsset>? assets)
        {
            if (assets == null) return null;
            foreach (var a in assets)
            {
                if (a.Name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        /// <summary>The checksum sidecar published next to an installer, if present.</summary>
        public static ReleaseAsset? FindChecksum(IReadOnlyList<ReleaseAsset>? assets, string installerName)
        {
            if (assets == null || string.IsNullOrEmpty(installerName)) return null;
            string wanted = installerName + ".sha256";
            foreach (var a in assets)
            {
                if (string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        /// <summary>
        /// Pulls the hash out of a <c>sha256sum</c>-style file: <c>&lt;hash&gt; *filename</c>.
        /// Returns null unless a well-formed 64-character hex digest is present, so a truncated
        /// download or an error page can never be mistaken for a valid checksum.
        /// </summary>
        public static string? ParseChecksum(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;

            foreach (string line in content.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
                string candidate = space > 0 ? trimmed.Substring(0, space) : trimmed;
                if (IsSha256Hex(candidate)) return candidate.ToLowerInvariant();
            }
            return null;
        }

        public static bool IsSha256Hex(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>Formats a byte count for a download progress line.</summary>
        public static string Size(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            double mb = bytes / 1024d / 1024d;
            return mb >= 1
                ? mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }
    }
}
