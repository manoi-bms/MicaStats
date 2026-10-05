using System.Text.Json;
using Kil0bitSystemMonitor.Models;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public class MeetingConfigTests
{
    [Theory]
    [InlineData("Both", "Both")]
    [InlineData("ASR1", "ASR1")]
    [InlineData("unknown", "ASR2")]
    public void Recognition_mode_round_trips_and_unknown_modes_keep_the_legacy_default(string input, string expected)
    {
        var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { MeetingAsrService = input }))!;
        Assert.Equal(expected, saved.MeetingAsrService);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("th", "th")]
    [InlineData("en", "en")]
    [InlineData("unknown", "auto")]
    public void Ai_response_language_round_trips_and_unknown_languages_follow_the_transcript(string input, string expected)
    {
        var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { MeetingAiResponseLanguage = input }))!;
        Assert.Equal(expected, saved.MeetingAiResponseLanguage);
        Assert.Equal("auto", JsonSerializer.Deserialize<AppConfig>("{}")!.MeetingAiResponseLanguage);
    }

    [Fact]
    public void Old_configs_default_to_asr2_and_selected_devices_survive_a_round_trip()
    {
        var old = JsonSerializer.Deserialize<AppConfig>("{}")!;
        using var defaults = JsonDocument.Parse(JsonSerializer.Serialize(old));
        Assert.True(defaults.RootElement.TryGetProperty("MeetingAsrService", out var service));
        Assert.Equal("ASR2", service.GetString());
        Assert.Equal("default", defaults.RootElement.GetProperty("MeetingVoice").GetString());
        Assert.Equal("", defaults.RootElement.GetProperty("MeetingPlaybackDeviceId").GetString());
        Assert.Equal("", defaults.RootElement.GetProperty("MeetingAsr1BaseUrl").GetString());
        Assert.Equal("", defaults.RootElement.GetProperty("MeetingAsr2BaseUrl").GetString());
        Assert.Equal("", defaults.RootElement.GetProperty("MeetingTtsBaseUrl").GetString());

        const string input = "{\"MeetingMicrophoneId\":\"mic\",\"MeetingOutputId\":\"out\",\"MeetingAsrService\":\"ASR1\"}";
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(JsonSerializer.Deserialize<AppConfig>(input)));
        Assert.Equal("mic", saved.RootElement.GetProperty("MeetingMicrophoneId").GetString());
        Assert.Equal("out", saved.RootElement.GetProperty("MeetingOutputId").GetString());
        Assert.Equal("ASR1", saved.RootElement.GetProperty("MeetingAsrService").GetString());
    }

    [Fact]
    public void Meeting_service_addresses_are_trimmed_and_survive_a_round_trip_without_built_in_hosts()
    {
        var config = new AppConfig
        {
            MeetingAsr1BaseUrl = "  https://one.example/v1  ",
            MeetingAsr2BaseUrl = "https://two.example/v1",
            MeetingTtsBaseUrl = "  https://voice.example/v1\r\n",
        };

        var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

        Assert.Equal("https://one.example/v1", saved.MeetingAsr1BaseUrl);
        Assert.Equal("https://two.example/v1", saved.MeetingAsr2BaseUrl);
        Assert.Equal("https://voice.example/v1", saved.MeetingTtsBaseUrl);
    }
}
