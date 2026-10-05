using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingSpeechTests
{
    [Fact]
    public async Task Active_meeting_keeps_listening_during_synthesis_then_pauses_plays_and_resumes()
    {
        var capture = new SpeechCapture();
        await using var session = NewSession(capture);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var tts = new ControlledTts();
        var playback = new ControlledPlayback(completeImmediately: true);
        var clock = new ImmediateTimeProvider();
        await using var speech = new MeetingSpeech(session, tts, playback, clock);

        var speaking = speech.SpeakAsync("Please review the plan.", "default", "render-1", ["note-1"]);
        await tts.Entered.Task;
        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Equal(0, capture.PauseCalls);

        tts.Complete([1, 2, 3]);
        await speaking;

        Assert.Equal(1, capture.PauseCalls);
        Assert.Equal(1, capture.ResumeCalls);
        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Equal("render-1", playback.DeviceId);
        Assert.Equal(new byte[] { 1, 2, 3 }, playback.Wav);
        Assert.Equal(TimeSpan.FromMilliseconds(300), clock.LastDueTime);
        Assert.False(speech.IsBusy);
    }

    [Fact]
    public async Task Stop_during_pause_waits_for_the_capture_barrier_and_resumes_without_playing()
    {
        var capture = new BlockingPauseCapture();
        await using var session = NewSession(capture);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var playback = new ControlledPlayback(completeImmediately: true);
        await using var speech = new MeetingSpeech(session, new ImmediateTts(), playback,
            new ImmediateTimeProvider());

        var speaking = speech.SpeakAsync("Cancel at the barrier", "default", "render-1");
        await capture.PauseEntered.Task;
        var stopping = speech.StopAsync();
        Assert.False(stopping.IsCompleted);

        capture.ReleasePause();
        await Task.WhenAll(speaking, stopping);

        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Equal(1, capture.ResumeCalls);
        Assert.Equal(0, playback.Calls);
    }

    [Fact]
    public async Task Stop_during_playback_waits_for_cleanup_then_resumes_the_same_meeting()
    {
        var capture = new SpeechCapture();
        await using var session = NewSession(capture);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var playback = new ControlledPlayback();
        await using var speech = new MeetingSpeech(session, new ImmediateTts(), playback,
            new ImmediateTimeProvider());

        var speaking = speech.SpeakAsync("Stop me", "default", "render-1");
        await playback.Entered.Task;
        var stopping = speech.StopAsync();
        await playback.Cancelled.Task;
        playback.Release();
        await Task.WhenAll(speaking, stopping);

        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Equal(1, capture.ResumeCalls);
        Assert.False(speech.IsBusy);
        Assert.Equal("Speech stopped.", speech.Status);
    }

    [Fact]
    public async Task Meeting_stop_rejects_a_late_synthesis_result_and_never_restarts_capture()
    {
        var capture = new SpeechCapture();
        await using var session = NewSession(capture);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var tts = new ControlledTts(ignoreCancellation: true);
        var playback = new ControlledPlayback(completeImmediately: true);
        await using var speech = new MeetingSpeech(session, tts, playback, new ImmediateTimeProvider());

        var speaking = speech.SpeakAsync("Obsolete", "default", "render-1");
        await tts.Entered.Task;
        await session.StopAsync();
        tts.Complete([9]);
        await speaking;

        Assert.Equal(MeetingState.Stopped, session.State);
        Assert.Equal(0, capture.ResumeCalls);
        Assert.Equal(0, playback.Calls);
    }

    [Fact]
    public async Task Partial_pause_failure_faults_the_meeting_and_never_starts_playback()
    {
        var capture = new SpeechCapture { FailPause = true };
        await using var session = NewSession(capture);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var playback = new ControlledPlayback(completeImmediately: true);
        await using var speech = new MeetingSpeech(session, new ImmediateTts(), playback,
            new ImmediateTimeProvider());

        await speech.SpeakAsync("Do not play", "default", "render-1");

        Assert.Equal(MeetingState.Faulted, session.State);
        Assert.Equal(0, playback.Calls);
        Assert.Equal(0, capture.ResumeCalls);
        Assert.Contains("pause", speech.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unconfirmed_playback_stop_keeps_listening_stopped_and_never_resumes_capture()
    {
        var capture = new SpeechCapture();
        await using var session = NewSession(capture);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var playback = new ControlledPlayback(throwUnconfirmedStop: true);
        await using var speech = new MeetingSpeech(session, new ImmediateTts(), playback,
            new ImmediateTimeProvider());

        await speech.SpeakAsync("Playback might still be running", "default", "render-1");

        Assert.Equal(MeetingState.Stopped, session.State);
        Assert.Equal(1, capture.PauseCalls);
        Assert.Equal(0, capture.ResumeCalls);
        Assert.Contains("could not be confirmed stopped", speech.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalidating_a_used_source_cancels_late_provider_output_and_clears_busy_state()
    {
        await using var session = NewSession(new SpeechCapture());
        var tts = new ControlledTts(ignoreCancellation: true);
        var playback = new ControlledPlayback(completeImmediately: true);
        await using var speech = new MeetingSpeech(session, tts, playback, new ImmediateTimeProvider());
        var speaking = speech.SpeakAsync("From a note", "default", "render-1", ["note-1", "segment-2"]);
        await tts.Entered.Task;

        speech.InvalidateSources(["unrelated"]);
        Assert.True(speech.IsBusy);
        speech.InvalidateSources(["note-1"]);
        tts.Complete([7]);
        await speaking;

        Assert.False(speech.IsBusy);
        Assert.Equal(0, playback.Calls);
    }

    [Fact]
    public async Task Starting_a_meeting_during_standalone_synthesis_invalidates_late_audio()
    {
        var capture = new SpeechCapture();
        await using var session = NewSession(capture);
        var tts = new ControlledTts(ignoreCancellation: true);
        var playback = new ControlledPlayback(completeImmediately: true);
        await using var speech = new MeetingSpeech(session, tts, playback, new ImmediateTimeProvider());
        var speaking = speech.SpeakAsync("Standalone", "default", "render-1");
        await tts.Entered.Task;

        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        tts.Complete([5]);
        await speaking;

        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Equal(0, playback.Calls);
        Assert.Equal(0, capture.PauseCalls);
    }

    [Fact]
    public async Task A_second_operation_and_oversized_text_are_rejected_without_replacing_the_first()
    {
        await using var session = NewSession(new SpeechCapture());
        var tts = new ControlledTts();
        await using var speech = new MeetingSpeech(session, tts,
            new ControlledPlayback(completeImmediately: true), new ImmediateTimeProvider());
        var first = speech.SpeakAsync("First", "default", "render-1");
        await tts.Entered.Task;

        await Assert.ThrowsAsync<MeetingException>(() => speech.SpeakAsync("Second", "default", "render-1"));
        await using var validator = new MeetingSpeech(session, new ImmediateTts(),
            new ControlledPlayback(true));
        await Assert.ThrowsAsync<MeetingException>(() =>
            validator.SpeakAsync(new string('x', 4097), "default", "render-1"));

        await speech.StopAsync();
        await first;
    }

    private static MeetingSession NewSession(IMeetingCapture capture) =>
        new(capture, new EmptyAsr());

    private sealed class EmptyAsr : IMeetingAsr
    {
        public Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service,
            CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }

    private sealed class SpeechCapture : IMeetingCapture
    {
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public bool FailPause { get; init; }
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => [];
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null)
        {
            PauseCalls++;
            onFirstSourceStopped?.Invoke(TimeSpan.FromSeconds(2));
            return FailPause ? Task.FromException(new InvalidOperationException("output failed")) : Task.CompletedTask;
        }
        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            ResumeCalls++;
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingPauseCapture : IMeetingCapture
    {
        private readonly TaskCompletionSource _pauseRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PauseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResumeCalls { get; private set; }
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => [];
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task PauseAsync(CancellationToken cancellationToken,
            Action<TimeSpan>? onFirstSourceStopped = null)
        {
            onFirstSourceStopped?.Invoke(TimeSpan.FromSeconds(1));
            PauseEntered.TrySetResult();
            await _pauseRelease.Task;
        }
        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            ResumeCalls++;
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void ReleasePause() => _pauseRelease.TrySetResult();
    }

    private sealed class ImmediateTts : IMeetingTts
    {
        public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken) =>
            Task.FromResult(new byte[] { 1 });
        public Task<IReadOnlyList<MeetingVoice>> GetVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MeetingVoice>>([]);
    }

    private sealed class ControlledTts(bool ignoreCancellation = false) : IMeetingTts
    {
        private readonly TaskCompletionSource<byte[]> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (ignoreCancellation)
                return await _result.Task;
            return await _result.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<MeetingVoice>> GetVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MeetingVoice>>([]);
        public void Complete(byte[] wav) => _result.TrySetResult(wav);
    }

    private sealed class ControlledPlayback(bool completeImmediately = false,
        bool throwUnconfirmedStop = false) : IMeetingPlayback
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public byte[]? Wav { get; private set; }
        public string? DeviceId { get; private set; }

        public async Task PlayAsync(byte[] wav, string deviceId, CancellationToken cancellationToken)
        {
            Calls++;
            Wav = (byte[])wav.Clone();
            DeviceId = deviceId;
            Entered.TrySetResult();
            if (throwUnconfirmedStop)
                throw new MeetingPlaybackStopException();
            if (completeImmediately)
                return;
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            await _release.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public TimeSpan? LastDueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime,
            TimeSpan period)
        {
            LastDueTime = dueTime;
            var timer = new ImmediateTimer();
            callback(state);
            return timer;
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
