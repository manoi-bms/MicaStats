using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Services.Conference;

namespace Kil0bitSystemMonitor.Conference;

public partial class MeetingWindow
{
    private string[] _speechSources = Array.Empty<string>();
    private bool _refreshingVoices;
    private CancellationTokenSource? _voiceRefresh;
    private Task _voiceRefreshTask = Task.CompletedTask;
    private string? _voiceCatalogStatus;

    private void RefreshSpeech()
    {
        SpeechStatus.Text = _speech.IsBusy ? _speech.Status : _voiceCatalogStatus ?? _speech.Status;
        bool configured = IsTtsConfigured();
        SpeakButton.IsEnabled = !_speech.IsBusy && !_refreshingVoices && !_busy && !_closing && configured;
        StopSpeechButton.IsEnabled = _speech.IsBusy;
        SpeechChoices.IsEnabled = !_speech.IsBusy && !_refreshingVoices && !_busy && !_closing;
        RefreshVoicesButton.IsEnabled = !_speech.IsBusy && !_refreshingVoices && !_busy && !_closing && configured;
    }

    private void ClearDerivedSpeech()
    {
        if (_speechSources.Length == 0) return;
        _ = _speech.StopAsync();
        SpeechText.Clear();
        _speechSources = Array.Empty<string>();
    }

    private void UseForSpeech_Click(object sender, RoutedEventArgs e)
    {
        if (QuestionChoice.SelectedItem is not MeetingQuestion question || string.IsNullOrWhiteSpace(question.Answer)) return;
        SpeechText.Text = question.Answer;
        // Uncited answers are still derived context, and are invalidated with it.
        _speechSources = question.Sources.Count == 0 ? new[] { "unsupported-answer" } : question.Sources.ToArray();
        MeetingTabs.SelectedItem = SpeechTab;
        SpeechStatus.Text = IsTtsConfigured()
            ? "Answer prepared. Press Speak when ready."
            : "Answer prepared. Configure the speech service to play it.";
    }

    private void SpeechText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_speech?.IsBusy == true) _ = _speech.StopAsync();
    }

    private void RefreshVoices_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _closed || _refreshingVoices || _speech.IsBusy) return;
        if (!IsTtsConfigured())
        {
            SpeechStatus.Text = "Configure the speech service before loading voices.";
            RefreshServiceReadiness();
            return;
        }
        _voiceRefreshTask = RefreshVoicesAsync();
    }

    private async Task RefreshVoicesAsync()
    {
        CancelVoiceRefresh();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _voiceRefresh = refresh;
        MeetingServiceEndpoints.TryNormalizeBaseUrl(_config.MeetingTtsBaseUrl, out string endpointAtStart);
        _refreshingVoices = true;
        _voiceCatalogStatus = "Loading available voices…";
        RefreshSpeech();
        try
        {
            var voices = await _tts.GetVoicesAsync(refresh.Token);
            MeetingServiceEndpoints.TryNormalizeBaseUrl(_config.MeetingTtsBaseUrl, out string currentEndpoint);
            if (_closing || !ReferenceEquals(_voiceRefresh, refresh) || endpointAtStart != currentEndpoint) return;
            string selected = (VoiceChoice.SelectedItem as MeetingVoice)?.Id ?? _config.MeetingVoice;
            VoiceChoice.ItemsSource = voices;
            VoiceChoice.SelectedItem = voices.FirstOrDefault(x => x.Id == selected);
            _voiceCatalogStatus = VoiceChoice.SelectedItem == null ? "Choose a voice from the refreshed catalog." : "Voices refreshed.";
        }
        catch (OperationCanceledException) { }
        catch { _voiceCatalogStatus = "Could not refresh voices. Try again."; }
        finally
        {
            if (ReferenceEquals(_voiceRefresh, refresh))
            {
                _voiceRefresh = null;
                _refreshingVoices = false;
                RefreshSpeech();
            }
        }
    }

    private async void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _closed || _speech.IsBusy || _refreshingVoices) return;
        if (!IsTtsConfigured())
        {
            SpeechStatus.Text = "Configure the speech service before speaking.";
            RefreshServiceReadiness();
            return;
        }
        if (PlaybackChoice.SelectedItem is not MeetingDevice device || VoiceChoice.SelectedItem is not MeetingVoice voice)
        { SpeechStatus.Text = "Choose an available playback device and voice."; return; }
        if (string.IsNullOrWhiteSpace(SpeechText.Text) || SpeechText.Text.Length > 4096)
        { SpeechStatus.Text = "Enter between 1 and 4,096 characters. Edit longer text before speaking."; return; }
        _voiceCatalogStatus = null;
        _config.MeetingPlaybackDeviceId = device.Id;
        _config.MeetingVoice = voice.Id;
        try { await _speech.SpeakAsync(SpeechText.Text, voice.Id, device.Id, _speechSources, _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { SpeechStatus.Text = "Speech failed. Check the service and playback device."; }
        RefreshState();
    }

    private async void StopSpeech_Click(object sender, RoutedEventArgs e)
    {
        await _speech.StopAsync();
        RefreshState();
    }

    private void CancelVoiceRefresh()
    {
        try { _voiceRefresh?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task WaitForVoiceRefreshAsync()
    {
        CancelVoiceRefresh();
        Task refresh = _voiceRefreshTask;
        try { await refresh; }
        catch (OperationCanceledException) { }
    }

    private void ResetVoiceCatalog()
    {
        _voiceCatalogStatus = null;
        VoiceChoice.ItemsSource = new[] { new MeetingVoice("default", "Default") };
        VoiceChoice.SelectedIndex = _config.MeetingVoice == "default" ? 0 : -1;
        if (VoiceChoice.SelectedIndex < 0)
            SpeechStatus.Text = "Refresh voices to validate your saved voice.";
    }
}
