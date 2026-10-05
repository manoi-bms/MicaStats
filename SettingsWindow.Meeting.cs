using System;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Conference;

namespace Kil0bitSystemMonitor
{
    public partial class SettingsWindow
    {
        private MeetingServiceTestAction[] _meetingServiceTests = Array.Empty<MeetingServiceTestAction>();

        private void InitializeMeetingServiceTests()
        {
            _meetingServiceTests = new[]
            {
                new MeetingServiceTestAction(MeetingServiceKind.Asr2, MeetingAsr2BaseUrlBox, TestMeetingAsr2Button, MeetingAsr2TestStatus),
                new MeetingServiceTestAction(MeetingServiceKind.Asr1, MeetingAsr1BaseUrlBox, TestMeetingAsr1Button, MeetingAsr1TestStatus),
                new MeetingServiceTestAction(MeetingServiceKind.Tts, MeetingTtsBaseUrlBox, TestMeetingTtsButton, MeetingTtsTestStatus),
            };
        }

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
            foreach (var test in _meetingServiceTests) test.Cancel();
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
            bool asr2 = MeetingServiceEndpoints.TryNormalizeBaseUrl(config.MeetingAsr2BaseUrl, out _);
            bool asr1 = MeetingServiceEndpoints.TryNormalizeBaseUrl(config.MeetingAsr1BaseUrl, out _);
            bool tts = MeetingServiceEndpoints.TryNormalizeBaseUrl(config.MeetingTtsBaseUrl, out _);
            string transcription = asr2 || asr1
                ? $"Transcription configured ({(asr2 && asr1 ? "ASR2, ASR1" : asr2 ? "ASR2" : "ASR1")})."
                : "Configure one ASR service for transcription.";
            string state = transcription + (tts ? " Speech output configured." : " Speech output not configured (optional).");
            return saved ? "Saved. " + state : state;
        }
    }
}
