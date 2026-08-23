using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Update
{
    /// <summary>
    /// Checks GitHub for a newer release, downloads its installer, and hands it to Windows.
    ///
    /// <para>
    /// The downloaded file is <b>never executed unless its SHA-256 matches the checksum published
    /// beside it</b>. An updater that runs whatever arrives is a remote-code-execution path into
    /// the user's machine; every release here ships a <c>.sha256</c> sidecar precisely so this
    /// check can be made. If the sidecar is missing or the hashes differ, the file is deleted and
    /// the update is refused.
    /// </para>
    ///
    /// <para>
    /// The installer requires administrator rights, so Windows will show a UAC prompt. That is
    /// deliberate and surfaced in the UI rather than hidden — an update cannot and should not
    /// install itself behind the user's back.
    /// </para>
    /// </summary>
    public static class UpdateService
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/manoi-bms/MicaStats/releases/latest";
        public const string ReleasesPage = "https://github.com/manoi-bms/MicaStats/releases";

        /// <summary>Only downloads served from this host are accepted.</summary>
        private const string TrustedDownloadHost = "github.com";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            // GitHub rejects API requests that do not identify themselves.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MicaStats-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>The running application's version.</summary>
        public static Version CurrentVersion =>
            typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

        public static string CurrentVersionText
        {
            get
            {
                var v = CurrentVersion;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>Where downloaded installers are staged.</summary>
        public static string DownloadFolder =>
            Path.Combine(Path.GetTempPath(), "MicaStats", "updates");

        /// <summary>
        /// Asks GitHub for the latest release. Network problems are returned as a message rather
        /// than thrown: a failed update check must never interrupt a monitoring app.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(bool includePreRelease = false,
                                                              CancellationToken cancel = default)
        {
            var current = CurrentVersion;
            try
            {
                using var response = await Http.GetAsync(LatestReleaseApi, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // 403 here is almost always GitHub's unauthenticated rate limit.
                    string why = (int)response.StatusCode == 403
                        ? "GitHub rate limit reached — try again later"
                        : $"GitHub returned {(int)response.StatusCode}";
                    DiagnosticsLog.Warn("update", "Check failed: " + why);
                    return UpdateCheckResult.Failed(why);
                }

                string json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                var release = ReleaseParser.Parse(json);
                if (release == null)
                {
                    DiagnosticsLog.Warn("update", "Could not parse the release response");
                    return UpdateCheckResult.Failed("Unexpected response from GitHub");
                }

                if (release.PreRelease && !includePreRelease)
                    return new UpdateCheckResult(false, current, release, null);

                bool newer = ReleaseParser.IsNewer(current, release.Version);
                DiagnosticsLog.Log("update",
                    $"Checked: current {CurrentVersionText}, latest {release.TagName}, update {(newer ? "available" : "not needed")}");

                return new UpdateCheckResult(newer, current, release, null);
            }
            catch (OperationCanceledException)
            {
                return UpdateCheckResult.Failed("Cancelled");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("update", "Check failed", ex);
                return UpdateCheckResult.Failed(ex is HttpRequestException ? "No connection to GitHub" : ex.Message);
            }
        }

        /// <summary>
        /// Downloads the release installer and verifies it. Returns the local path, or null with
        /// a reason reported through <paramref name="status"/>. A file that fails verification is
        /// deleted rather than left on disk.
        /// </summary>
        public static async Task<string?> DownloadAsync(ReleaseInfo release,
                                                        IProgress<double>? progress,
                                                        Action<string>? status,
                                                        CancellationToken cancel = default)
        {
            var installer = ReleaseParser.FindInstaller(release.Assets);
            if (installer == null)
            {
                DiagnosticsLog.Warn("update", "Release " + release.TagName + " has no installer asset");
                status?.Invoke("This release has no installer to download.");
                return null;
            }

            if (!IsTrusted(installer.Url))
            {
                DiagnosticsLog.Error("update", "Refusing download from unexpected host: " + installer.Url);
                status?.Invoke("The download link is not on github.com — refused.");
                return null;
            }

            var checksumAsset = ReleaseParser.FindChecksum(release.Assets, installer.Name);
            if (checksumAsset == null)
            {
                // Without a published hash there is nothing to verify against, and running an
                // unverified installer is exactly what this method exists to prevent.
                DiagnosticsLog.Error("update", "No checksum published for " + installer.Name);
                status?.Invoke("No checksum published for this release — refusing to install.");
                return null;
            }

            Directory.CreateDirectory(DownloadFolder);
            string target = Path.Combine(DownloadFolder, installer.Name);

            try
            {
                status?.Invoke("Downloading " + ReleaseParser.Size(installer.Size) + "…");
                await DownloadFileAsync(installer.Url, target, installer.Size, progress, cancel).ConfigureAwait(false);

                status?.Invoke("Verifying…");
                string expected = await Http.GetStringAsync(checksumAsset.Url, cancel).ConfigureAwait(false);
                string? wanted = ReleaseParser.ParseChecksum(expected);
                if (wanted == null)
                {
                    Delete(target);
                    DiagnosticsLog.Error("update", "Checksum file was not readable");
                    status?.Invoke("The published checksum could not be read — refusing to install.");
                    return null;
                }

                string actual = await Task.Run(() => Sha256(target), cancel).ConfigureAwait(false);
                if (!string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    Delete(target);
                    DiagnosticsLog.Error("update", $"Checksum mismatch: expected {wanted}, got {actual}");
                    status?.Invoke("The download did not match its checksum — it was deleted.");
                    return null;
                }

                DiagnosticsLog.Log("update", $"Downloaded and verified {installer.Name} ({actual})");
                status?.Invoke("Verified.");
                return target;
            }
            catch (OperationCanceledException)
            {
                Delete(target);
                status?.Invoke("Cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                Delete(target);
                DiagnosticsLog.Error("update", "Download failed", ex);
                status?.Invoke("Download failed — see the diagnostics log.");
                return null;
            }
        }

        private static bool IsTrusted(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.Equals(TrustedDownloadHost, StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith("." + TrustedDownloadHost, StringComparison.OrdinalIgnoreCase));

        private static async Task DownloadFileAsync(string url, string target, long expectedSize,
                                                    IProgress<double>? progress, CancellationToken cancel)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel)
                                           .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? expectedSize;
            using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var file = File.Create(target);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Clamp(done * 100d / total, 0, 100));
            }
        }

        public static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Runs a verified installer. Windows shows a UAC prompt because the installer requires
        /// administrator rights; declining it simply leaves the current version in place.
        ///
        /// <para>
        /// <c>/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS</c> lets Setup shut MicaStats down through
        /// the Restart Manager and start it again afterwards, so the running instance does not
        /// hold its own files locked mid-upgrade.
        /// </para>
        /// </summary>
        public static bool LaunchInstaller(string installerPath)
        {
            try
            {
                if (!File.Exists(installerPath)) return false;

                var info = new ProcessStartInfo(installerPath)
                {
                    Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true,   // required for the UAC elevation prompt
                };
                Process.Start(info);
                DiagnosticsLog.Log("update", "Launched installer " + Path.GetFileName(installerPath));
                return true;
            }
            catch (Exception ex)
            {
                // A cancelled UAC prompt lands here; it is a normal outcome, not a crash.
                DiagnosticsLog.Warn("update", "Installer was not started: " + ex.Message);
                return false;
            }
        }

        /// <summary>Removes previously downloaded installers, optionally keeping one.</summary>
        public static void CleanOldDownloads(string? keep = null)
        {
            try
            {
                if (!Directory.Exists(DownloadFolder)) return;
                foreach (string file in Directory.GetFiles(DownloadFolder, "*.exe"))
                {
                    if (keep != null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    Delete(file);
                }
            }
            catch { }
        }
    }
}
