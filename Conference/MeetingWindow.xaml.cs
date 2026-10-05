using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Conference;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Conference;

public partial class MeetingWindow : Window
{
    private readonly AppConfig _config;
    private readonly IMeetingCapture _capture;
    private readonly MeetingSession _session;
    private readonly IDisposable _asrClient;
    private readonly MeetingIntelligence _intelligence;
    private readonly Func<(int Used, int Limit)> _usage;
    private readonly MeetingReferenceSelection _references;
    private readonly MeetingSpeech _speech;
    private readonly IMeetingTts _tts;
    private readonly Action<string> _copyText;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private bool _closed;
    private bool _busy;

    public MeetingWindow(AppConfig config, IMeetingCapture capture, MeetingSession session, IDisposable asrClient,
        MeetingIntelligence intelligence, Func<(int Used, int Limit)> usage,
        MeetingReferenceSelection references, MeetingSpeech speech, IMeetingTts tts, Action<string>? copyText = null)
    {
        _config = config;
        _capture = capture;
        _session = session;
        _asrClient = asrClient;
        _intelligence = intelligence;
        _usage = usage;
        _references = references;
        _speech = speech;
        _tts = tts;
        _copyText = copyText ?? System.Windows.Clipboard.SetText;
        InitializeComponent();
        InitializeAudioMonitor();
        ServiceChoice.SelectedIndex = config.MeetingAsrService switch { "ASR1" => 1, "Both" => 2, _ => 0 };
        AiLanguageChoice.SelectedValue = config.MeetingAiResponseLanguage;
        ApplyTheme();
        _config.PropertyChanged += ConfigChanged;
        _session.Changed += SessionChanged;
        _intelligence.Changed += SessionChanged;
        _references.Changed += SessionChanged;
        _references.ContextInvalidated += ReferenceContextInvalidated;
        _speech.Changed += SessionChanged;
        VoiceChoice.ItemsSource = new[] { new MeetingVoice("default", "Default") };
        VoiceChoice.SelectedIndex = config.MeetingVoice == "default" ? 0 : -1;
        if (VoiceChoice.SelectedIndex < 0) SpeechStatus.Text = "Refresh voices to validate your saved voice.";
        Loaded += (_, _) => { RefreshDevices(); RefreshState(); };
        Closing += OnClosing;
    }

