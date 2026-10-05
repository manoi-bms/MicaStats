using System.Collections.Concurrent;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingDualAsrTests
{
    [Fact]
    public async Task Comparing_with_asr1_as_the_preferred_service_is_rejected_before_capture()
    {
        var capture = new FakeCapture();
        await using var session = new MeetingSession(capture, new DelegateAsr((_, _, _) => Task.FromResult("unused")));

        var error = await Assert.ThrowsAsync<MeetingException>(() =>
            session.StartAsync(new MeetingStartOptions("mic", "out", AsrService.Asr1, CompareBothServices: true)));

        Assert.Contains("ASR2", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, capture.StartCalls);
    }

    [Fact]
    public async Task Both_requests_start_before_either_completes_and_matching_readings_share_one_segment()
    {
        var capture = new FakeCapture();
        var completions = new ConcurrentDictionary<AsrService, TaskCompletionSource<string>>();
        var asr = new DelegateAsr((_, service, _) =>
            completions.GetOrAdd(service, _ => NewCompletion()).Task);
        await using var session = await StartAsync(capture, asr);

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => completions.Count == 2);
        Assert.All(completions.Values, completion => Assert.False(completion.Task.IsCompleted));

        completions[AsrService.Asr2].SetResult("  Cafe\u0301   NOW  ");
        completions[AsrService.Asr1].SetResult("Café now");
        await EventuallyAsync(() => session.Segments.Count == 1);

        var segment = Assert.Single(session.Segments);
        Assert.Equal("Cafe\u0301   NOW", segment.Text);
        Assert.Equal("Café now", Assert.IsType<MeetingAsrComparison>(segment.Comparison).Asr1Text);
        Assert.Null(segment.Comparison.AlternativeText);
        Assert.Contains("same", segment.Comparison.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("approve 15 users", "approve 50 users")]
    [InlineData("deploy today", "do not deploy today")]
    public async Task Different_numbers_or_negation_remain_visible_as_alternative_readings(
        string asr2Text, string asr1Text)
    {
        var capture = new FakeCapture();
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, service, _) => Task.FromResult(service == AsrService.Asr2 ? asr2Text : asr1Text)));

        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => session.Segments.Count == 1);

        var segment = Assert.Single(session.Segments);
        Assert.Equal(asr2Text, segment.Text);
        Assert.Equal(asr1Text, segment.Comparison?.AlternativeText);
        Assert.Equal(AsrService.Asr2, segment.Comparison?.PreferredService);
        Assert.Contains("different", segment.Comparison?.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_provider_unicode_remains_data_and_does_not_break_comparison_metadata()
    {
        var comparison = new MeetingAsrComparison("valid", "invalid \uD800 text", true, true);

        Assert.Equal("invalid \uD800 text", comparison.AlternativeText);
        Assert.Contains("different", comparison.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "fallback", "fallback", AsrService.Asr1)]
    [InlineData("preferred", "", "preferred", AsrService.Asr2)]
    public async Task One_empty_reading_uses_the_nonempty_reading(
        string asr2Text, string asr1Text, string expected, AsrService preferred)
    {
        var capture = new FakeCapture();
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, service, _) => Task.FromResult(service == AsrService.Asr2 ? asr2Text : asr1Text)));

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => session.Segments.Count == 1);

        var comparison = Assert.IsType<MeetingAsrComparison>(Assert.Single(session.Segments).Comparison);
        Assert.Equal(expected, Assert.Single(session.Segments).Text);
        Assert.Equal(preferred, comparison.PreferredService);
        Assert.Null(comparison.AlternativeText);
        Assert.Contains("no speech", comparison.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Both_successful_empty_readings_do_not_publish_a_segment()
    {
        var capture = new FakeCapture();
        var calls = 0;
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult("  "); }));

        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => Volatile.Read(ref calls) == 2);
        await Task.Delay(30);

        Assert.Empty(session.Segments);
        Assert.Equal(MeetingState.Listening, session.State);
    }

    [Theory]
    [InlineData(AsrService.Asr2)]
    [InlineData(AsrService.Asr1)]
    public async Task Failed_provider_is_disabled_for_the_generation_and_degraded_status_survives_pause_resume(
        AsrService failingService)
    {
        var capture = new FakeCapture();
        var calls = new ConcurrentDictionary<AsrService, int>();
        var asr = new DelegateAsr((chunk, service, _) =>
        {
            calls.AddOrUpdate(service, 1, (_, count) => count + 1);
            if (service == failingService)
                throw new InvalidOperationException("provider detail");
            return Task.FromResult(chunk.Start == TimeSpan.Zero ? "first survivor" : "second survivor");
        });
        await using var session = await StartAsync(capture, asr);

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => session.Segments.Count == 1);
        Assert.Contains("unavailable", session.Status, StringComparison.OrdinalIgnoreCase);

        await session.PauseAsync();
        Assert.Contains("unavailable", session.Status, StringComparison.OrdinalIgnoreCase);
        await session.ResumeAsync();
        Assert.Contains("unavailable", session.Status, StringComparison.OrdinalIgnoreCase);

        capture.Emit(Chunk(MeetingSource.Microphone, 4));
        await EventuallyAsync(() => session.Segments.Count == 2);
        Assert.Equal(1, calls[failingService]);
        Assert.Equal(2, calls[Other(failingService)]);
    }

    [Fact]
    public async Task Provider_failure_on_one_source_cancels_same_provider_request_on_the_other_source()
    {
        var capture = new FakeCapture();
        var outputAsr2Started = NewSignal();
        var outputAsr2Cancelled = NewSignal();
        var releaseMicFailure = NewSignal();
        var asr = new DelegateAsr(async (chunk, service, token) =>
        {
            if (service == AsrService.Asr1)
                return $"survivor-{chunk.Source}";
            if (chunk.Source == MeetingSource.Output)
            {
                outputAsr2Started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    outputAsr2Cancelled.SetResult();
                    throw;
                }
            }
            await releaseMicFailure.Task;
            throw new InvalidOperationException("fail ASR2");
        });
        await using var session = await StartAsync(capture, asr);

        capture.Emit(Chunk(MeetingSource.Output, 0));
        await outputAsr2Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        releaseMicFailure.SetResult();

        await outputAsr2Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => session.Segments.Count == 2);
        Assert.All(session.Segments, segment => Assert.Equal(AsrService.Asr1, segment.Comparison?.PreferredService));
    }

    [Fact]
    public async Task Failures_on_different_sources_fault_as_soon_as_both_shared_breakers_are_open()
    {
        var capture = new FakeCapture();
        var calls = 0;
        var releaseFailures = NewSignal();
        var asr = new DelegateAsr(async (chunk, service, _) =>
        {
            Interlocked.Increment(ref calls);
            var fails = (chunk.Source == MeetingSource.Microphone && service == AsrService.Asr2) ||
                        (chunk.Source == MeetingSource.Output && service == AsrService.Asr1);
            if (!fails)
                return $"survivor-{chunk.Source}";
            await releaseFailures.Task;
            throw new InvalidOperationException("provider failed");
        });
        await using var session = await StartAsync(capture, asr);

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => Volatile.Read(ref calls) == 4);
        releaseFailures.SetResult();

        await EventuallyAsync(() => session.State == MeetingState.Faulted);
        Assert.Contains("both", session.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Both_provider_failures_fault_the_session_without_raw_details()
    {
        var capture = new FakeCapture();
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, _, _) => throw new InvalidOperationException("secret response body")));

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => session.State == MeetingState.Faulted);

        Assert.Contains("both", session.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", session.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(capture.StopCalls > 0);
    }

    [Fact]
    public async Task Failed_provider_with_silent_survivor_keeps_degraded_status_without_a_segment()
    {
        var capture = new FakeCapture();
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, service, _) => service == AsrService.Asr2
                ? throw new InvalidOperationException("unavailable")
                : Task.FromResult(string.Empty)));

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => session.Status.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        await Task.Delay(30);

        Assert.Empty(session.Segments);
        Assert.Equal(MeetingState.Listening, session.State);
        Assert.Contains("ASR2", session.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_deadlines_trip_both_breakers_and_fault_the_session()
    {
        var capture = new FakeCapture();
        var clock = new ManualTimeProvider();
        var calls = 0;
        await using var session = new MeetingSession(capture,
            new DelegateAsr(async (_, _, token) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return string.Empty;
            }), clock);
        await session.StartAsync(Options());
        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => Volatile.Read(ref calls) == 2);

        clock.Advance(TimeSpan.FromSeconds(31));
        await EventuallyAsync(() => session.State == MeetingState.Faulted);

        Assert.Contains("both", session.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_then_restart_retries_both_and_rejects_stale_results()
    {
        var capture = new FakeCapture();
        var first = new ConcurrentDictionary<AsrService, TaskCompletionSource<string>>();
        var generation = 1;
        var asr = new DelegateAsr((_, service, _) => generation == 1
            ? first.GetOrAdd(service, _ => NewCompletion()).Task
            : Task.FromResult(service == AsrService.Asr2 ? "fresh" : "fresh"));
        await using var session = await StartAsync(capture, asr);
        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => first.Count == 2);

        await session.StopAsync();
        generation = 2;
        await session.StartAsync(Options());
        first[AsrService.Asr2].SetResult("obsolete");
        first[AsrService.Asr1].SetResult("obsolete");
        capture.Emit(Chunk(MeetingSource.Output, 4));
        await EventuallyAsync(() => session.Segments.Count == 1);

        Assert.Equal("fresh", Assert.Single(session.Segments).Text);
    }

    [Fact]
    public async Task Transcript_limit_counts_both_provider_readings()
    {
        var capture = new FakeCapture();
        var large = new string('ก', 1_000_001);
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, _, _) => Task.FromResult(large)));

        capture.Emit(Chunk(MeetingSource.Microphone, 0));
        await EventuallyAsync(() => session.State == MeetingState.Faulted);

        Assert.Empty(session.Segments);
        Assert.Contains("size limit", session.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Markdown_quotes_both_provider_readings_as_transcript_data()
    {
        var capture = new FakeCapture();
        await using var session = await StartAsync(capture,
            new DelegateAsr((_, service, _) => Task.FromResult(service == AsrService.Asr2
                ? "primary"
                : "alternate\n# forged heading\n- forged item")));
        capture.Emit(Chunk(MeetingSource.Output, 0));
        await EventuallyAsync(() => session.Segments.Count == 1);

        var markdown = session.ExportMarkdown();

        Assert.Contains("ASR2 preferred reading:", markdown);
        Assert.Contains("ASR1 alternative reading:", markdown);
        Assert.Contains("> alternate\n> # forged heading\n> - forged item", markdown);
        Assert.Equal(1, markdown.Split("primary", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("\n# forged heading", markdown.Replace("\n> # forged heading", "", StringComparison.Ordinal));
    }

    private static MeetingStartOptions Options() => new("mic", "out", AsrService.Asr2, CompareBothServices: true);

    private static async Task<MeetingSession> StartAsync(FakeCapture capture, IMeetingAsr asr)
    {
        var session = new MeetingSession(capture, asr);
        await session.StartAsync(Options());
        return session;
    }

    private static MeetingAudioChunk Chunk(MeetingSource source, int startSeconds) =>
        new(source, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(4), [1, 2, 3]);

    private static AsrService Other(AsrService service) =>
        service == AsrService.Asr2 ? AsrService.Asr1 : AsrService.Asr2;

    private static TaskCompletionSource<string> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeCapture : IMeetingCapture
    {
        private Action<MeetingAudioChunk>? _onChunk;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => [];
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken)
        {
            StartCalls++;
            _onChunk = onChunk;
            return Task.CompletedTask;
        }
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null) =>
            Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() { StopCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(MeetingAudioChunk chunk) => _onChunk?.Invoke(chunk);
    }

    private sealed class DelegateAsr(
        Func<MeetingAudioChunk, AsrService, CancellationToken, Task<string>> transcribe) : IMeetingAsr
    {
        public Task<string> TranscribeAsync(
            MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken) =>
            transcribe(chunk, service, cancellationToken);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _timestamp += elapsed.Ticks;
                due = _timers.Where(timer => timer.IsDue(_timestamp)).ToArray();
            }
            foreach (var timer in due)
                timer.Fire(_timestamp);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
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
