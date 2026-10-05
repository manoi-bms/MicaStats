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
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private bool _closed;
    private bool _busy;

    public MeetingWindow(AppConfig config, IMeetingCapture capture, MeetingSession session, IDisposable asrClient,
        MeetingIntelligence intelligence, Func<(int Used, int Limit)> usage,
        MeetingReferenceSelection references, MeetingSpeech speech, IMeetingTts tts)
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
        InitializeComponent();
        ServiceChoice.SelectedIndex = config.MeetingAsrService == "ASR1" ? 1 : 0;
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
        if (e.PropertyName?.StartsWith("Ai", StringComparison.Ordinal) == true)
        {
            _intelligence.RefreshConfiguration();
            ReferenceContextInvalidated();
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

    private bool IsSelectedAsrConfigured() => MeetingServiceEndpoints.TryNormalizeBaseUrl(
        ServiceChoice.SelectedIndex == 1 ? _config.MeetingAsr1BaseUrl : _config.MeetingAsr2BaseUrl, out _);

    private bool IsTtsConfigured() => MeetingServiceEndpoints.TryNormalizeBaseUrl(_config.MeetingTtsBaseUrl, out _);

    private void RefreshServiceReadiness()
    {
        bool asrReady = IsSelectedAsrConfigured();
        bool ttsReady = IsTtsConfigured();
        ServiceText.Text = $"{(ServiceChoice.SelectedIndex == 1 ? "ASR1 · typhoon-asr-realtime · Thai" : "ASR2 · Qwen3-ASR · automatic language")} · {(asrReady ? "configured" : "setup required")}";
        if (asrReady && ttsReady)
        {
            ReadinessBanner.Visibility = Visibility.Collapsed;
            return;
        }
        ReadinessText.Text = !asrReady && !ttsReady
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
            "Starting a new session clears the current transcript. Save it first if you want to keep it. Start a new session?",
            "New meeting", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _config.MeetingMicrophoneId = microphone.Id;
        _config.MeetingOutputId = output.Id;
        _config.MeetingAsrService = ServiceChoice.SelectedIndex == 1 ? "ASR1" : "ASR2";
        _busy = true;
        RefreshState();
        try
        {
            if (_session.Generation > 0) _references.ResetForNewSession();
            await _session.StartAsync(new(microphone.Id, output.Id, ServiceChoice.SelectedIndex == 1 ? AsrService.Asr1 : AsrService.Asr2));
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
        CaptureChoices.IsEnabled = !_session.IsActive && !_busy;
        StartButton.IsEnabled = !_session.IsActive && !_busy && !_speech.IsBusy && IsSelectedAsrConfigured();
        StopButton.IsEnabled = _session.IsActive || _busy;
        SessionStateText.Text = _session.State.ToString();
        SegmentCountText.Text = _session.Segments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var latest = _session.Segments.OrderBy(segment => segment.Start + segment.Duration).LastOrDefault();
        MeetingTimeText.Text = latest == null ? "—" : (latest.Start + latest.Duration).ToString(@"hh\:mm\:ss");
        ReferenceCountText.Text = _references.Selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RefreshServiceReadiness();
        var text = new StringBuilder();
        var rows = _session.Segments.Select(segment => (Time: segment.Start,
                Text: $"[{segment.Start:hh\\:mm\\:ss}] {segment.Source} · {segment.Id}\n{segment.Text}\n"))
            .Concat(_session.Gaps.Select(gap => (Time: gap.Start,
                Text: $"[Listening gap {gap.Start:hh\\:mm\\:ss} – {(gap.End is { } end ? end.ToString(@"hh\:mm\:ss") : "ongoing")}]\n")));
        foreach (var row in rows.OrderBy(row => row.Time)) text.AppendLine(row.Text);
        string content = text.ToString();
        if (TranscriptText.Text != content) { TranscriptText.Text = content; TranscriptText.ScrollToEnd(); }
        RefreshAnalysis();
        RefreshNotes();
        RefreshSpeech();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = $"meeting-{DateTime.Now:yyyyMMdd-HHmm}.md", AddExtension = true, DefaultExt = ".md" };
        if (picker.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(picker.FileName, _intelligence.ExportMarkdown(), new UTF8Encoding(false)); StatusText.Text = "Markdown saved."; }
        catch { StatusText.Text = "Could not save the report. Choose a writable location."; }
    }

    public Task StopForExitAsync() => Task.WhenAll(_session.StopAsync(), _speech.StopAsync());

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
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
