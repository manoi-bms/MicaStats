using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Kil0bitSystemMonitor.Conference;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public class MeetingWindowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Dual_mode_requires_both_endpoints_and_shows_one_segment_with_both_readings(bool missingAsr1) => UiThread.RunAsync(async () =>
    {
        var config = Configured();
        config.MeetingAsrService = "Both";
        if (missingAsr1) config.MeetingAsr1BaseUrl = "";
        else config.MeetingAsr2BaseUrl = "";
        var window = CreateWindow(config, out var io, out var session, out _);
        io.Asr1Text = "Could we move the rollout to Friday?";
        try
        {
            Assert.Equal(2, window.ServiceChoice.SelectedIndex);
            Assert.False(window.StartButton.IsEnabled);
            Assert.Contains("both transcription endpoints", window.ReadinessText.Text);
            Assert.Equal(Visibility.Visible, window.DualAsrNotice.Visibility);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(0, io.Starts);

            window.ServiceChoice.SelectedIndex = missingAsr1 ? 0 : 1;
            Assert.True(window.StartButton.IsEnabled);
            window.ServiceChoice.SelectedIndex = 2;
            if (missingAsr1) config.MeetingAsr1BaseUrl = "https://asr1.example";
            else config.MeetingAsr2BaseUrl = "https://asr2.example";
            await WaitUntil(() => window.StartButton.IsEnabled);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            io.Emit(MeetingSource.Output, TimeSpan.Zero);
            await WaitUntil(() => window.TranscriptText.Text.Contains("ASR1 alternative:", StringComparison.Ordinal));

            var segment = Assert.Single(session.Segments);
            Assert.NotNull(segment.Comparison);
            Assert.Equal("Both", config.MeetingAsrService);
            Assert.Equal(new[] { AsrService.Asr2, AsrService.Asr1 }.Order(), io.AsrServices.Order());
            Assert.Contains("ASR2: Could we move the rollout to Thursday?", window.TranscriptText.Text);
            Assert.Contains("ASR1 alternative: Could we move the rollout to Friday?", window.TranscriptText.Text);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Theory]
    [InlineData("auto", "th")]
    [InlineData("th", "en")]
    [InlineData("en", "auto")]
    public Task Response_language_is_restored_and_changing_it_invalidates_answers_and_late_analysis(string initial, string next) => UiThread.RunAsync(async () =>
    {
        var config = Configured();
        config.MeetingAiResponseLanguage = initial;
        var window = CreateWindow(config, out var io, out var session, out var intelligence);
        io.Analysis = Answer("Old language answer");
        try
        {
            Assert.Equal(initial, window.AiLanguageChoice.SelectedValue);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await intelligence.AskAsync("What should I say?");
            await WaitUntil(() => window.UseForSpeechButton.IsEnabled);
            window.UseForSpeechButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("Old language answer", window.SpeechText.Text);
            io.PendingAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task pending = intelligence.AskAsync("Pending answer");

            window.AiLanguageChoice.SelectedValue = next;
            Assert.Equal(next, config.MeetingAiResponseLanguage);
            Assert.Null(intelligence.Analysis);
            io.PendingAnalysis.SetResult(Answer("Obsolete language result"));
            await pending;
            await WaitUntil(() => window.AnswerText.Text == "" && window.SpeechText.Text == "");

            Assert.Null(intelligence.Analysis);
            Assert.Equal(MeetingState.Listening, session.State);
            Assert.Equal(1, io.Starts);
            Assert.Equal(0, io.Playbacks);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Replacing_the_selected_answer_cancels_its_speech_and_clears_the_prepared_text() => UiThread.RunAsync(async () =>
    {
        var io = new Boundary { PendingSpeech = new(TaskCreationOptions.RunContinuationsAsynchronously), Analysis = Answer("First answer") };
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(Configured(), io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await intelligence.AskAsync("What should I say?");
            await WaitUntil(() => window.UseForSpeechButton.IsEnabled);
            window.UseForSpeechButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.SpeakButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(speech.IsBusy);
            Assert.Equal("First answer", window.SpeechText.Text);

            io.Analysis = Answer("Updated answer");
            await intelligence.AskAsync("What changed?");
            await WaitUntil(() => !speech.IsBusy && window.SpeechText.Text == "");
            Assert.Equal(0, io.Playbacks);
            Assert.Contains("Updated answer", window.AnswerText.Text);
        }
        finally { window.Close(); }
    });

    private static MeetingAnalysis Answer(string text) => new("Summary", Array.Empty<MeetingPoint>(),
        new[] { new MeetingQuestion("Question", text, "", new[] { "segment-1" }) });

    [Fact]
    public Task Standalone_speech_blocks_start_and_credential_notification_clears_it() => UiThread.RunAsync(async () =>
    {
        var io = new Boundary { PendingSpeech = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(Configured(), io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SpeechText.Text = "Read this explicitly chosen text.";
            window.SpeakButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => !window.StartButton.IsEnabled);
            Assert.True(speech.IsBusy);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(0, io.Starts);
            Assert.Equal(1, io.Requests);

            window.CredentialsStored();
            await WaitUntil(() => !speech.IsBusy && window.SpeechText.Text == "");
            Assert.Equal(0, io.Playbacks);
            Assert.True(window.StartButton.IsEnabled);
        }
        finally { window.Close(); }
    });

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public Task Opening_requires_explicit_start_and_close_releases_capture() => UiThread.RunAsync(async () =>
    {
        var io = new Boundary();
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(Configured(), io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(0, io.Starts);
            Assert.Equal(0, io.Requests);
            Assert.Equal("mic", ((MeetingDevice)window.MicrophoneChoice.SelectedItem).Id);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, io.Starts);
            Assert.Equal(MeetingState.Listening, session.State);
            Assert.False(window.StartButton.IsEnabled);
            window.Close();
            await Task.Yield();
            Assert.True(io.Disposed);
            Assert.False(session.IsActive);
            Assert.Equal(0, io.Requests);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Unconfigured_services_disable_and_guard_capture_speech_and_voice_requests() => UiThread.RunAsync(async () =>
    {
        var io = new Boundary();
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(new AppConfig(), io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.False(window.StartButton.IsEnabled);
            Assert.False(window.SpeakButton.IsEnabled);
            Assert.False(window.RefreshVoicesButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.ReadinessBanner.Visibility);

            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.SpeechText.Text = "Do not send this.";
            window.SpeakButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.RefreshVoicesButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Yield();

            Assert.Equal(0, io.Starts);
            Assert.Equal(0, io.Requests);
            Assert.Equal(0, io.Playbacks);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Applying_service_settings_stops_session_and_speech_before_mutating_configuration() => UiThread.RunAsync(async () =>
    {
        var config = Configured();
        var io = new Boundary { PendingSpeech = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(config, io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.SpeechText.Text = "Pending speech.";
            window.SpeakButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => session.IsActive && speech.IsBusy);

            bool stoppedBeforeApply = false;
            await window.ApplyServiceSettingsAsync(() =>
            {
                stoppedBeforeApply = !session.IsActive && !speech.IsBusy;
                config.MeetingAsr2BaseUrl = "https://new-asr.example";
                config.MeetingTtsBaseUrl = "https://new-tts.example";
            });

            Assert.True(stoppedBeforeApply);
            Assert.False(session.IsActive);
            Assert.False(speech.IsBusy);
            Assert.Equal(0, io.Playbacks);
            Assert.True(window.StartButton.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Applying_service_settings_waits_for_the_old_voice_catalog_request() => UiThread.RunAsync(async () =>
    {
        var config = Configured();
        var io = new Boundary { PendingVoices = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(config, io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.RefreshVoicesButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => io.Requests == 1);

            bool applied = false;
            Task applying = window.ApplyServiceSettingsAsync(() =>
            {
                applied = true;
                config.MeetingTtsBaseUrl = "https://replacement.example";
            });
            await Task.Yield();
            Assert.False(applied);
            Assert.False(session.IsActive);
            window.SpeechText.Text = "Must remain blocked during settings apply.";
            window.SpeakButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.RefreshVoicesButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, io.Requests);

            io.PendingVoices.SetResult(new[] { new MeetingVoice("old", "Old service voice") });
            await applying;

            Assert.True(applied);
            Assert.Equal("default", ((MeetingVoice)window.VoiceChoice.SelectedItem).Id);
            Assert.DoesNotContain(window.VoiceChoice.Items.Cast<MeetingVoice>(), voice => voice.Id == "old");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Voice_catalog_status_survives_unrelated_session_refreshes() => UiThread.RunAsync(async () =>
    {
        var io = new Boundary { PendingVoices = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new MeetingSession(io, io);
        var intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(Configured(), io, session, io, intelligence, () => (0, 100), references, speech, io);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.RefreshVoicesButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => window.SpeechStatus.Text == "Loading available voices…");

            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(20);
            Assert.Equal("Loading available voices…", window.SpeechStatus.Text);

            io.PendingVoices.SetResult(new[] { new MeetingVoice("default", "Default") });
            await WaitUntil(() => window.SpeechStatus.Text == "Voices refreshed.");
            io.Emit(MeetingSource.Output, TimeSpan.Zero);
            await WaitUntil(() => session.Segments.Count == 1);
            await Task.Delay(20);
            Assert.Equal("Voices refreshed.", window.SpeechStatus.Text);
        }
        finally { window.Close(); }
    });

    private static AppConfig Configured() => new()
    {
        MeetingAsr1BaseUrl = "https://asr1.example",
        MeetingAsr2BaseUrl = "https://asr2.example",
        MeetingTtsBaseUrl = "https://tts.example",
    };

    [Fact]
    public Task Dual_transcript_and_language_selector_render_at_minimum_width() => UiThread.RunAsync(async () =>
    {
        string output = Path.Combine(FindRepoRoot(), "artifacts", "meeting-ui");
        Directory.CreateDirectory(output);
        var config = Configured();
        config.MeetingAsrService = "Both";
        config.MeetingAiResponseLanguage = "th";
        config.AskTheme = "Dark";
        var window = CreateWindow(config, out var io, out var session, out var intelligence);
        io.Asr2Text = "กำหนดส่งงานวันพฤหัสบดี";
        io.Asr1Text = "กำหนดส่งงานวันศุกร์";
        try
        {
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            io.Emit(MeetingSource.Output, TimeSpan.Zero);
            await WaitUntil(() => window.TranscriptText.Text.Contains("ASR1 alternative:", StringComparison.Ordinal));
            RenderWindow(window, 780, 600, Path.Combine(output, "meeting-dual-transcript-780x600.png"));
            string source = Assert.Single(session.Segments).Id;
            io.Analysis = new MeetingAnalysis("กำหนดส่งงานยังไม่ชัดเจน: ผลถอดเสียงสองบริการระบุวันต่างกัน", [],
                [new MeetingQuestion("กำหนดส่งเมื่อไร?", "ควรยืนยันกำหนดส่งอีกครั้ง", "วันพฤหัสบดีหรือวันศุกร์", [source])]);
            await intelligence.AskAsync("กำหนดส่งเมื่อไร?");
            await WaitUntil(() => window.AnswerText.Text.Contains("ยืนยัน", StringComparison.Ordinal));
            window.MeetingTabs.SelectedIndex = 1;
            RenderWindow(window, 780, 600, Path.Combine(output, "meeting-ai-language-780x600.png"));
            Assert.True(window.AiLanguageChoice.IsVisible);
            Assert.Equal("th", window.AiLanguageChoice.SelectedValue);
            Assert.True(window.SummaryText.ActualHeight >= 40);
            Assert.True(window.AnswerText.ActualHeight >= 40);
            Assert.True(window.AnalysisScroll.ScrollableHeight > 0);
            window.AnalysisScroll.ScrollToBottom();
            RenderWindow(window, 780, 600, Path.Combine(output, "meeting-ai-language-scrolled-780x600.png"));
            window.AnalysisScroll.ScrollToTop();
            RenderWindow(window, 1180, 820, Path.Combine(output, "meeting-ai-language-1180x820.png"));
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Meeting_dashboard_renders_normal_and_minimum_hardware_free_fixtures() => UiThread.RunAsync(async () =>
    {
        string output = Path.Combine(FindRepoRoot(), "artifacts", "meeting-ui");
        Directory.CreateDirectory(output);

        var unconfigured = CreateWindow(new AppConfig(), out _, out _, out _);
        try
        {
            RenderWindow(unconfigured, 1180, 820, Path.Combine(output, "meeting-unconfigured-1180x820.png"));
        }
        finally { unconfigured.Close(); await Task.Yield(); }

        var populatedConfig = Configured();
        populatedConfig.AskTheme = "Light";
        var populated = CreateWindow(populatedConfig, out var io, out var session, out var intelligence);
        try
        {
            populated.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            io.Emit(MeetingSource.Output, TimeSpan.Zero);
            io.Emit(MeetingSource.Microphone, TimeSpan.FromSeconds(4));
            await WaitUntil(() => session.Segments.Count == 2 && populated.TranscriptText.Text.Contains("Thursday", StringComparison.Ordinal));
            RenderWindow(populated, 780, 600, Path.Combine(output, "meeting-populated-light-780x600.png"));

            string rolloutSource = session.Segments.First(segment => segment.Source == MeetingSource.Output).Id;
            io.Analysis = new MeetingAnalysis("The team proposed moving the rollout to Thursday and will confirm readiness checks today.",
                new[] { new MeetingPoint("Thursday is the proposed rollout day.", new[] { rolloutSource }) },
                new[] { new MeetingQuestion("When is the rollout?", "The proposed rollout is Thursday.", "", new[] { rolloutSource }) });
            await intelligence.AskAsync("When is the rollout?");
            await WaitUntil(() => populated.UseForSpeechButton.IsEnabled);
            populated.MeetingTabs.SelectedIndex = 1;
            RenderWindow(populated, 1180, 820, Path.Combine(output, "meeting-summary-populated-light-1180x820.png"));

            populated.UseForSpeechButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            RenderWindow(populated, 1180, 820, Path.Combine(output, "meeting-speech-prepared-light-1180x820.png"));
        }
        finally { populated.Close(); await Task.Yield(); }
    });

    private static MeetingWindow CreateWindow(AppConfig config, out Boundary io, out MeetingSession session,
        out MeetingIntelligence intelligence)
    {
        io = new Boundary();
        session = new MeetingSession(io, io);
        intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var window = new MeetingWindow(config, io, session, io, intelligence, () => (2, 100), references, speech, io)
        {
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
        };
        window.Show();
        return window;
    }

    private static void RenderWindow(MeetingWindow window, int width, int height, string path)
    {
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var image = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Kil0bitSystemMonitor.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class Boundary : IMeetingCapture, IMeetingAsr, IMeetingAnalyzer, IMeetingNotes, IMeetingTts, IMeetingPlayback, IDisposable
    {
        public int Starts, Requests, Playbacks;
        public TaskCompletionSource<byte[]>? PendingSpeech;
        public TaskCompletionSource<IReadOnlyList<MeetingVoice>>? PendingVoices;
        public TaskCompletionSource<MeetingAnalysis>? PendingAnalysis;
        public string? Asr1Text;
        public string? Asr2Text;
        public System.Collections.Concurrent.ConcurrentQueue<AsrService> AsrServices = new();
        public MeetingAnalysis? Analysis;
        private Action<MeetingAudioChunk>? _onChunk;
        public bool Disposed;
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => new[] { new MeetingDevice(source == MeetingSource.Microphone ? "mic" : "out", "Test device", true) };
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk, Action<string> onFault, CancellationToken cancellationToken) { Starts++; _onChunk = onChunk; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null) { onFirstSourceStopped?.Invoke(TimeSpan.Zero); return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Dispose() => Disposed = true;
        public Task<IReadOnlyList<MeetingNoteChoice>> ListAsync(CancellationToken cancellationToken) { Requests++; return Task.FromResult<IReadOnlyList<MeetingNoteChoice>>(Array.Empty<MeetingNoteChoice>()); }
        public Task<MeetingReference?> ReadAsync(string id, CancellationToken cancellationToken) { Requests++; return Task.FromResult<MeetingReference?>(null); }
        public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken) { Requests++; return PendingSpeech?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Array.Empty<byte>()); }
        public Task<IReadOnlyList<MeetingVoice>> GetVoicesAsync(CancellationToken cancellationToken) { Requests++; return PendingVoices?.Task ?? Task.FromResult<IReadOnlyList<MeetingVoice>>(Array.Empty<MeetingVoice>()); }
        public Task PlayAsync(byte[] wav, string deviceId, CancellationToken cancellationToken) { Playbacks++; return Task.CompletedTask; }
        public Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken)
        {
            Requests++;
            AsrServices.Enqueue(service);
            if (service == AsrService.Asr1 && Asr1Text != null) return Task.FromResult(Asr1Text);
            if (service == AsrService.Asr2 && Asr2Text != null) return Task.FromResult(Asr2Text);
            return Task.FromResult(chunk.Source == MeetingSource.Output
                ? "Could we move the rollout to Thursday?"
                : "Yes. I will confirm the readiness checks today.");
        }
        public Task<MeetingAnalysis> AnalyzeAsync(MeetingContext context, string? question, CancellationToken cancellationToken) { Requests++; return PendingAnalysis?.Task ?? Task.FromResult(Analysis ?? throw new InvalidOperationException("No implicit requests expected")); }
        public void Emit(MeetingSource source, TimeSpan start) => _onChunk?.Invoke(new MeetingAudioChunk(source, start, TimeSpan.FromSeconds(4), new byte[] { 1 }));
    }
}
