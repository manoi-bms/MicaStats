using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingSessionTests
{
    [Fact]
    public async Task Start_transcribes_both_sources_and_orders_the_snapshot_by_session_time()
    {
        var capture = new ScriptedCapture();
        var asr = new ScriptedAsr(chunk => Task.FromResult(chunk.Source == MeetingSource.Microphone ? "mine" : "theirs"));
        await using var session = new MeetingSession(capture, asr);

        await session.StartAsync(new MeetingStartOptions("mic", "speakers"));
        capture.Emit(Chunk(MeetingSource.Microphone, 8));
        capture.Emit(Chunk(MeetingSource.Output, 4));

        await EventuallyAsync(() => session.Segments.Count == 2);
        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Equal(new[] { "theirs", "mine" }, session.Segments.Select(segment => segment.Text));
        Assert.True(string.CompareOrdinal(session.Segments[0].Id, session.Segments[1].Id) < 0);
        Assert.Equal("mic", capture.MicrophoneId);
        Assert.Equal("speakers", capture.OutputId);
    }

    [Fact]
    public async Task Stop_rejects_a_late_result_from_the_previous_generation()
    {
        var capture = new ScriptedCapture();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asr = new ScriptedAsr(_ => completion.Task);
        await using var session = new MeetingSession(capture, asr);

        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => asr.Calls == 1);
        await session.StopAsync();
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        completion.SetResult("obsolete");
        await Task.Delay(30);

        Assert.Empty(session.Segments);
        Assert.Equal(MeetingState.Listening, session.State);
    }

    [Fact]
    public async Task More_than_eight_waiting_chunks_faults_the_whole_session()
    {
        var capture = new ScriptedCapture();
        var asr = new ScriptedAsr(_ => new TaskCompletionSource<string>().Task);
        await using var session = new MeetingSession(capture, asr);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));

        for (var index = 0; index < 12; index++)
            capture.Emit(Chunk(MeetingSource.Microphone, index * 4));

        await EventuallyAsync(() => session.State == MeetingState.Faulted);
        Assert.Contains("backlog", session.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(capture.StopCalls > 0);
    }

    [Fact]
    public async Task Pause_and_resume_preserve_queued_asr_and_record_the_listening_gap()
    {
        var capture = new ScriptedCapture();
        var clock = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-10-05T00:00:00Z"));
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asr = new ScriptedAsr(_ => completion.Task);
        await using var session = new MeetingSession(capture, asr, clock);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => asr.Calls == 1);

        clock.Advance(TimeSpan.FromSeconds(12));
        await session.PauseAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        await session.ResumeAsync();
        completion.SetResult("before pause");

        await EventuallyAsync(() => session.Segments.Count == 1);
        var gap = Assert.Single(session.Gaps);
        Assert.Equal(TimeSpan.FromSeconds(12), gap.Start);
        Assert.Equal(TimeSpan.FromSeconds(15), gap.End);
        Assert.Equal("before pause", session.Segments[0].Text);
        Assert.Equal(1, capture.PauseCalls);
        Assert.Equal(1, capture.ResumeCalls);
    }

    [Fact]
    public async Task Export_quotes_every_transcript_line_as_data()
    {
        var capture = new ScriptedCapture();
        var asr = new ScriptedAsr(_ => Task.FromResult("hello\n# forged heading\n- forged item"));
        await using var session = new MeetingSession(capture, asr);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => session.Segments.Count == 1);

        var markdown = session.ExportMarkdown();

        Assert.Contains("> hello\n> # forged heading\n> - forged item", markdown);
        Assert.DoesNotContain("\n# forged heading", markdown.Replace("\n> # forged heading", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_fault_is_sanitized_and_stops_both_sources()
    {
        var capture = new ScriptedCapture();
        await using var session = new MeetingSession(capture, new ScriptedAsr(_ => Task.FromResult("unused")));
        await session.StartAsync(new MeetingStartOptions("mic", "out"));

        capture.Fault("device path and provider details");

        await EventuallyAsync(() => session.State == MeetingState.Faulted);
        Assert.Equal("Audio device became unavailable.", session.Status);
        Assert.True(capture.StopCalls > 0);
    }

    [Fact]
    public async Task Stop_cancels_a_capture_start_before_waiting_for_the_lifecycle_transition()
    {
        var capture = new BlockingStartCapture();
        await using var session = new MeetingSession(capture, new ScriptedAsr(_ => Task.FromResult("unused")));

        var starting = session.StartAsync(new MeetingStartOptions("mic", "out"));
        await capture.StartEntered.Task;
        var stopping = session.StopAsync();

        await Task.WhenAll(starting, stopping).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MeetingState.Stopped, session.State);
    }

    [Fact]
    public async Task A_partial_pause_failure_preserves_the_open_gap_and_faults_the_session()
    {
        var capture = new FailingPauseCapture();
        await using var session = new MeetingSession(capture, new ScriptedAsr(_ => Task.FromResult("unused")));
        await session.StartAsync(new MeetingStartOptions("mic", "out"));

        await session.PauseAsync();

        Assert.Equal(MeetingState.Faulted, session.State);
        var gap = Assert.Single(session.Gaps);
        Assert.Equal(TimeSpan.FromSeconds(7), gap.Start);
        Assert.Null(gap.End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_breaks_a_pending_pause_or_resume_barrier(bool blockResume)
    {
        var capture = new BlockingTransitionCapture(blockResume);
        await using var session = new MeetingSession(capture, new ScriptedAsr(_ => Task.FromResult("unused")));
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        if (blockResume)
            await session.PauseAsync();

        var transition = blockResume ? session.ResumeAsync() : session.PauseAsync();
        await capture.Entered.Task;
        var stopping = session.StopAsync();

        await Task.WhenAll(transition, stopping).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MeetingState.Stopped, session.State);
    }

    [Fact]
    public async Task Eight_hour_clock_limit_faults_and_preserves_received_transcript()
    {
        var capture = new ScriptedCapture();
        var clock = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-10-05T00:00:00Z"));
        await using var session = new MeetingSession(capture,
            new ScriptedAsr(_ => Task.FromResult("received before the limit")), clock);
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => session.Segments.Count == 1);

        clock.Advance(TimeSpan.FromHours(8));

        await EventuallyAsync(() => session.State == MeetingState.Faulted);
        Assert.Contains("eight-hour", session.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("received before the limit", Assert.Single(session.Segments).Text);
    }

    [Fact]
    public async Task Two_million_character_limit_faults_without_discarding_received_segments()
    {
        var capture = new ScriptedCapture();
        var recognized = new string('a', 16_000);
        await using var session = new MeetingSession(capture,
            new ScriptedAsr(_ => Task.FromResult(recognized)));
        await session.StartAsync(new MeetingStartOptions("mic", "out"));

        for (var index = 0; index < 125; index++)
        {
            capture.Emit(Chunk(MeetingSource.Microphone, index * 4));
            var expected = index + 1;
            await EventuallyAsync(() => session.Segments.Count == expected);
        }
        capture.Emit(Chunk(MeetingSource.Microphone, 125 * 4));

        await EventuallyAsync(() => session.State == MeetingState.Faulted);
        Assert.Contains("size limit", session.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(125, session.Segments.Count);
        Assert.Equal(2_000_000, session.Segments.Sum(segment => segment.Text.Length));
    }

    private static MeetingAudioChunk Chunk(MeetingSource source, int startSeconds) =>
        new(source, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(4), new byte[] { 1, 2, 3 });

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ScriptedCapture : IMeetingCapture
    {
        private Action<MeetingAudioChunk>? _onChunk;
        private Action<string>? _onFault;
        public string? MicrophoneId { get; private set; }
        public string? OutputId { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int StopCalls { get; private set; }

        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => Array.Empty<MeetingDevice>();

        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken)
        {
            MicrophoneId = microphoneId;
            OutputId = outputId;
            _onChunk = onChunk;
            _onFault = onFault;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null)
        {
            PauseCalls++;
            onFirstSourceStopped?.Invoke(TimeSpan.FromSeconds(12));
            return Task.CompletedTask;
        }
        public Task ResumeAsync(CancellationToken cancellationToken) { ResumeCalls++; return Task.CompletedTask; }
        public Task StopAsync() { StopCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(MeetingAudioChunk chunk) => _onChunk?.Invoke(chunk);
        public void Fault(string detail) => _onFault?.Invoke(detail);
    }

    private sealed class ScriptedAsr(Func<MeetingAudioChunk, Task<string>> transcribe) : IMeetingAsr
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return transcribe(chunk);
        }
    }

    private sealed class BlockingStartCapture : IMeetingCapture
    {
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => Array.Empty<MeetingDevice>();
        public async Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken)
        {
            StartEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null) =>
            Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingPauseCapture : IMeetingCapture
    {
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => Array.Empty<MeetingDevice>();
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null)
        {
            onFirstSourceStopped?.Invoke(TimeSpan.FromSeconds(7));
            throw new InvalidOperationException("second source failed");
        }
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTransitionCapture(bool blockResume) : IMeetingCapture
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => Array.Empty<MeetingDevice>();
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task PauseAsync(CancellationToken cancellationToken,
            Action<TimeSpan>? onFirstSourceStopped = null)
        {
            onFirstSourceStopped?.Invoke(TimeSpan.FromSeconds(1));
            if (!blockResume)
            {
                Entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
        public async Task ResumeAsync(CancellationToken cancellationToken)
        {
            if (blockResume)
            {
                Entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<AdjustableTimer> _timers = new();
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => current;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new AdjustableTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            AdjustableTimer[] due;
            lock (_gate)
            {
                current += elapsed;
                _timestamp += elapsed.Ticks;
                due = _timers.Where(timer => timer.IsDue(_timestamp)).ToArray();
            }
            foreach (var timer in due)
                timer.Fire(_timestamp);
        }

        private sealed class AdjustableTimer(AdjustableTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _dueAt = long.MaxValue;
            private long _period = Timeout.Infinite;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._timestamp + dueTime.Ticks;
                    _period = period == Timeout.InfiniteTimeSpan ? Timeout.Infinite : period.Ticks;
                    return true;
                }
            }

            public bool IsDue(long timestamp)
            {
                lock (owner._gate) return !_disposed && timestamp >= _dueAt;
            }

            public void Fire(long timestamp)
            {
                lock (owner._gate)
                {
                    if (_disposed || timestamp < _dueAt) return;
                    _dueAt = _period > 0 ? timestamp + _period : long.MaxValue;
                }
                callback(state);
            }

            public void Dispose()
            {
                lock (owner._gate) _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
