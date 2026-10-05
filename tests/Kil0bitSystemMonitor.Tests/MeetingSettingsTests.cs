using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Kil0bitSystemMonitor.Models;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public class MeetingSettingsTests
{
    [Fact]
    public async Task Valid_addresses_are_normalized_applied_then_saved()
    {
        var config = new AppConfig();
        var order = new List<string>();

        string status = await SettingsWindow.SaveMeetingServiceSettingsAsync(
            config,
            " https://asr-two.example/v1/ ",
            "https://asr-one.example/v1",
            "https://speech.example/v1/",
            async apply =>
            {
                order.Add("stop voice work");
                await Task.Yield();
                apply();
                order.Add("apply");
            },
            () => order.Add("save"));

        Assert.Equal(new[] { "stop voice work", "apply", "save" }, order);
        Assert.Equal("https://asr-two.example/v1/", config.MeetingAsr2BaseUrl);
        Assert.Equal("https://asr-one.example/v1/", config.MeetingAsr1BaseUrl);
        Assert.Equal("https://speech.example/v1/", config.MeetingTtsBaseUrl);
        Assert.Equal("Saved. Transcription configured (ASR2, ASR1). Speech output configured.", status);
    }

    [Fact]
    public async Task Blank_addresses_are_a_valid_explicitly_unconfigured_state()
    {
        var config = new AppConfig { MeetingAsr2BaseUrl = "https://old.example/v1" };
        int applied = 0;
        int saved = 0;

        string status = await SettingsWindow.SaveMeetingServiceSettingsAsync(
            config, " ", "", null,
            apply => { applied++; apply(); return Task.CompletedTask; },
            () => saved++);

        Assert.Equal(1, applied);
        Assert.Equal(1, saved);
        Assert.Equal("", config.MeetingAsr2BaseUrl);
        Assert.Equal("Saved. Configure one ASR service for transcription. Speech output not configured (optional).", status);
    }

    [Fact]
    public async Task One_invalid_address_changes_nothing_and_does_not_apply_or_save()
    {
        var config = new AppConfig
        {
            MeetingAsr2BaseUrl = "https://current.example/v1",
            MeetingAsr1BaseUrl = "https://fallback.example/v1",
        };
        int applied = 0;
        int saved = 0;

        string status = await SettingsWindow.SaveMeetingServiceSettingsAsync(
            config, "ftp://invalid.example/v1", "", "",
            apply => { applied++; apply(); return Task.CompletedTask; },
            () => saved++);

        Assert.StartsWith("ASR2 needs", status, StringComparison.Ordinal);
        Assert.Equal(0, applied);
        Assert.Equal(0, saved);
        Assert.Equal("https://current.example/v1", config.MeetingAsr2BaseUrl);
        Assert.Equal("https://fallback.example/v1", config.MeetingAsr1BaseUrl);
    }

    [Fact]
    public void Readiness_counts_only_valid_configured_services()
    {
        var config = new AppConfig
        {
            MeetingAsr2BaseUrl = "not an address",
            MeetingAsr1BaseUrl = "https://fallback.example/v1",
            MeetingTtsBaseUrl = "",
        };

        Assert.Equal("Transcription configured (ASR1). Speech output not configured (optional).", SettingsWindow.DescribeMeetingReadiness(config));
    }

    [Fact]
    public void One_asr_and_optional_speech_do_not_require_an_alternative_endpoint()
    {
        var config = new AppConfig { MeetingAsr2BaseUrl = "https://asr.example/v1", MeetingTtsBaseUrl = "https://tts.example/v1" };
        Assert.Equal("Transcription configured (ASR2). Speech output configured.", SettingsWindow.DescribeMeetingReadiness(config));
        Assert.Empty(config.MeetingAsr1BaseUrl);
    }

    [Fact]
    public void Meeting_has_a_dedicated_navigation_page_with_generic_examples_and_no_private_host()
    {
        string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml.cs"));

        Assert.Contains("Content=\"Meeting\" Tag=\"Meeting\"", xaml);
        Assert.Contains("x:Name=\"MeetingSection\"", xaml);
        Assert.Contains("x:Name=\"MeetingAsr2BaseUrlBox\"", xaml);
        Assert.Contains("x:Name=\"MeetingAsr1BaseUrlBox\"", xaml);
        Assert.Contains("x:Name=\"MeetingTtsBaseUrlBox\"", xaml);
        Assert.Contains("https://asr.example/v1", xaml);
        Assert.Contains("https://tts.example/v1", xaml);
        Assert.Contains("case \"Meeting\": MeetingSection.Visibility = Visibility.Visible; LoadMeetingSettings(); break;", code);
    }

    [Fact]
    public void Every_direct_color_in_settings_markup_is_accepted_by_Wpf() => UiThread.Run(() =>
    {
        string path = Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml");
        XDocument markup = XDocument.Load(path);
        string[] brushAttributes = { "Background", "BorderBrush", "Foreground", "Color" };
        string[] colors = markup.Descendants()
            .Attributes()
            .Where(attribute => brushAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
            .Select(attribute => attribute.Value)
            .Where(value => value.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(colors);
        foreach (string color in colors)
            Assert.NotNull(System.Windows.Media.ColorConverter.ConvertFromString(color));
    });
}
