using System;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Conference;

namespace Kil0bitSystemMonitor
{
    public partial class SettingsWindow
    {
        private void LoadMeetingSettings()
        {
            AppConfig? config = _config?.Config;
            if (config == null) return;

            MeetingAsr2BaseUrlBox.Text = config.MeetingAsr2BaseUrl;
            MeetingAsr1BaseUrlBox.Text = config.MeetingAsr1BaseUrl;
            MeetingTtsBaseUrlBox.Text = config.MeetingTtsBaseUrl;
            MeetingServicesStatus.Text = DescribeMeetingReadiness(config);
        }

        private async void OnSaveMeetingServices(object sender, System.Windows.RoutedEventArgs e)
        {
            SaveMeetingServicesButton.IsEnabled = false;
            MeetingServicesStatus.Text = "Saving…";
            try
            {
                string status = await SaveMeetingServiceSettingsAsync(
                    _config.Config,
                    MeetingAsr2BaseUrlBox.Text,
                    MeetingAsr1BaseUrlBox.Text,
                    MeetingTtsBaseUrlBox.Text,
                    App.ApplyMeetingServiceSettingsAsync,
                    _config.SaveConfig);
                MeetingServicesStatus.Text = status;

                if (status.StartsWith("Saved.", StringComparison.Ordinal))
                {
                    MeetingAsr2BaseUrlBox.Text = _config.Config.MeetingAsr2BaseUrl;
                    MeetingAsr1BaseUrlBox.Text = _config.Config.MeetingAsr1BaseUrl;
                    MeetingTtsBaseUrlBox.Text = _config.Config.MeetingTtsBaseUrl;
                }
            }
            catch
            {
                MeetingServicesStatus.Text = "Settings could not be applied. Try again.";
            }
            finally
            {
                SaveMeetingServicesButton.IsEnabled = true;
            }
        }

        internal static async Task<string> SaveMeetingServiceSettingsAsync(
            AppConfig config,
            string? asr2Input,
            string? asr1Input,
            string? ttsInput,
            Func<Action, Task> apply,
            Action save)
        {
            if (!TryNormalizeOptionalBaseUrl(asr2Input, out string asr2))
                return "ASR2 needs an HTTP or HTTPS base URL including its API path, or leave it blank.";
            if (!TryNormalizeOptionalBaseUrl(asr1Input, out string asr1))
                return "ASR1 needs an HTTP or HTTPS base URL including its API path, or leave it blank.";
            if (!TryNormalizeOptionalBaseUrl(ttsInput, out string tts))
                return "VoxCPM TTS needs an HTTP or HTTPS base URL including its API path, or leave it blank.";

            await apply(() =>
            {
                config.MeetingAsr2BaseUrl = asr2;
                config.MeetingAsr1BaseUrl = asr1;
                config.MeetingTtsBaseUrl = tts;
            });
            save();
            return DescribeMeetingReadiness(config, saved: true);
        }

        private static bool TryNormalizeOptionalBaseUrl(string? input, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                normalized = "";
                return true;
            }
            return MeetingServiceEndpoints.TryNormalizeBaseUrl(input, out normalized);
        }

        internal static string DescribeMeetingReadiness(AppConfig config, bool saved = false)
        {
            int configured = 0;
            if (MeetingServiceEndpoints.TryNormalizeBaseUrl(config.MeetingAsr2BaseUrl, out _)) configured++;
            if (MeetingServiceEndpoints.TryNormalizeBaseUrl(config.MeetingAsr1BaseUrl, out _)) configured++;
            if (MeetingServiceEndpoints.TryNormalizeBaseUrl(config.MeetingTtsBaseUrl, out _)) configured++;

            string state = configured switch
            {
                0 => "No meeting services configured.",
                3 => "All meeting services are configured.",
                _ => $"{configured} of 3 meeting services configured.",
            };
            return saved ? "Saved. " + state : state;
        }
    }
}