    private void ApplyTheme()
    {
        var palette = AskPalette.For(_config.AskTheme);
        AskThemeApplier.ApplyResources(Resources, palette);
        ModernWpf.ThemeManager.SetRequestedTheme(this, palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
        Kil0bitSystemMonitor.Pad.PadThemeApplier.ApplyTitleBar(this, palette.IsDark);
    }

    private void ConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppConfig.AskTheme)) Dispatcher.BeginInvoke(ApplyTheme);
        if (e.PropertyName?.StartsWith("Ai", StringComparison.Ordinal) == true ||
            e.PropertyName == nameof(AppConfig.MeetingAiResponseLanguage))
        {
            _intelligence.RefreshConfiguration();
            ReferenceContextInvalidated();
            if (e.PropertyName == nameof(AppConfig.MeetingAiResponseLanguage) && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => AiLanguageChoice.SelectedValue = _config.MeetingAiResponseLanguage));
        }
        if (e.PropertyName is nameof(AppConfig.MeetingAsr1BaseUrl) or nameof(AppConfig.MeetingAsr2BaseUrl) or nameof(AppConfig.MeetingTtsBaseUrl))
        {
            if (e.PropertyName == nameof(AppConfig.MeetingTtsBaseUrl))
                CancelVoiceRefresh();
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.PropertyName == nameof(AppConfig.MeetingTtsBaseUrl)) ResetVoiceCatalog();
                RefreshState();
            }));
        }
    }

    private bool IsSelectedAsrConfigured()
    {
        bool asr2 = MeetingServiceEndpoints.TryNormalizeBaseUrl(_config.MeetingAsr2BaseUrl, out _);
        bool asr1 = MeetingServiceEndpoints.TryNormalizeBaseUrl(_config.MeetingAsr1BaseUrl, out _);
        return ServiceChoice.SelectedIndex switch { 1 => asr1, 2 => asr1 && asr2, _ => asr2 };
    }

    private bool IsTtsConfigured() => MeetingServiceEndpoints.TryNormalizeBaseUrl(_config.MeetingTtsBaseUrl, out _);

    private void RefreshServiceReadiness()
    {
        bool asrReady = IsSelectedAsrConfigured();
        bool ttsReady = IsTtsConfigured();
        string service = ServiceChoice.SelectedIndex switch
        {
            1 => "ASR1 · typhoon-asr-realtime · Thai",
            2 => "ASR2 + ASR1 · compare both readings",
            _ => "ASR2 · Qwen3-ASR · automatic language",
        };
        ServiceText.Text = $"{service} · {(asrReady ? "configured" : "setup required")}";
        DualAsrNotice.Visibility = ServiceChoice.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (asrReady && ttsReady)
        {
            ReadinessBanner.Visibility = Visibility.Collapsed;
            return;
        }
        ReadinessText.Text = !asrReady && ServiceChoice.SelectedIndex == 2
            ? "ASR2 + ASR1 requires both transcription endpoints. Configure both services or select a single service."
            : !asrReady && !ttsReady
            ? "Meeting services are not configured. Add a transcription endpoint to listen and a speech endpoint to speak."
            : !asrReady ? "The selected transcription service needs an endpoint before listening can start."
            : "Speech output needs an endpoint before voices can be loaded or text can be spoken.";
        ReadinessBanner.Visibility = Visibility.Visible;
    }

    private void OpenServiceSettings_Click(object sender, RoutedEventArgs e) => App.ShowSettingsSection("Meeting");

    internal async Task ApplyServiceSettingsAsync(Action apply)
    {
        if (_closing || _closed) throw new InvalidOperationException("Meeting Assistant is closing.");
        _busy = true;
        CancelVoiceRefresh();
        RefreshState();
        try
        {
            Task stop = StopForExitAsync();
            Task voices = WaitForVoiceRefreshAsync();
            await Task.WhenAll(stop, voices);
            apply();
            ResetVoiceCatalog();
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        try
        {
            var microphones = _capture.GetDevices(MeetingSource.Microphone);
            var outputs = _capture.GetDevices(MeetingSource.Output);
            MicrophoneChoice.ItemsSource = microphones;
            OutputChoice.ItemsSource = outputs;
            PlaybackChoice.ItemsSource = outputs;
            PlaybackChoice.SelectedItem = outputs.FirstOrDefault(x => x.Id == _config.MeetingPlaybackDeviceId)
                ?? (string.IsNullOrEmpty(_config.MeetingPlaybackDeviceId) ? outputs.FirstOrDefault(x => x.IsDefault) : null);
            MicrophoneChoice.SelectedItem = microphones.FirstOrDefault(x => x.Id == _config.MeetingMicrophoneId)
                ?? (string.IsNullOrEmpty(_config.MeetingMicrophoneId) ? microphones.FirstOrDefault(x => x.IsDefault) : null);
            OutputChoice.SelectedItem = outputs.FirstOrDefault(x => x.Id == _config.MeetingOutputId)
                ?? (string.IsNullOrEmpty(_config.MeetingOutputId) ? outputs.FirstOrDefault(x => x.IsDefault) : null);
        }
        catch { StatusText.Text = "Could not list audio devices. Connect your devices and refresh."; }
    }

    private void Service_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ServiceText == null) return;
        RefreshServiceReadiness();
        if (StartButton != null) RefreshState();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _speech.IsBusy) return;
        if (!IsSelectedAsrConfigured())
        {
            StatusText.Text = "Configure the selected transcription service before listening.";
            RefreshServiceReadiness();
            return;
        }
        if (MicrophoneChoice.SelectedItem is not MeetingDevice microphone || OutputChoice.SelectedItem is not MeetingDevice output)
        { StatusText.Text = "Choose an available microphone and output device."; return; }
        if (_session.Segments.Count > 0 && System.Windows.MessageBox.Show(this,
            "Start a new meeting? The current transcript stays in Saved transcripts. Use Save Markdown first if you also want to keep the current AI summary and answers.",
            "New meeting", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _config.MeetingMicrophoneId = microphone.Id;
        _config.MeetingOutputId = output.Id;
        _config.MeetingAsrService = ServiceChoice.SelectedIndex switch { 1 => "ASR1", 2 => "Both", _ => "ASR2" };
        _busy = true;
        RefreshState();
        try
        {
            if (_session.Generation > 0) _references.ResetForNewSession();
            await _session.StartAsync(new(microphone.Id, output.Id,
                ServiceChoice.SelectedIndex == 1 ? AsrService.Asr1 : AsrService.Asr2,
                CompareBothServices: ServiceChoice.SelectedIndex == 2));
            AudioSetup.IsExpanded = false;
        }
        catch { StatusText.Text = "Could not start listening. Check the selected devices."; }
        finally { _busy = false; RefreshState(); }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await StopForExitAsync();
        RefreshState();
    }

    private void SessionChanged()
    {
        if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshState);
    }

    private void RefreshState()
    {
        if (_closed) return;
        StatusText.Text = _session.Status;
        AutosaveText.Text = _session.AutosaveStatus;
        AutosaveText.ToolTip = _session.SavedTranscriptsFolder;
        SavedTranscriptsButton.IsEnabled = _session.SavedTranscriptsFolder != null;
        CaptureChoices.IsEnabled = !_session.IsActive && !_busy;
        StartButton.IsEnabled = !_session.IsActive && !_busy && !_speech.IsBusy && IsSelectedAsrConfigured();
        StopButton.IsEnabled = _session.IsActive || _busy;
        SessionStateText.Text = _session.State.ToString();
        var segments = _session.Segments;
        var gaps = _session.Gaps;
        SegmentCountText.Text = segments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var latest = segments.OrderBy(segment => segment.Start + segment.Duration).LastOrDefault();
        MeetingTimeText.Text = latest == null ? "—" : (latest.Start + latest.Duration).ToString(@"hh\:mm\:ss");
        ReferenceCountText.Text = _references.Selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RefreshServiceReadiness();
        TranscriptText.SetTranscript(segments, gaps);
        TranscriptEmpty.Visibility = segments.Count == 0 && gaps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TranscriptEmptyHint.Text = _session.State switch
        {
            MeetingState.Listening => "Listening now. Text appears after each audio chunk is transcribed.",
            MeetingState.Paused => "Listening is paused during speech playback.",
            MeetingState.Faulted => "Check the session status, then start again when ready.",
            _ => "Choose your devices and start listening.",
        };
        RefreshAudioMonitor();
        RefreshAnalysis();
        RefreshNotes();
        RefreshSpeech();
    }

    private void LatestTranscript_Click(object sender, RoutedEventArgs e) => TranscriptText.ScrollToEnd();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = $"meeting-{DateTime.Now:yyyyMMdd-HHmm}.md", AddExtension = true, DefaultExt = ".md" };
        if (picker.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(picker.FileName, _intelligence.ExportMarkdown(), new UTF8Encoding(false)); StatusText.Text = "Markdown saved."; }
        catch { StatusText.Text = "Could not save the report. Choose a writable location."; }
    }

    public Task StopForExitAsync() => Task.WhenAll(_session.StopAsync(), _speech.StopAsync());

    private void SavedTranscripts_Click(object sender, RoutedEventArgs e)
    {
        if (_session.SavedTranscriptsFolder is not { } folder) return;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { StatusText.Text = "Could not open Saved transcripts. Check folder access."; }
    }

    internal async Task<bool> SaveBeforeExitAsync()
    {
        try { await StopForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); }
        catch { /* Keep received text even if an audio driver cannot finish stopping. */ }
        if (await _session.FlushTranscriptAsync()) return true;
        RefreshState();
        StatusText.Text = "Transcript save did not finish. Keep this window open to retry, or use Save Markdown.";
        Activate();
        System.Windows.MessageBox.Show(this,
            "Your latest transcript could not be saved. The meeting is kept open so you can retry or use Save Markdown to choose another location.",
            "Transcript not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    internal Task<bool> FlushTranscriptAsync() => _session.FlushTranscriptAsync();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        if (!await SaveBeforeExitAsync())
        {
            _closing = false;
            IsEnabled = true;
            return;
        }
        _audioTimer.Stop();
        _audioTimer.Tick -= AudioTimerTick;
        IsEnabled = false;
        _session.Changed -= SessionChanged;
        _intelligence.Changed -= SessionChanged;
        _references.Changed -= SessionChanged;
        _references.ContextInvalidated -= ReferenceContextInvalidated;
        _speech.Changed -= SessionChanged;
        _lifetime.Cancel();
        _config.PropertyChanged -= ConfigChanged;
        try
        {
            await StopForExitAsync();
            await _speech.DisposeAsync();
            await _references.DisposeAsync();
            await _intelligence.DisposeAsync();
            await _session.DisposeAsync();
        }
        catch { /* Closing still releases the provider and window after an audio failure. */ }
        finally
        {
            _asrClient.Dispose();
            (_tts as IDisposable)?.Dispose();
            _lifetime.Dispose();
            _closed = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
