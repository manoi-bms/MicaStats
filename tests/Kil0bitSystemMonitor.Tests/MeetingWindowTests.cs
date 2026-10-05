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
    public Task Closing_and_reopening_restores_autosaved_transcript_without_starting_any_service() => UiThread.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "meeting-window-save-" + Guid.NewGuid().ToString("N"));
        var store = new MeetingTranscriptStore(root);
        MeetingWindow? window = null;
        try
        {
            window = CreateWindow(Configured(), out var io, out var session, out _, store);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => session.IsActive && io.Starts == 1);
            io.Asr2Text = "บันทึกข้อความการประชุมอัตโนมัติ";
            io.Emit(MeetingSource.Output, TimeSpan.FromSeconds(4));
            await WaitUntil(() => session.Segments.Count == 1);
            window.Close();
            await WaitUntil(() => io.Disposed);
            window = CreateWindow(Configured(), out var restoredIo, out var restored, out _, store);
            Assert.Equal("บันทึกข้อความการประชุมอัตโนมัติ", Assert.Single(restored.Segments).Text);
            Assert.Contains("บันทึกข้อความการประชุมอัตโนมัติ", window.TranscriptText.Text);
            Assert.Contains("saved locally", window.AutosaveText.Text);
            Assert.True(window.SavedTranscriptsButton.IsEnabled);
            Assert.Equal(root, window.AutosaveText.ToolTip);
            Assert.Contains("Restored", window.StatusText.Text);
            Assert.False(restored.IsActive);
            Assert.Equal(0, restoredIo.Starts);
            Assert.Equal(0, restoredIo.Requests);
            var images = Path.Combine(FindRepoRoot(), "artifacts", "meeting-ui");
            Directory.CreateDirectory(images);
            RenderWindow(window, 780, 600, Path.Combine(images, "meeting-autosave-restored-780x600.png"));
            window.Close();
            await WaitUntil(() => restoredIo.Disposed);
            window = null;
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public Task Question_buttons_validate_show_progress_copy_context_and_prepare_speech() => UiThread.RunAsync(async () =>
    {
        var window = CreateWindow(Configured(), out var io, out _, out _);
        try
        {
            window.MeetingTabs.SelectedIndex = 1;
            Assert.False(window.AskButton.IsEnabled);
            Assert.Contains("Start listening", window.QuestionStatus.Text);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.AskButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Contains("Type a question", window.QuestionStatus.Text);
            Assert.Equal(0, io.Requests);
            io.PendingAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.QuestionText.Text = "What should I say?";
            window.QuestionText.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.False(window.AskButton.IsEnabled);
            Assert.True(window.QuestionText.IsReadOnly);
            Assert.Contains("Preparing", window.QuestionStatus.Text);
            window.AskButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, io.Requests);
            io.PendingAnalysis.SetResult(Answer("Confirm the date with the team."));
            await WaitUntil(() => window.AskButton.IsEnabled && window.CopyButton.IsEnabled);
            window.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(window.AnswerText.Text, io.CopiedText);
            Assert.Contains("Sources:", io.CopiedText);
            Assert.Contains("copied", window.AnswerActionStatus.Text);
            io.FailCopy = true;
            window.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Contains("Could not copy", window.AnswerActionStatus.Text);
            window.UseForSpeechButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(window.SpeechTab, window.MeetingTabs.SelectedItem);
            Assert.Equal("Confirm the date with the team.", window.SpeechText.Text);
            Assert.Contains("Press Speak", window.SpeechStatus.Text);
            Assert.Equal(0, io.Playbacks);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Refreshed_analysis_preserves_selected_question_and_explains_missing_answers() => UiThread.RunAsync(async () =>
    {
        var window = CreateWindow(Configured(), out var io, out _, out var intelligence);
        try
        {
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            io.Analysis = new("Summary", [], [new("First?", "First answer", "", []), new("Second?", "Second answer", "", [])]);
            await intelligence.AskAsync("Questions?");
            await WaitUntil(() => window.QuestionChoice.Items.Count == 2);
            window.QuestionChoice.SelectedIndex = 1;
            io.Analysis = new("Updated summary", [], [new("First?", "New first", "", []), new("Second?", "Updated second", "", [])]);
            await intelligence.AskAsync("Update?");
            await WaitUntil(() => window.SummaryText.Text.Contains("Updated summary", StringComparison.Ordinal));
            Assert.Contains("Updated second", window.AnswerText.Text);
            Assert.Equal(1, window.QuestionChoice.SelectedIndex);
            io.Analysis = new("No detected questions yet", [], []);
            await intelligence.AskAsync("Update?");
            await WaitUntil(() => window.QuestionChoice.Items.Count == 0);
            Assert.False(window.CopyButton.IsEnabled);
            Assert.False(window.UseForSpeechButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.AnswerEmpty.Visibility);
            Assert.Contains("Ask a question", window.AnswerActionStatus.Text);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Transcript_keeps_reading_position_selection_and_live_follow_across_refreshes() => UiThread.RunAsync(async () =>
    {
        var window = CreateWindow(Configured(), out _, out _, out _);
        try
        {
            window.Width = 780;
            window.Height = 600;
            var rows = Enumerable.Range(0, 40).Select(i => new MeetingSegment($"private-source-{i}",
                i % 2 == 0 ? MeetingSource.Microphone : MeetingSource.Output, TimeSpan.FromSeconds(i * 4), TimeSpan.FromSeconds(4),
                $"Conversation {i}: กำหนดส่งงานวันพฤหัสบดี Please confirm the readiness checks with the team.")).ToList();
            var view = window.TranscriptText;
            view.SetTranscript(rows, []);
            window.UpdateLayout();
            await Task.Delay(30);
            Assert.True(view.ExtentHeight > view.ViewportHeight * 2);
            Assert.InRange(view.ExtentHeight - view.ViewportHeight - view.VerticalOffset, -1, 3);
            Assert.DoesNotContain("private-source", view.Text);
            Assert.Contains("Microphone", view.Text);
            Assert.Contains("Conference audio", view.Text);
            view.ScrollToVerticalOffset(view.ExtentHeight / 3);
            window.UpdateLayout();
            double offset = view.VerticalOffset;
            var first = view.Document.Blocks.FirstBlock;
            view.Selection.Select(first.ContentStart, first.ContentEnd);
            string selected = view.Selection.Text;
            rows.Add(new("new-source", MeetingSource.Output, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(4), "A new update."));
            view.SetTranscript(rows, []);
            window.UpdateLayout();
            await Task.Delay(30);
            Assert.InRange(Math.Abs(view.VerticalOffset - offset), 0, 2);
            Assert.Equal(selected, view.Selection.Text);
            view.Selection.Select(view.Document.ContentStart, view.Document.ContentStart);
            view.ScrollToEnd();
            window.UpdateLayout();
            rows.Add(new("late-source", MeetingSource.Microphone, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Late recognition result."));
            view.SetTranscript(rows, []);
            window.UpdateLayout();
            await Task.Delay(30);
            Assert.InRange(view.ExtentHeight - view.ViewportHeight - view.VerticalOffset, -1, 3);
            Assert.True(view.Text.IndexOf("Late recognition", StringComparison.Ordinal) < view.Text.IndexOf("Conversation 1:", StringComparison.Ordinal));
            double latest = view.VerticalOffset;
            view.SetTranscript(rows, []);
            window.UpdateLayout();
            Assert.Equal(latest, view.VerticalOffset);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Hiding_and_closing_stop_display_polling_without_stopping_a_hidden_meeting() => UiThread.RunAsync(async () =>
    {
        var window = CreateWindow(Configured(), out var io, out var session, out _);
        try
        {
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Yield();
            Assert.Equal("Waiting for audio", window.MicrophoneAudioStatus.Text);
            Assert.Equal("— dBFS", window.MicrophoneLevel.Text);
            int initial = io.SnapshotReads;
            await WaitUntil(() => io.SnapshotReads > initial);
            window.Hide();
            await Task.Yield();
            int hidden = io.SnapshotReads;
            await Task.Delay(300);
            Assert.Equal(hidden, io.SnapshotReads);
            Assert.Equal(MeetingState.Listening, session.State);
            window.Show();
            await WaitUntil(() => io.SnapshotReads > hidden);
            window.Close();
            await WaitUntil(() => io.Disposed);
            int closed = io.SnapshotReads;
            await Task.Delay(300);
            Assert.Equal(closed, io.SnapshotReads);
            Assert.False(session.IsActive);
            Assert.Equal(0, io.Requests);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Audio_cards_show_independent_real_levels_and_clear_during_pause_stop_and_fault() => UiThread.RunAsync(async () =>
    {
        var window = CreateWindow(Configured(), out var io, out var session, out _);
        try
        {
            io.MicrophoneAudio = new([0.1f, 0.5f], 0.5f, TimeSpan.Zero);
            io.OutputAudio = new([1f], 1f, TimeSpan.Zero);
            window.RefreshAudioMonitor();
            Assert.False(window.MicrophoneWaveform.HasSignal);
            Assert.Equal(0, io.SnapshotReads);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.RefreshAudioMonitor();
            Assert.False(window.AudioSetup.IsExpanded);
            Assert.True(window.MicrophoneWaveform.HasSignal);
            Assert.Equal("-6 dBFS", window.MicrophoneLevel.Text);
            Assert.Contains("Sound detected", window.MicrophoneAudioStatus.Text);
            Assert.Contains("Near clipping", window.OutputAudioStatus.Text);
            Assert.Equal("0 dBFS", window.OutputLevel.Text);

            await session.PauseAsync();
            window.RefreshAudioMonitor();
            Assert.False(window.MicrophoneWaveform.HasSignal);
            Assert.False(window.OutputWaveform.HasSignal);
            Assert.Contains("Paused", window.MicrophoneAudioStatus.Text);
            Assert.Equal("— dBFS", window.MicrophoneLevel.Text);
            await session.ResumeAsync();
            io.MicrophoneAudio = new([], 0, TimeSpan.FromSeconds(4));
            io.OutputAudio = new([], 0, TimeSpan.Zero);
            window.RefreshAudioMonitor();
            Assert.Equal("No recent audio", window.MicrophoneAudioStatus.Text);
            Assert.Equal("Quiet", window.OutputAudioStatus.Text);
            Assert.False(window.MicrophoneWaveform.HasSignal);
            Assert.Equal("−∞ dBFS", window.OutputLevel.Text);

            await session.StopAsync();
            window.RefreshAudioMonitor();
            Assert.Equal("Not listening", window.MicrophoneAudioStatus.Text);
            await session.StartAsync(new("mic", "out"));
            io.FailCapture();
            await WaitUntil(() => session.State == MeetingState.Faulted);
            window.RefreshAudioMonitor();
            Assert.Contains("Capture stopped", window.OutputAudioStatus.Text);
            Assert.False(window.OutputWaveform.HasSignal);
            Assert.Equal(0, io.Requests);
        }
        finally { window.Close(); await Task.Yield(); }
    });

    [Fact]
    public Task Live_waveforms_render_both_themes_and_compact_pause_without_capturing_audio() => UiThread.RunAsync(async () =>
    {
        string output = Path.Combine(FindRepoRoot(), "artifacts", "meeting-ui");
        Directory.CreateDirectory(output);
        var config = Configured();
        config.AskTheme = "Dark";
        var window = CreateWindow(config, out var io, out var session, out _);
        try
        {
            Assert.Equal(Visibility.Visible, window.TranscriptEmpty.Visibility);
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // Fixed synthetic capture samples only; no device or HTTP boundary is contacted.
            io.MicrophoneAudio = new(Enumerable.Range(0, 60).Select(i => i % 13 < 4 ? 0f : (float)(0.08 + 0.48 * Math.Abs(Math.Sin(i * 0.7)))).ToArray(), 0.48f, TimeSpan.Zero);
            io.OutputAudio = new(Enumerable.Range(0, 60).Select(i => i < 20 || i > 48 ? 0f : (float)(0.03 + 0.25 * Math.Abs(Math.Cos(i * 0.9)))).ToArray(), 0.23f, TimeSpan.Zero);
            window.RefreshAudioMonitor();
            io.Emit(MeetingSource.Output, TimeSpan.Zero);
            await WaitUntil(() => window.TranscriptText.Text.Contains("Thursday", StringComparison.Ordinal));
            Assert.Equal(Visibility.Collapsed, window.TranscriptEmpty.Visibility);
            RenderWindow(window, 1180, 820, Path.Combine(output, "meeting-waveforms-dark-1180x820.png"));
            RenderWindow(window, 780, 600, Path.Combine(output, "meeting-waveforms-dark-780x600.png"));
            Assert.True(window.TranscriptText.ActualHeight > 80);
            Assert.True(window.MicrophoneWaveform.ActualWidth > 250);
            Assert.True(window.StopButton.IsEnabled);
            config.AskTheme = "Light";
            await Task.Yield();
            RenderWindow(window, 780, 600, Path.Combine(output, "meeting-waveforms-light-780x600.png"));
            await session.PauseAsync();
            await WaitUntil(() => window.SessionStateText.Text == "Paused");
            window.RefreshAudioMonitor();
            RenderWindow(window, 780, 600, Path.Combine(output, "meeting-waveforms-paused-780x600.png"));
            Assert.False(window.OutputWaveform.HasSignal);
        }
        finally { window.Close(); await Task.Yield(); }
    });

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
            RenderWindow(unconfigured, 780, 600, Path.Combine(output, "meeting-unconfigured-780x600.png"));
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
            Assert.InRange(populated.AskButton.TranslatePoint(new System.Windows.Point(0, populated.AskButton.ActualHeight), populated.AnalysisScroll).Y,
                0, populated.AnalysisScroll.ActualHeight);

            populated.UseForSpeechButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            RenderWindow(populated, 1180, 820, Path.Combine(output, "meeting-speech-prepared-light-1180x820.png"));
        }
        finally { populated.Close(); await Task.Yield(); }
    });

    private static MeetingWindow CreateWindow(AppConfig config, out Boundary io, out MeetingSession session,
        out MeetingIntelligence intelligence, MeetingTranscriptStore? store = null)
    {
        io = new Boundary();
        session = new MeetingSession(io, io, transcriptStore: store);
        intelligence = new MeetingIntelligence(session, io);
        var references = new MeetingReferenceSelection(io, intelligence);
        var speech = new MeetingSpeech(session, io, io);
        var boundary = io;
        var window = new MeetingWindow(config, io, session, io, intelligence, () => (2, 100), references, speech, io,
            text => { if (boundary.FailCopy) throw new InvalidOperationException("Synthetic clipboard failure"); boundary.CopiedText = text; })
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

    private sealed class Boundary : IMeetingCapture, IMeetingAudioMonitor, IMeetingAsr, IMeetingAnalyzer, IMeetingNotes, IMeetingTts, IMeetingPlayback, IDisposable
    {
        public int Starts, Requests, Playbacks;
        public string? CopiedText;
        public bool FailCopy;
        public TaskCompletionSource<byte[]>? PendingSpeech;
        public TaskCompletionSource<IReadOnlyList<MeetingVoice>>? PendingVoices;
        public TaskCompletionSource<MeetingAnalysis>? PendingAnalysis;
        public string? Asr1Text;
        public string? Asr2Text;
        public System.Collections.Concurrent.ConcurrentQueue<AsrService> AsrServices = new();
        public MeetingAnalysis? Analysis;
        private Action<MeetingAudioChunk>? _onChunk;
        private Action<string>? _onFault;
        public MeetingAudioSnapshot MicrophoneAudio = new([], 0, null);
        public MeetingAudioSnapshot OutputAudio = new([], 0, null);
        public int SnapshotReads;
        public MeetingAudioSnapshot GetAudioSnapshot(MeetingSource source) { SnapshotReads++; return source == MeetingSource.Microphone ? MicrophoneAudio : OutputAudio; }
        public void FailCapture() => _onFault?.Invoke("Synthetic device failure");
        public bool Disposed;
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => new[] { new MeetingDevice(source == MeetingSource.Microphone ? "mic" : "out", "Test device", true) };
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk, Action<string> onFault, CancellationToken cancellationToken) { Starts++; _onChunk = onChunk; _onFault = onFault; return Task.CompletedTask; }
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
