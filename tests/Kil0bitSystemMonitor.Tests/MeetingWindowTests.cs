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
            return Task.FromResult(chunk.Source == MeetingSource.Output
                ? "Could we move the rollout to Thursday?"
                : "Yes. I will confirm the readiness checks today.");
        }
        public Task<MeetingAnalysis> AnalyzeAsync(MeetingContext context, string? question, CancellationToken cancellationToken) { Requests++; return Task.FromResult(Analysis ?? throw new InvalidOperationException("No implicit requests expected")); }
        public void Emit(MeetingSource source, TimeSpan start) => _onChunk?.Invoke(new MeetingAudioChunk(source, start, TimeSpan.FromSeconds(4), new byte[] { 1 }));
    }
}
